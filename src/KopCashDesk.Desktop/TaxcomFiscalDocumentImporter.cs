using DocumentFormat.OpenXml;
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
    private const int BatchSize = 5000;

    private readonly Database _database;
    private readonly Guid? _fallbackOrganizationId;
    private List<Organization> _organizations = [];
    private HashSet<Guid> _registerIds = [];
    private readonly Dictionary<string, RegisterBinding> _registerCache = new(StringComparer.Ordinal);
    private readonly List<CashOperation> _pendingOperations = new(BatchSize);

    public TaxcomFiscalDocumentImporter(Database database, Guid? fallbackOrganizationId = null)
    {
        _database = database;
        _fallbackOrganizationId = fallbackOrganizationId;
    }

    public TaxcomFiscalDocumentImportSummary ImportFiles(IEnumerable<string> paths)
    {
        KnownOrganizations.Ensure(_database);
        _organizations = _database.Organizations().ToList();
        _registerIds = _database.RegisterBindings().Select(x => x.Id).ToHashSet();
        _registerCache.Clear();
        _pendingOperations.Clear();

        var locationCountBefore = _database.Locations().Count;
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
                AddMessage(summary, $"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        FlushOperations(summary);
        summary.LocationsCreated = Math.Max(0, _database.Locations().Count - locationCountBefore);
        _database.RebuildCrossSourceShiftMatches();
        _database.Audit("taxcom.fiscal_documents.import",
            $"files={summary.FilesProcessed}; documents={summary.DocumentsProcessed}; inserted={summary.FiscalOperationsInserted}; updated={summary.FiscalOperationsUpdated}; skipped={summary.RowsSkipped}; failed={summary.FilesFailed}; streaming=true; batch=true");
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
            FlushOperations(summary);
            return;
        }

        using var archive = ZipFile.OpenRead(path);
        var entries = archive.Entries
            .Where(x => x.FullName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) &&
                        !x.FullName.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase))
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
            FlushOperations(summary);
        }
    }

    private void ImportWorkbook(
        Stream stream,
        string originalName,
        string? taxId,
        string documentId,
        TaxcomFiscalDocumentImportSummary summary)
    {
        using var document = SpreadsheetDocument.Open(stream, false);
        var workbookPart = document.WorkbookPart ?? throw new InvalidDataException("В XLSX отсутствует книга.");
        var sheets = workbookPart.Workbook.Sheets?.Elements<Sheet>().ToArray() ?? [];
        if (sheets.Length == 0) throw new InvalidDataException("В XLSX нет листов.");

        var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable
            .Elements<SharedStringItem>().Select(x => x.InnerText).ToArray() ?? [];
        var organization = ResolveOrganization(taxId, originalName);
        var compatible = 0;

        foreach (var sheet in sheets)
        {
            var relationshipId = sheet.Id?.Value;
            if (string.IsNullOrWhiteSpace(relationshipId) || workbookPart.GetPartById(relationshipId) is not WorksheetPart worksheetPart)
                continue;

            Dictionary<int, string>? headers = null;
            var metadata = new ReportMetadata();
            var rowsBeforeHeader = 0;
            var sheetCompatible = false;

            using var reader = OpenXmlReader.Create(worksheetPart);
            while (reader.Read())
            {
                if (!reader.IsStartElement || reader.ElementType != typeof(Row)) continue;
                if (reader.LoadCurrentElement() is not Row row) continue;

                if (headers is null)
                {
                    metadata = UpdateMetadata(metadata, row, sharedStrings);
                    var candidate = ReadHeader(row, sharedStrings);
                    if (HasRequiredHeaders(candidate.Values))
                    {
                        headers = candidate;
                        sheetCompatible = true;
                        compatible++;
                        summary.SheetsProcessed++;
                        if (metadata.IsTruncated)
                            AddMessage(summary, "Такском ограничил этот отчёт последними 30 000 документами. Более старые документы в этом XLSX отсутствуют.");
                        continue;
                    }

                    rowsBeforeHeader++;
                    if (rowsBeforeHeader >= 80) break;
                    continue;
                }

                ImportRow(row, headers, sharedStrings, organization, documentId, metadata, summary);
            }

            if (!sheetCompatible) continue;
        }

        if (compatible == 0)
            throw new InvalidDataException("Не найден лист 'Сводный отчет по фискальным документам' с колонками Дата и время, Тип операции, Наличными, Безналичными, Сумма и ККТ.");
    }

    private void ImportRow(
        Row row,
        Dictionary<int, string> headers,
        string[] sharedStrings,
        Organization organization,
        string documentId,
        ReportMetadata metadata,
        TaxcomFiscalDocumentImportSummary summary)
    {
        var values = ReadRow(row, headers, sharedStrings);
        if (values.Count == 0) return;

        var firstText = values.Values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? string.Empty;
        if (string.Equals(firstText, "Итог", StringComparison.OrdinalIgnoreCase)) return;

        if (!TryParseExcelDate(Get(values, "Дата и время"), out var occurredAt))
        {
            if (values.Values.Any(x => !string.IsNullOrWhiteSpace(x))) summary.RowsSkipped++;
            return;
        }

        if (!TryParseAmount(Get(values, "Наличными"), out var cash) ||
            !TryParseAmount(Get(values, "Безналичными"), out var electronic) ||
            !TryParseAmount(Get(values, "Сумма"), out var total))
        {
            summary.RowsSkipped++;
            return;
        }

        var operationText = Get(values, "Тип операции").Trim();
        var sign = OperationSign(operationText);
        if (sign == 0)
        {
            summary.RowsSkipped++;
            AddMessage(summary, $"Неизвестный тип операции '{operationText}' в {occurredAt:dd.MM.yyyy HH:mm}.");
            return;
        }

        cash = Signed(cash, sign);
        electronic = Signed(electronic, sign);
        total = Signed(total, sign);

        var fn = DigitsOnly(Get(values, "Зав. № ФН"));
        var registerNumber = DigitsOnly(Get(values, "Рег. № ККТ"));
        var serial = DigitsOnly(Get(values, "Зав. № ККТ"));
        if (string.IsNullOrWhiteSpace(serial) && ContainsDigit(metadata.RegisterHint))
            serial = DigitsOnly(metadata.RegisterHint);

        var kktName = CleanPointName(Get(values, "Название ККТ"));
        var pointName = CleanPointName(Get(values, "Торговая точка"));

        // "Без торговой точки" in Taxcom is not useful as a physical location. If the row has
        // a clear KKT name, use it as the reporting point instead (e.g. "Запасная ДВВС").
        if (IsGenericPoint(pointName) && !IsGenericPoint(kktName))
            pointName = kktName;

        if (string.IsNullOrWhiteSpace(fn) &&
            string.IsNullOrWhiteSpace(registerNumber) &&
            string.IsNullOrWhiteSpace(serial) &&
            string.IsNullOrWhiteSpace(kktName))
        {
            summary.RowsSkipped++;
            return;
        }

        var day = DateOnly.FromDateTime(occurredAt.DateTime);
        var register = ResolveRegister(organization, serial, fn, registerNumber, kktName, pointName, day);
        if (_registerIds.Add(register.Id) && register.LocationId is not null) summary.RegistersBound++;
        if (register.LocationId is null)
            AddMessage(summary, $"ККТ {kktName} ({serial}, ФН {fn}): требуется привязка; исходные суммы сохранены.");

        var fd = DigitsOnly(Get(values, "№ ФД"));
        var fpd = DigitsOnly(Get(values, "ФПД"));
        var shift = Get(values, "№ смены").Trim();
        var numberInShift = Get(values, "№ за смену").Trim();
        var documentType = Get(values, "Документ").Trim();
        var externalId = BuildDocumentExternalId(
            organization.TaxId, fn, registerNumber, serial, fd, fpd, shift, numberInShift, occurredAt, total);
        var kind = DocumentKind(documentType, operationText);
        int? shiftNumber = int.TryParse(shift, out var parsedShift) ? parsedShift : null;

        if (cash != 0m)
            QueueFiscalPart(externalId + ":cash", organization, register, occurredAt, kind, PaymentKind.Cash, cash, documentId, shiftNumber, summary);
        if (electronic != 0m)
            QueueFiscalPart(externalId + ":electronic", organization, register, occurredAt, kind, PaymentKind.Electronic, electronic, documentId, shiftNumber, summary);

        summary.DocumentsProcessed++;
        summary.CashTotal += cash;
        summary.ElectronicTotal += electronic;
        summary.RevenueTotal += total;
    }

    private RegisterBinding ResolveRegister(
        Organization organization,
        string serial,
        string fn,
        string registerNumber,
        string kktName,
        string pointName,
        DateOnly day)
    {
        var key = string.Join("|",
            organization.Id.ToString("N"),
            serial,
            fn,
            registerNumber,
            Normalize(pointName),
            Normalize(kktName));

        if (_registerCache.TryGetValue(key, out var cached) && InPeriod(cached, day))
            return cached;

        var resolved = RegisterBindingService.Resolve(
            _database, organization.Id, serial, fn, registerNumber, kktName, pointName, day);
        _registerCache[key] = resolved;
        return resolved;
    }

    private static bool InPeriod(RegisterBinding binding, DateOnly day) =>
        (binding.ValidFrom is null || binding.ValidFrom <= day) &&
        (binding.ValidTo is null || binding.ValidTo >= day);

    private void QueueFiscalPart(
        string externalId,
        Organization organization,
        RegisterBinding register,
        DateTimeOffset occurredAt,
        OperationKind kind,
        PaymentKind payment,
        decimal amount,
        string documentId,
        int? shiftNumber,
        TaxcomFiscalDocumentImportSummary summary)
    {
        _pendingOperations.Add(new CashOperation(
            Source,
            externalId,
            organization.Id,
            register.LocationId,
            occurredAt,
            SourceKind.Fiscal,
            kind,
            payment,
            amount,
            documentId,
            register.FiscalDriveNumber,
            register.KktSerial,
            register.RegisterNumber,
            register.DisplayName,
            shiftNumber));

        if (_pendingOperations.Count >= BatchSize)
            FlushOperations(summary);
    }

    private void FlushOperations(TaxcomFiscalDocumentImportSummary summary)
    {
        if (_pendingOperations.Count == 0) return;
        var result = _database.UpsertFiscalOperationsBatch(_pendingOperations);
        summary.FiscalOperationsInserted += result.Inserted;
        summary.FiscalOperationsUpdated += result.Updated;
        _pendingOperations.Clear();
    }

    private Organization ResolveOrganization(string? taxId, string originalName)
    {
        if (!string.IsNullOrWhiteSpace(taxId))
        {
            var matches = _organizations.Where(x => DigitsOnly(x.TaxId) == taxId).ToArray();
            if (matches.Length == 1) return matches[0];
            if (matches.Length > 1) throw new InvalidDataException($"ИНН {taxId} найден у нескольких организаций.");
            throw new InvalidDataException($"ИНН {taxId} из файла не найден среди организаций программы.");
        }

        if (_fallbackOrganizationId is Guid fallback)
        {
            var organization = _organizations.FirstOrDefault(x => x.Id == fallback);
            if (organization is not null) return organization;
        }

        throw new InvalidDataException($"В имени файла '{originalName}' не найден ИНН. Выберите организацию в верхней части программы и повторите импорт.");
    }

    private static ReportMetadata UpdateMetadata(ReportMetadata current, Row row, string[] sharedStrings)
    {
        var cells = row.Elements<Cell>().OrderBy(ColumnIndex).ToArray();
        if (cells.Length == 0) return current;

        var values = cells.Select(x => ReadCell(x, sharedStrings).Trim()).ToArray();
        var truncated = current.IsTruncated || values.Any(x =>
        {
            var normalized = Normalize(x);
            return normalized.Contains("30000", StringComparison.Ordinal) &&
                   normalized.Contains("послед", StringComparison.Ordinal) &&
                   normalized.Contains("документ", StringComparison.Ordinal);
        });

        if (cells.Length < 2) return current with { IsTruncated = truncated };

        var key = values[0];
        var value = values[1];
        if (key.Equals("Торговая точка", StringComparison.OrdinalIgnoreCase))
            return current with { PointHint = value, IsTruncated = truncated };
        if (key.Equals("ККТ", StringComparison.OrdinalIgnoreCase))
            return current with { RegisterHint = value, IsTruncated = truncated };
        return current with { IsTruncated = truncated };
    }

    private static bool IsGenericPoint(string value) => RegisterBindingService.IsGenericPoint(value);

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
            int.TryParse(cell.CellValue?.InnerText, out var sharedIndex) &&
            sharedIndex >= 0 && sharedIndex < sharedStrings.Length)
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
        string[] required =
        [
            "дата и время", "документ", "тип операции", "наличными", "безналичными", "сумма",
            "№ фд", "название ккт", "зав. № ккт", "рег. № ккт", "зав. № фн"
        ];
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
        var match = Regex.Match(
            name,
            @"(?:ИНН|INN)[^0-9]{0,12}([0-9]{10}|[0-9]{12})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string NormalizeHeader(string value) =>
        Normalize(value).Replace("расчёт", "расчет", StringComparison.Ordinal);

    private static string Normalize(string value) =>
        string.Join(' ', (value ?? string.Empty).Trim().ToLowerInvariant().Replace('ё', 'е')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static bool ContainsDigit(string value) => (value ?? string.Empty).Any(char.IsDigit);
    private static string DigitsOnly(string value) => new((value ?? string.Empty).Where(char.IsDigit).ToArray());

    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void AddMessage(TaxcomFiscalDocumentImportSummary summary, string message)
    {
        if (summary.Messages.Count < 12 && !summary.Messages.Contains(message, StringComparer.OrdinalIgnoreCase))
            summary.Messages.Add(message);
    }

    private sealed record ReportMetadata(
        string PointHint = "",
        string RegisterHint = "",
        bool IsTruncated = false);
}
