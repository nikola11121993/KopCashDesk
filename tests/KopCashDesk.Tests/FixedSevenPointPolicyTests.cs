using KopCashDesk.Core;
using KopCashDesk.Data;
using KopCashDesk.Desktop;
using Microsoft.Data.Sqlite;
using Xunit;

namespace KopCashDesk.Tests;

public sealed class FixedSevenPointPolicyTests
{
    [Fact]
    public void CleanDatabase_HasExactlySevenConfirmedPoints()
    {
        using var f = new Fixture();

        SmartKnownRules.PrepareCleanDatabase(f.Db);

        var active = f.Db.Locations().Where(x => x.OrganizationId == f.Org.Id).ToArray();
        Assert.Equal(7, active.Length);
        Assert.Equal(
            KnownBusinessRules.FixedPointNames.OrderBy(x => x),
            active.Select(x => x.Name).OrderBy(x => x));
    }

    [Fact]
    public void LegacyNames_AreMergedIntoSevenConfirmedPoints()
    {
        using var f = new Fixture();
        f.Db.Save(new Location(Guid.NewGuid(), f.Org.Id, "ЗАВОД АТИ"));
        f.Db.Save(new Location(Guid.NewGuid(), f.Org.Id, "Колледж искусств"));
        f.Db.Save(new Location(Guid.NewGuid(), f.Org.Id, "Вороний Брод"));
        f.Db.Save(new Location(Guid.NewGuid(), f.Org.Id, "Ленинградская 1"));
        f.Db.Save(new Location(Guid.NewGuid(), f.Org.Id, "Мира 4"));

        SmartKnownRules.PrepareCleanDatabase(f.Db);

        Assert.Equal(7, f.Db.Locations().Count(x => x.OrganizationId == f.Org.Id));
        var all = f.Db.Locations(includeInactive: true).Where(x => x.OrganizationId == f.Org.Id).ToArray();
        Assert.All(all.Where(x => x.Name is "ЗАВОД АТИ" or "Колледж искусств" or "Вороний Брод" or "Ленинградская 1" or "Мира 4"), x => Assert.False(x.IsActive));
    }

    [Fact]
    public void UnexpectedEighthPoint_IsDeactivatedWithoutDeletingItsData()
    {
        using var f = new Fixture();
        var extra = new Location(Guid.NewGuid(), f.Org.Id, "Случайно созданная восьмая точка");
        f.Db.Save(extra);
        f.Db.Insert(new CashOperation(
            "Legacy", "extra-op", f.Org.Id, extra.Id,
            new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero),
            SourceKind.Bank, OperationKind.Sale, PaymentKind.Electronic, 123m));

        SmartKnownRules.PrepareCleanDatabase(f.Db);

        Assert.Equal(7, f.Db.Locations().Count(x => x.OrganizationId == f.Org.Id));
        var stored = Assert.Single(f.Db.Locations(includeInactive: true), x => x.Id == extra.Id);
        Assert.False(stored.IsActive);
        Assert.Equal(123m, f.Db.SumOperations("Legacy", f.Org.Id));
    }

    [Fact]
    public void SevenKktSerials_AreHardMapped_And9015DoesNotExist()
    {
        Assert.Equal(KnownBusinessRules.ReftinskayaPointName, KnownBusinessRules.PointNameForRegisterSerial("00106900361561"));
        Assert.Equal(KnownBusinessRules.LadyzhenskogoPointName, KnownBusinessRules.PointNameForRegisterSerial("00108202518113"));
        Assert.Equal(KnownBusinessRules.MiraPointName, KnownBusinessRules.PointNameForRegisterSerial("00178945"));
        Assert.Equal(KnownBusinessRules.AtiAppetitPointName, KnownBusinessRules.PointNameForRegisterSerial("00301000370264"));
        Assert.Equal(KnownBusinessRules.AtiMercuryPointName, KnownBusinessRules.PointNameForRegisterSerial("08050950"));
        Assert.Equal(KnownBusinessRules.ChapaevaPointName, KnownBusinessRules.PointNameForRegisterSerial("08052160"));
        Assert.Equal(KnownBusinessRules.MusicCollegePointName, KnownBusinessRules.PointNameForRegisterSerial("00178241"));
        Assert.False(KnownBusinessRules.IsKnownRegisterSerial("00179015"));
        Assert.Null(KnownBusinessRules.PointNameForRegisterSerial("00179015"));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "fixed-seven-" + Guid.NewGuid().ToString("N"));
        public Database Db { get; }
        public Organization Org { get; }

        public Fixture()
        {
            Directory.CreateDirectory(root);
            Db = new Database(Path.Combine(root, "cash.db"));
            Db.Initialize();
            Org = new Organization(Guid.NewGuid(), "ООО ЦОП", KnownBusinessRules.CopTaxId);
            Db.Save(Org);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); }
            catch { }
        }
    }
}
