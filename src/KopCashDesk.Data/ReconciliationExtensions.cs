using KopCashDesk.Core;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace KopCashDesk.Data;

public static class ReconciliationExtensions
{
    private const int AlgorithmVersion = 1;

    private sealed record BankDay(DateOnly Date, long AmountKopecks, int Count);
    private sealed record CashEvent(DateOnly Date, string Source, string ExternalId, long AmountKopecks, string OccurredAt);
    private sealed record ShiftMeta(DateOnly Date, DateTimeOffset? LastClosedAt, string ShiftNumbers);

    private sealed class OpenTerminalBucket(DateOnly date, long amountKopecks)
    {
        public DateOnly Date { get; } = date;
        public long OriginalKopecks { get; } = amountKopecks;
        public long RemainingKopecks { get; set; } = amountKopecks;
    }

    private sealed class ReturnCreditLot(DateOnly date, long amountKopecks)
    {
        public DateOnly Date { get; } = date;
        public long RemainingKopecks { get; set; } = amountKopecks;
    }

    private sealed record PendingAllocation(
        DateOnly TerminalDate,
        ReconciliationAllocationKind Kind,
        string SettlementSource,
        string SettlementExternalId,
        DateOnly SettlementDate,
        long AmountKopecks);

    public static IReadOnlyList<ReconciliationDay> ReconciliationDays(
        this Database database,
        Guid? organizationId = null,
        int? year = null,
        int? month = null,
        Guid? locationId = null)
    {
        var organizations = database.Organizations().ToDictionary(x => x.Id);
        var locations = database.Locations()
            .Where(x => organizationId is null || x.OrganizationId == organizationId)
            .Where(x => locationId is null || x.Id == locationId)
            .OrderBy(x => x.OrganizationId)
            .ThenBy(x => x.Name)
            .ToArray();

        var result = new List<ReconciliationDay>();
        foreach (var location in locations)
        {
            if (!organizations.TryGetValue(location.OrganizationId, out var organization)) continue;
            var days = RecalculateLocation(database, organization, location);
            result.AddRange(days.Where(x =>
                (year is null || x.Date.Year == year) &&
                (month is null || x.Date.Month == month)));
        }

        return result
            .OrderByDescending(x => x.Date)
            .ThenBy(x => x.Organization)
            .ThenBy(x => x.Location)
            .ToArray();
    }

    public static IReadOnlyList<ReconciliationAllocation> ReconciliationAllocations(
        this Database database,
        Guid organizationId,
        Guid locationId)
    {
        using var db = Open(database);
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT id,organization_id,location_id,terminal_date,settlement_kind,settlement_source,
                   settlement_external_id,settlement_date,allocated_amount_kopecks,algorithm_version,created_at
            FROM reconciliation_allocations
            WHERE organization_id=$org AND location_id=$loc
            ORDER BY terminal_date,settlement_date,settlement_source,settlement_external_id,id
            """;
        command.Parameters.AddWithValue("$org", organizationId.ToString());
        command.Parameters.AddWithValue("$loc", locationId.ToString());
        using var reader = command.ExecuteReader();
        var result = new List<ReconciliationAllocation>();
        while (reader.Read())
        {
            result.Add(new(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                Guid.Parse(reader.GetString(2)),
                ParseDate(reader.GetString(3)),
                Enum.Parse<ReconciliationAllocationKind>(reader.GetString(4), true),
                reader.GetString(5),
                reader.GetString(6),
                ParseDate(reader.GetString(7)),
                Money.FromKopecks(reader.GetInt64(8)),
                reader.GetInt32(9),
                DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture)));
        }
        return result;
    }

    private static IReadOnlyList<ReconciliationDay> RecalculateLocation(Database database, Organization organization, Location location)
    {
        using var db = Open(database);
        using var transaction = db.BeginTransaction();

        var bankDays = ReadBankDays(db, transaction, organization.Id, location.Id);
        var fiscalEvents = ReadFiscalEvents(db, transaction, organization.Id, location.Id);
        var manualEvents = ReadManualEvents(db, transaction, organization.Id, location.Id);
        var shifts = ReadShiftMeta(db, transaction, organization.Id, location.Id);

        DeleteAllocations(db, transaction, organization.Id, location.Id);

        var actualFiscalDates = fiscalEvents.Select(x => x.Date).ToHashSet();
        var effectiveCashEvents = fiscalEvents
            .Concat(manualEvents.Where(x => !actualFiscalDates.Contains(x.Date)))
            .OrderBy(x => x.Date)
            .ThenBy(x => x.OccurredAt, StringComparer.Ordinal)
            .ThenBy(x => x.Source, StringComparer.Ordinal)
            .ThenBy(x => x.ExternalId, StringComparer.Ordinal)
            .ToArray();

        var bankByDate = bankDays.ToDictionary(x => x.Date);
        var cashByDate = effectiveCashEvents.GroupBy(x => x.Date).ToDictionary(x => x.Key, x => x.ToArray());
        var shiftByDate = shifts.ToDictionary(x => x.Date);
        var dates = bankDays.Select(x => x.Date)
            .Concat(effectiveCashEvents.Select(x => x.Date))
            .Concat(shifts.Select(x => x.Date))
            .Distinct()
            .OrderBy(x => x)
            .ToArray();

        if (dates.Length == 0)
        {
            transaction.Commit();
            return [];
        }

        if (location.IsExcluded)
        {
            var excludedRows = BuildExcludedRows(organization, location, dates, bankByDate, cashByDate, shiftByDate);
            transaction.Commit();
            return excludedRows;
        }

        var open = new LinkedList<OpenTerminalBucket>();
        var returnCredits = new Queue<ReturnCreditLot>();
        var allocations = new List<PendingAllocation>();
        var priorOutstanding = new Dictionary<DateOnly, long>();
        var endOutstanding = new Dictionary<DateOnly, long>();
        var unmatchedCash = new Dictionary<DateOnly, long>();
        var reviewDates = new HashSet<DateOnly>();

        foreach (var date in dates)
        {
            priorOutstanding[date] = Outstanding(open);

            if (bankByDate.TryGetValue(date, out var bank))
            {
                if (bank.AmountKopecks > 0)
                {
                    var remaining = bank.AmountKopecks;
                    while (remaining > 0 && returnCredits.Count > 0)
                    {
                        var credit = returnCredits.Peek();
                        var amount = Math.Min(remaining, credit.RemainingKopecks);
                        allocations.Add(new(date, ReconciliationAllocationKind.BankReturnCredit,
                            "Bank.ReturnCredit", $"return-credit:{credit.Date:yyyy-MM-dd}", credit.Date, amount));
                        remaining -= amount;
                        credit.RemainingKopecks -= amount;
                        if (credit.RemainingKopecks == 0) returnCredits.Dequeue();
                    }
                    if (remaining > 0) open.AddLast(new OpenTerminalBucket(date, remaining));
                }
                else if (bank.AmountKopecks < 0)
                {
                    reviewDates.Add(date);
                    var credit = Math.Abs(bank.AmountKopecks);
                    while (credit > 0 && open.First is not null)
                    {
                        var bucket = open.First.Value;
                        var amount = Math.Min(credit, bucket.RemainingKopecks);
                        allocations.Add(new(bucket.Date, ReconciliationAllocationKind.BankReturnCredit,
                            "Bank.NetReturn", $"bank-return:{date:yyyy-MM-dd}", date, amount));
                        bucket.RemainingKopecks -= amount;
                        credit -= amount;
                        if (bucket.RemainingKopecks == 0) open.RemoveFirst();
                    }
                    if (credit > 0) returnCredits.Enqueue(new ReturnCreditLot(date, credit));
                }
            }

            if (cashByDate.TryGetValue(date, out var cashEvents))
            {
                foreach (var cash in cashEvents)
                {
                    if (cash.AmountKopecks < 0)
                    {
                        reviewDates.Add(date);
                        unmatchedCash[date] = unmatchedCash.GetValueOrDefault(date) + Math.Abs(cash.AmountKopecks);
                        continue;
                    }

                    var remainingCash = cash.AmountKopecks;
                    while (remainingCash > 0 && open.First is not null)
                    {
                        var bucket = open.First.Value;
                        var amount = Math.Min(remainingCash, bucket.RemainingKopecks);
                        allocations.Add(new(bucket.Date, ReconciliationAllocationKind.Cash,
                            cash.Source, cash.ExternalId, date, amount));
                        bucket.RemainingKopecks -= amount;
                        remainingCash -= amount;
                        if (bucket.RemainingKopecks == 0) open.RemoveFirst();
                    }

                    if (remainingCash > 0)
                    {
                        unmatchedCash[date] = unmatchedCash.GetValueOrDefault(date) + remainingCash;
                        reviewDates.Add(date);
                    }
                }
            }

            endOutstanding[date] = Outstanding(open);
        }

        foreach (var credit in returnCredits)
            if (credit.RemainingKopecks > 0) reviewDates.Add(credit.Date);

        PersistAllocations(db, transaction, organization.Id, location.Id, allocations);
        var result = BuildRows(
            organization, location, dates, bankByDate, cashByDate, shiftByDate,
            allocations, priorOutstanding, endOutstanding, unmatchedCash, reviewDates);

        transaction.Commit();
        return result;
    }

    private static IReadOnlyList<ReconciliationDay> BuildRows(
        Organization organization,
        Location location,
        IReadOnlyList<DateOnly> dates,
        IReadOnlyDictionary<DateOnly, BankDay> bankByDate,
        IReadOnlyDictionary<DateOnly, CashEvent[]> cashByDate,
        IReadOnlyDictionary<DateOnly, ShiftMeta> shiftByDate,
        IReadOnlyList<PendingAllocation> allocations,
        IReadOnlyDictionary<DateOnly, long> priorOutstanding,
        IReadOnlyDictionary<DateOnly, long> endOutstanding,
        IReadOnlyDictionary<DateOnly, long> unmatchedCash,
        IReadOnlySet<DateOnly> reviewDates)
    {
        var closedByTerminal = allocations
            .GroupBy(x => x.TerminalDate)
            .ToDictionary(x => x.Key, x => x.Sum(a => a.AmountKopecks));
        var closedLater = allocations
            .Where(x => x.Kind == ReconciliationAllocationKind.Cash && x.SettlementDate > x.TerminalDate)
            .GroupBy(x => x.TerminalDate)
            .ToDictionary(x => x.Key, x => x.Sum(a => a.AmountKopecks));
        var cashApplied = allocations
            .Where(x => x.Kind == ReconciliationAllocationKind.Cash)
            .GroupBy(x => x.SettlementDate)
            .ToDictionary(x => x.Key, x => x.Sum(a => a.AmountKopecks));
        var lastSettlement = allocations
            .Where(x => x.Kind == ReconciliationAllocationKind.Cash)
            .GroupBy(x => x.TerminalDate)
            .ToDictionary(x => x.Key, x => (DateOnly?)x.Max(a => a.SettlementDate));
        var hasReturnAllocation = allocations
            .Where(x => x.Kind == ReconciliationAllocationKind.BankReturnCredit)
            .Select(x => x.TerminalDate)
            .ToHashSet();

        var result = new List<ReconciliationDay>();
        foreach (var date in dates)
        {
            bankByDate.TryGetValue(date, out var bank);
            cashByDate.TryGetValue(date, out var cashEvents);
            shiftByDate.TryGetValue(date, out var shift);

            var hasBank = bank is not null;
            var hasCash = cashEvents is { Length: > 0 };
            decimal? bankAmount = hasBank ? Money.FromKopecks(bank!.AmountKopecks) : null;
            decimal? cashAmount = hasCash ? Money.FromKopecks(cashEvents!.Sum(x => x.AmountKopecks)) : null;
            var closed = closedByTerminal.GetValueOrDefault(date);
            var later = closedLater.GetValueOrDefault(date);
            var rawPositiveBank = Math.Max(0, bank?.AmountKopecks ?? 0);
            var remainingForDay = Math.Max(0, rawPositiveBank - closed);
            var unmatched = unmatchedCash.GetValueOrDefault(date);
            var requiresReview = reviewDates.Contains(date) || hasReturnAllocation.Contains(date) || unmatched > 0;

            var status = Status(
                hasBank,
                bank?.AmountKopecks,
                hasCash,
                closed,
                later,
                remainingForDay,
                cashApplied.GetValueOrDefault(date),
                unmatched,
                requiresReview);

            result.Add(new(
                date,
                organization.Id,
                organization.Name,
                location.Id,
                location.Name,
                bankAmount,
                cashAmount,
                Money.FromKopecks(priorOutstanding.GetValueOrDefault(date)),
                Money.FromKopecks(cashApplied.GetValueOrDefault(date)),
                Money.FromKopecks(closed),
                Money.FromKopecks(later),
                Money.FromKopecks(remainingForDay),
                Money.FromKopecks(endOutstanding.GetValueOrDefault(date)),
                Money.FromKopecks(unmatched),
                hasBank,
                hasCash,
                requiresReview,
                false,
                lastSettlement.GetValueOrDefault(date),
                shift?.LastClosedAt,
                shift?.ShiftNumbers ?? string.Empty,
                status));
        }
        return result;
    }

    private static IReadOnlyList<ReconciliationDay> BuildExcludedRows(
        Organization organization,
        Location location,
        IReadOnlyList<DateOnly> dates,
        IReadOnlyDictionary<DateOnly, BankDay> bankByDate,
        IReadOnlyDictionary<DateOnly, CashEvent[]> cashByDate,
        IReadOnlyDictionary<DateOnly, ShiftMeta> shiftByDate)
    {
        return dates.Select(date =>
        {
            bankByDate.TryGetValue(date, out var bank);
            cashByDate.TryGetValue(date, out var cash);
            shiftByDate.TryGetValue(date, out var shift);
            return new ReconciliationDay(
                date, organization.Id, organization.Name, location.Id, location.Name,
                bank is null ? null : Money.FromKopecks(bank.AmountKopecks),
                cash is null ? null : Money.FromKopecks(cash.Sum(x => x.AmountKopecks)),
                0m, 0m, 0m, 0m, 0m, 0m, 0m,
                bank is not null, cash is { Length: > 0 }, false, true, null,
                shift?.LastClosedAt, shift?.ShiftNumbers ?? string.Empty,
                "Исключено из автосверки");
        }).ToArray();
    }

    private static string Status(
        bool hasBank,
        long? bankAmount,
        bool hasCash,
        long closed,
        long closedLater,
        long remainingForDay,
        long cashAppliedOnDate,
        long unmatchedCash,
        bool requiresReview)
    {
        if (requiresReview) return "Требует проверки";
        if (!hasBank && hasCash)
            return cashAppliedOnDate > 0 && unmatchedCash == 0 ? "Касса закрывает ранее" : "Нет данных терминалов";
        if (hasBank && bankAmount < 0) return "Требует проверки";
        if (hasBank && bankAmount == 0)
            return hasCash ? "Сошлось" : "Нет данных кассы";
        if (hasBank)
        {
            if (remainingForDay == 0)
                return closedLater > 0 ? "Пробито позже" : "Сошлось";
            if (closed > 0) return "Частично пробито";
            return hasCash ? "Не пробито" : "Нет данных кассы";
        }
        return "Нет данных";
    }

    private static List<BankDay> ReadBankDays(SqliteConnection db, SqliteTransaction tx, Guid organizationId, Guid locationId)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT substr(occurred_at,1,10),SUM(amount_kopecks),COUNT(*)
            FROM operations
            WHERE organization_id=$org AND location_id=$loc AND source_kind='Bank' AND payment='Electronic'
            GROUP BY substr(occurred_at,1,10)
            ORDER BY substr(occurred_at,1,10)
            """;
        command.Parameters.AddWithValue("$org", organizationId.ToString());
        command.Parameters.AddWithValue("$loc", locationId.ToString());
        using var reader = command.ExecuteReader();
        var result = new List<BankDay>();
        while (reader.Read()) result.Add(new(ParseDate(reader.GetString(0)), reader.GetInt64(1), reader.GetInt32(2)));
        return result;
    }

    private static List<CashEvent> ReadFiscalEvents(SqliteConnection db, SqliteTransaction tx, Guid organizationId, Guid locationId)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT substr(occurred_at,1,10),source,external_id,amount_kopecks,occurred_at
            FROM operations
            WHERE organization_id=$org AND location_id=$loc AND source_kind='Fiscal' AND payment='Electronic'
            ORDER BY occurred_at,source,external_id
            """;
        command.Parameters.AddWithValue("$org", organizationId.ToString());
        command.Parameters.AddWithValue("$loc", locationId.ToString());
        using var reader = command.ExecuteReader();
        var result = new List<CashEvent>();
        while (reader.Read())
            result.Add(new(ParseDate(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetString(4)));
        return result;
    }

    private static List<CashEvent> ReadManualEvents(SqliteConnection db, SqliteTransaction tx, Guid organizationId, Guid locationId)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT business_date,electronic_kopecks,updated_at
            FROM manual_cash_postings
            WHERE organization_id=$org AND location_id=$loc
            ORDER BY business_date
            """;
        command.Parameters.AddWithValue("$org", organizationId.ToString());
        command.Parameters.AddWithValue("$loc", locationId.ToString());
        using var reader = command.ExecuteReader();
        var result = new List<CashEvent>();
        while (reader.Read())
        {
            var date = ParseDate(reader.GetString(0));
            result.Add(new(date, "Manual.Cash", $"manual:{date:yyyy-MM-dd}", reader.GetInt64(1), reader.GetString(2)));
        }
        return result;
    }

    private static List<ShiftMeta> ReadShiftMeta(SqliteConnection db, SqliteTransaction tx, Guid organizationId, Guid locationId)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT closed_at,shift_number
            FROM shift_closures
            WHERE organization_id=$org AND location_id=$loc
            ORDER BY closed_at,shift_number
            """;
        command.Parameters.AddWithValue("$org", organizationId.ToString());
        command.Parameters.AddWithValue("$loc", locationId.ToString());
        using var reader = command.ExecuteReader();
        var rows = new List<(DateTimeOffset ClosedAt, int? Number)>();
        while (reader.Read())
            rows.Add((DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture), reader.IsDBNull(1) ? null : reader.GetInt32(1)));

        return rows
            .GroupBy(x => DateOnly.FromDateTime(x.ClosedAt.LocalDateTime))
            .Select(g => new ShiftMeta(
                g.Key,
                g.Max(x => x.ClosedAt),
                string.Join(", ", g.Where(x => x.Number is not null).Select(x => x.Number!.Value).Distinct().OrderBy(x => x))))
            .ToList();
    }

    private static void DeleteAllocations(SqliteConnection db, SqliteTransaction tx, Guid organizationId, Guid locationId)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "DELETE FROM reconciliation_allocations WHERE organization_id=$org AND location_id=$loc";
        command.Parameters.AddWithValue("$org", organizationId.ToString());
        command.Parameters.AddWithValue("$loc", locationId.ToString());
        command.ExecuteNonQuery();
    }

    private static void PersistAllocations(
        SqliteConnection db,
        SqliteTransaction tx,
        Guid organizationId,
        Guid locationId,
        IEnumerable<PendingAllocation> allocations)
    {
        var createdAt = DateTimeOffset.UtcNow.ToString("O");
        foreach (var allocation in allocations.Where(x => x.AmountKopecks > 0))
        {
            var canonical = string.Join("|",
                organizationId.ToString("N"), locationId.ToString("N"),
                allocation.TerminalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                allocation.Kind, allocation.SettlementSource, allocation.SettlementExternalId,
                allocation.SettlementDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                allocation.AmountKopecks, AlgorithmVersion);
            var id = DeterministicGuid(canonical);

            using var command = db.CreateCommand();
            command.Transaction = tx;
            command.CommandText = """
                INSERT INTO reconciliation_allocations(
                    id,organization_id,location_id,terminal_date,settlement_kind,settlement_source,
                    settlement_external_id,settlement_date,allocated_amount_kopecks,algorithm_version,created_at)
                VALUES($id,$org,$loc,$terminal,$kind,$source,$external,$settlement,$amount,$version,$created)
                """;
            command.Parameters.AddWithValue("$id", id.ToString());
            command.Parameters.AddWithValue("$org", organizationId.ToString());
            command.Parameters.AddWithValue("$loc", locationId.ToString());
            command.Parameters.AddWithValue("$terminal", allocation.TerminalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$kind", allocation.Kind.ToString());
            command.Parameters.AddWithValue("$source", allocation.SettlementSource);
            command.Parameters.AddWithValue("$external", allocation.SettlementExternalId);
            command.Parameters.AddWithValue("$settlement", allocation.SettlementDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$amount", allocation.AmountKopecks);
            command.Parameters.AddWithValue("$version", AlgorithmVersion);
            command.Parameters.AddWithValue("$created", createdAt);
            command.ExecuteNonQuery();
        }
    }

    private static long Outstanding(LinkedList<OpenTerminalBucket> open)
    {
        long result = 0;
        for (var node = open.First; node is not null; node = node.Next) result += node.Value.RemainingKopecks;
        return result;
    }

    private static SqliteConnection Open(Database database)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true
        }.ToString());
        connection.Open();
        return connection;
    }

    private static DateOnly ParseDate(string value) => DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static Guid DeterministicGuid(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        return new Guid(bytes);
    }
}
