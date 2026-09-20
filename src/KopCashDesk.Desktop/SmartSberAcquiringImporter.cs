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

/// <summary>
/// Imports all Sber acquiring layouts seen in production. Only terminals which are already
/// manually bound or belong to the user-confirmed seven physical/reporting points are accepted.
/// Unknown terminals are skipped so an import can never invent an eighth point.
/// </summary>
public sealed class SmartSberAcquiringImporter
{
    private const string Source = "Sber.Acquiring";
    private const long MaxWorkbookBytes = 100L * 1024 * 1024;
    private readonly Database _database;
    private List<Organization> _organizations = [];
    private List<Location> _locations = [];
    private Dictionary<string, TerminalBinding> _terminals = new(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, string> KnownTerminalPoints =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Кулинария Аппетит, ККТ 00301000370264.
            ["42526205"] = KnownBusinessRules.AtiAppetitPointName,
            ["42526204"] = KnownBusinessRules.AtiAppetitPointName,

            // Столовая АТИ, ККТ 08050950.
            ["34723825"] = KnownBusinessRules.AtiMercuryPointName,
            ["34723835"] = KnownBusinessRules.AtiMercuryPointName,
            ["34723837"] = KnownBusinessRules.AtiMercuryPointName,

            // Рефтинская ГРЭС 6 столовая.
            ["34765811"] = KnownBusinessRules.ReftinskayaPointName,
            ["34765817"] = KnownBusinessRules.ReftinskayaPointName,
            ["34773474"] = KnownBusinessRules.ReftinskayaPointName,

            // Ладыженского 7.
            ["39413044"] = KnownBusinessRules.LadyzhenskogoPointName,
            ["39413045"] = KnownBusinessRules.LadyzhenskogoPointName,
            ["39413043"] = KnownBusinessRules.LadyzhenskogoPointName,
            ["45080359"] = KnownBusinessRules.LadyzhenskogoPointName,
            ["45080360"] = KnownBusinessRules.LadyzhenskogoPointName,
            ["45080361"] = KnownBusinessRules.LadyzhenskogoPointName,

            // Музыкальный колледж.
            ["39413112"] = KnownBusinessRules.MusicCollegePointName,
            ["39413114"] = KnownBusinessRules.MusicCollegePointName,
            ["39413113"] = KnownBusinessRules.MusicCollegePointName,

            // Чапаева 28.
            ["42162000"] = KnownBusinessRules.ChapaevaPointName,
            ["42162001"] = KnownBusinessRules.ChapaevaPointName,
            ["42161999"] = KnownBusinessRules.ChapaevaPointName,
            ["45080612"] = KnownBusinessRules.ChapaevaPointName,
            ["45080613"] = KnownBusinessRules.ChapaevaPointName,
            ["45080614"] = KnownBusinessRules.ChapaevaPointName,

            // One historical KKT/point chain: Вороний Брод -> Ленинградская 1 -> Мира 4.
            // All bank history is intentionally consolidated into the closed reporting point.
            ["43151534"] = KnownBusinessRules.MiraPointName,
            ["43151535"] = KnownBusinessRules.MiraPointName,
            ["43151533"] = KnownBusinessRules.MiraPointName,
            ["39413189"] = KnownBusinessRules.MiraPointName,
            ["39413190"] = KnownBusinessRules.MiraPointName,
            ["42638079"] = KnownBusinessRules.MiraPointName,
            ["42638078"] = KnownBusinessRules.MiraPointName,
            ["42638080"] = KnownBusinessRules.MiraPointName
        };

    private static readonly IReadOnlyDictionary<string, string> KnownKopTerminalPoints =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Хризотил. Основной POS и его QR/SBP каналы пробиваются на кассе Хризотила.
            ["37446500"] = "Хризотил",
            ["37446501"] = "Хризотил",
            ["37446502"] = "Хризотил",

            // ВРЕМЕННОЕ ПОДТВЕРЖДЁННОЕ ПРАВИЛО:
            // терминал физически находится в Хризотиле, но его продажи пробиваются на кассе Сиесты.
            // Физическая привязка терминала остаётся «Хризотил»; финансовая сверка маршрутизируется отдельно.
            ["37446495"] = "Хризотил",

            // Собственные каналы Кафе Сиеста.
            ["39887320"] = "Кафе Сиеста",
            ["39887319"] = "Кафе Сиеста",
            ["39974228"] = "Кафе Сиеста",

            // Лакомка.
            ["37446428"] = "Лакомка"
        };

    public SmartSberAcquiringImporter(Database database) => _database = database;

    public static string? CanonicalPointNameForTerminal(string terminalId, string? organizationTaxId = null)
    {
        var tid = DigitsOnly(terminalId);
        var taxId = DigitsOnly(organizationTaxId ?? string.Empty);
        if (taxId == KnownOrganizations.KopTaxId &&
            KnownKopTerminalPoints.TryGetValue(tid, out var kopPoint))
            return kopPoint;

        return KnownTerminalPoints.TryGetValue(tid, out var value) ? value : null;
    }

    public static string? ReconciliationPointNameForTerminal(string terminalId, string? organizationTaxId = null)
    {
        var tid = DigitsOnly(terminalId);
        var taxId = DigitsOnly(organizationTaxId ?? string.Empty);

        // Temporary real-world exception:
        // TID 37446495 stands physically at «Хризотил», but its card sales are rung
        // on the Siesta fiscal register (KKT 0014943 / RNM 0001113145061553).
        if (taxId == KnownOrganizations.KopTaxId && tid == "37446495")
            return "Кафе Сиеста";

        return CanonicalPointNameForTerminal(tid, taxId);
    }

    public SmartSberImportSummary ImportFiles(IEnumerable<string> paths)
    {
        _organizations = _database.Organizations().ToList();
        _locations = _database.Locations().Where(x => x.IsActive).ToList();
        _terminals = _database.TerminalBindings().ToDictionary(TerminalKey, StringComparer.OrdinalIgnoreCase);

        var summary = new SmartSberImportSummary();
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path))
            {
                summary.Messages.Add($"{Path.GetFileName(path)}: файл не найден.");
                continue;
            }

            try
            {
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".xlsx")
                {
                    using var stream = File.OpenRead(path);
                    if (TryImportWorkbook(stream, Path.GetFileName(path), HashFile(path), summary))
                        summary.WorkbooksProcessed++;
                    else
                        summary.Messages.Add($"{Path.GetFileName(path)}: XLSX не является отчётом Сбер.");
                }
                else if (ext == ".zip")
                {
                    ImportZip(path, summary);
                }
                else
                {
                    summary.Messages.Add($"{Path.GetFileName(path)}: поддерживаются XLSX и ZIP.");
                }
                summary.FilesProcessed++;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or FormatException or OpenXmlPackageException)
            {
                summary.Messages.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        _database.Audit("sber.smart_import",
            $"files={summary.FilesProcessed}; workbooks={summary.WorkbooksProcessed}; added={summary.OperationsAdded}; duplicates={summary.DuplicatesIgnored}; skipped={summary.RowsSkipped}");
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
                // A regular Sber ZIP can contain an unrelated Taxcom workbook.
                // It must not make the whole acquiring archive "Unknown" or abort the bank import.
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
            if (string.IsNullOrWhiteSpace(relationshipId)) continue;
            if (workbookPart.GetPartById(relationshipId) is not WorksheetPart worksheetPart) continue;
            var data = worksheetPart.Worksheet.GetFirstChild<SheetData>();
            if (data is null) continue;
            var rows = data.Elements<Row>().ToList();
            if (rows.Count == 0) continue;

            Dictionary<int, string>? headers = null;
            var headerIndex = -1;
            for (var i = 0; i < Math.Min(40, rows.Count); i++)
            {
                var candidate = ReadHeader(rows[i], shared);
                if (IsBankHeader(candidate.Values))
                {
                    headers = candidate;
                    headerIndex = i;
                    break;
                }
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
            if (string.IsNullOrWhiteSpace(terminalId) || string.IsNullOrWhiteSpace(rrn) || !rrn.Any(char.IsDigit))
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
            if (string.IsNullOrWhiteSpace(taxId)) taxId = fileTaxId;
            var legalName = FirstNonEmpty(Get(values, "наименование юридического лица"), Get(values, "наименование организации"));
            if (string.IsNullOrWhiteSpace(taxId))
            {
                summary.RowsSkipped++;
                if (summary.Messages.Count < 12) summary.Messages.Add($"{originalName}: у строки не найден ИНН.");
                continue;
            }

            var organization = ResolveOrganization(legalName, taxId, summary);
            var sourcePointName = Get(values, "наименование тст");
            var address = FirstNonEmpty(Get(values, "адрес тст"), Get(values, "адрес"));
            var location = ResolveLocation(organization, terminalId, sourcePointName, address, summary);
            if (location is null)
            {
                summary.RowsSkipped++;
                if (summary.Messages.Count < 12)
                    summary.Messages.Add($"TID {terminalId}: терминал не известен и не привязан вручную — строка пропущена.");
                continue;
            }

            var merchantId = DigitsOnly(Get(values, "номер мерчанта"));
            SaveTerminal(organization, location, terminalId, merchantId,
                SberAcquiringImporter.DetectPaymentMethod(sourcePointName), summary);

            // Normally the bank operation is reconciled against the cash register of the same physical point.
            // The confirmed temporary exception TID 37446495 remains physically bound to «Хризотил»,
            // but its bank amount is compared with «Кафе Сиеста».
            var reconciliationLocation = ResolveReconciliationLocation(organization, terminalId, location, summary);

            var externalId = BuildExternalId(taxId, terminalId, rrn, occurredAt, amount, kind);
            var inserted = _database.UpsertBankOperation(new CashOperation(
                Source, externalId, organization.Id, reconciliationLocation.Id, occurredAt,
                SourceKind.Bank, kind, PaymentKind.Electronic, amount, documentId));
            if (inserted) summary.OperationsAdded++;
            else summary.DuplicatesIgnored++;
        }
    }

    private Organization ResolveOrganization(string legalName, string taxId, SmartSberImportSummary summary)
    {
        var existing = _organizations.FirstOrDefault(x => DigitsOnly(x.TaxId) == taxId);
        if (existing is not null) return existing;

        var name = string.IsNullOrWhiteSpace(legalName)
            ? $"Организация ИНН {taxId}"
            : SberAcquiringImporter.FriendlyOrganizationName(legalName);
        var created = new Organization(Guid.NewGuid(), name, taxId);
        _database.Save(created);
        _organizations.Add(created);
        summary.OrganizationsCreated++;
        return created;
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
            if (current is not null && (terminal.IsLocked || terminal.BindingSource == BindingSource.Manual))
                return current;
        }

        // Never trust a new TST name/address to create a point. Only a confirmed TID or a manual binding is accepted.
        var canonical = CanonicalPointNameForTerminal(terminalId, organization.TaxId);
        if (canonical is null) return null;

        var key = SberAcquiringImporter.NormalizeForMatch(canonical);
        var matches = _locations.Where(x => x.OrganizationId == organization.Id && x.IsActive &&
            SberAcquiringImporter.NormalizeForMatch(x.Name) == key).ToArray();
        if (matches.Length == 1) return matches[0];
        if (matches.Length > 1) return null;

        // Known TIDs are allowed to create only their confirmed canonical point. Source address is intentionally
        // ignored: reports have contained wrong house numbers (for example 64 instead of 62 for Кулинария Аппетит).
        var created = new Location(Guid.NewGuid(), organization.Id, canonical);
        _database.Save(created);
        _locations.Add(created);
        summary.LocationsCreated++;
        return created;
    }

    private Location ResolveReconciliationLocation(
        Organization organization,
        string terminalId,
        Location physicalLocation,
        SmartSberImportSummary summary)
    {
        var reconciliationName = ReconciliationPointNameForTerminal(terminalId, organization.TaxId);
        if (string.IsNullOrWhiteSpace(reconciliationName) ||
            SberAcquiringImporter.NormalizeForMatch(reconciliationName) ==
            SberAcquiringImporter.NormalizeForMatch(physicalLocation.Name))
            return physicalLocation;

        var key = SberAcquiringImporter.NormalizeForMatch(reconciliationName);
        var matches = _locations.Where(x => x.OrganizationId == organization.Id && x.IsActive &&
            SberAcquiringImporter.NormalizeForMatch(x.Name) == key).ToArray();
        if (matches.Length == 1) return matches[0];
        if (matches.Length > 1)
            throw new InvalidDataException($"Найдено несколько точек «{reconciliationName}» для специального правила TID {terminalId}.");

        var created = new Location(Guid.NewGuid(), organization.Id, reconciliationName);
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
            _database.Save(updated);
            _terminals[key] = updated;
            return;
        }

        var created = new TerminalBinding(
            Guid.NewGuid(), organization.Id, location.Id, "Sber", terminalId, merchantId, paymentMethod,
            BindingSource.Rule);
        _database.Save(created);
        _terminals[key] = created;
        summary.TerminalsCreated++;
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
        if (cell.DataType?.Value == CellValues.SharedString &&
            int.TryParse(cell.CellValue?.InnerText, out var index) && index >= 0 && index < shared.Length)
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

    private static string Get(IReadOnlyDictionary<string, string> values, string normalizedHeader) =>
        values.TryGetValue(normalizedHeader, out var value) ? value ?? string.Empty : string.Empty;

    private static string NormalizeHeader(string value)
    {
        var text = (value ?? string.Empty).Replace('\u00A0', ' ').Trim().ToLowerInvariant().Replace('ё', 'е');
        return Regex.Replace(text, @"\s+", " ");
    }

    private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;

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
        var text = SberAcquiringImporter.NormalizeForMatch(value);
        return text.Contains("учтен", StringComparison.Ordinal) ||
               text.Contains("успеш", StringComparison.Ordinal) ||
               text.Contains("исполн", StringComparison.Ordinal) ||
               text.Contains("заверш", StringComparison.Ordinal);
    }

    private static OperationKind DetectOperationKind(string text, decimal amount)
    {
        if (amount < 0m) return OperationKind.Return;
        var normalized = SberAcquiringImporter.NormalizeForMatch(text);
        return normalized.Contains("возврат", StringComparison.Ordinal) ? OperationKind.Return : OperationKind.Sale;
    }

    private static string BuildExternalId(
        string taxId,
        string terminalId,
        string rrn,
        DateTimeOffset occurredAt,
        decimal amount,
        OperationKind kind)
    {
        // Date only is intentional: some Sber layouts print the same RRN with a few seconds difference.
        var canonical = string.Join("|",
            DigitsOnly(taxId), DigitsOnly(terminalId), rrn.Trim(),
            occurredAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Money.ToKopecks(amount), kind.ToString());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string ExtractTaxId(string name)
    {
        var match = Regex.Match(name ?? string.Empty, @"(?<!\d)(\d{10}|\d{12})(?!\d)");
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string HashBytes(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string DigitsOnly(string value) => new((value ?? string.Empty).Where(char.IsDigit).ToArray());
    private static string TerminalKey(TerminalBinding terminal) => TerminalKey(terminal.OrganizationId, terminal.TerminalId);
    private static string TerminalKey(Guid organizationId, string terminalId) => $"{organizationId:N}|Sber|{DigitsOnly(terminalId)}";
}
