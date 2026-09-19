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

public enum SmartImportKind
{
    Unknown,
    Sber,
    Taxcom,
    TaxcomFiscalDocuments,
    ClosedShifts,
    UbrdDaily,
    Frontol,
    Crpt
}

public static class SmartReportDetector
{
    public static SmartImportKind Detect(string path)
    {
        if (!File.Exists(path)) return SmartImportKind.Unknown;
        var extension = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            if (extension == ".crpt")
                return UnifiedImportWindow.Detect(path) == UnifiedImportKind.Crpt ? SmartImportKind.Crpt : SmartImportKind.Unknown;
            if (extension == ".txt")
                return UnifiedImportWindow.Detect(path) == UnifiedImportKind.Frontol ? SmartImportKind.Frontol : SmartImportKind.Unknown;
            if (extension == ".xlsx")
            {
                using var stream = File.OpenRead(path);
                return ProbeWorkbook(stream, Path.GetFileName(path));
            }
            if (extension == ".zip")
            {
                using var archive = ZipFile.OpenRead(path);
                var kinds = new List<SmartImportKind>();
                foreach (var entry in archive.Entries.Where(x => x.FullName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)).Take(100))
                {
                    using var input = entry.Open();
                    using var memory = new MemoryStream();
                    input.CopyTo(memory);
                    memory.Position = 0;
                    var kind = ProbeWorkbook(memory, entry.Name);
                    if (kind != SmartImportKind.Unknown) kinds.Add(kind);
                }
                // A Sber regular archive can contain a Taxcom XLSX as an extra attachment.
                // Bank import knows how to ignore the unrelated workbook, so do not mark such ZIP as Unknown.
                if (kinds.Contains(SmartImportKind.Sber)) return SmartImportKind.Sber;
                return kinds.Distinct().Count() == 1 ? kinds[0] : SmartImportKind.Unknown;
            }
        }
        catch
        {
            return SmartImportKind.Unknown;
        }
        return SmartImportKind.Unknown;
    }

    public static SmartImportKind ProbeWorkbook(Stream stream, string fileName)
    {
        using var document = SpreadsheetDocument.Open(stream, false);
        var workbookPart = document.WorkbookPart;
        if (workbookPart?.Workbook.Sheets is null) return SmartImportKind.Unknown;

        // Shared strings are only a few MB even when the worksheet XML itself is tens of MB.
        // The important optimisation is to stream worksheet rows instead of materialising the
        // entire SheetData DOM just to identify the report type.
        var shared = workbookPart.SharedStringTablePart?.SharedStringTable
            .Elements<SharedStringItem>().Select(x => x.InnerText).ToArray() ?? [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var name = Normalize(fileName);

        foreach (var sheet in workbookPart.Workbook.Sheets.Elements<Sheet>().Take(20))
        {
            var id = sheet.Id?.Value;
            if (string.IsNullOrWhiteSpace(id) || workbookPart.GetPartById(id) is not WorksheetPart ws) continue;

            var rowsSeen = 0;
            using var reader = OpenXmlReader.Create(ws);
            while (reader.Read() && rowsSeen < 120)
            {
                if (!reader.IsStartElement || reader.ElementType != typeof(Row)) continue;
                if (reader.LoadCurrentElement() is not Row row) continue;
                rowsSeen++;

                var rowValues = new List<string>();
                foreach (var cell in row.Elements<Cell>())
                {
                    var value = Normalize(ReadCell(cell, shared));
                    if (value.Length == 0) continue;
                    seen.Add(value);
                    rowValues.Add(value);
                }

                if (SmartSberAcquiringImporter.IsBankHeader(rowValues))
                    return SmartImportKind.Sber;

                var classified = ClassifySeen(seen, name);
                if (classified != SmartImportKind.Unknown)
                    return classified;
            }
        }

        return ClassifySeen(seen, name);
    }

    private static SmartImportKind ClassifySeen(HashSet<string> seen, string normalizedFileName)
    {
        if (Has(seen, "торговая точка", "номер смены", "дата закрытия смены", "получено наличными", "получено безналичными", "номер фн") &&
            (seen.Contains("наименование ккт") || seen.Contains("заводской номер ккт")))
            return SmartImportKind.ClosedShifts;

        if (seen.Contains("свод по дням (по опердню)") && Has(seen, "точка", "дата", "сумма") && seen.Contains("терминал"))
            return SmartImportKind.UbrdDaily;

        var fiscalCore = new[] { "дата и время", "документ", "тип операции", "наличными", "безналичными", "сумма" };
        var fiscalIds = new[] { "№ фд", "фпд", "название ккт", "зав. № ккт", "рег. № ккт", "зав. № фн" };
        if (fiscalCore.All(seen.Contains) &&
            (seen.Contains("сводный отчет по фискальным документам") ||
             normalizedFileName.Contains("сводный отчет по фискальным документам", StringComparison.Ordinal) ||
             fiscalIds.Count(seen.Contains) >= 3))
            return SmartImportKind.TaxcomFiscalDocuments;

        var shiftCore = new[] { "дата закрытия", "№ смены", "выручка нал.", "выручка безнал." };
        var shiftIds = new[] { "название ккт", "зав. № фн", "рег. № ккт", "зав. № ккт" };
        if (shiftCore.All(seen.Contains) &&
            (seen.Contains("сводный отчет по сменам") ||
             normalizedFileName.Contains("сводный отчет по сменам", StringComparison.Ordinal) ||
             shiftIds.Any(seen.Contains)))
            return SmartImportKind.Taxcom;

        return SmartImportKind.Unknown;
    }

    private static bool Has(HashSet<string> set, params string[] values) => values.All(set.Contains);
    private static string Normalize(string value) => Regex.Replace((value ?? string.Empty).Replace('\u00A0', ' ').Trim().ToLowerInvariant().Replace('ё', 'е'), @"\s+", " ");

    private static string ReadCell(Cell cell, string[] shared)
    {
        if (cell.DataType?.Value == CellValues.SharedString && int.TryParse(cell.CellValue?.InnerText, out var i) && i >= 0 && i < shared.Length)
            return shared[i];
        if (cell.DataType?.Value == CellValues.InlineString) return cell.InlineString?.InnerText ?? string.Empty;
        return cell.CellValue?.InnerText ?? cell.InnerText ?? string.Empty;
    }
}

public static class SmartKnownRules
{
    public const string CopTaxId = KnownBusinessRules.CopTaxId;

    public static void PrepareCleanDatabase(Database database)
    {
        var org = database.Organizations().FirstOrDefault(x => Digits(x.TaxId) == CopTaxId);
        if (org is null) return;

        // The user confirmed exactly seven physical/reporting points. Only these may be created automatically.
        foreach (var name in KnownBusinessRules.FixedPointNames)
            EnsureLocation(database, org.Id, name);

        KnownBusinessRules.ApplyPending(database);
    }

    private static Location EnsureLocation(Database database, Guid organizationId, string name)
    {
        var all = database.Locations(includeInactive: true).Where(x => x.OrganizationId == organizationId &&
            Normalize(x.Name) == Normalize(name)).ToArray();
        var location = all.FirstOrDefault(x => x.IsActive) ?? all.FirstOrDefault();
        if (location is not null)
        {
            if (!location.IsActive || location.IsExcluded || location.MergedIntoLocationId is not null || location.Name != name)
            {
                location = location with { Name = name, IsActive = true, IsExcluded = false, MergedIntoLocationId = null };
                database.Save(location);
            }
            return location;
        }

        location = new Location(Guid.NewGuid(), organizationId, name);
        database.Save(location);
        return location;
    }

    private static string Normalize(string value) => SberAcquiringImporter.NormalizeForMatch(value);
    private static string Digits(string value) => new((value ?? string.Empty).Where(char.IsDigit).ToArray());
}

public sealed class UbrdImportSummary
{
    public int Rows { get; internal set; }
    public int Added { get; internal set; }
    public int Duplicates { get; internal set; }
    public int Skipped { get; internal set; }
    public string ToDisplayText() => $"Строк прочитано: {Rows}\nОпераций добавлено: {Added}\nДублей пропущено: {Duplicates}\nСтрок пропущено: {Skipped}";
}

public sealed class UbrdDailyImporter
{
    private const string Source = "UBRiR.DailySummary";
    private const string TerminalId = "26204835";
    private readonly Database _database;
    public UbrdDailyImporter(Database database) => _database = database;

    public UbrdImportSummary ImportFiles(IEnumerable<string> paths)
    {
        var summary = new UbrdImportSummary();
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase)) ImportFile(path, summary);
        _database.Audit("ubrir.daily.import", $"rows={summary.Rows}; added={summary.Added}; duplicates={summary.Duplicates}; skipped={summary.Skipped}");
        return summary;
    }

    private void ImportFile(string path, UbrdImportSummary summary)
    {
        var org = _database.Organizations().FirstOrDefault(x => Digits(x.TaxId) == SmartKnownRules.CopTaxId)
            ?? throw new InvalidDataException("Для УБРиР не найдена организация ИНН 6683009222. Сначала загрузите отчёты Сбер.");

        // UBRiR TID 26204835 belongs to the same historical chain as KKT 00178945:
        // Вороний Брод -> Ленинградская 1 -> Мира 4. The combined reporting point is Мира 4 (неактив.).
        SmartKnownRules.PrepareCleanDatabase(_database);
        var location = _database.Locations().SingleOrDefault(x => x.OrganizationId == org.Id && x.IsActive &&
                           Normalize(x.Name) == Normalize(KnownBusinessRules.MiraPointName))
            ?? throw new InvalidDataException("Не найдена фиксированная точка «Мира 4 (неактив.)».");
        _database.Save(new TerminalBinding(Guid.NewGuid(), org.Id, location.Id, "UBRiR", TerminalId, string.Empty, "POS", BindingSource.Rule));

        var hash = HashFile(path);
        var documentId = _database.RegisterSourceDocument(Source, hash, hash, Path.GetFileName(path));
        using var stream = File.OpenRead(path);
        using var document = SpreadsheetDocument.Open(stream, false);
        var wb = document.WorkbookPart ?? throw new InvalidDataException("Некорректный XLSX УБРиР.");
        var shared = wb.SharedStringTablePart?.SharedStringTable.Elements<SharedStringItem>().Select(x => x.InnerText).ToArray() ?? [];

        foreach (var sheet in wb.Workbook.Sheets!.Elements<Sheet>())
        {
            var id = sheet.Id?.Value;
            if (string.IsNullOrWhiteSpace(id) || wb.GetPartById(id) is not WorksheetPart ws) continue;
            var rows = ws.Worksheet.GetFirstChild<SheetData>()?.Elements<Row>().ToList() ?? [];
            var header = rows.FindIndex(r => RowValues(r, shared).Select(Normalize).SequenceEqual(new[] { "точка", "дата", "сумма" }));
            if (header < 0) continue;
            for (var i = header + 1; i < rows.Count; i++)
            {
                var v = RowValues(rows[i], shared);
                if (v.Count < 3 || string.IsNullOrWhiteSpace(v[0])) continue;
                summary.Rows++;
                if (!TryDate(v[1], out var date) || !TryAmount(v[2], out var amount) || amount == 0m)
                {
                    summary.Skipped++;
                    continue;
                }
                amount = Money.Normalize(amount);
                var kind = amount < 0m ? OperationKind.Return : OperationKind.Sale;
                var external = HashText($"{TerminalId}|{date:yyyy-MM-dd}|{Money.ToKopecks(amount)}|{kind}");
                var inserted = _database.Insert(new CashOperation(Source, external, org.Id, location.Id,
                    new DateTimeOffset(date, TimeOnly.Noon, TimeZoneInfo.Local.GetUtcOffset(date.ToDateTime(TimeOnly.Noon))),
                    SourceKind.Bank, kind, PaymentKind.Electronic, amount, documentId));
                if (inserted) summary.Added++; else summary.Duplicates++;
            }
            break;
        }
    }

    private static List<string> RowValues(Row row, string[] shared)
    {
        var cells = row.Elements<Cell>().ToArray();
        if (cells.Length == 0) return [];
        var result = Enumerable.Repeat(string.Empty, cells.Max(ColumnIndex) + 1).ToList();
        foreach (var cell in cells) result[ColumnIndex(cell)] = ReadCell(cell, shared).Trim();
        return result;
    }

    private static int ColumnIndex(Cell cell)
    {
        var s = cell.CellReference?.Value ?? string.Empty; var n = 0;
        foreach (var ch in s) { if (!char.IsLetter(ch)) break; n = n * 26 + char.ToUpperInvariant(ch) - 'A' + 1; }
        return Math.Max(0, n - 1);
    }
    private static string ReadCell(Cell c, string[] shared)
    {
        if (c.DataType?.Value == CellValues.SharedString && int.TryParse(c.CellValue?.InnerText, out var i) && i >= 0 && i < shared.Length) return shared[i];
        if (c.DataType?.Value == CellValues.InlineString) return c.InlineString?.InnerText ?? string.Empty;
        return c.CellValue?.InnerText ?? c.InnerText ?? string.Empty;
    }
    private static bool TryDate(string value, out DateOnly date)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial))
        {
            try { date = DateOnly.FromDateTime(DateTime.FromOADate(serial)); return true; } catch { }
        }
        if (DateOnly.TryParse(value, CultureInfo.GetCultureInfo("ru-RU"), DateTimeStyles.AllowWhiteSpaces, out date)) return true;
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dt)) { date = DateOnly.FromDateTime(dt); return true; }
        date = default; return false;
    }
    private static bool TryAmount(string value, out decimal amount) =>
        decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out amount) ||
        decimal.TryParse(value, NumberStyles.Any, CultureInfo.GetCultureInfo("ru-RU"), out amount);
    private static string Normalize(string value) => SberAcquiringImporter.NormalizeForMatch(value);
    private static string Digits(string value) => new((value ?? string.Empty).Where(char.IsDigit).ToArray());
    private static string HashText(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string HashFile(string path) { using var s = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant(); }
}

public sealed class ClosedShiftImportSummary
{
    public int Shifts { get; internal set; }
    public int Skipped { get; internal set; }
    public int FiscalInserted { get; internal set; }
    public int FiscalUpdated { get; internal set; }
    public decimal Cash { get; internal set; }
    public decimal Electronic { get; internal set; }
    public string ToDisplayText() => $"Смен обработано: {Shifts}\nФискальных записей добавлено: {FiscalInserted}\nФискальных записей обновлено: {FiscalUpdated}\nСтрок пропущено: {Skipped}\n\nНаличные: {Cash:N2} ₽\nБезнал: {Electronic:N2} ₽";
}

public sealed class ClosedShiftReportImporter
{
    private const string Source = "Taxcom.ShiftReport";
    private readonly Database _database;
    public ClosedShiftReportImporter(Database database) => _database = database;

    public ClosedShiftImportSummary ImportFiles(IEnumerable<string> paths)
    {
        var summary = new ClosedShiftImportSummary();
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase)) ImportFile(path, summary);
        _database.RebuildCrossSourceShiftMatches();
        _database.Audit("closed_shift.import", $"shifts={summary.Shifts}; skipped={summary.Skipped}");
        return summary;
    }

    private void ImportFile(string path, ClosedShiftImportSummary summary)
    {
        var taxId = ExtractTaxId(Path.GetFileName(path));
        var org = _database.Organizations().FirstOrDefault(x => Digits(x.TaxId) == taxId)
            ?? throw new InvalidDataException($"ИНН {taxId} из отчёта закрытых смен не найден среди организаций.");
        var hash = HashFile(path);
        var documentId = _database.RegisterSourceDocument(Source, hash, hash, Path.GetFileName(path));

        using var stream = File.OpenRead(path);
        using var doc = SpreadsheetDocument.Open(stream, false);
        var wb = doc.WorkbookPart ?? throw new InvalidDataException("Некорректный XLSX закрытых смен.");
        var shared = wb.SharedStringTablePart?.SharedStringTable.Elements<SharedStringItem>().Select(x => x.InnerText).ToArray() ?? [];
        foreach (var sheet in wb.Workbook.Sheets!.Elements<Sheet>())
        {
            var id = sheet.Id?.Value;
            if (string.IsNullOrWhiteSpace(id) || wb.GetPartById(id) is not WorksheetPart ws) continue;
            var rows = ws.Worksheet.GetFirstChild<SheetData>()?.Elements<Row>().ToList() ?? [];
            if (rows.Count == 0) continue;
            var headers = ReadHeader(rows[0], shared);
            if (!headers.Values.Contains("дата закрытия смены") || !headers.Values.Contains("номер смены")) continue;

            for (var i = 1; i < rows.Count; i++)
            {
                var v = ReadRow(rows[i], headers, shared);
                if (!TryClosedAt(Get(v, "дата закрытия смены"), Get(v, "время закрытия смены"), out var closedAt) ||
                    !TryInt(Get(v, "номер смены"), out var shiftNumber) ||
                    !TryAmount(Get(v, "получено наличными"), out var cash) ||
                    !TryAmount(Get(v, "получено безналичными"), out var electronic))
                {
                    summary.Skipped++; continue;
                }

                cash = Money.Normalize(cash); electronic = Money.Normalize(electronic);
                var total = TryAmount(Get(v, "выручка"), out var reported) ? Money.Normalize(reported) : Money.Normalize(cash + electronic);
                var fn = Digits(Get(v, "номер фн"));
                var serial = Digits(Get(v, "заводской номер ккт"));
                var rnm = Digits(Get(v, "регномер ккт"));
                var display = Get(v, "наименование ккт").Trim();
                var point = Get(v, "торговая точка").Trim();
                if (fn.Length == 0 && serial.Length == 0 && rnm.Length == 0) { summary.Skipped++; continue; }

                var binding = RegisterBindingService.Resolve(_database, org.Id, serial, fn, rnm, display, point, DateOnly.FromDateTime(closedAt.DateTime));
                var external = ShiftId(org.TaxId, fn, rnm, serial, shiftNumber, closedAt);
                _database.Save(new ShiftClosure(Source, external, org.Id, binding.LocationId, closedAt, total, cash, electronic,
                    fn, shiftNumber, documentId, serial, rnm, display));
                Upsert(external + ":cash", org, binding, closedAt, shiftNumber, PaymentKind.Cash, cash, documentId, summary);
                Upsert(external + ":electronic", org, binding, closedAt, shiftNumber, PaymentKind.Electronic, electronic, documentId, summary);
                summary.Shifts++; summary.Cash += cash; summary.Electronic += electronic;
            }
        }
    }

    private void Upsert(string id, Organization org, RegisterBinding binding, DateTimeOffset time, int shift, PaymentKind payment, decimal amount, string document, ClosedShiftImportSummary summary)
    {
        var inserted = _database.UpsertFiscalOperation(new CashOperation(Source, id, org.Id, binding.LocationId, time,
            SourceKind.Fiscal, amount < 0 ? OperationKind.Return : OperationKind.Sale, payment, amount, document,
            binding.FiscalDriveNumber, binding.KktSerial, binding.RegisterNumber, binding.DisplayName, shift));
        if (inserted) summary.FiscalInserted++; else summary.FiscalUpdated++;
    }

    private static Dictionary<int, string> ReadHeader(Row row, string[] shared)
    {
        var d = new Dictionary<int, string>();
        foreach (var c in row.Elements<Cell>()) { var s = Normalize(ReadCell(c, shared)); if (s.Length > 0) d[ColumnIndex(c)] = s; }
        return d;
    }
    private static Dictionary<string, string> ReadRow(Row row, Dictionary<int, string> headers, string[] shared)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in row.Elements<Cell>()) if (headers.TryGetValue(ColumnIndex(c), out var h)) d[h] = ReadCell(c, shared);
        return d;
    }
    private static string Get(Dictionary<string, string> d, string key) => d.TryGetValue(key, out var v) ? v ?? string.Empty : string.Empty;
    private static int ColumnIndex(Cell cell) { var s = cell.CellReference?.Value ?? string.Empty; var n = 0; foreach (var ch in s) { if (!char.IsLetter(ch)) break; n = n * 26 + char.ToUpperInvariant(ch) - 'A' + 1; } return Math.Max(0, n - 1); }
    private static string ReadCell(Cell c, string[] shared) { if (c.DataType?.Value == CellValues.SharedString && int.TryParse(c.CellValue?.InnerText, out var i) && i >= 0 && i < shared.Length) return shared[i]; if (c.DataType?.Value == CellValues.InlineString) return c.InlineString?.InnerText ?? string.Empty; return c.CellValue?.InnerText ?? c.InnerText ?? string.Empty; }
    private static string Normalize(string value) => Regex.Replace((value ?? string.Empty).Replace('\u00A0', ' ').Trim().ToLowerInvariant().Replace('ё', 'е'), @"\s+", " ");
    private static string Digits(string value) => new((value ?? string.Empty).Where(char.IsDigit).ToArray());
    private static string ExtractTaxId(string name) { var m = Regex.Match(name ?? string.Empty, @"(?:ИНН|INN)[^0-9]{0,12}([0-9]{10}|[0-9]{12})", RegexOptions.IgnoreCase); return m.Success ? m.Groups[1].Value : string.Empty; }
    private static bool TryAmount(string v, out decimal a) { if (string.IsNullOrWhiteSpace(v)) { a = 0; return true; } return decimal.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out a) || decimal.TryParse(v, NumberStyles.Any, CultureInfo.GetCultureInfo("ru-RU"), out a); }
    private static bool TryInt(string v, out int n) { if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return true; if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) { n = (int)Math.Round(x); return true; } return false; }
    private static bool TryClosedAt(string dateText, string timeText, out DateTimeOffset result)
    {
        result = default;
        if (!DateTime.TryParse(dateText, CultureInfo.GetCultureInfo("ru-RU"), DateTimeStyles.AllowWhiteSpaces, out var date) &&
            !DateTime.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date)) return false;
        TimeSpan time = TimeSpan.Zero;
        if (!string.IsNullOrWhiteSpace(timeText)) TimeSpan.TryParse(timeText, CultureInfo.InvariantCulture, out time);
        var dt = DateTime.SpecifyKind(date.Date + time, DateTimeKind.Unspecified);
        result = new DateTimeOffset(dt, TimeZoneInfo.Local.GetUtcOffset(dt)); return true;
    }
    private static string ShiftId(string taxId, string fn, string rnm, string serial, int shift, DateTimeOffset time) => HashText(string.Join("|", Digits(taxId), fn, rnm, serial, shift.ToString(CultureInfo.InvariantCulture), time.ToString("O", CultureInfo.InvariantCulture)));
    private static string HashText(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string HashFile(string path) { using var s = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant(); }
}
