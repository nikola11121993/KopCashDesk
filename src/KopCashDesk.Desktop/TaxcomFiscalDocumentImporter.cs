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

public sealed class TaxcomFiscalDocumentImportSummary
{
    public int FilesProcessed { get; internal set; }
    public int FilesFailed { get; internal set; }
    public int SheetsProcessed { get; internal set; }
    public int DocumentsProcessed { get; internal set; }
    public int RowsSkipped { get; internal set; }
    public int LocationsCreated { get; internal set; }
    public int RegistersBound { get; internal set; }
    public int FiscalOperationsInserted { get; internal set; }
    public int FiscalOperationsUpdated { get; internal set; }
    public decimal CashTotal { get; internal set; }
    public decimal ElectronicTotal { get; internal set; }
    public decimal RevenueTotal { get; internal set; }
    public List<string> Messages { get; } = [];

    public string ToDisplayText()
    {
        var text = $"Файлов обработано: {FilesProcessed}\n" +
                   $"Листов с фискальными документами: {SheetsProcessed}\n" +
                   $"Фискальных документов обработано: {DocumentsProcessed}\n" +
                   $"ККТ привязано: {RegistersBound}\n" +
                   $"Новых точек создано: {LocationsCreated}\n" +
                   $"Фискальных записей добавлено: {FiscalOperationsInserted}\n" +
                   $"Фискальных записей обновлено: {FiscalOperationsUpdated}\n" +
                   $"Строк пропущено: {RowsSkipped}\n\n" +
                   $"Наличные: {CashTotal:N2} ₽\n" +
                   $"Безналичные: {ElectronicTotal:N2} ₽\n" +
                   $"Сумма документов: {RevenueTotal:N2} ₽";
        if (FilesFailed > 0) text += $"\nОшибок файлов: {FilesFailed}";
        if (Messages.Count > 0) text += "\n\n" + string.Join("\n", Messages.Take(12));
        return text;
    }
}

public sealed class TaxcomFiscalDocumentImporter
{
    public const string Source = "Taxcom.FiscalDocuments";
    private const long MaxWorkbookBytes = 100L * 1024 * 1024;

    private readonly Database _database;
    private readonly Guid? _fallbackOrganizationId;
    private List<Organization> _organizations = [];
    private List<Location> _locations = [];
    private Dictionary<string, RegisterBinding> _registers = new(StringComparer.OrdinalIgnoreCase);

    public TaxcomFiscalDocumentImporter(Database database, Guid? fallbackOrganizationId = null)
    {
        _database = database;
        _fallbackOrganizationId = fallbackOrganizationId;
    }

    public TaxcomFiscalDocumentImportSummary ImportFiles(IEnumerable<string> paths)
    {
        _organizations = _database.Organizations().ToList();
        _locations = _database.Locations().ToList();
        _registers = _database.RegisterBindings().ToDictionary(RegisterKey, StringComparer.OrdinalIgnoreCase);

        var summary = new TaxcomFiscalDocumentImportSummary();
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

        _database.Audit("taxcom.fiscal_documents.import",
            $"files={summary.FilesProcessed}; documents={summary.DocumentsProcessed}; inserted={summary.FiscalOperationsInserted}; updated={summary.FiscalOperationsUpdated}; skipped={summary.RowsSkipped}; failed={summary.FilesFailed}");
        return summary;
    }

    private void ImportFile(string path, TaxcomFiscalDocumentImportSummary summary)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Файл не найден.", path);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not ".xlsx" and not ".zip")
            throw new InvalidDataException("Поддерживаются XLSX и ZIP с отчётом Такском по фискальным документам.");

        var fileInfo = new FileInfo(path);
        if (fileInfo.Length <= 0) throw new InvalidDataException("Файл пустой.");
        if (fileInfo.Length > MaxWorkbookBytes) throw new InvalidDataException("Файл слишком большой для безопасного импорта.");

        var fileHash = HashFile(path);
        var fileTaxId = ExtractTaxId(Path.GetFileName(path));

        if (extension == ".xlsx")
        {
            var documentId = _database.RegisterSourceDocument(Source, fileHash, fileHash, Path.GetFileName(path));
            using var stream = File.OpenRead(path);
            ImportWorkbook(stream, Path.GetFileName(path), fileTaxId, documentId, summary);
            return;
        }

        using var archive = ZipFile.OpenRead(path);
        var entries = archive.Entries
            .Where(x => x.FullName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) && !x.FullName.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (entries.Length == 0) throw new InvalidDataException("В архиве нет XLSX-отчёта.");
        if (entries.Length > 50) throw new InvalidDataException("В архиве слишком много XLSX-файлов.");

        foreach (var entry in entries)
        {
            if (entry.Length <= 0 || entry.Length > MaxWorkbookBytes)
                throw new InvalidDataException($"Некорректный размер файла {entry.Name} внутри архива.");

            var entryTaxId = ExtractTaxId(entry.Name) ?? fileTaxId;
            var externalDocumentId = HashText(fileHash + "|" + entry.FullName);
            var documentId = _database.RegisterSourceDocument(Source, externalDocumentId, fileHash, $"{Path.GetFileName(path)}::{entry.Name}");
            using var input = entry.Open();
            using var memory = new MemoryStream((int)Math.Min(entry.Length, int.MaxValue));
            input.CopyTo(memory);
            memory.Position = 0;
            ImportWorkbook(memory, entry.Name, entryTaxId, documentId, summary);
        }
    }

    private void ImportWorkbook(Stream stream, string originalName, string? taxId, string documentId, TaxcomFiscalDocumentImportSummary summary)
    {
        using var document = SpreadsheetDocument.Open(stream, false);
        var workbookPart = document.WorkbookPart ?? throw new InvalidDataException("В XLSX отсутствует книга.");
        var sheets = workbookPart.Workbook.Sheets?.Elements<Sheet>().ToArray() ?? [];
        if (sheets.Length == 0) throw new InvalidDataException("В XLSX нет листов.");

        var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable.Elements<SharedStringItem>().Select(x => x.InnerText).ToArray() ?? [];
        var compatible = 0;

        foreach (var sheet in sheets)
        {
            var relationshipId = sheet.Id?.Value;
            if (string.IsNullOrWhiteSpace(relationshipId)) continue;
            if (workbookPart.GetPartById(relationshipId) is not WorksheetPart worksheetPart) continue;
            var sheetData = worksheetPart.Worksheet.GetFirstChild<SheetData>();
            if (sheetData is null) continue;
            var rows = sheetData.Elements<Row>().ToList();
            if (rows.Count == 0) continue;

            var headerIndex = -1;
            Dictionary<int, string>? headers = null;
            for (var i = 0; i < Math.Min(rows.Count, 80); i++)
            {
                var candidate = ReadHeader(rows[i], sharedStrings);
                if (HasRequiredHeaders(candidate.Values))
                {
                    headerIndex = i;
                    headers = candidate;
                    break;
                }
            }

            if (headers is null) continue;
            compatible++;
            summary.SheetsProcessed++;
            var metadata = ReadReportMetadata(rows.Take(headerIndex), sharedStrings);
            ImportRows(rows, headerIndex, headers, sharedStrings, originalName, taxId, documentId, metadata, summary);
        }

        if (compatible == 0)
            throw new InvalidDataException("Не найден лист 'Сводный отчет по фискальным документам' с колонками Дата и время, Тип операции, Наличными, Безналичными, Сумма и ККТ.");
    }

    private void ImportRows(
        IReadOnlyList<Row> rows,
        int headerIndex,
        Dictionary<int, string> headers,
        string[] sharedStrings,
        string originalName,
        string? taxId,
        string documentId,
        ReportMetadata metadata,
        TaxcomFiscalDocumentImportSummary summary)
    {
        var organization = ResolveOrganization(taxId, originalName);

        for (var i = headerIndex + 1; i < rows.Count; i++)
        {
            var values = ReadRow(rows[i], headers, sharedStrings);
            if (values.Count == 0) continue;

            var firstText = values.Values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? string.Empty;
            if (string.Equals(firstText, "Итог", StringComparison.OrdinalIgnoreCase)) continue;

            if (!TryParseExcelDate(Get(values, "Дата и время"), out var occurredAt))
            {
                if (values.Values.Any(x => !string.IsNullOrWhiteSpace(x))) summary.RowsSkipped++;
                continue;
            }

            if (!TryParseAmount(Get(values, "Наличными"), out var cash) ||
                !TryParseAmount(Get(values, "Безналичными"), out var electronic) ||
                !TryParseAmount(Get(values, "Сумма"), out var total))
            {
                summary.RowsSkipped++;
                continue;
            }

            var operationText = Get(values, "Тип операции").Trim();
            var sign = OperationSign(operationText);
            if (sign == 0)
            {
                summary.RowsSkipped++;
                if (summary.Messages.Count < 12) summary.Messages.Add($"Неизвестный тип операции '{operationText}' в {occurredAt:dd.MM.yyyy HH:mm}.");
                continue;
            }

            cash = Signed(cash, sign);
            electronic = Signed(electronic, sign);
            total = Signed(total, sign);

            var fn = DigitsOnly(Get(values, "Зав. № ФН"));
            var registerNumber = DigitsOnly(Get(values, "Рег. № ККТ"));
            var serial = DigitsOnly(Get(values, "Зав. № ККТ"));
            if (string.IsNullOrWhiteSpace(serial)) serial = DigitsOnly(metadata.RegisterSerial);
            var kktName = Get(values, "Название ККТ").Trim();
            var pointName = Get(values, "Торговая точка").Trim();
            if (IsGenericPoint(pointName)) pointName = metadata.PointName;

            if (string.IsNullOrWhiteSpace(fn) && string.IsNullOrWhiteSpace(registerNumber) && string.IsNullOrWhiteSpace(serial) && string.IsNullOrWhiteSpace(kktName))
            {
                summary.RowsSkipped++;
                continue;
            }

            var location = ResolveLocation(organization, fn, registerNumber, serial, kktName, pointName, summary);
            var fd = DigitsOnly(Get(values, "№ ФД"));
            var fpd = DigitsOnly(Get(values, "ФПД"));
            var shift = Get(values, "№ смены").Trim();
            var numberInShift = Get(values, "№ за смену").Trim();
            var documentType = Get(values, "Документ").Trim();
            var externalId = BuildDocumentExternalId(organization.TaxId, fn, registerNumber, serial, fd, fpd, shift, numberInShift, occurredAt, total);
            var kind = DocumentKind(documentType, operationText);

            if (cash != 0m)
                UpsertFiscalPart(externalId + ":cash", organization, location, occurredAt, kind, PaymentKind.Cash, cash, documentId, summary);
            if (electronic != 0m)
                UpsertFiscalPart(externalId + ":electronic", organization, location, occurredAt, kind, PaymentKind.Electronic, electronic, documentId, summary);

            summary.DocumentsProcessed++;
            summary.CashTotal += cash;
            summary.ElectronicTotal += electronic;
            summary.RevenueTotal += total;
        }
    }

    private void UpsertFiscalPart(
        string externalId,
        Organization organization,
        Location location,
        DateTimeOffset occurredAt,
        OperationKind kind,
        PaymentKind payment,
        decimal amount,
        string documentId,
        TaxcomFiscalDocumentImportSummary summary)
    {
        var inserted = _database.UpsertFiscalOperation(new CashOperation(
            Source,
            externalId,
            organization.Id,
            location.Id,
            occurredAt,
            SourceKind.Fiscal,
            kind,
            payment,
            amount,
            documentId));

        if (inserted) summary.FiscalOperationsInserted++;
        else summary.FiscalOperationsUpdated++;
    }

    private Organization ResolveOrganization(string? taxId, string originalName)
    {
        if (!string.IsNullOrWhiteSpace(taxId))
        {
            var matches = _organizations.Where(x => DigitsOnly(x.TaxId) == taxId).ToArray();
            if (matches.Length == 1) return matches[0];
            if (matches.Length > 1) throw new InvalidDataException($"ИНН {taxId} найден у нескольких организаций.");
            throw new InvalidDataException($"ИНН {taxId} из файла не найден среди организаций программы. Сначала создайте организацию с этим ИНН или выберите её в программе.");
        }

        if (_fallbackOrganizationId is Guid fallback)
        {
            var organization = _organizations.FirstOrDefault(x => x.Id == fallback);
            if (organization is not null) return organization;
        }

        throw new InvalidDataException($"В имени файла '{originalName}' не найден ИНН. Выберите организацию в верхней части программы и повторите импорт.");
    }

    private Location ResolveLocation(
        Organization organization,
        string fn,
        string registerNumber,
        string serial,
        string kktName,
        string pointName,
        TaxcomFiscalDocumentImportSummary summary)
    {
        var organizationLocations = _locations.Where(x => x.OrganizationId == organization.Id && x.IsActive).ToArray();
        var knownPoint = KnownBusinessRules.PointNameForRegisterSerial(serial) ?? KnownBusinessRules.PointNameForRegisterSerial(kktName);
        if (knownPoint is not null)
        {
            var location = KnownBusinessRules.FindKnownPoint(organizationLocations, knownPoint);
            if (location is null)
            {
                location = new Location(Guid.NewGuid(), organization.Id, knownPoint, string.Empty, false);
                _database.Save(location);
                _locations.Add(location);
                summary.LocationsCreated++;
            }
            EnsureRegisterBinding(organization, location, fn, registerNumber, summary, BindingSource.Rule, true);
            return location;
        }

        if (!string.IsNullOrWhiteSpace(fn) && _registers.TryGetValue(RegisterKey(organization.Id, fn), out var existingBinding))
        {
            var bound = _locations.FirstOrDefault(x => x.Id == existingBinding.LocationId && x.IsActive);
            if (bound is not null) return bound;
        }

        var preferredName = PreferredPointName(kktName, pointName, serial, fn);
        var nameKey = Normalize(preferredName);
        var byName = organizationLocations.Where(x => Normalize(x.Name) == nameKey).ToArray();
        Location? resolved = byName.Length == 1 ? byName[0] : null;

        if (resolved is null && ContainsDigit(preferredName))
        {
            var byAddress = organizationLocations.Where(x =>
            {
                var address = Normalize(x.Address);
                return !string.IsNullOrWhiteSpace(address) &&
                       (address.Contains(nameKey, StringComparison.Ordinal) || nameKey.Contains(address, StringComparison.Ordinal));
            }).ToArray();
            if (byAddress.Length == 1) resolved = byAddress[0];
        }

        if (resolved is null)
        {
            resolved = new Location(Guid.NewGuid(), organization.Id, preferredName, string.Empty, false);
            _database.Save(resolved);
            _locations.Add(resolved);
            summary.LocationsCreated++;
        }

        EnsureRegisterBinding(organization, resolved, fn, registerNumber, summary, BindingSource.Automatic, false);
        return resolved;
    }

    private void EnsureRegisterBinding(
        Organization organization,
        Location location,
        string fn,
        string registerNumber,
        TaxcomFiscalDocumentImportSummary summary,
        BindingSource bindingSource,
        bool isLocked)
    {
        if (string.IsNullOrWhiteSpace(fn)) return;
        var key = RegisterKey(organization.Id, fn);
        _registers.TryGetValue(key, out var existing);

        var requested = new RegisterBinding(
            existing?.Id ?? Guid.NewGuid(), organization.Id, location.Id, fn, registerNumber,
            bindingSource, isLocked);
        _database.SaveRegisterBinding(requested);

        var stored = _database.RegisterBindings().FirstOrDefault(x =>
            x.OrganizationId == organization.Id &&
            string.Equals(DigitsOnly(x.FiscalDriveNumber), DigitsOnly(fn), StringComparison.Ordinal));
        if (stored is null) return;
        _registers[key] = stored;

        if (existing is null && stored.LocationId == location.Id) summary.RegistersBound++;
    }

    private static ReportMetadata ReadReportMetadata(IEnumerable<Row> rows, string[] sharedStrings)
    {
        var point = string.Empty;
        var serial = string.Empty;
        foreach (var row in rows)
        {
            var cells = row.Elements<Cell>().OrderBy(ColumnIndex).ToArray();
            if (cells.Length < 2) continue;
            var key = ReadCell(cells[0], sharedStrings).Trim();
            var value = ReadCell(cells[1], sharedStrings).Trim();
            if (key.Equals("Торговая точка", StringComparison.OrdinalIgnoreCase)) point = value;
            else if (key.Equals("ККТ", StringComparison.OrdinalIgnoreCase)) serial = value;
        }
        return new(point, serial);
    }

    private static string PreferredPointName(string kktName, string pointName, string serial, string fn)
    {
        var kkt = CleanPointName(kktName);
        if (!IsGenericPoint(kkt)) return kkt;
        var point = CleanPointName(pointName);
        if (!IsGenericPoint(point)) return point;
        var id = !string.IsNullOrWhiteSpace(serial) ? serial : fn;
        return string.IsNullOrWhiteSpace(id) ? "Неопределённая ККТ" : $"ККТ {id}";
    }

    private static bool IsGenericPoint(string value)
    {
        var key = Normalize(value);
        return string.IsNullOrWhiteSpace(key) || key is "без торговой точки" or "ккт" or "касса" or "неопределенная ккт";
    }

    private static string CleanPointName(string value)
    {
        var result = Regex.Replace((value ?? string.Empty).Trim(), @"\s+", " ");
        result = Regex.Replace(result, @"\s*,\s*", ", ");
        return result.Trim().TrimEnd('.');
    }

    private static Dictionary<int, string> ReadHeader(Row row, string[] sharedStrings)
    {
        var result = new Dictionary<int, string>();
        foreach (var cell in row.Elements<Cell>())
        {
            var value = ReadCell(cell, sharedStrings).Trim();
            if (!string.IsNullOrWhiteSpace(value)) result[ColumnIndex(cell)] = value;
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
        if (cell.DataType?.Value == CellValues.SharedString &&
            int.TryParse(cell.CellValue?.InnerText, out var sharedIndex) && sharedIndex >= 0 && sharedIndex < sharedStrings.Length)
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
        var set = new HashSet<string>(headers.Select(NormalizeHeader), StringComparer.OrdinalIgnoreCase);
        string[] required = ["дата и время", "документ", "тип операции", "наличными", "безналичными", "сумма", "№ фд", "название ккт", "зав. № ккт", "рег. № ккт", "зав. № фн"];
        return required.All(set.Contains);
    }

    private static string Get(IReadOnlyDictionary<string, string> values, string key)
    {
        if (values.TryGetValue(key, out var direct)) return direct ?? string.Empty;
        var normalized = NormalizeHeader(key);
        foreach (var pair in values)
            if (NormalizeHeader(pair.Key) == normalized) return pair.Value ?? string.Empty;
        return string.Empty;
    }

    private static bool TryParseAmount(string value, out decimal amount)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            amount = 0m;
            return true;
        }
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

    private static int OperationSign(string value)
    {
        var text = Normalize(value);
        if (text.Contains("возврат прихода", StringComparison.Ordinal)) return -1;
        if (text.Contains("возврат расхода", StringComparison.Ordinal)) return 1;
        if (text.Contains("расход", StringComparison.Ordinal)) return -1;
        if (text.Contains("приход", StringComparison.Ordinal)) return 1;
        return 0;
    }

    private static OperationKind DocumentKind(string documentType, string operationType)
    {
        var document = Normalize(documentType);
        var operation = Normalize(operationType);
        if (document.Contains("коррекц", StringComparison.Ordinal)) return OperationKind.Correction;
        if (operation.Contains("возврат", StringComparison.Ordinal)) return OperationKind.Return;
        return OperationKind.Sale;
    }

    private static decimal Signed(decimal value, int sign) => Money.Normalize(Math.Abs(value) * sign);

    private static string BuildDocumentExternalId(
        string taxId,
        string fn,
        string registerNumber,
        string serial,
        string fd,
        string fpd,
        string shift,
        string numberInShift,
        DateTimeOffset occurredAt,
        decimal total)
    {
        var canonical = string.Join("|",
            DigitsOnly(taxId), DigitsOnly(fn), DigitsOnly(registerNumber), DigitsOnly(serial),
            DigitsOnly(fd), DigitsOnly(fpd), shift.Trim(), numberInShift.Trim(),
            occurredAt.ToString("O", CultureInfo.InvariantCulture), total.ToString("0.00", CultureInfo.InvariantCulture));
        return HashText(canonical);
    }

    private static string? ExtractTaxId(string name)
    {
        var match = Regex.Match(name, @"(?:ИНН|INN)[^0-9]{0,12}([0-9]{10}|[0-9]{12})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string NormalizeHeader(string value) => Normalize(value).Replace("расчёт", "расчет", StringComparison.Ordinal);
    private static string Normalize(string value) => string.Join(' ', (value ?? string.Empty).Trim().ToLowerInvariant().Replace('ё', 'е').Split(' ', StringSplitOptions.RemoveEmptyEntries));
    private static bool ContainsDigit(string value) => (value ?? string.Empty).Any(char.IsDigit);
    private static string DigitsOnly(string value) => new((value ?? string.Empty).Where(char.IsDigit).ToArray());
    private static string RegisterKey(RegisterBinding binding) => RegisterKey(binding.OrganizationId, binding.FiscalDriveNumber);
    private static string RegisterKey(Guid organizationId, string fn) => $"{organizationId:N}|{DigitsOnly(fn)}";

    private static string HashText(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private sealed record ReportMetadata(string PointName, string RegisterSerial);
}
