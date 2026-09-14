using KopCashDesk.Core;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace KopCashDesk.Data;

public sealed record CrossSourceShiftMatchResult(
    int MatchedPairs,
    int NewMatches,
    int Conflicts,
    int NewConflicts);

public sealed record CrossSourceShiftLinkView(
    Guid Id,
    Guid OrganizationId,
    Guid LocationId,
    DateOnly BusinessDate,
    string CanonicalShiftId,
    string ObservedShiftId,
    string MatchKind,
    double MatchConfidence,
    int TimeDifferenceSeconds,
    DateTimeOffset CreatedAt);

public sealed record FiscalSourceConflictView(
    Guid Id,
    Guid OrganizationId,
    Guid LocationId,
    DateOnly BusinessDate,
    string TaxcomShiftId,
    string FrontolShiftId,
    string Reason,
    int TimeDifferenceSeconds,
    DateTimeOffset CreatedAt);

public static class CrossSourceShiftExtensions
{
    public const int DefaultToleranceSeconds = 5 * 60;
    private const string Taxcom = "Taxcom.ShiftReport";
    private const string Frontol = "Frontol.Report";
    private const string AutomaticMatchKind = "AutomaticExactMoneyWithinTolerance";
    private const string AutomaticConflictReason = "NearTimeMoneyMismatch";

    private sealed record Observation(
        string Id,
        string Source,
        string ExternalId,
        Guid OrganizationId,
        string Organization,
        Guid LocationId,
        string Location,
        DateOnly BusinessDate,
        DateTimeOffset ClosedAt,
        long TotalKopecks,
        long CashKopecks,
        long ElectronicKopecks,
        string FiscalDriveNumber,
        int? ShiftNumber);

    private sealed record MatchCandidate(Observation Taxcom, Observation Frontol, int DeltaSeconds);
    private sealed record ConflictCandidate(Observation Taxcom, Observation Frontol, int DeltaSeconds);

    public static CrossSourceShiftMatchResult RebuildCrossSourceShiftMatches(
        this Database database,
        int toleranceSeconds = DefaultToleranceSeconds)
    {
        if (toleranceSeconds < 0) throw new ArgumentOutOfRangeException(nameof(toleranceSeconds));

        using var db = Open(database);
        using var tx = db.BeginTransaction();
        var observations = ReadObservations(db, tx);
        var existingMatchCreated = ReadExistingCreatedAt(db, tx, "shift_source_links", "id");
        var existingConflictCreated = ReadExistingCreatedAt(db, tx, "fiscal_source_conflicts", "id");

        var desiredMatches = new List<MatchCandidate>();
        var desiredConflicts = new List<ConflictCandidate>();

        foreach (var group in observations.GroupBy(x => (x.OrganizationId, x.LocationId, x.BusinessDate)))
        {
            var taxcom = group.Where(x => x.Source == Taxcom).ToArray();
            var frontol = group.Where(x => x.Source == Frontol).ToArray();
            if (taxcom.Length == 0 || frontol.Length == 0) continue;

            var usedTaxcom = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var usedFrontol = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var exactCandidates = (from t in taxcom
                                   from f in frontol
                                   let delta = DeltaSeconds(t.ClosedAt, f.ClosedAt)
                                   where delta <= toleranceSeconds && MoneyEqual(t, f)
                                   orderby delta, t.ClosedAt, f.ClosedAt, t.Id, f.Id
                                   select new MatchCandidate(t, f, delta)).ToArray();

            foreach (var candidate in exactCandidates)
            {
                if (!usedTaxcom.Add(candidate.Taxcom.Id)) continue;
                if (!usedFrontol.Add(candidate.Frontol.Id))
                {
                    usedTaxcom.Remove(candidate.Taxcom.Id);
                    continue;
                }
                desiredMatches.Add(candidate);
            }

            var conflictCandidates = (from t in taxcom
                                      where !usedTaxcom.Contains(t.Id)
                                      from f in frontol
                                      where !usedFrontol.Contains(f.Id)
                                      let delta = DeltaSeconds(t.ClosedAt, f.ClosedAt)
                                      where delta <= toleranceSeconds && !MoneyEqual(t, f)
                                      orderby delta, t.ClosedAt, f.ClosedAt, t.Id, f.Id
                                      select new ConflictCandidate(t, f, delta)).ToArray();

            foreach (var candidate in conflictCandidates)
            {
                if (!usedTaxcom.Add(candidate.Taxcom.Id)) continue;
                if (!usedFrontol.Add(candidate.Frontol.Id))
                {
                    usedTaxcom.Remove(candidate.Taxcom.Id);
                    continue;
                }
                desiredConflicts.Add(candidate);
            }
        }

        // Rebuild only automatic relationships. Raw source rows are never deleted.
        Execute(db, tx, $"DELETE FROM shift_source_links WHERE match_kind='{AutomaticMatchKind}'");
        Execute(db, tx, $"DELETE FROM fiscal_source_conflicts WHERE reason='{AutomaticConflictReason}'");
        Execute(db, tx, """
            UPDATE operations
            SET source_kind='Fiscal'
            WHERE source IN ('Taxcom.ShiftReport','Frontol.Report')
              AND source_kind IN ('FiscalObservation','FiscalConflict');
            """);

        var newMatches = 0;
        foreach (var match in desiredMatches)
        {
            var id = DeterministicGuid($"match|{match.Taxcom.Id}|{match.Frontol.Id}");
            var createdAt = existingMatchCreated.GetValueOrDefault(id.ToString(), DateTimeOffset.UtcNow.ToString("O"));
            InsertMatch(db, tx, id, match, createdAt);
            MarkOperationRole(db, tx, match.Frontol, "FiscalObservation");

            if (existingMatchCreated.ContainsKey(id.ToString())) continue;
            newMatches++;
            Audit(db, tx, "Cross-source fiscal shift matched",
                $"organization={match.Taxcom.Organization}; organization_id={match.Taxcom.OrganizationId}; " +
                $"location={match.Taxcom.Location}; location_id={match.Taxcom.LocationId}; " +
                $"taxcom_shift_id={match.Taxcom.Id}; frontol_shift_id={match.Frontol.Id}; " +
                $"date={match.Taxcom.BusinessDate:yyyy-MM-dd}; total_kopecks={match.Taxcom.TotalKopecks}; " +
                $"cash_kopecks={match.Taxcom.CashKopecks}; electronic_kopecks={match.Taxcom.ElectronicKopecks}; " +
                $"time_difference_seconds={match.DeltaSeconds}; canonical_source={Taxcom}");
        }

        var newConflicts = 0;
        foreach (var conflict in desiredConflicts)
        {
            var id = DeterministicGuid($"conflict|{conflict.Taxcom.Id}|{conflict.Frontol.Id}");
            var createdAt = existingConflictCreated.GetValueOrDefault(id.ToString(), DateTimeOffset.UtcNow.ToString("O"));
            InsertConflict(db, tx, id, conflict, createdAt);
            MarkOperationRole(db, tx, conflict.Taxcom, "FiscalConflict");
            MarkOperationRole(db, tx, conflict.Frontol, "FiscalConflict");

            if (existingConflictCreated.ContainsKey(id.ToString())) continue;
            newConflicts++;
            Audit(db, tx, "Cross-source fiscal shift conflict",
                $"organization={conflict.Taxcom.Organization}; organization_id={conflict.Taxcom.OrganizationId}; " +
                $"location={conflict.Taxcom.Location}; location_id={conflict.Taxcom.LocationId}; " +
                $"date={conflict.Taxcom.BusinessDate:yyyy-MM-dd}; taxcom_shift_id={conflict.Taxcom.Id}; " +
                $"frontol_shift_id={conflict.Frontol.Id}; taxcom_total_kopecks={conflict.Taxcom.TotalKopecks}; " +
                $"frontol_total_kopecks={conflict.Frontol.TotalKopecks}; taxcom_cash_kopecks={conflict.Taxcom.CashKopecks}; " +
                $"frontol_cash_kopecks={conflict.Frontol.CashKopecks}; taxcom_electronic_kopecks={conflict.Taxcom.ElectronicKopecks}; " +
                $"frontol_electronic_kopecks={conflict.Frontol.ElectronicKopecks}; time_difference_seconds={conflict.DeltaSeconds}");
        }

        tx.Commit();
        return new(desiredMatches.Count, newMatches, desiredConflicts.Count, newConflicts);
    }

    public static IReadOnlyList<CrossSourceShiftLinkView> CrossSourceShiftLinks(this Database database)
    {
        using var db = Open(database);
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT id,organization_id,location_id,business_date,canonical_shift_id,observed_shift_id,
                   match_kind,match_confidence,time_difference_seconds,created_at
            FROM shift_source_links
            ORDER BY business_date,organization_id,location_id,id
            """;
        using var reader = command.ExecuteReader();
        var result = new List<CrossSourceShiftLinkView>();
        while (reader.Read())
        {
            result.Add(new(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                Guid.Parse(reader.GetString(2)),
                DateOnly.ParseExact(reader.GetString(3), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetDouble(7),
                reader.GetInt32(8),
                DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture)));
        }
        return result;
    }

    public static IReadOnlyList<FiscalSourceConflictView> FiscalSourceConflicts(this Database database)
    {
        using var db = Open(database);
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT id,organization_id,location_id,business_date,taxcom_shift_id,frontol_shift_id,
                   reason,time_difference_seconds,created_at
            FROM fiscal_source_conflicts
            ORDER BY business_date,organization_id,location_id,id
            """;
        using var reader = command.ExecuteReader();
        var result = new List<FiscalSourceConflictView>();
        while (reader.Read())
        {
            result.Add(new(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                Guid.Parse(reader.GetString(2)),
                DateOnly.ParseExact(reader.GetString(3), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetInt32(7),
                DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture)));
        }
        return result;
    }

    public static IReadOnlySet<DateOnly> CrossSourceConflictDates(
        this Database database,
        Guid organizationId,
        Guid locationId)
    {
        using var db = Open(database);
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT business_date
            FROM fiscal_source_conflicts
            WHERE organization_id=$org AND location_id=$loc
            """;
        command.Parameters.AddWithValue("$org", organizationId.ToString());
        command.Parameters.AddWithValue("$loc", locationId.ToString());
        using var reader = command.ExecuteReader();
        var result = new HashSet<DateOnly>();
        while (reader.Read())
            result.Add(DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture));
        return result;
    }

    private static List<Observation> ReadObservations(SqliteConnection db, SqliteTransaction tx)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT s.id,s.source,s.external_id,s.organization_id,o.name,s.location_id,l.name,s.closed_at,
                   s.total_kopecks,s.cash_kopecks,s.electronic_kopecks,s.fn,s.shift_number
            FROM shift_closures s
            JOIN organizations o ON o.id=s.organization_id
            JOIN locations l ON l.id=s.location_id
            WHERE s.source IN ('Taxcom.ShiftReport','Frontol.Report')
            ORDER BY s.organization_id,s.location_id,s.closed_at,s.source,s.id
            """;
        using var reader = command.ExecuteReader();
        var result = new List<Observation>();
        while (reader.Read())
        {
            var closedAt = DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture);
            result.Add(new(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                Guid.Parse(reader.GetString(3)),
                reader.GetString(4),
                Guid.Parse(reader.GetString(5)),
                reader.GetString(6),
                DateOnly.FromDateTime(closedAt.DateTime),
                closedAt,
                reader.GetInt64(8),
                reader.GetInt64(9),
                reader.GetInt64(10),
                reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetInt32(12)));
        }
        return result;
    }

    private static Dictionary<string, string> ReadExistingCreatedAt(
        SqliteConnection db,
        SqliteTransaction tx,
        string table,
        string idColumn)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = $"SELECT {idColumn},created_at FROM {table}";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }

    private static bool MoneyEqual(Observation left, Observation right) =>
        left.TotalKopecks == right.TotalKopecks &&
        left.CashKopecks == right.CashKopecks &&
        left.ElectronicKopecks == right.ElectronicKopecks;

    private static int DeltaSeconds(DateTimeOffset left, DateTimeOffset right) =>
        checked((int)Math.Round(Math.Abs((left - right).TotalSeconds), MidpointRounding.AwayFromZero));

    private static void InsertMatch(
        SqliteConnection db,
        SqliteTransaction tx,
        Guid id,
        MatchCandidate match,
        string createdAt)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO shift_source_links(
                id,organization_id,location_id,business_date,canonical_shift_id,observed_shift_id,
                match_kind,match_confidence,time_difference_seconds,created_at)
            VALUES($id,$org,$loc,$day,$canonical,$observed,$kind,1.0,$delta,$created)
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$org", match.Taxcom.OrganizationId.ToString());
        command.Parameters.AddWithValue("$loc", match.Taxcom.LocationId.ToString());
        command.Parameters.AddWithValue("$day", match.Taxcom.BusinessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$canonical", match.Taxcom.Id);
        command.Parameters.AddWithValue("$observed", match.Frontol.Id);
        command.Parameters.AddWithValue("$kind", AutomaticMatchKind);
        command.Parameters.AddWithValue("$delta", match.DeltaSeconds);
        command.Parameters.AddWithValue("$created", createdAt);
        command.ExecuteNonQuery();
    }

    private static void InsertConflict(
        SqliteConnection db,
        SqliteTransaction tx,
        Guid id,
        ConflictCandidate conflict,
        string createdAt)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO fiscal_source_conflicts(
                id,organization_id,location_id,business_date,taxcom_shift_id,frontol_shift_id,
                reason,time_difference_seconds,created_at)
            VALUES($id,$org,$loc,$day,$taxcom,$frontol,$reason,$delta,$created)
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$org", conflict.Taxcom.OrganizationId.ToString());
        command.Parameters.AddWithValue("$loc", conflict.Taxcom.LocationId.ToString());
        command.Parameters.AddWithValue("$day", conflict.Taxcom.BusinessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$taxcom", conflict.Taxcom.Id);
        command.Parameters.AddWithValue("$frontol", conflict.Frontol.Id);
        command.Parameters.AddWithValue("$reason", AutomaticConflictReason);
        command.Parameters.AddWithValue("$delta", conflict.DeltaSeconds);
        command.Parameters.AddWithValue("$created", createdAt);
        command.ExecuteNonQuery();
    }

    private static void MarkOperationRole(SqliteConnection db, SqliteTransaction tx, Observation shift, string role)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            UPDATE operations
            SET source_kind=$role
            WHERE source=$source
              AND (external_id=$cash OR external_id=$electronic)
            """;
        command.Parameters.AddWithValue("$role", role);
        command.Parameters.AddWithValue("$source", shift.Source);
        command.Parameters.AddWithValue("$cash", shift.ExternalId + ":cash");
        command.Parameters.AddWithValue("$electronic", shift.ExternalId + ":electronic");
        command.ExecuteNonQuery();
    }

    private static void Audit(SqliteConnection db, SqliteTransaction tx, string action, string details)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "INSERT INTO audit_log(occurred_at,action,details) VALUES($t,$a,$d)";
        command.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$a", action);
        command.Parameters.AddWithValue("$d", details);
        command.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection db, SqliteTransaction tx, string sql)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        command.ExecuteNonQuery();
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

    private static Guid DeterministicGuid(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        return new Guid(bytes);
    }
}
