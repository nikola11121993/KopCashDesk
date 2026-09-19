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

public sealed class SmartSberImportSummary
{
    public int FilesProcessed { get; internal set; }
    public int WorkbooksProcessed { get; internal set; }
    public int RowsRead { get; internal set; }
    public int OperationsAdded { get; internal set; }
    public int DuplicatesIgnored { get; internal set; }
    public int RowsSkipped { get; internal set; }
    public int OrganizationsCreated { get; internal set; }
    public int LocationsCreated { get; internal set; }
    public int TerminalsCreated { get; internal set; }
    public int NonSberWorkbooksSkipped { get; internal set; }
    public List<string> Messages { get; } = [];

    public string ToDisplayText()
    {
        var text = $"Файлов обработано: {FilesProcessed}\n" +
                   $"Отчётов Сбер внутри файлов: {WorkbooksProcessed}\n" +
                   $"Операций добавлено: {OperationsAdded}\n" +
                   $"Дублей пропущено: {DuplicatesIgnored}\n" +
                   $"Организаций создано: {OrganizationsCreated}\n" +
                   $"Торговых точек создано: {LocationsCreated}\n" +
                   $"Терминалов привязано: {TerminalsCreated}\n" +
                   $"Строк пропущено: {RowsSkipped}";
        if (NonSberWorkbooksSkipped > 0)
            text += $"\nНесберовских XLSX внутри ZIP пропущено: {NonSberWorkbooksSkipped}";
        if (Messages.Count > 0) text += "\n\n" + string.Join("\n", Messages.Take(12));
        return text;
    }
}

public sealed class SmartSberAcquiringImporter
{
    private const string Source = "Sber.Acquiring";
    private const long MaxWorkbookBytes = 100L * 1024 * 1024;
    private const int BatchSize = 5000;

    private readonly Database _database;
    private List<Organization> _organizations = [];
    private List<Location> _locations = [];
    private Dictionary<string, TerminalBinding> _terminals = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CashOperation> _pendingOperations = new(BatchSize);

    private static readonly IReadOnlyDictionary<string, string> KnownTerminalPoints =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["42526205"] = KnownBusinessRules.AtiAppetitPointName,
            ["42526204"] = KnownBusinessRules.AtiAppetitPointName,
            ["34723825"] = KnownBusinessRules.AtiMercuryPointName,
            ["34723835"] = KnownBusinessRules.AtiMercuryPointName,
            ["34723837"] = KnownBusinessRules.AtiMercuryPointName,
            ["34765811"] = KnownBusinessRules.ReftinskayaPointName,
            ["34765817"] = KnownBusinessRules.ReftinskayaPointName,
            ["34773474"] = KnownBusinessRules.ReftinskayaPointName,
            ["39413044"] = KnownBusinessRules.LadyzhenskogoPointName,
            ["39413045"] = KnownBusinessRules.LadyzhenskogoPointName,
            ["39413043"] = KnownBusinessRules.LadyzhenskogoPointName,
            ["45080359"] = KnownBusinessRules.LadyzhenskogoPointName,
            ["45080360"] = KnownBusinessRules.LadyzhenskogoPointName,
            ["45080361"] = KnownBusinessRules.LadyzhenskogoPointName,
            ["39413112"] = KnownBusinessRules.MusicCollegePointName,
            ["39413114"] = KnownBusinessRules.MusicCollegePointName,
            ["39413113"] = KnownBusinessRules.MusicCollegePointName,
            ["42162000"] = KnownBusinessRules.ChapaevaPointName,
            ["42162001"] = KnownBusinessRules.ChapaevaPointName,
            ["42161999"] = KnownBusinessRules.ChapaevaPointName,
            ["45080612"] = KnownBusinessRules.ChapaevaPointName,
            ["45080613"] = KnownBusinessRules.ChapaevaPointName,
            ["45080614"] = KnownBusinessRules.ChapaevaPointName,
            ["43151534"] = KnownBusinessRules.MiraPointName,
            ["43151535"] = KnownBusinessRules.MiraPointName,
            ["43151533"] = KnownBusinessRules.MiraPointName,
            ["39413189"] = KnownBusinessRules.MiraPointName,
            ["39413190"] = KnownBusinessRules.MiraPointName,
            ["42638079"] = KnownBusinessRules.MiraPointName,
            ["42638078"] = KnownBusinessRules.MiraPointName,
            ["42638080"] = KnownBusinessRules.MiraPointName
        };

    public SmartSberAcquiringImporter(Database database) => _database = database;

    public static string? CanonicalPointNameForTerminal(string terminalId)
    {
        var tid = DigitsOnly(terminalId);
        return KnownTerminalPoints.TryGetValue(tid, out var value) ? value : null;
    }

    public SmartSberImportSummary ImportFiles(IEnumerable<string> paths)
    {
        var createdOrganizations = KnownOrganizations.Ensure(_database);
        _organizations = _database.Organizations().ToList();
        _locations = _database.Locations().Where(x => x.IsActive).ToList();
        _terminals = _database.TerminalBindings().ToDictionary(TerminalKey, StringComparer.OrdinalIgnoreCase);
        _pendingOperations.Clear();

        var summary = new SmartSberImportSummary { OrganizationsCreated = createdOrganizations };
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path))
            {
                AddMessage(summary, $"{Path.GetFileName(path)}: файл не найден.");
                continue;
            }

            try
            {
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".xlsx")
                {
                    using var stream = File.OpenRead(path);
                    if (TryImportWorkbook(stream, Path.GetFileName(path), HashFile(path), summary)) summary.WorkbooksProcessed++;
                    else AddMessage(summary, $"{Path.GetFileName(path)}: XLSX не является отчётом Сбер.");
                }
                else if (ext == ".zip")
                {
                    ImportZip(path, summary);
                }
                else
                {
                    AddMessage(summary, $"{Path.GetFileName(path)}: поддерживаются XLSX и ZIP.");
                }
                summary.FilesProcessed++;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or FormatException or OpenXmlPackageException)
            {
                AddMessage(summary, $"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        FlushOperations(summary);
        _database.Audit("sber.smart_import",
            $"files={summary.FilesProcessed}; workbooks={summary.WorkbooksProcessed}; added={summary.OperationsAdded}; duplicates={summary.DuplicatesIgnored}; skipped={summary.RowsSkipped}; batch=true");
        return summary;
    }

    private void ImportZip(string path, SmartSberImportSummary summary)
    {
        using var archive = ZipFile.OpenRead(path);
        var entries = archive.Entries
            .Where(x => x.FullName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) &&
                        !x.FullName.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase))
            .Take(100)
            .ToArray();
        if (entries.Length == 0) throw new InvalidDataException("В ZIP нет XLSX-файлов.");

        var imported = 0;
        foreach (var entry in entries)
        {
            if (entry.Length <= 0 || entry.Length > MaxWorkbookBytes)
            {
                summary.RowsSkipped++;
                continue;
            }

            using var input = entry.Open();
            using var memory = new MemoryStream((int)Math.Min(entry.Length, int.MaxValue));
            input.CopyTo(memory);
            var bytes = memory.ToArray();
            using var workbook = new MemoryStream(bytes, writable: false);
            var sourceName = $"{Path.GetFileName(path)}::{entry.Name}";
            if (TryImportWorkbook(workbook, sourceName, HashBytes(bytes), summary))
            {
                imported++;
                summary.WorkbooksProcessed++;
            }
            else
            {
                summary.NonSberWorkbooksSkipped++;
            }
        }

        if (imported == 0)
            throw new InvalidDataException("В ZIP не найден ни один отчёт Сбер по эквайрингу.");
    }

    private bool TryImportWorkbook(Stream stream, string originalName, string hash, SmartSberImportSummary summary)
    {
        using var document = SpreadsheetDocument.Open(stream, false);
        var workbookPart = document.WorkbookPart;
        if (workbookPart?.Workbook.Sheets is null) return false;
        var shared = workbookPart.SharedStringTablePart?.SharedStringTable
            .Elements<SharedStringItem>().Select(x => x.InnerText).ToArray() ?? [];

        var compatible = 0;
        foreach (var sheet in workbookPart.Workbook.Sheets.Elements<Sheet>())
        {
            var relationshipId = sheet.Id?.Value;
            if (string.IsNullOrWhiteSpace(relationshipId) || workbookPart.GetPartById(relationshipId) is not WorksheetPart worksheetPart) continue;
            var data = worksheetPart.Worksheet.GetFirstChild<SheetData>();
            if (data is null) continue;
            var rows = data.Elements<Row>().ToList();
            if (rows.Count == 0) continue;

            Dictionary<int, string>? headers = null;
            var headerIndex = -1;
            for (var i = 0; i < Math.Min(40, rows.Count); i++)
            {
                var candidate = ReadHeader(rows[i], shared);
                if (!IsBankHeader(candidate.Values)) continue;
                headers = candidate;
                headerIndex = i;
                break;
            }
            if (headers is null) continue;

            compatible++;
            var documentId = _database.RegisterSourceDocument(Source, hash + ":" + compatible, hash, originalName);
            ImportRows(rows, headerIndex, headers, shared, originalName, documentId, summary);
        }
        return compatible > 0;
    }

    private void ImportRows(
        IReadOnlyList<Row> rows,
        int headerIndex,
        Dictionary<int, string> headers,
        string[] shared,
        string originalName,
        string documentId,
        SmartSberImportSummary summary)
    {
        var fileTaxId = ExtractTaxId(originalName);

        for (var i = headerIndex + 1; i < rows.Count; i++)
        {
            var values = ReadRow(rows[i], headers, shared);
            if (values.Count == 0) continue;
            summary.RowsRead++;

            var terminalId = DigitsOnly(Get(values, "номер терминала"));
            var rrn = Get(values, "rrn").Trim();
            if (terminalId.Length == 0 || rrn.Length == 0 || !rrn.Any(char.IsDigit))
            {
                summary.RowsSkipped++;
                continue;
            }

            if (!TryParseAmount(Get(values, "сумма операции"), out var amount) || amount == 0m ||
                !TryParseExcelDate(Get(values, "дата операции"), out var occurredAt))
            {
                summary.RowsSkipped++;
                continue;
            }

            var status = Get(values, "статус транзакции");
            if (!string.IsNullOrWhiteSpace(status) && !IsAcceptedStatus(status))
            {
                summary.RowsSkipped++;
                continue;
            }

            var operationText = string.Join(' ', status, Get(values, "причина списания"), Get(values, "тип операции"));
            var kind = DetectOperationKind(operationText, amount);
            if (kind == OperationKind.Return && amount > 0m) amount = -amount;
            amount = Money.Normalize(amount);

            var taxId = DigitsOnly(Get(values, "инн"));
            if (taxId.Length == 0) taxId = fileTaxId;
            if (!IsSupportedOrganization(taxId))
            {
                summary.RowsSkipped++;
                AddMessage(summary, $"{originalName}: ИНН {taxId} не относится к трём настроенным организациям — строка пропущена.");
                continue;
            }

            var legalName = FirstNonEmpty(Get(values, "наименование юридического лица"), Get(values, "наименование организации"));
            var organization = ResolveOrganization(legalName, taxId);
            var sourcePointName = Get(values, "наименование тст");
            var address = FirstNonEmpty(Get(values, "адрес тст"), Get(values, "адрес"));
            var location = ResolveLocation(organization, terminalId, sourcePointName, address, summary);
            if (location is null)
            {
                summary.RowsSkipped++;
                AddMessage(summary, $"TID {terminalId}: терминал ООО ЦОП не входит в подтверждённые точки — строка пропущена.");
                continue;
            }

            var merchantId = DigitsOnly(Get(values, "номер мерчанта"));
            SaveTerminal(organization, location, terminalId, merchantId, SberAcquiringImporter.DetectPaymentMethod(sourcePointName), summary);

            var externalId = BuildExternalId(taxId, terminalId, rrn, occurredAt, amount, kind);
            _pendingOperations.Add(new CashOperation(
                Source, externalId, organization.Id, location.Id, occurredAt,
                SourceKind.Bank, kind, PaymentKind.Electronic, amount, documentId));
            if (_pendingOperations.Count >= BatchSize) FlushOperations(summary);
        }
    }

    private Organization ResolveOrganization(string legalName, string taxId)
    {
        var existing = _organizations.First(x => DigitsOnly(x.TaxId) == taxId);
        var canonical = KnownOrganizations.All.First(x => x.TaxId == taxId).Name;
        if (existing.Name == canonical) return existing;

        var updated = existing with { Name = canonical };
        _database.Save(updated);
        _organizations[_organizations.IndexOf(existing)] = updated;
        return updated;
    }

    private Location? ResolveLocation(
        Organization organization,
        string terminalId,
        string sourcePointName,
        string sourceAddress,
        SmartSberImportSummary summary)
    {
        var terminalKey = TerminalKey(organization.Id, terminalId);
        if (_terminals.TryGetValue(terminalKey, out var terminal))
        {
            var current = _locations.FirstOrDefault(x => x.Id == terminal.LocationId && x.IsActive);
            if (current is not null && (terminal.IsLocked || terminal.BindingSource == BindingSource.Manual)) return current;
        }

        if (DigitsOnly(organization.TaxId) == KnownOrganizations.CenterTaxId)
        {
            var canonical = CanonicalPointNameForTerminal(terminalId);
            if (canonical is null) return null;
            return FindOrCreateLocation(organization, canonical, string.Empty, summary, trustAddress: false);
        }

        // Reports for ООО КОП and ООО ГАРАНТ have stable TST names and addresses. For these two
        // organizations the Sber report itself is the source of the point directory.
        var pointName = SberAcquiringImporter.CleanPointName(sourcePointName);
        if (string.IsNullOrWhiteSpace(pointName)) return null;
        return FindOrCreateLocation(organization, pointName, sourceAddress, summary, trustAddress: true);
    }

    private Location FindOrCreateLocation(
        Organization organization,
        string pointName,
        string sourceAddress,
        SmartSberImportSummary summary,
        bool trustAddress)
    {
        var nameKey = SberAcquiringImporter.NormalizeForMatch(pointName);
        var address = trustAddress ? CleanAddress(sourceAddress) : string.Empty;
        var addressKey = SberAcquiringImporter.NormalizeForMatch(address);
        var candidates = _locations.Where(x => x.OrganizationId == organization.Id && x.IsActive).ToArray();

        Location? found = candidates.FirstOrDefault(x => SberAcquiringImporter.NormalizeForMatch(x.Name) == nameKey &&
            (addressKey.Length == 0 || SberAcquiringImporter.NormalizeForMatch(x.Address) == addressKey));
        found ??= candidates.FirstOrDefault(x => SberAcquiringImporter.NormalizeForMatch(x.Name) == nameKey);
        if (found is not null)
        {
            if (trustAddress && string.IsNullOrWhiteSpace(found.Address) && address.Length > 0)
            {
                var updated = found with { Address = address };
                _database.Save(updated);
                _locations[_locations.IndexOf(found)] = updated;
                return updated;
            }
            return found;
        }

        var created = new Location(Guid.NewGuid(), organization.Id, pointName, address);
        _database.Save(created);
        _locations.Add(created);
        summary.LocationsCreated++;
        return created;
    }

    private void SaveTerminal(
        Organization organization,
        Location location,
        string terminalId,
        string merchantId,
        string paymentMethod,
        SmartSberImportSummary summary)
    {
        var key = TerminalKey(organization.Id, terminalId);
        if (_terminals.TryGetValue(key, out var existing))
        {
            if (existing.IsLocked || existing.BindingSource == BindingSource.Manual) return;
            var updated = existing with
            {
                LocationId = location.Id,
                MerchantId = string.IsNullOrWhiteSpace(merchantId) ? existing.MerchantId : merchantId,
                PaymentMethod = paymentMethod,
                BindingSource = BindingSource.Rule
            };
            if (updated.LocationId == existing.LocationId && updated.MerchantId == existing.MerchantId &&
                updated.PaymentMethod == existing.PaymentMethod && updated.BindingSource == existing.BindingSource) return;
            _database.Save(updated);
            _terminals[key] = updated;
            return;
        }

        var created = new TerminalBinding(
            Guid.NewGuid(), organization.Id, location.Id, "Sber", terminalId, merchantId, paymentMethod, BindingSource.Rule);
        _database.Save(created);
        _terminals[key] = created;
        summary.TerminalsCreated++;
    }

    private void FlushOperations(SmartSberImportSummary summary)
    {
        if (_pendingOperations.Count == 0) return;
        var attempted = _pendingOperations.Count;
        var inserted = _database.InsertOperationsBatch(_pendingOperations);
        summary.OperationsAdded += inserted;
        summary.DuplicatesIgnored += attempted - inserted;
        _pendingOperations.Clear();
    }

    public static bool IsBankHeader(IEnumerable<string> headers)
    {
        var set = headers.Select(NormalizeHeader).Where(x => x.Length > 0).ToHashSet(StringComparer.Ordinal);
        return set.Contains("номер терминала") && set.Contains("rrn") &&
               set.Contains("дата операции") && set.Contains("сумма операции") &&
               (set.Contains("наименование юридического лица") || set.Contains("наименование организации"));
    }

    private static Dictionary<int, string> ReadHeader(Row row, string[] shared)
    {
        var result = new Dictionary<int, string>();
        foreach (var cell in row.Elements<Cell>())
        {
            var value = NormalizeHeader(ReadCell(cell, shared));
            if (value.Length > 0) result[ColumnIndex(cell)] = value;
        }
        return result;
    }

    private static Dictionary<string, string> ReadRow(Row row, Dictionary<int, string> headers, string[] shared)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var cell in row.Elements<Cell>())
        {
            var index = ColumnIndex(cell);
            if (headers.TryGetValue(index, out var header)) result[header] = ReadCell(cell, shared);
        }
        return result;
    }

    private static string ReadCell(Cell cell, string[] shared)
    {
        if (cell.DataType?.Value == CellValues.SharedString && int.TryParse(cell.CellValue?.InnerText, out var index) && index >= 0 && index < shared.Length)
            return shared[index];
        if (cell.DataType?.Value == CellValues.InlineString) return cell.InlineString?.InnerText ?? string.Empty;
        return cell.CellValue?.InnerText ?? cell.InnerText ?? string.Empty;
    }

    private static int ColumnIndex(Cell cell)
    {
        var reference = cell.CellReference?.Value ?? string.Empty;
        var index = 0;
        foreach (var ch in reference)
        {
            if (!char.IsLetter(ch)) break;
            index = index * 26 + char.ToUpperInvariant(ch) - 'A' + 1;
        }
        return Math.Max(0, index - 1);
    }

    private static string Get(IReadOnlyDictionary<string, string> values, string normalizedHeader)
        => values.TryGetValue(normalizedHeader, out var value) ? value ?? string.Empty : string.Empty;

    private static string NormalizeHeader(string value)
    {
        var text = (value ?? string.Empty).Replace('\u00A0', ' ').Trim().ToLowerInvariant().Replace('ё', 'е');
        return Regex.Replace(text, @"\s+", " ");
    }

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;

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
                 !DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date)) return false;
        date = DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
        result = new DateTimeOffset(date, TimeZoneInfo.Local.GetUtcOffset(date));
        return true;
    }

    private static bool IsAcceptedStatus(string value)
    {
        var text = SberAcquiringImporter.NormalizeForMatch(value);
        return text.Contains("учтен", StringComparison.Ordinal) || text.Contains("успеш", StringComparison.Ordinal) ||
               text.Contains("исполн", StringComparison.Ordinal) || text.Contains("заверш", StringComparison.Ordinal);
    }

    private static OperationKind DetectOperationKind(string text, decimal amount)
    {
        if (amount < 0m) return OperationKind.Return;
        var normalized = SberAcquiringImporter.NormalizeForMatch(text);
        return normalized.Contains("возврат", StringComparison.Ordinal) ? OperationKind.Return : OperationKind.Sale;
    }

    private static string BuildExternalId(string taxId, string terminalId, string rrn, DateTimeOffset occurredAt, decimal amount, OperationKind kind)
    {
        var canonical = string.Join("|", DigitsOnly(taxId), DigitsOnly(terminalId), rrn.Trim(),
            occurredAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Money.ToKopecks(amount), kind.ToString());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static bool IsSupportedOrganization(string taxId)
        => KnownOrganizations.All.Any(x => x.TaxId == DigitsOnly(taxId));

    private static string ExtractTaxId(string name)
    {
        var match = Regex.Match(name ?? string.Empty, @"(?<!\d)(\d{10}|\d{12})(?!\d)");
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private static string CleanAddress(string value)
        => string.Join(", ", (value ?? string.Empty).Split(',').Select(x => x.Trim()).Where(x => x.Length > 0));

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string HashBytes(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string DigitsOnly(string value) => new((value ?? string.Empty).Where(char.IsDigit).ToArray());
    private static string TerminalKey(TerminalBinding terminal) => TerminalKey(terminal.OrganizationId, terminal.TerminalId);
    private static string TerminalKey(Guid organizationId, string terminalId) => $"{organizationId:N}|Sber|{DigitsOnly(terminalId)}";

    private static void AddMessage(SmartSberImportSummary summary, string message)
    {
        if (summary.Messages.Count < 12 && !summary.Messages.Contains(message, StringComparer.OrdinalIgnoreCase))
            summary.Messages.Add(message);
    }
}
