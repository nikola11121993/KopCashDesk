using KopCashDesk.Core;
using KopCashDesk.Data;
using Xunit;

namespace KopCashDesk.Tests;

public sealed class SummaryTests
{
    [Fact]
    public void PointDaySummaries_ShowAllDaysAndKeepMissingFiscalAsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kopcashdesk-{Guid.NewGuid():N}.db");
        try
        {
            var db = new Database(path);
            db.Initialize();
            var organization = new Organization(Guid.NewGuid(), "ООО \"КОП\"", "6600000000");
            var location = new Location(Guid.NewGuid(), organization.Id, "Мира 4", "г. Асбест, ул. Мира, 4");
            db.Save(organization);
            db.Save(location);

            Assert.True(db.Insert(new CashOperation("Sber.Acquiring", "a", organization.Id, location.Id, new DateTimeOffset(2026, 1, 10, 10, 0, 0, TimeSpan.FromHours(5)), SourceKind.Bank, OperationKind.Sale, PaymentKind.Electronic, 100m)));
            Assert.True(db.Insert(new CashOperation("Sber.Acquiring", "b", organization.Id, location.Id, new DateTimeOffset(2026, 1, 10, 11, 0, 0, TimeSpan.FromHours(5)), SourceKind.Bank, OperationKind.Sale, PaymentKind.Electronic, 50m)));
            Assert.True(db.Insert(new CashOperation("Sber.Acquiring", "c", organization.Id, location.Id, new DateTimeOffset(2026, 2, 5, 12, 0, 0, TimeSpan.FromHours(5)), SourceKind.Bank, OperationKind.Sale, PaymentKind.Electronic, 75m)));

            var year = db.PointDaySummaries(organization.Id, 2026);
            Assert.Equal(2, year.Count);

            var january = db.PointDaySummaries(organization.Id, 2026, 1, location.Id);
            var day = Assert.Single(january);
            Assert.Equal(new DateOnly(2026, 1, 10), day.Date);
            Assert.Equal(150m, day.BankElectronic);
            Assert.Null(day.FiscalElectronic);
            Assert.Null(day.ShiftTotal);
            Assert.Equal(0, day.ShiftCount);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void PointDaySummaries_ShowShiftClosureAmountAndDate()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kopcashdesk-{Guid.NewGuid():N}.db");
        try
        {
            var db = new Database(path);
            db.Initialize();
            var organization = new Organization(Guid.NewGuid(), "ООО \"КОП\"", "6600000000");
            var location = new Location(Guid.NewGuid(), organization.Id, "Мира 4", "г. Асбест, ул. Мира, 4");
            db.Save(organization);
            db.Save(location);

            var closedAt = new DateTimeOffset(2026, 3, 2, 20, 15, 0, TimeSpan.FromHours(5));
            db.Save(new ShiftClosure("Taxcom", "shift-1", organization.Id, location.Id, closedAt, 12500m, 2500m, 10000m, "9999999999999999", 42));

            var row = Assert.Single(db.PointDaySummaries(organization.Id, 2026, 3, location.Id));
            Assert.Null(row.BankElectronic);
            Assert.Equal(12500m, row.ShiftTotal);
            Assert.Equal(2500m, row.ShiftCash);
            Assert.Equal(10000m, row.ShiftElectronic);
            Assert.Equal(1, row.ShiftCount);
            Assert.Equal(closedAt, row.LastShiftClosedAt);
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static void TryDelete(string path)
    {
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
        {
            try { if (File.Exists(file)) File.Delete(file); }
            catch { }
        }
    }
}
