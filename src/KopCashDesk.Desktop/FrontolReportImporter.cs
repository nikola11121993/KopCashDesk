using KopCashDesk.Core;
using KopCashDesk.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace KopCashDesk.Desktop;

public sealed class FrontolReportImportSummary
{
    public int FilesProcessed { get; internal set; }
    public int FilesFailed { get; internal set; }
    public int ShiftsProcessed { get; internal set; }
    public int ClosedDocuments { get; internal set; }
    public int CancelledDocuments { get; internal set; }
    public int RowsSkipped { get; internal set; }
    public int FiscalOperationsInserted { get; internal set; }
    public int FiscalOperationsUpdated { get; internal set; }
    public decimal CashTotal { get; internal set; }
    public decimal ElectronicTotal { get; internal set; }
    public decimal RevenueTotal { get; internal set; }
    public List<string> Messages { get; } = [];

    public string ToDisplayText()
    {
        var text = $"Файлов обработано: {FilesProcessed}\n" +
                   $"Смен обработано: {ShiftsProcessed}\n" +
                   $"Закрытых чеков учтено: {ClosedDocuments}\n" +
                   $"Отменённых чеков исключено: {CancelledDocuments}\n" +
                   $"Фискальных записей добавлено: {FiscalOperationsInserted}\n" +
                   $"Фискальных записей обновлено: {FiscalOperationsUpdated}\n" +
                   $"Строк пропущено: {RowsSkipped}\n\n" +
                   $"Наличные: {CashTotal:N2} ₽\n" +
                   $"Безнал: {ElectronicTotal:N2} ₽\n" +
                   $"Выручка: {RevenueTotal:N2} ₽";
        if (FilesFailed > 0) text += $"\nОшибок файлов: {FilesFailed}";
        if (Messages.Count > 0) text += "\n\n" + string.Join("\n", Messages.Take(12));
        return text;
    }
}

public sealed class FrontolReportImporter
{
    private const string Source = "Frontol.Report";
    private const long MaxReportBytes = 2L * 1024 * 1024 * 1024;

    private readonly Database _database;
    private readonly Guid _organizationId;
    private readonly Guid _locationId;

    private readonly record struct DocumentKey(int Workstation, int DocumentNumber, int ShiftNumber);
    private readonly record struct ShiftKey(int Workstation, int ShiftNumber);

    private sealed class PaymentTotals
    {
        public decimal Cash { get; set; }
        public decimal Electronic { get; set; }
        public HashSet<string> UnknownPaymentCodes { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string paymentCode, decimal amount)
        {
            if (paymentCode == "1") Cash += amount;
            else if (paymentCode == "2") Electronic += amount;
            else UnknownPaymentCodes.Add(string.IsNullOrWhiteSpace(paymentCode) ? "(пусто)" : paymentCode);
        }

        public void Add(PaymentTotals other)
        {
            Cash += other.Cash;
            Electronic += other.Electronic;
            UnknownPaymentCodes.UnionWith(other.UnknownPaymentCodes);
        }
    }

    private sealed record ShiftClose(DateTimeOffset ClosedAt, decimal ReportedTotal);

    public FrontolReportImporter(Database database, Guid organizationId, Guid locationId)
    {
        _database = database;
        _organizationId = organizationId;
        _locationId = locationId;
    }

    public FrontolReportImportSummary ImportFiles(IEnumerable<string> paths)
    {
        ValidateTarget();
        var summary = new FrontolReportImportSummary();

        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                ImportFile(path, summary);
                summary.FilesProcessed++;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or FormatException or OverflowException)
            {
                summary.FilesFailed++;
                summary.Messages.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        _database.Audit("frontol.report.import",
            $"files={summary.FilesProcessed}; shifts={summary.ShiftsProcessed}; closedDocs={summary.ClosedDocuments}; cancelledDocs={summary.CancelledDocuments}; inserted={summary.FiscalOperationsInserted}; updated={summary.FiscalOperationsUpdated}; skipped={summary.RowsSkipped}; failed={summary.FilesFailed}");
        return summary;
    }

    private void ValidateTarget()
    {
        var organization = _database.Organizations().FirstOrDefault(x => x.Id == _organizationId)
            ?? throw new InvalidDataException("Выбранная организация не найдена.");
        var location = _database.Locations().FirstOrDefault(x => x.Id == _locationId)
            ?? throw new InvalidDataException("Выбранная торговая точка не найдена.");
        if (location.OrganizationId != organization.Id)
            throw new InvalidDataException("Торговая точка относится к другой организации.");
    }

    private void ImportFile(string path, FrontolReportImportSummary summary)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Файл не найден.", path);
        if (!Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Поддерживается текстовая выгрузка Frontol 6 report.txt.");

        var fileInfo = new FileInfo(path);
        if (fileInfo.Length <= 0) throw new InvalidDataException("Файл пустой.");
        if (fileInfo.Length > MaxReportBytes) throw new InvalidDataException("Файл Frontol больше 2 ГБ и не будет импортирован автоматически.");

        var fileHash = HashFile(path);
        var documentId = _database.RegisterSourceDocument(Source, fileHash, fileHash, Path.GetFileName(path));

        var pendingDocuments = new Dictionary<DocumentKey, PaymentTotals>();
        var shiftPayments = new Dictionary<ShiftKey, PaymentTotals>();
        var shiftClosures = new Dictionary<ShiftKey, ShiftClose>();
        var seenTransactions = new HashSet<long>();
        var unknownPaymentCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var relevantRows = 0;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024 * 1024, leaveOpen: false);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            if (!TryParseInt(Field(line, 4), out var transactionType)) continue;
            if (transactionType != 40 && transactionType != 55 && transactionType != 56 && transactionType != 61) continue;

            relevantRows++;
            if (!TryParseLong(Field(line, 1), out var transactionNumber))
            {
                summary.RowsSkipped++;
                continue;
            }
            if (!seenTransactions.Add(transactionNumber)) continue;

            if (!TryParseInt(Field(line, 5), out var workstation) ||
                !TryParseInt(Field(line, 14), out var shiftNumber))
            {
                summary.RowsSkipped++;
                continue;
            }

            if (transactionType == 61)
            {
                if (!TryParseDateTime(Field(line, 2), Field(line, 3), out var closedAt) ||
                    !TryParseAmount(Field(line, 10), out var total))
                {
                    summary.RowsSkipped++;
                    continue;
                }

                shiftClosures[new ShiftKey(workstation, shiftNumber)] = new ShiftClose(closedAt, Money.Normalize(total));
                continue;
            }

            if (!TryParseInt(Field(line, 6), out var documentNumber))
            {
                summary.RowsSkipped++;
                continue;
            }

            var documentKey = new DocumentKey(workstation, documentNumber, shiftNumber);
            if (transactionType == 40)
            {
                if (!TryParseAmount(Field(line, 12), out var amount))
                {
                    summary.RowsSkipped++;
                    continue;
                }

                var paymentCode = Field(line, 9).Trim().ToString();
                if (!pendingDocuments.TryGetValue(documentKey, out var totals))
                {
                    totals = new PaymentTotals();
                    pendingDocuments[documentKey] = totals;
                }
                totals.Add(paymentCode, Money.Normalize(amount));
                continue;
            }

            if (transactionType == 56)
            {
                pendingDocuments.Remove(documentKey);
                summary.CancelledDocuments++;
                continue;
            }

            if (pendingDocuments.Remove(documentKey, out var closedTotals))
            {
                var shiftKey = new ShiftKey(workstation, shiftNumber);
                if (!shiftPayments.TryGetValue(shiftKey, out var aggregate))
                {
                    aggregate = new PaymentTotals();
                    shiftPayments[shiftKey] = aggregate;
                }
                aggregate.Add(closedTotals);
                unknownPaymentCodes.UnionWith(closedTotals.UnknownPaymentCodes);
            }
            summary.ClosedDocuments++;
        }

        if (relevantRows == 0 || shiftClosures.Count == 0)
            throw new InvalidDataException("Файл не похож на выгрузку транзакций Frontol 6: не найдены закрытия смен (транзакция 61).");

        if (unknownPaymentCodes.Count > 0)
            throw new InvalidDataException("В закрытых чеках Frontol найдены неизвестные коды видов оплаты: " + string.Join(", ", unknownPaymentCodes.OrderBy(x => x)) + ". Для них нужно настроить соответствие наличные/безнал.");

        foreach (var (shiftKey, close) in shiftClosures.OrderBy(x => x.Value.ClosedAt).ThenBy(x => x.Key.Workstation).ThenBy(x => x.Key.ShiftNumber))
        {
            shiftPayments.TryGetValue(shiftKey, out var payments);
            var cash = Money.Normalize(payments?.Cash ?? 0m);
            var electronic = Money.Normalize(payments?.Electronic ?? 0m);
            var computedTotal = Money.Normalize(cash + electronic);
            var reportedTotal = Money.Normalize(close.ReportedTotal);

            if (computedTotal != reportedTotal && summary.Messages.Count < 12)
                summary.Messages.Add($"Смена {shiftKey.ShiftNumber}: Frontol закрыл {reportedTotal:N2} ₽, по закрытым чекам получилось {computedTotal:N2} ₽.");

            var externalBase = $"{_organizationId:N}:{_locationId:N}:{shiftKey.Workstation}:{shiftKey.ShiftNumber}";
            _database.Save(new ShiftClosure(
                Source,
                externalBase,
                _organizationId,
                _locationId,
                close.ClosedAt,
                reportedTotal,
                cash,
                electronic,
                string.Empty,
                shiftKey.ShiftNumber,
                documentId));

            UpsertFiscalPart(externalBase + ":cash", close.ClosedAt, PaymentKind.Cash, cash, documentId, summary);
            UpsertFiscalPart(externalBase + ":electronic", close.ClosedAt, PaymentKind.Electronic, electronic, documentId, summary);

            summary.ShiftsProcessed++;
            summary.CashTotal += cash;
            summary.ElectronicTotal += electronic;
            summary.RevenueTotal += reportedTotal;
        }
    }

    private void UpsertFiscalPart(
        string externalId,
        DateTimeOffset occurredAt,
        PaymentKind payment,
        decimal amount,
        string documentId,
        FrontolReportImportSummary summary)
    {
        var inserted = _database.UpsertFiscalOperation(new CashOperation(
            Source,
            externalId,
            _organizationId,
            _locationId,
            occurredAt,
            SourceKind.Fiscal,
            amount < 0m ? OperationKind.Return : OperationKind.Sale,
            payment,
            amount,
            documentId));

        if (inserted) summary.FiscalOperationsInserted++;
        else summary.FiscalOperationsUpdated++;
    }

    private static ReadOnlySpan<char> Field(string line, int oneBasedIndex)
    {
        var span = line.AsSpan();
        var field = 1;
        var start = 0;
        for (var i = 0; i <= span.Length; i++)
        {
            if (i != span.Length && span[i] != ';') continue;
            if (field == oneBasedIndex) return span[start..i];
            field++;
            start = i + 1;
        }
        return ReadOnlySpan<char>.Empty;
    }

    private static bool TryParseInt(ReadOnlySpan<char> value, out int number) =>
        int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number);

    private static bool TryParseLong(ReadOnlySpan<char> value, out long number) =>
        long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number);

    private static bool TryParseAmount(ReadOnlySpan<char> value, out decimal amount)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            amount = 0m;
            return true;
        }

        if (decimal.TryParse(trimmed, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out amount)) return true;
        var normalized = trimmed.ToString().Replace(',', '.');
        return decimal.TryParse(normalized, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out amount);
    }

    private static bool TryParseDateTime(ReadOnlySpan<char> date, ReadOnlySpan<char> time, out DateTimeOffset result)
    {
        result = default;
        var text = $"{date.Trim().ToString()} {time.Trim().ToString()}";
        string[] formats = ["dd.MM.yyyy H:mm:ss", "dd.MM.yyyy HH:mm:ss"];
        if (!DateTime.TryParseExact(text, formats, CultureInfo.GetCultureInfo("ru-RU"), DateTimeStyles.AllowWhiteSpaces, out var parsed) &&
            !DateTime.TryParse(text, CultureInfo.GetCultureInfo("ru-RU"), DateTimeStyles.AllowWhiteSpaces, out parsed))
            return false;

        parsed = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
        result = new DateTimeOffset(parsed, TimeZoneInfo.Local.GetUtcOffset(parsed));
        return true;
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
