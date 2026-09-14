using KopCashDesk.Core;
using KopCashDesk.Data;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace KopCashDesk.Desktop;

public sealed class CrptImportSummary
{
    public int FilesProcessed { get; internal set; }
    public int FilesFailed { get; internal set; }
    public int RecordsRead { get; internal set; }
    public int ReceiptsRead { get; internal set; }
    public int OperationsInserted { get; internal set; }
    public int OperationsUpdated { get; internal set; }
    public int ReceiptsSkippedBecauseTaxcomExists { get; internal set; }
    public int LocationsCreated { get; internal set; }
    public decimal CashTotal { get; internal set; }
    public decimal ElectronicTotal { get; internal set; }
    public List<string> Messages { get; } = [];

    public string ToDisplayText()
    {
        var text =
            $"CRPT файлов: {FilesProcessed}\n" +
            $"Записей прочитано: {RecordsRead}\n" +
            $"Чеков прочитано: {ReceiptsRead}\n" +
            $"Операций добавлено: {OperationsInserted}\n" +
            $"Операций обновлено: {OperationsUpdated}\n" +
            $"Чеков пропущено из-за уже загруженного Такском: {ReceiptsSkippedBecauseTaxcomExists}\n" +
            $"Новых точек: {LocationsCreated}\n\n" +
            $"Наличные: {CashTotal:N2} ₽\n" +
            $"Безнал: {ElectronicTotal:N2} ₽";
        if (FilesFailed > 0) text += $"\nОшибок файлов: {FilesFailed}";
        if (Messages.Count > 0) text += "\n\n" + string.Join("\n", Messages.Take(12));
        return text;
    }
}

public sealed class CrptArchiveImporter
{
    private const string Source = "CRPT.Archive";
    private const long MaxFileBytes = 250L * 1024 * 1024;
    private readonly Database _database;

    private sealed record RegistrationInfo(
        string TaxId,
        string FiscalDrive,
        string RegisterNumber,
        string SerialNumber,
        string Address,
        string PointName);

    private sealed record Receipt(
        string TaxId,
        string FiscalDrive,
        string RegisterNumber,
        string SerialNumber,
        string Address,
        string PointName,
        int ShiftNumber,
        long FiscalDocumentNumber,
        DateTimeOffset OccurredAt,
        OperationKind Kind,
        decimal Cash,
        decimal Electronic);

    public CrptArchiveImporter(Database database) => _database = database;

    public CrptImportSummary ImportFiles(IEnumerable<string> paths)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var summary = new CrptImportSummary();

        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                ImportFile(path, summary);
                summary.FilesProcessed++;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or OverflowException)
            {
                summary.FilesFailed++;
                summary.Messages.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        var repaired = _database.EnsureV052Fixes();
        if (repaired > 0)
            summary.Messages.Add($"После импорта удалено задвоенных кассовых записей: {repaired}.");

        _database.Audit("crpt.import",
            $"files={summary.FilesProcessed}; receipts={summary.ReceiptsRead}; inserted={summary.OperationsInserted}; updated={summary.OperationsUpdated}; skipped_taxcom={summary.ReceiptsSkippedBecauseTaxcomExists}; failed={summary.FilesFailed}");
        return summary;
    }

    private void ImportFile(string path, CrptImportSummary summary)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Файл не найден.", path);
        if (!Path.GetExtension(path).Equals(".crpt", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Поддерживается архив ККТ с расширением .crpt.");

        var info = new FileInfo(path);
        if (info.Length <= 0) throw new InvalidDataException("CRPT-файл пустой.");
        if (info.Length > MaxFileBytes) throw new InvalidDataException("CRPT-файл больше 250 МБ.");

        var bytes = File.ReadAllBytes(path);
        var records = ReadRecords(bytes);
        summary.RecordsRead += records.Count;

        RegistrationInfo registration = new("", "", "", "", "", "");
        var receipts = new List<Receipt>();

        foreach (var record in records)
        {
            var fields = ReadTlvs(record.Payload);

            registration = registration with
            {
                TaxId = Text(fields, 1018, registration.TaxId),
                FiscalDrive = Text(fields, 1041, registration.FiscalDrive),
                RegisterNumber = Text(fields, 1037, registration.RegisterNumber),
                SerialNumber = Text(fields, 1013, registration.SerialNumber),
                Address = Text(fields, 1009, registration.Address),
                PointName = Text(fields, 1187, registration.PointName)
            };

            if (record.Type != 3) continue;

            var timestamp = Unsigned(fields, 1012);
            var shift = checked((int)Unsigned(fields, 1038));
            var fiscalDocument = checked((long)Unsigned(fields, 1040));
            var calculationSign = checked((int)Unsigned(fields, 1054));
            if (timestamp <= 0 || shift <= 0 || fiscalDocument <= 0 || calculationSign is < 1 or > 4)
                continue;

            var multiplier = calculationSign is 1 or 4 ? 1m : -1m;
            var kind = calculationSign switch
            {
                1 => OperationKind.Sale,
                2 => OperationKind.Return,
                3 => OperationKind.Correction,
                4 => OperationKind.Correction,
                _ => OperationKind.Unknown
            };

            var receipt = new Receipt(
                Text(fields, 1018, registration.TaxId),
                Text(fields, 1041, registration.FiscalDrive),
                Text(fields, 1037, registration.RegisterNumber),
                registration.SerialNumber,
                registration.Address,
                registration.PointName,
                shift,
                fiscalDocument,
                DateTimeOffset.FromUnixTimeSeconds(checked((long)timestamp)),
                kind,
                Money.Normalize(multiplier * Money.FromKopecks(checked((long)Unsigned(fields, 1031)))),
                Money.Normalize(multiplier * Money.FromKopecks(checked((long)Unsigned(fields, 1081)))));

            receipts.Add(receipt);
        }

        if (receipts.Count == 0)
            throw new InvalidDataException("В CRPT не найдено кассовых чеков ФФД.");

        summary.ReceiptsRead += receipts.Count;
        var fileHash = HashFile(path);
        var documentId = _database.RegisterSourceDocument(Source, fileHash, fileHash, Path.GetFileName(path));

        var organizations = _database.Organizations().ToArray();
        var locations = _database.Locations().ToList();
        var registers = _database.RegisterBindings().ToList();
        var resolved = new Dictionary<string, (Organization Organization, Location Location)>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in receipts.GroupBy(x => $"{DigitsOnly(x.TaxId)}|{DigitsOnly(x.FiscalDrive)}|{x.ShiftNumber}"))
        {
            var sample = group.First();
            var taxId = DigitsOnly(sample.TaxId);
            var organization = organizations.SingleOrDefault(x => DigitsOnly(x.TaxId) == taxId)
                ?? throw new InvalidDataException($"ИНН {taxId} из CRPT не найден среди организаций программы.");

            var resolveKey = $"{organization.Id:N}|{DigitsOnly(sample.FiscalDrive)}";
            if (!resolved.TryGetValue(resolveKey, out var target))
            {
                var location = ResolveLocation(organization, sample, locations, registers, summary);
                target = (organization, location);
                resolved[resolveKey] = target;
            }

            var dates = group.Select(x => DateOnly.FromDateTime(x.OccurredAt.UtcDateTime)).Distinct().ToArray();
            var taxcomExists = dates.Any(date => _database.ShiftClosures(target.Organization.Id, target.Location.Id, date)
                .Any(x => x.Source == "Taxcom.ShiftReport"
                          && DigitsOnly(x.FiscalDriveNumber) == DigitsOnly(sample.FiscalDrive)
                          && x.ShiftNumber == sample.ShiftNumber));

            if (taxcomExists)
            {
                summary.ReceiptsSkippedBecauseTaxcomExists += group.Count();
                continue;
            }

            foreach (var receipt in group.OrderBy(x => x.OccurredAt).ThenBy(x => x.FiscalDocumentNumber))
            {
                if (receipt.Cash != 0m)
                    SaveOperation(receipt, target.Organization, target.Location, PaymentKind.Cash, receipt.Cash, documentId, summary);
                if (receipt.Electronic != 0m)
                    SaveOperation(receipt, target.Organization, target.Location, PaymentKind.Electronic, receipt.Electronic, documentId, summary);

                summary.CashTotal += receipt.Cash;
                summary.ElectronicTotal += receipt.Electronic;
            }
        }
    }

    private Location ResolveLocation(
        Organization organization,
        Receipt sample,
        List<Location> locations,
        List<RegisterBinding> registers,
        CrptImportSummary summary)
    {
        var fn = DigitsOnly(sample.FiscalDrive);
        var existingBinding = registers.FirstOrDefault(x =>
            x.OrganizationId == organization.Id && DigitsOnly(x.FiscalDriveNumber) == fn);
        if (existingBinding is not null)
        {
            var bound = locations.FirstOrDefault(x => x.Id == existingBinding.LocationId);
            if (bound is not null) return bound;
        }

        var organizationLocations = locations.Where(x => x.OrganizationId == organization.Id).ToArray();
        Location? location = null;
        var knownPoint = KnownBusinessRules.PointNameForRegisterSerial(sample.SerialNumber);
        var source = BindingSource.Automatic;
        var locked = false;

        if (!string.IsNullOrWhiteSpace(knownPoint))
        {
            location = KnownBusinessRules.FindKnownPoint(organizationLocations, knownPoint);
            source = BindingSource.Rule;
            locked = true;
        }

        var addressKey = AddressKey(sample.Address);
        if (location is null && !string.IsNullOrWhiteSpace(addressKey))
        {
            var byAddress = organizationLocations
                .Where(x =>
                {
                    var existing = AddressKey(x.Address);
                    return existing.Length > 0 &&
                           (existing == addressKey || existing.Contains(addressKey, StringComparison.Ordinal)
                            || addressKey.Contains(existing, StringComparison.Ordinal));
                })
                .ToArray();
            if (byAddress.Length == 1) location = byAddress[0];
        }

        if (location is null && !string.IsNullOrWhiteSpace(sample.Address))
        {
            var normalizedAddress = SberAcquiringImporter.NormalizeForMatch(sample.Address);
            var byNameInAddress = organizationLocations
                .Where(x =>
                {
                    var name = SberAcquiringImporter.NormalizeForMatch(x.Name);
                    return name.Length >= 4 && normalizedAddress.Contains(name, StringComparison.Ordinal);
                })
                .ToArray();
            if (byNameInAddress.Length == 1) location = byNameInAddress[0];
        }

        if (location is null && !string.IsNullOrWhiteSpace(sample.PointName))
        {
            var pointKey = SberAcquiringImporter.NormalizeForMatch(sample.PointName);
            var byName = organizationLocations
                .Where(x => SberAcquiringImporter.NormalizeForMatch(x.Name) == pointKey)
                .ToArray();
            if (byName.Length == 1) location = byName[0];
        }

        if (location is null)
        {
            var name = !string.IsNullOrWhiteSpace(knownPoint)
                ? knownPoint
                : !string.IsNullOrWhiteSpace(sample.PointName)
                    ? sample.PointName.Trim()
                    : !string.IsNullOrWhiteSpace(sample.SerialNumber)
                        ? $"ККТ {sample.SerialNumber.Trim()}"
                        : $"ККТ {sample.FiscalDrive.Trim()}";
            location = new Location(Guid.NewGuid(), organization.Id, name, sample.Address.Trim(), false);
            _database.Save(location);
            locations.Add(location);
            summary.LocationsCreated++;
        }

        if (!string.IsNullOrWhiteSpace(fn))
        {
            var binding = new RegisterBinding(
                existingBinding?.Id ?? Guid.NewGuid(),
                organization.Id,
                location.Id,
                fn,
                DigitsOnly(sample.RegisterNumber),
                source,
                locked);
            _database.SaveRegisterBinding(binding);
            registers.RemoveAll(x => x.OrganizationId == organization.Id && DigitsOnly(x.FiscalDriveNumber) == fn);
            registers.Add(binding);
        }

        return location;
    }

    private void SaveOperation(
        Receipt receipt,
        Organization organization,
        Location location,
        PaymentKind payment,
        decimal amount,
        string documentId,
        CrptImportSummary summary)
    {
        var externalId =
            $"receipt:{DigitsOnly(receipt.FiscalDrive)}:{receipt.ShiftNumber}:{receipt.FiscalDocumentNumber}:{payment}";
        var inserted = _database.UpsertFiscalOperation(new CashOperation(
            Source,
            externalId,
            organization.Id,
            location.Id,
            receipt.OccurredAt,
            SourceKind.Fiscal,
            receipt.Kind,
            payment,
            amount,
            documentId));

        if (inserted) summary.OperationsInserted++;
        else summary.OperationsUpdated++;
    }

    private sealed record RawRecord(ushort Type, byte[] Payload);

    private static IReadOnlyList<RawRecord> ReadRecords(byte[] bytes)
    {
        if (bytes.Length < 12 || bytes[0] != (byte)'C' || bytes[1] != (byte)'R' || bytes[2] != (byte)'P' || bytes[3] != (byte)'T')
            throw new InvalidDataException("Файл не имеет сигнатуру CRPT.");

        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4));
        if (count == 0 || count > 500_000) throw new InvalidDataException("Некорректное количество записей CRPT.");

        var offsetCount = checked((int)count - 1);
        var dataStart = checked(12 + offsetCount * 4);
        if (dataStart > bytes.Length) throw new InvalidDataException("Повреждена таблица смещений CRPT.");

        var ends = new List<int>(checked((int)count));
        var previous = 0;
        for (var i = 0; i < offsetCount; i++)
        {
            var end = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12 + i * 4, 4)));
            if (end <= previous || dataStart + end > bytes.Length)
                throw new InvalidDataException("Повреждена таблица записей CRPT.");
            ends.Add(end);
            previous = end;
        }
        ends.Add(bytes.Length - dataStart);

        var result = new List<RawRecord>(checked((int)count));
        var start = 0;
        foreach (var end in ends)
        {
            var length = end - start;
            if (length < 4) throw new InvalidDataException("Повреждена запись CRPT.");
            var span = bytes.AsSpan(dataStart + start, length);
            var type = BinaryPrimitives.ReadUInt16LittleEndian(span[..2]);
            var payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(2, 2));
            if (payloadLength > length - 4) throw new InvalidDataException("Некорректная длина записи CRPT.");
            result.Add(new RawRecord(type, span.Slice(4, payloadLength).ToArray()));
            start = end;
        }
        return result;
    }

    private static Dictionary<ushort, byte[]> ReadTlvs(byte[] payload)
    {
        var result = new Dictionary<ushort, byte[]>();
        var position = 0;
        while (position + 4 <= payload.Length)
        {
            var tag = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(position, 2));
            var length = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(position + 2, 2));
            position += 4;
            if (position + length > payload.Length) break;
            result[tag] = payload.AsSpan(position, length).ToArray();
            position += length;
        }
        return result;
    }

    private static ulong Unsigned(IReadOnlyDictionary<ushort, byte[]> fields, ushort tag)
    {
        if (!fields.TryGetValue(tag, out var bytes) || bytes.Length == 0) return 0;
        if (bytes.Length > 8) return 0;
        ulong result = 0;
        for (var i = 0; i < bytes.Length; i++) result |= (ulong)bytes[i] << (8 * i);
        return result;
    }

    private static string Text(IReadOnlyDictionary<ushort, byte[]> fields, ushort tag, string fallback = "")
    {
        if (!fields.TryGetValue(tag, out var bytes) || bytes.Length == 0) return fallback;
        var ascii = bytes.All(x => x is >= 0x20 and <= 0x7E);
        var encoding = ascii ? Encoding.ASCII : Encoding.GetEncoding(866);
        return encoding.GetString(bytes).Trim('\0', ' ');
    }

    private static string AddressKey(string value)
    {
        var normalized = SberAcquiringImporter.NormalizeForMatch(value);
        if (string.IsNullOrWhiteSpace(normalized)) return string.Empty;
        var ignored = new HashSet<string>(StringComparer.Ordinal)
        {
            "свердловская", "обл", "область", "г", "город", "ул", "улица", "д", "дом",
            "россия", "рф"
        };
        return string.Join(" ", normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => !(x.Length == 6 && x.All(char.IsDigit)))
            .Where(x => !ignored.Contains(x)));
    }

    private static string DigitsOnly(string value) => new(value.Where(char.IsDigit).ToArray());

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
