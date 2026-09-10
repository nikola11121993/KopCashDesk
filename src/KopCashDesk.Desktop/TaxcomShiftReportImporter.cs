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

public sealed class TaxcomShiftImportSummary
{
    public int FilesProcessed { get; internal set; }
    public int FilesFailed { get; internal set; }
    public int SheetsProcessed { get; internal set; }
    public int ShiftsProcessed { get; internal set; }
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
                   $"Листов со сменами: {SheetsProcessed}\n" +
                   $"Смен обработано: {ShiftsProcessed}\n" +
                   $"ККТ привязано: {RegistersBound}\n" +
                   $"Новых точек создано: {LocationsCreated}\n" +
                   $"Фискальных записей добавлено: {FiscalOperationsInserted}\n" +
                   $"Фискальных записей обновлено: {FiscalOperationsUpdated}\n" +
                   $"Строк пропущено: {RowsSkipped}\n\n" +
                   $"Наличные по загруженным сменам: {CashTotal:N2} ₽\n" +
                   $"Безнал по загруженным сменам: {ElectronicTotal:N2} ₽\n" +
                   $"Выручка по загруженным сменам: {RevenueTotal:N2} ₽";
        if (FilesFailed > 0) text += $"\nОшибок файлов: {FilesFailed}";
        if (Messages.Count > 0) text += "\n\n" + string.Join("\n", Messages.Take(12));
        return text;
    }
}

public sealed class TaxcomShiftReportImporter
{
    private const string Source = "Taxcom.ShiftReport";
    private const long MaxWorkbookBytes = 100L * 1024 * 1024;
    private const string ReftinskayaRegisterSerial = "00106900361561";
    private const string ReftinskayaPointName = "Рефтинская ГРЭС 6 столовая";

    private readonly Database _database;
    private readonly Guid? _fallbackOrganizationId;
    private List<Organization> _organizations = [];
    private List<Location> _locations = [];
    private Dictionary<string, RegisterBinding> _registers = new(StringComparer.OrdinalIgnoreCase);

    public TaxcomShiftReportImporter(Database database, Guid? fallbackOrganizationId = null)
    {
        _database = database;
        _fallbackOrganizationId = fallbackOrganizationId;
    }

    public TaxcomShiftImportSummary ImportFiles(IEnumerable<string> paths)
    {
        _organizations = _database.Organizations().ToList();
        _locations = _database.Locations().ToList();
        _registers = _database.RegisterBindings().ToDictionary(RegisterKey, StringComparer.OrdinalIgnoreCase);

        var summary = new TaxcomShiftImportSummary();
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

        _database.Audit("taxcom.shift_report.import",
            $"files={summary.FilesProcessed}; shifts={summary.ShiftsProcessed}; inserted={summary.FiscalOperationsInserted}; updated={summary.FiscalOperationsUpdated}; skipped={summary.RowsSkipped}; failed={summary.FilesFailed}");
        return summary;
    }

    private void ImportFile(string path, TaxcomShiftImportSummary summary)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Файл не найден.", path);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not ".xlsx" and not ".zip")
            throw new InvalidDataException("Поддерживаются XLSX и ZIP с отчётом Такском по сменам.");

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

    private void ImportWorkbook(Stream stream, string originalName, string? taxId, string documentId, TaxcomShiftImportSummary summary)
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
            for (var i = 0; i < Math.Min(rows.Count, 40); i++)
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
            ImportRows(rows, headerIndex, headers, sharedStrings, originalName, taxId, documentId, summary);
        }

        if (compatible == 0)
            throw new InvalidDataException("Не найден лист 'Сводный отчет по сменам' с колонками Дата закрытия, № смены, Выручка нал./безнал и ККТ.");
    }

    private void ImportRows(
        IReadOnlyList<Row> rows,
        int headerIndex,
        Dictionary<int, string> headers,
        string[] sharedStrings,
        string originalName,
        string? taxId,
        string documentId,
        TaxcomShiftImportSummary summary)
    {
        var organization = ResolveOrganization(taxId, originalName);

        for (var i = headerIndex + 1; i < rows.Count; i++)
        {
            var values = ReadRow(rows[i], headers, sharedStrings);
            if (values.Count == 0) continue;

            if (!TryParseExcelDate(Get(values, "Дата закрытия"), out var closedAt) ||
                !TryParseInt(Get(values, "№ смены"), out var shiftNumber))
            {
                summary.RowsSkipped++;
                continue;
            }

            if (!TryParseAmount(Get(values, "Выручка нал."), out var cash) ||
                !TryParseAmount(Get(values, "Выручка безнал."), out var electronic))
            {
                summary.RowsSkipped++;
                continue;
            }

            cash = Money.Normalize(cash);
            electronic = Money.Normalize(electronic);
            var total = TryParseAmount(Get(values, "Выручка"), out var reportedTotal)
                ? Money.Normalize(reportedTotal)
                : Money.Normalize(cash + electronic);

            var fn = DigitsOnly(Get(values, "Зав. № ФН"));
            var registerNumber = DigitsOnly(Get(values, "Рег. № ККТ"));
            var serial = DigitsOnly(Get(values, "Зав. № ККТ"));
            var kktName = Get(values, "Название ККТ").Trim();
            var pointName = Get(values, "Торговая точка").Trim();

            if (string.IsNullOrWhiteSpace(fn) && string.IsNullOrWhiteSpace(registerNumber) && string.IsNullOrWhiteSpace(serial))
            {
                summary.RowsSkipped++;
                continue;
            }

            var location = ResolveLocation(organization, fn, registerNumber, serial, kktName, pointName, summary);
            var shiftExternalId = BuildShiftExternalId(organization.TaxId, fn, registerNumber, serial, shiftNumber, closedAt);

            _database.Save(new ShiftClosure(
                Source,
                shiftExternalId,
                organization.Id,
                location.Id,
                closedAt,
                total,
                cash,
                electronic,
                fn,
                shiftNumber,
                documentId));

            UpsertFiscalPart(shiftExternalId + ":cash", organization, location, closedAt, PaymentKind.Cash, cash, documentId, summary);
            UpsertFiscalPart(shiftExternalId + ":electronic", organization, location, closedAt, PaymentKind.Electronic, electronic, documentId, summary);

            summary.ShiftsProcessed++;
            summary.CashTotal += cash;
            summary.ElectronicTotal += electronic;
            summary.RevenueTotal += total;

            if (Money.Normalize(total - (cash + electronic)) != 0m && summary.Messages.Count < 12)
                summary.Messages.Add($"Смена {shiftNumber}, {location.Name}: выручка {total:N2} ₽ не равна нал+безнал {(cash + electronic):N2} ₽.");
        }
    }

    private void UpsertFiscalPart(
        string externalId,
        Organization organization,
        Location location,
        DateTimeOffset occurredAt,
        PaymentKind payment,
        decimal amount,
        string documentId,
        TaxcomShiftImportSummary summary)
    {
        var kind = amount < 0m ? OperationKind.Return : OperationKind.Sale;
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
            throw new InvalidDataException($"ИНН {taxId} из файла не найден среди организаций программы. Сначала загрузите отчёт Сбер по этой организации или создайте организацию с этим ИНН.");
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
        TaxcomShiftImportSummary summary)
    {
        var organizationLocations = _locations.Where(x => x.OrganizationId == organization.Id).ToArray();
        var knownPoint = KnownPointName(serial, kktName);
        if (knownPoint is not null)
        {
            var location = FindKnownPoint(organizationLocations, knownPoint);
            if (location is null)
            {
                location = new Location(Guid.NewGuid(), organization.Id, knownPoint, string.Empty, false);
                _database.Save(location);
                _locations.Add(location);
                summary.LocationsCreated++;
            }
            EnsureRegisterBinding(organization, location, fn, registerNumber, summary);
            return location;
        }

        if (!string.IsNullOrWhiteSpace(fn) && _registers.TryGetValue(RegisterKey(organization.Id, fn), out var existingBinding))
        {
            var bound = _locations.FirstOrDefault(x => x.Id == existingBinding.LocationId);
            if (bound is not null) return bound;
        }

        var preferredName = PreferredPointName(kktName, pointName, fn);
        var nameKey = Normalize(preferredName);

        var byName = organizationLocations.Where(x => Normalize(x.Name) == nameKey).ToArray();
        Location? resolved = byName.Length == 1 ? byName[0] : null;

        if (resolved is null && ContainsDigit(preferredName))
        {
            var byAddress = organizationLocations
                .Where(x =>
                {
                    var address = Normalize(x.Address);
                    return !string.IsNullOrWhiteSpace(address) &&
                           (address.Contains(nameKey, StringComparison.Ordinal) || nameKey.Contains(address, StringComparison.Ordinal));
                })
                .ToArray();
            if (byAddress.Length == 1) resolved = byAddress[0];
        }

        if (resolved is null)
        {
            resolved = new Location(Guid.NewGuid(), organization.Id, preferredName, string.Empty, false);
            _database.Save(resolved);
            _locations.Add(resolved);
            summary.LocationsCreated++;
        }

        EnsureRegisterBinding(organization, resolved, fn, registerNumber, summary);
        return resolved;
    }

    private void EnsureRegisterBinding(Organization organization, Location location, string fn, string registerNumber, TaxcomShiftImportSummary summary)
    {
        if (string.IsNullOrWhiteSpace(fn)) return;
        var key = RegisterKey(organization.Id, fn);
        if (_registers.TryGetValue(key, out var existing) &&
            existing.LocationId == location.Id &&
            string.Equals(DigitsOnly(existing.RegisterNumber), DigitsOnly(registerNumber), StringComparison.Ordinal))
            return;

        var binding = new RegisterBinding(existing?.Id ?? Guid.NewGuid(), organization.Id, location.Id, fn, registerNumber);
        _database.SaveRegisterBinding(binding);
        _registers[key] = binding;
        summary.RegistersBound++;
    }

    private static string? KnownPointName(string serial, string kktName)
    {
        if (DigitsOnly(serial) == ReftinskayaRegisterSerial || DigitsOnly(kktName) == ReftinskayaRegisterSerial)
            return ReftinskayaPointName;
        return null;
    }

    private static Location? FindKnownPoint(IEnumerable<Location> locations, string knownPoint)
    {
        var list = locations.ToArray();
        var exact = list.Where(x => Normalize(x.Name) == Normalize(knownPoint)).ToArray();
        if (exact.Length == 1) return exact[0];

        if (knownPoint == ReftinskayaPointName)
        {
            var cafeteria6 = list.Where(x =>
            {
                var name = Normalize(x.Name);
                return name.Contains("рефтин", StringComparison.Ordinal) &&
                       name.Contains("грэс", StringComparison.Ordinal) &&
                       (name.Contains("6 стол", StringComparison.Ordinal) || name.Contains("столовая 6", StringComparison.Ordinal));
            }).ToArray();
            if (cafeteria6.Length == 1) return cafeteria6[0];

            var gres = list.Where(x =>
            {
                var name = Normalize(x.Name);
                return name.Contains("рефтин", StringComparison.Ordinal) && name.Contains("грэс", StringComparison.Ordinal);
            }).ToArray();
            if (gres.Length == 1) return gres[0];
        }

        return null;
    }

    private static string PreferredPointName(string kktName, string pointName, string fn)
    {
        var kkt = CleanPointName(kktName);
        if (!IsGenericPoint(kkt)) return kkt;
        var point = CleanPointName(pointName);
        if (!IsGenericPoint(point)) return point;
        return string.IsNullOrWhiteSpace(fn) ? "Неопределённая ККТ" : $"ККТ {fn}";
    }

    private static bool IsGenericPoint(string value)
    {
        var key = Normalize(value);
        return string.IsNullOrWhiteSpace(key) || key is "без торговой точки" or "ккт" or "касса" or "неопределенная ккт";
    }

    private static string CleanPointName(string value)
    {
        var result = Regex.Replace(value.Trim(), @"\s+", " ");
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
        var set = new HashSet<string>(headers, StringComparer.OrdinalIgnoreCase);
        string[] required = ["Дата закрытия", "№ смены", "Выручка нал.", "Выручка безнал.", "Название ККТ", "Зав. № ФН"];
        return required.All(set.Contains);
    }

    private static string Get(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) ? value ?? string.Empty : string.Empty;

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

    private static bool TryParseInt(string value, out int number)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return true;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric))
        {
            number = checked((int)Math.Round(numeric));
            return true;
        }
        return false;
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

    private static string? ExtractTaxId(string name)
    {
        var match = Regex.Match(name, @"(?:ИНН|INN)[^0-9]{0,12}([0-9]{10}|[0-9]{12})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string Normalize(string value) => SberAcquiringImporter.NormalizeForMatch(value);
    private static bool ContainsDigit(string value) => value.Any(char.IsDigit);
    private static string DigitsOnly(string value) => new(value.Where(char.IsDigit).ToArray());
    private static string RegisterKey(RegisterBinding binding) => RegisterKey(binding.OrganizationId, binding.FiscalDriveNumber);
    private static string RegisterKey(Guid organizationId, string fn) => $"{organizationId:N}|{DigitsOnly(fn)}";

    private static string BuildShiftExternalId(string taxId, string fn, string registerNumber, string serial, int shiftNumber, DateTimeOffset closedAt)
    {
        var canonical = string.Join("|", DigitsOnly(taxId), DigitsOnly(fn), DigitsOnly(registerNumber), DigitsOnly(serial), shiftNumber.ToString(CultureInfo.InvariantCulture), closedAt.ToString("O", CultureInfo.InvariantCulture));
        return HashText(canonical);
    }

    private static string HashText(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
