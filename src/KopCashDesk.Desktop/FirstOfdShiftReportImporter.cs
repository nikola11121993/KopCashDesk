using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using KopCashDesk.Core;
using KopCashDesk.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace KopCashDesk.Desktop;

public sealed class FirstOfdShiftImportSummary
{
    public int FilesProcessed { get; internal set; }
    public int Shifts { get; internal set; }
    public int Skipped { get; internal set; }
    public int FiscalInserted { get; internal set; }
    public int FiscalUpdated { get; internal set; }
    public decimal Cash { get; internal set; }
    public decimal Electronic { get; internal set; }
    public decimal Revenue { get; internal set; }
    public List<string> Messages { get; } = [];

    public string ToDisplayText()
    {
        var text =
            $"Файлов обработано: {FilesProcessed}\n" +
            $"Смен обработано: {Shifts}\n" +
            $"Фискальных записей добавлено: {FiscalInserted}\n" +
            $"Фискальных записей обновлено: {FiscalUpdated}\n" +
            $"Строк пропущено: {Skipped}\n\n" +
            $"Наличные: {Cash:N2} ₽\n" +
            $"Безнал: {Electronic:N2} ₽\n" +
            $"Выручка: {Revenue:N2} ₽";
        if (Messages.Count > 0) text += "\n\n" + string.Join("\n", Messages.Take(12));
        return text;
    }
}

public sealed class FirstOfdShiftReportImporter
{
    public const string Source = "FirstOFD.ShiftReport";
    private const long MaxWorkbookBytes = 250L * 1024 * 1024;

    private sealed record RegisterRule(string PointName, string Address, string Serial, string Rnm);

    // Confirmed by the user for ООО «КОП».
    private static readonly IReadOnlyDictionary<string, RegisterRule> KopRegisters =
        new Dictionary<string, RegisterRule>(StringComparer.Ordinal)
        {
            ["лакомка"] = new("Лакомка", "п. Рефтинский, ул. Молодежная, 23А", "04207570", "0006435906064640"),
            ["хризотил"] = new("Хризотил", "г. Асбест, ул. Королева, 30", "00180494", "0006220550041581"),
            ["сиеста"] = new("Кафе Сиеста", "п. Рефтинский, ул. Гагарина, 18А/1", "0014943", "0001113145061553")
        };

    private readonly Database _database;
    private readonly Dictionary<(Guid Org, string Serial), RegisterBinding> _bindings = new();

    public FirstOfdShiftReportImporter(Database database)
    {
        _database = database;
    }

    public FirstOfdShiftImportSummary ImportFiles(IEnumerable<string> paths)
    {
        KnownOrganizations.Ensure(_database);
        var summary = new FirstOfdShiftImportSummary();

        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ImportFile(path, summary);
            summary.FilesProcessed++;
        }

        _database.RebuildCrossSourceShiftMatches();
        _database.Audit("firstofd.shift.import",
            $"files={summary.FilesProcessed}; shifts={summary.Shifts}; inserted={summary.FiscalInserted}; updated={summary.FiscalUpdated}; skipped={summary.Skipped}");
        return summary;
    }

    private void ImportFile(string path, FirstOfdShiftImportSummary summary)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Файл не найден.", path);
        if (!Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Первый ОФД: поддерживается XLSX «Отчет по сменам с налогами».");

        var info = new FileInfo(path);
        if (info.Length <= 0) throw new InvalidDataException("Файл пустой.");
        if (info.Length > MaxWorkbookBytes) throw new InvalidDataException("Файл Первого ОФД слишком большой.");

        var fileHash = HashFile(path);
        var documentId = _database.RegisterSourceDocument(Source, fileHash, fileHash, Path.GetFileName(path));

        using var stream = File.OpenRead(path);
        using var document = SpreadsheetDocument.Open(stream, false);
        var workbookPart = document.WorkbookPart ?? throw new InvalidDataException("В XLSX отсутствует книга.");
        var shared = workbookPart.SharedStringTablePart?.SharedStringTable
            .Elements<SharedStringItem>().Select(x => x.InnerText).ToArray() ?? [];

        var foundHeader = false;

        foreach (var sheet in workbookPart.Workbook.Sheets?.Elements<Sheet>() ?? [])
        {
            var id = sheet.Id?.Value;
            if (string.IsNullOrWhiteSpace(id) || workbookPart.GetPartById(id) is not WorksheetPart ws) continue;

            Dictionary<int, string>? headers = null;
            using var reader = OpenXmlReader.Create(ws);
            while (reader.Read())
            {
                if (!reader.IsStartElement || reader.ElementType != typeof(Row)) continue;
                if (reader.LoadCurrentElement() is not Row row) continue;

                if (headers is null)
                {
                    var candidate = ReadHeader(row, shared);
                    if (!IsHeader(candidate)) continue;
                    headers = candidate;
                    foundHeader = true;
                    continue;
                }

                var values = ReadRow(row, headers, shared);
                if (values.Count == 0) continue;

                var taxId = Digits(Get(values, "инн"));
                if (taxId.Length == 0 || Normalize(Get(values, "инн")) == "итого") continue;

                var organization = _database.Organizations().FirstOrDefault(x => Digits(x.TaxId) == taxId);
                if (organization is null)
                {
                    summary.Skipped++;
                    AddMessage(summary, $"ИНН {taxId}: организация не найдена, строка пропущена.");
                    continue;
                }

                var display = Get(values, "наименование ккт").Trim();
                if (!TryRule(organization, display, out var rule))
                {
                    summary.Skipped++;
                    AddMessage(summary, $"ККТ «{display}» (ИНН {taxId}) не настроена для отчёта Первого ОФД.");
                    continue;
                }

                if (!TryInt(Get(values, "смена"), out var shiftNumber) ||
                    !TryDateTime(Get(values, "закрыта"), out var closedAt) ||
                    !TryAmount(Get(values, "сумма выручки наличными"), out var cash) ||
                    !TryAmount(Get(values, "сумма выручки безналичными"), out var electronic))
                {
                    summary.Skipped++;
                    continue;
                }

                cash = Money.Normalize(cash);
                electronic = Money.Normalize(electronic);
                var total = TryAmount(Get(values, "сумма выручки"), out var reported)
                    ? Money.Normalize(reported)
                    : Money.Normalize(cash + electronic);

                var binding = EnsureBinding(organization, rule, display, Get(values, "адрес места установки ккт"));
                var external = ShiftId(taxId, rule.Serial, rule.Rnm, shiftNumber, closedAt);

                _database.Save(new ShiftClosure(
                    Source, external, organization.Id, binding.LocationId, closedAt,
                    total, cash, electronic, string.Empty, shiftNumber, documentId,
                    rule.Serial, rule.Rnm, display));

                var batch = _database.UpsertFiscalOperationsBatch(
                [
                    new CashOperation(
                        Source, external + ":cash", organization.Id, binding.LocationId, closedAt,
                        SourceKind.Fiscal, cash < 0m ? OperationKind.Return : OperationKind.Sale,
                        PaymentKind.Cash, cash, documentId, string.Empty, rule.Serial, rule.Rnm, display, shiftNumber),
                    new CashOperation(
                        Source, external + ":electronic", organization.Id, binding.LocationId, closedAt,
                        SourceKind.Fiscal, electronic < 0m ? OperationKind.Return : OperationKind.Sale,
                        PaymentKind.Electronic, electronic, documentId, string.Empty, rule.Serial, rule.Rnm, display, shiftNumber)
                ]);

                summary.FiscalInserted += batch.Inserted;
                summary.FiscalUpdated += batch.Updated;
                summary.Shifts++;
                summary.Cash += cash;
                summary.Electronic += electronic;
                summary.Revenue += total;
            }
        }

        if (!foundHeader)
            throw new InvalidDataException("Файл не похож на отчёт Первого ОФД «Отчет по сменам с налогами»: не найдена строка заголовков.");
    }

    private RegisterBinding EnsureBinding(Organization organization, RegisterRule rule, string display, string sourceAddress)
    {
        var key = (organization.Id, rule.Serial);
        if (_bindings.TryGetValue(key, out var cached)) return cached;

        var locations = _database.Locations(includeInactive: true)
            .Where(x => x.OrganizationId == organization.Id)
            .ToArray();

        var matches = locations.Where(x => IsPointAlias(x.Name, rule.PointName)).ToArray();
        Location location;
        if (matches.Length == 1)
        {
            location = matches[0];
            var address = string.IsNullOrWhiteSpace(location.Address)
                ? PreferredAddress(rule, sourceAddress)
                : location.Address;
            if (!location.IsActive || location.IsExcluded || location.MergedIntoLocationId is not null ||
                location.Name != rule.PointName || location.Address != address)
            {
                location = location with
                {
                    Name = rule.PointName,
                    Address = address,
                    IsActive = true,
                    IsExcluded = false,
                    MergedIntoLocationId = null
                };
                _database.Save(location);
            }
        }
        else if (matches.Length == 0)
        {
            location = new Location(Guid.NewGuid(), organization.Id, rule.PointName, PreferredAddress(rule, sourceAddress));
            _database.Save(location);
        }
        else
        {
            throw new InvalidDataException($"Первый ОФД: найдено несколько точек, похожих на «{rule.PointName}». Сначала объедините дубли.");
        }

        var existing = _database.RegisterBindings()
            .Where(x => x.OrganizationId == organization.Id && Digits(x.KktSerial) == rule.Serial)
            .OrderByDescending(x => x.BindingSource)
            .FirstOrDefault();

        RegisterBinding binding;
        if (existing is not null && (existing.IsLocked || existing.BindingSource == BindingSource.Manual))
        {
            binding = existing;
        }
        else
        {
            binding = existing is null
                ? new RegisterBinding(Guid.NewGuid(), organization.Id, location.Id, string.Empty, rule.Rnm,
                    BindingSource.Rule, false, null, null, rule.Serial, display)
                : existing with
                {
                    LocationId = location.Id,
                    RegisterNumber = rule.Rnm,
                    KktSerial = rule.Serial,
                    DisplayName = display,
                    BindingSource = BindingSource.Rule
                };
            _database.SaveRegisterBinding(binding);
        }

        _bindings[key] = binding;
        return binding;
    }

    private static string PreferredAddress(RegisterRule rule, string sourceAddress)
    {
        if (!string.IsNullOrWhiteSpace(rule.Address)) return rule.Address;
        return sourceAddress?.Trim() ?? string.Empty;
    }

    private static bool IsPointAlias(string existing, string canonical)
    {
        var value = Normalize(existing);
        var target = Normalize(canonical);
        if (value == target) return true;

        if (target == "лакомка") return value.Contains("лакомка", StringComparison.Ordinal);
        if (target == "хризотил") return value.Contains("хризотил", StringComparison.Ordinal);
        if (target == "кафе сиеста" || target == "кафесиеста")
            return value.Contains("сиеста", StringComparison.Ordinal);
        return false;
    }

    private static bool TryRule(Organization organization, string display, out RegisterRule rule)
    {
        rule = null!;
        if (Digits(organization.TaxId) != KnownOrganizations.KopTaxId) return false;
        return KopRegisters.TryGetValue(Normalize(display), out rule!);
    }

    private static bool IsHeader(Dictionary<int, string> headers)
    {
        var values = headers.Values.ToHashSet(StringComparer.Ordinal);
        return values.Contains("инн") &&
               values.Contains("наименование ккт") &&
               values.Contains("адрес места установки ккт") &&
               values.Contains("смена") &&
               values.Contains("закрыта") &&
               values.Contains("сумма выручки") &&
               values.Contains("сумма выручки наличными") &&
               values.Contains("сумма выручки безналичными");
    }

    private static Dictionary<int, string> ReadHeader(Row row, string[] shared)
    {
        var result = new Dictionary<int, string>();
        foreach (var cell in row.Elements<Cell>())
        {
            var value = Normalize(ReadCell(cell, shared));
            if (value.Length > 0) result[ColumnIndex(cell)] = value;
        }
        return result;
    }

    private static Dictionary<string, string> ReadRow(Row row, IReadOnlyDictionary<int, string> headers, string[] shared)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var cell in row.Elements<Cell>())
            if (headers.TryGetValue(ColumnIndex(cell), out var header))
                result[header] = ReadCell(cell, shared);
        return result;
    }

    private static string Get(IReadOnlyDictionary<string, string> values, string key)
        => values.TryGetValue(key, out var value) ? value ?? string.Empty : string.Empty;

    private static int ColumnIndex(Cell cell)
    {
        var reference = cell.CellReference?.Value ?? string.Empty;
        var value = 0;
        foreach (var ch in reference)
        {
            if (!char.IsLetter(ch)) break;
            value = value * 26 + char.ToUpperInvariant(ch) - 'A' + 1;
        }
        return Math.Max(0, value - 1);
    }

    private static string ReadCell(Cell cell, string[] shared)
    {
        if (cell.DataType?.Value == CellValues.SharedString &&
            int.TryParse(cell.CellValue?.InnerText, out var index) &&
            index >= 0 && index < shared.Length)
            return shared[index];

        if (cell.DataType?.Value == CellValues.InlineString)
            return cell.InlineString?.InnerText ?? string.Empty;

        return cell.CellValue?.InnerText ?? cell.InnerText ?? string.Empty;
    }

    private static bool TryDateTime(string value, out DateTimeOffset result)
    {
        result = default;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial))
        {
            try
            {
                var date = DateTime.SpecifyKind(DateTime.FromOADate(serial), DateTimeKind.Unspecified);
                result = new DateTimeOffset(date, TimeZoneInfo.Local.GetUtcOffset(date));
                return true;
            }
            catch
            {
                return false;
            }
        }

        if (!DateTime.TryParse(value, CultureInfo.GetCultureInfo("ru-RU"), DateTimeStyles.AllowWhiteSpaces, out var parsed) &&
            !DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out parsed))
            return false;

        parsed = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
        result = new DateTimeOffset(parsed, TimeZoneInfo.Local.GetUtcOffset(parsed));
        return true;
    }

    private static bool TryInt(string value, out int number)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return true;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var raw))
        {
            number = checked((int)Math.Round(raw, MidpointRounding.AwayFromZero));
            return true;
        }
        return false;
    }

    private static bool TryAmount(string value, out decimal amount)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            amount = 0m;
            return true;
        }

        return decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out amount) ||
               decimal.TryParse(value, NumberStyles.Any, CultureInfo.GetCultureInfo("ru-RU"), out amount);
    }

    private static string ShiftId(string taxId, string serial, string rnm, int shift, DateTimeOffset closedAt)
        => HashText(string.Join("|", Digits(taxId), serial, rnm, shift.ToString(CultureInfo.InvariantCulture),
            closedAt.ToString("O", CultureInfo.InvariantCulture)));

    private static void AddMessage(FirstOfdShiftImportSummary summary, string message)
    {
        if (summary.Messages.Count < 12 && !summary.Messages.Contains(message, StringComparer.Ordinal))
            summary.Messages.Add(message);
    }

    private static string Normalize(string value)
        => Regex.Replace((value ?? string.Empty).Replace('\u00A0', ' ').Trim().ToLowerInvariant().Replace('ё', 'е'), @"\s+", " ");

    private static string Digits(string value)
        => new((value ?? string.Empty).Where(char.IsDigit).ToArray());

    private static string HashText(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
