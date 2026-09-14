using KopCashDesk.Core;
using KopCashDesk.Data;
using KopCashDesk.Desktop;
using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace KopCashDesk.Tests;

public sealed class CrptArchiveImportTests
{
    [Fact]
    public void CrptArchive_ImportsReceipts_AndMapsByPointName()
    {
        var root = Path.Combine(Path.GetTempPath(), "KopCashDesk.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "Arc_7381440800477733_1_8446.crpt");
        var db = new Database(Path.Combine(root, "cashdesk.db"));
        db.Initialize();

        var organization = new Organization(Guid.NewGuid(), "ООО ЦОП", "6683009222");
        var location = new Location(Guid.NewGuid(), organization.Id, "Mira 4", "Asbest, Mira 4");
        db.Save(organization);
        db.Save(location);

        CreateCrpt(file);

        var summary = new CrptArchiveImporter(db).ImportFiles([file]);

        Assert.Equal(1, summary.FilesProcessed);
        Assert.Equal(2, summary.ReceiptsRead);
        Assert.Equal(0, summary.LocationsCreated);
        Assert.Equal(800m, summary.ElectronicTotal);
        Assert.Equal(0m, summary.CashTotal);

        var day = Assert.Single(db.PointDaySummaries(organization.Id, 2026, 7, location.Id));
        Assert.Equal(800m, day.FiscalElectronic);

        var repeat = new CrptArchiveImporter(db).ImportFiles([file]);
        Assert.Equal(0, repeat.OperationsInserted);
        Assert.Equal(2, repeat.OperationsUpdated);
        Assert.Equal(800m, Assert.Single(db.PointDaySummaries(organization.Id, 2026, 7, location.Id)).FiscalElectronic);
    }

    private static void CreateCrpt(string path)
    {
        var fn = "7381440800477733";
        var register = "0005908318035040";
        var taxId = "6683009222";

        var registration = Record(11,
            TlvText(1041, fn),
            TlvText(1037, register),
            TlvText(1018, taxId),
            TlvText(1013, "00178945"),
            TlvText(1009, "Asbest, Mira 4"),
            TlvText(1187, "Mira 4"));

        var sale = ReceiptRecord(fn, register, taxId, 299, 8208, 1, 1000, 0, 1000,
            new DateTimeOffset(2026, 7, 8, 10, 0, 0, TimeSpan.Zero));
        var refund = ReceiptRecord(fn, register, taxId, 299, 8209, 2, 200, 0, 200,
            new DateTimeOffset(2026, 7, 8, 11, 0, 0, TimeSpan.Zero));

        var records = new[] { registration, sale, refund };
        using var stream = File.Create(path);
        stream.Write(Encoding.ASCII.GetBytes("CRPT"));

        Span<byte> four = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(four, (uint)records.Length);
        stream.Write(four);
        BinaryPrimitives.WriteUInt32LittleEndian(four, 0);
        stream.Write(four);

        var cumulative = 0;
        for (var i = 0; i < records.Length - 1; i++)
        {
            cumulative += records[i].Length;
            BinaryPrimitives.WriteUInt32LittleEndian(four, (uint)cumulative);
            stream.Write(four);
        }

        foreach (var record in records) stream.Write(record);
    }

    private static byte[] ReceiptRecord(
        string fn, string register, string taxId,
        int shift, int fiscalDocument, int calculationSign,
        long totalKopecks, long cashKopecks, long electronicKopecks,
        DateTimeOffset occurredAt)
    {
        return Record(3,
            TlvText(1041, fn),
            TlvText(1037, register),
            TlvText(1018, taxId),
            TlvNumber(1040, (ulong)fiscalDocument),
            TlvNumber(1012, (ulong)occurredAt.ToUnixTimeSeconds()),
            TlvNumber(1038, (ulong)shift),
            TlvNumber(1042, 1),
            TlvNumber(1054, (ulong)calculationSign),
            TlvNumber(1020, (ulong)totalKopecks),
            TlvNumber(1031, (ulong)cashKopecks),
            TlvNumber(1081, (ulong)electronicKopecks));
    }

    private static byte[] Record(ushort type, params byte[][] tlvs)
    {
        var payload = tlvs.SelectMany(x => x).ToArray();
        var result = new byte[payload.Length + 4];
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0, 2), type);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(2, 2), checked((ushort)payload.Length));
        payload.CopyTo(result, 4);
        return result;
    }

    private static byte[] TlvText(ushort tag, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        return Tlv(tag, bytes);
    }

    private static byte[] TlvNumber(ushort tag, ulong value)
    {
        var length = value switch
        {
            <= byte.MaxValue => 1,
            <= ushort.MaxValue => 2,
            <= uint.MaxValue => 4,
            _ => 8
        };
        var bytes = new byte[length];
        for (var i = 0; i < length; i++) bytes[i] = (byte)(value >> (8 * i));
        return Tlv(tag, bytes);
    }

    private static byte[] Tlv(ushort tag, byte[] value)
    {
        var result = new byte[value.Length + 4];
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0, 2), tag);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(2, 2), checked((ushort)value.Length));
        value.CopyTo(result, 4);
        return result;
    }
}
