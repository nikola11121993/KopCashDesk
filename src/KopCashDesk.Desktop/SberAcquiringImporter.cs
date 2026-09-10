using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using KopCashDesk.Core;
using KopCashDesk.Data;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace KopCashDesk.Desktop;

public sealed class SberImportSummary
{
    public int FilesProcessed { get; internal set; }
    public int FilesFailed { get; internal set; }
    public int SheetsProcessed { get; internal set; }
    public int RowsRead { get; internal set; }
    public int OperationsAdded { get; internal set; }
    public int DuplicatesIgnored { get; internal set; }
    public int RowsSkipped { get; internal set; }
    public int OrganizationsCreated { get; internal set; }
    public int LocationsCreated { get; internal set; }
    public int TerminalsCreated { get; internal set; }
    public List<string> Messages { get; } = [];

    public string ToDisplayText()
    {
        var text = $"Файлов обработано: {FilesProcessed}\n" +
                   $"Листов обработано: {SheetsProcessed}\n" +
                   $"Операций добавлено: {OperationsAdded}\n" +
                   $"Дублей пропущено: {DuplicatesIgnored}\n" +
                   $"Организаций создано: {OrganizationsCreated}\n" +
                   $"Торговых точек создано: {LocationsCreated}\n" +
                   $"Терминалов привязано: {TerminalsCreated}\n" +
                   $"Строк пропущено: {RowsSkipped}";
        if (FilesFailed > 0) text += $"\nОшибок файлов: {FilesFailed}";
        if (Messages.Count > 0) text += "\n\n" + string.Join("\n", Messages.Take(12));
        return text;
    }
}

public sealed class SberAcquiringImporter
{
    private const string Source = "Sber.Acquiring";
    private const long MaxWorkbookBytes = 100L * 1024 * 1024;
    private readonly Database _database;
    private List<Organization> _organizations = [];
    private List<Location> _locations = [];
    private Dictionary<string, TerminalBinding> _terminals = new(StringComparer.OrdinalIgnoreCase);

    public SberAcquiringImporter(Database database) => _database = database;

    public SberImportSummary ImportFiles(IEnumerable<string> paths)
    {
        _organizations = _database.Organizations().ToList();
        _locations = _database.Locations().ToList();
        _terminals = _database.TerminalBindings().ToDictionary(TerminalKey, StringComparer.OrdinalIgnoreCase);

        var summary = new SberImportSummary();
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                ImportFile(path, summary);
                summary.FilesProcessed++;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or FormatException or OpenXmlPackageException)
            {
                summary.FilesFailed++;
                summary.Messages.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        _database.Audit("sber.import", $"files={summary.FilesProcessed}; sheets={summary.SheetsProcessed}; rows={summary.RowsRead}; added={summary.OperationsAdded}; duplicates={summary.DuplicatesIgnored}; skipped={summary.RowsSkipped}; failed={summary.FilesFailed}");
        return summary;
    }

    private void ImportFile(string path, SberImportSummary summary)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Файл не найден.", path);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not ".zip" and not ".xlsx") throw new InvalidDataException("Поддерживаются только ZIP и XLSX отчёты Сбер.");

        var fileInfo = new FileInfo(path);
        if (fileInfo.Length <= 0) throw new InvalidDataException("Файл пустой.");
        if (fileInfo.Length > MaxWorkbookBytes) throw new InvalidDataException("Файл слишком большой для безопасного импорта.");

        var hash = HashFile(path);
        var documentId = _database.RegisterSourceDocument(Source, hash, hash, Path.GetFileName(path));

        if (extension == ".xlsx")
        {
            using var stream = File.OpenRead(path);
            ImportWorkbook(stream, documentId, summary);
            return;
        }

        using var archive = ZipFile.OpenRead(path);
        var entries = archive.Entries
            .Where(x => x.FullName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) && !x.FullName.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (entries.Length == 0) throw new InvalidDataException("В архиве нет XLSX-отчёта.");
        if (entries.Length > 10) throw new InvalidDataException("В архиве слишком много XLSX-файлов.");

        foreach (var entry in entries)
        {
            if (entry.Length <= 0 || entry.Length > MaxWorkbookBytes) throw new InvalidDataException("Некорректный размер XLSX внутри архива.");
            using var input = entry.Open();
            using var memory = new MemoryStream((int)Math.Min(entry.Length, int.MaxValue));
            input.CopyTo(memory);
            memory.Position = 0;
            ImportWorkbook(memory, documentId, summary);
        }
    }

    private void ImportWorkbook(Stream stream, string documentId, SberImportSummary summary)
    {
        using var document = SpreadsheetDocument.Open(stream, false);
        var workbookPart = document.WorkbookPart ?? throw new InvalidDataException("В XLSX отсутствует книга.");
        var sheets = workbookPart.Workbook.Sheets?.Elements<Sheet>().ToArray() ?? [];
        if (sheets.Length == 0) throw new InvalidDataException("В XLSX нет листов.");

        var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable.Elements<SharedStringItem>().Select(x => x.InnerText).ToArray() ?? [];
        var compatibleSheets = 0;

        foreach (var sheet in sheets)
        {
            var relationshipId = sheet.Id?.Value;
            if (string.IsNullOrWhiteSpace(relationshipId)) continue;
            if (workbookPart.GetPartById(relationshipId) is not WorksheetPart worksheetPart) continue;
            var sheetData = worksheetPart.Worksheet.GetFirstChild<SheetData>();
            if (sheetData is null) continue;
            var rows = sheetData.Elements<Row>().ToList();
            if (rows.Count == 0) continue;

            var headerRowIndex = -1;
            Dictionary<int, string>? headers = null;
            for (var i = 0; i < Math.Min(rows.Count, 30); i++)
            {
                var candidate = ReadHeader(rows[i], sharedStrings);
                if (HasRequiredHeaders(candidate.Values))
                {
                    headers = candidate;
                    headerRowIndex = i;
                    break;
                }
            }

            if (headers is null) continue;
            compatibleSheets++;
            summary.SheetsProcessed++;
            ImportRows(rows, headerRowIndex, headers, sharedStrings, documentId, summary);
        }

        if (compatibleSheets == 0)
            throw new InvalidDataException("Это не общий отчёт Сбер по эквайрингу: ни на одном листе не найдены обязательные колонки.");
    }

    private void ImportRows(
        IReadOnlyList<Row> rows,
        int headerRowIndex,
        Dictionary<int, string> headers,
        string[] sharedStrings,
        string documentId,
        SberImportSummary summary)
    {
        for (var i = headerRowIndex + 1; i < rows.Count; i++)
        {
            var values = ReadRow(rows[i], headers, sharedStrings);
            if (values.Count == 0) continue;
            summary.RowsRead++;

            var legalName = Get(values, "Наименование юридического лица");
            var taxId = DigitsOnly(Get(values, "ИНН"));
            var sourcePointName = Get(values, "Наименование ТСТ");
            var sourceAddress = Get(values, "Адрес ТСТ");
            var merchantId = DigitsOnly(Get(values, "Номер мерчанта"));
            var terminalId = DigitsOnly(Get(values, "Номер терминала"));
            var rrn = Get(values, "RRN").Trim();
            var status = Get(values, "Статус транзакции");
            var reason = Get(values, "Причина списания");

            if (string.IsNullOrWhiteSpace(taxId) || string.IsNullOrWhiteSpace(sourcePointName) || string.IsNullOrWhiteSpace(terminalId))
            {
                summary.RowsSkipped++;
                continue;
            }
            if (!string.IsNullOrWhiteSpace(status) && !IsAcceptedStatus(status))
            {
                summary.RowsSkipped++;
                continue;
            }
            if (!TryParseAmount(Get(values, "Сумма операции"), out var amount) || amount == 0m)
            {
                summary.RowsSkipped++;
                continue;
            }
            if (!TryParseExcelDate(Get(values, "Дата операции"), out var occurredAt))
            {
                summary.RowsSkipped++;
                continue;
            }

            var organization = ResolveOrganization(legalName, taxId, summary);
            var pointName = CleanPointName(sourcePointName);
            var location = ResolveLocation(organization, pointName, sourceAddress, terminalId, summary);
            var paymentMethod = DetectPaymentMethod(sourcePointName);
            SaveTerminal(organization, location, terminalId, merchantId, paymentMethod, summary);

            var kind = DetectOperationKind(status, reason, amount);
            if (kind == OperationKind.Return && amount > 0m) amount = -amount;
            amount = Money.Normalize(amount);

            var requestNumber = Get(values, "Номер запроса");
            var extraTransactionId = Get(values, "Доп. информация_2");
            var externalId = BuildExternalId(taxId, terminalId, merchantId, rrn, occurredAt, amount, requestNumber, extraTransactionId);
            var inserted = _database.Insert(new CashOperation(
                Source,
                externalId,
                organization.Id,
                location.Id,
                occurredAt,
                SourceKind.Bank,
                kind,
                PaymentKind.Electronic,
                amount,
                documentId));

            if (inserted) summary.OperationsAdded++;
            else summary.DuplicatesIgnored++;
        }
    }

    private Organization ResolveOrganization(string legalName, string taxId, SberImportSummary summary)
    {
        var exact = _organizations.FirstOrDefault(x => string.Equals(DigitsOnly(x.TaxId), taxId, StringComparison.Ordinal));
        if (exact is not null) return exact;

        var friendly = FriendlyOrganizationName(legalName);
        var friendlyKey = NormalizeForMatch(friendly);
        var fullKey = NormalizeForMatch(legalName);
        var byName = _organizations.FirstOrDefault(x => string.IsNullOrWhiteSpace(x.TaxId) &&
            (NormalizeForMatch(x.Name) == friendlyKey || NormalizeForMatch(x.Name) == fullKey));
        if (byName is not null)
        {
            var updated = byName with { TaxId = taxId };
            _database.Save(updated);
            _organizations[_organizations.IndexOf(byName)] = updated;
            return updated;
        }

        var created = new Organization(Guid.NewGuid(), friendly, taxId);
        _database.Save(created);
        _organizations.Add(created);
        summary.OrganizationsCreated++;
        return created;
    }

    private Location ResolveLocation(Organization organization, string pointName, string address, string terminalId, SberImportSummary summary)
    {
        var terminalKey = TerminalKey(organization.Id, terminalId);
        if (_terminals.TryGetValue(terminalKey, out var existingTerminal))
        {
            var bound = _locations.FirstOrDefault(x => x.Id == existingTerminal.LocationId);
            if (bound is not null) return bound;
        }

        var organizationLocations = _locations.Where(x => x.OrganizationId == organization.Id).ToArray();
        var addressKey = NormalizeForMatch(address);
        var nameKey = NormalizeForMatch(pointName);
        Location? found = null;

        if (!string.IsNullOrWhiteSpace(addressKey))
        {
            var byAddress = organizationLocations.Where(x => NormalizeForMatch(x.Address) == addressKey).ToArray();
            found = byAddress.Length == 1 ? byAddress[0] : byAddress.FirstOrDefault(x => NormalizeForMatch(x.Name) == nameKey);
        }
        found ??= organizationLocations.FirstOrDefault(x => NormalizeForMatch(x.Name) == nameKey);

        if (found is not null)
        {
            if (string.IsNullOrWhiteSpace(found.Address) && !string.IsNullOrWhiteSpace(address))
            {
                var updated = found with { Address = CleanAddress(address) };
                _database.Save(updated);
                _locations[_locations.IndexOf(found)] = updated;
                return updated;
            }
            return found;
        }

        var created = new Location(Guid.NewGuid(), organization.Id, pointName, CleanAddress(address), ShouldAutoExclude(pointName));
        _database.Save(created);
        _locations.Add(created);
        summary.LocationsCreated++;
        return created;
    }

    private void SaveTerminal(Organization organization, Location location, string terminalId, string merchantId, string paymentMethod, SberImportSummary summary)
    {
        var key = TerminalKey(organization.Id, terminalId);
        if (_terminals.TryGetValue(key, out var current))
        {
            if (current.LocationId != location.Id || current.MerchantId != merchantId || current.PaymentMethod != paymentMethod)
            {
                var updated = current with { LocationId = location.Id, MerchantId = merchantId, PaymentMethod = paymentMethod };
                _database.Save(updated);
                _terminals[key] = updated;
            }
            return;
        }

        var created = new TerminalBinding(Guid.NewGuid(), organization.Id, location.Id, "Sber", terminalId, merchantId, paymentMethod);
        _database.Save(created);
        _terminals[key] = created;
        summary.TerminalsCreated++;
    }

    private static Dictionary<int, string> ReadHeader(Row row, string[] sharedStrings)
    {
        var result = new Dictionary<int, string>();
        foreach (var cell in row.Elements<Cell>())
        {
            var text = ReadCell(cell, sharedStrings).Trim();
            if (!string.IsNullOrWhiteSpace(text)) result[ColumnIndex(cell)] = text;
        }
        return result;
    }

    private static Dictionary<string, string> ReadRow(Row row, Dictionary<int, string> headers, string[] sharedStrings)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in row.Elements<Cell>())
        {
            var index = ColumnIndex(cell);
            if (!headers.TryGetValue(index, out var header)) continue;
            result[header] = ReadCell(cell, sharedStrings);
        }
        return result;
    }

    private static string ReadCell(Cell cell, string[] sharedStrings)
    {
        if (cell.DataType?.Value == CellValues.SharedString && int.TryParse(cell.CellValue?.InnerText, out var sharedIndex) && sharedIndex >= 0 && sharedIndex < sharedStrings.Length)
            return sharedStrings[sharedIndex];
        if (cell.DataType?.Value == CellValues.InlineString)
            return cell.InlineString?.InnerText ?? string.Empty;
        return cell.CellValue?.InnerText ?? cell.InnerText ?? string.Empty;
    }

    private static int ColumnIndex(Cell cell)
    {
        var reference = cell.CellReference?.Value ?? string.Empty;
        var index = 0;
        foreach (var ch in reference)
        {
            if (!char.IsLetter(ch)) break;
            index = index * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
        }
        return Math.Max(0, index - 1);
    }

    private static bool HasRequiredHeaders(IEnumerable<string> headers)
    {
        var set = new HashSet<string>(headers, StringComparer.OrdinalIgnoreCase);
        string[] required = ["Наименование юридического лица", "ИНН", "Наименование ТСТ", "Адрес ТСТ", "Номер терминала", "RRN", "Дата операции", "Сумма операции"];
        return required.All(set.Contains);
    }

    private static string Get(IReadOnlyDictionary<string, string> row, string key) => row.TryGetValue(key, out var value) ? value ?? string.Empty : string.Empty;

    private static bool TryParseAmount(string value, out decimal amount)
    {
        if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out amount)) return true;
        return decimal.TryParse(value, NumberStyles.Any, CultureInfo.GetCultureInfo("ru-RU"), out amount);
    }

    private static bool TryParseExcelDate(string value, out DateTimeOffset result)
    {
        result = default;
        DateTime date;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial))
        {
            try { date = DateTime.FromOADate(serial); }
            catch (ArgumentException) { return false; }
        }
        else if (!DateTime.TryParse(value, CultureInfo.GetCultureInfo("ru-RU"), DateTimeStyles.AllowWhiteSpaces, out date) &&
                 !DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date))
        {
            return false;
        }

        date = DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
        result = new DateTimeOffset(date, TimeZoneInfo.Local.GetUtcOffset(date));
        return true;
    }

    private static bool IsAcceptedStatus(string value)
    {
        var text = NormalizeForMatch(value);
        return text.Contains("учтен", StringComparison.Ordinal) ||
               text.Contains("успеш", StringComparison.Ordinal) ||
               text.Contains("исполн", StringComparison.Ordinal) ||
               text.Contains("заверш", StringComparison.Ordinal);
    }

    private static OperationKind DetectOperationKind(string status, string reason, decimal amount)
    {
        if (amount < 0m) return OperationKind.Return;
        var text = NormalizeForMatch(status + " " + reason);
        if (text.Contains("возврат", StringComparison.Ordinal)) return OperationKind.Return;
        return OperationKind.Sale;
    }

    public static string DetectPaymentMethod(string sourcePointName)
    {
        var value = sourcePointName.ToUpperInvariant();
        if (value.Contains("SBP", StringComparison.Ordinal) || value.Contains("СБП", StringComparison.Ordinal)) return "SBP";
        if (value.Contains("P_QR", StringComparison.Ordinal) || value.Contains("S_QR", StringComparison.Ordinal) || value.Contains("SBERPAY", StringComparison.Ordinal) || value.Contains("QR", StringComparison.Ordinal)) return "QR";
        if (value.Contains("FACE", StringComparison.Ordinal) || value.Contains("ЛИЦ", StringComparison.Ordinal)) return "FACE";
        return "POS";
    }

    public static string CleanPointName(string value)
    {
        var result = value.Trim();
        result = Regex.Replace(result, "^SP_", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        result = Regex.Replace(result, "_(?:P_QR|S_QR|S_SBP|SBP|SBERPAY|QR|FACE)$", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        result = result.Replace('_', ' ');
        result = Regex.Replace(result, @"\s+", " ").Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(result) ? value.Trim() : result;
    }

    public static string FriendlyOrganizationName(string legalName)
    {
        var match = Regex.Match(legalName, "[\\\"«]([^\\\"»]+)[\\\"»]");
        var core = match.Success ? match.Groups[1].Value.Trim() : legalName.Trim();
        var words = Regex.Matches(core, @"[\p{L}\p{Nd}]+")
            .Select(x => x.Value)
            .Where(x => x.Length > 0)
            .ToArray();
        if (words.Length >= 3)
            core = string.Concat(words.Select(x => char.ToUpperInvariant(x[0])));
        return $"ООО \"{core}\"";
    }

    public static string NormalizeForMatch(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var text = value.ToLowerInvariant().Replace('ё', 'е');
        text = Regex.Replace(text, @"[^\p{L}\p{Nd}]+", " ");
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private static string CleanAddress(string value) => string.Join(", ", value.Split(',').Select(x => x.Trim()).Where(x => !string.IsNullOrWhiteSpace(x)));
    private static string DigitsOnly(string value) => new(value.Where(char.IsDigit).ToArray());
    private static bool ShouldAutoExclude(string pointName) => NormalizeForMatch(pointName) is "столовая 5" or "столовая аппетит";
    private static string TerminalKey(TerminalBinding terminal) => TerminalKey(terminal.OrganizationId, terminal.TerminalId);
    private static string TerminalKey(Guid organizationId, string terminalId) => $"{organizationId:N}|Sber|{terminalId}";

    private static string BuildExternalId(string taxId, string terminalId, string merchantId, string rrn, DateTimeOffset occurredAt, decimal amount, string requestNumber, string extraTransactionId)
    {
        var canonical = string.Join("|", taxId, terminalId, merchantId, rrn, occurredAt.ToString("O", CultureInfo.InvariantCulture), Money.ToKopecks(amount), requestNumber.Trim(), extraTransactionId.Trim());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
