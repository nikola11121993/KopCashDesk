using KopCashDesk.Core;
using KopCashDesk.Data;
using Xunit;

namespace KopCashDesk.Tests;

public sealed class LocationMergeTests
{
    [Fact]
    public void MergeLocations_MovesOperationsTerminalsAndShiftsToTarget()
    {
        var root = Path.Combine(Path.GetTempPath(), "KopCashDesk.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var db = new Database(Path.Combine(root, "cashdesk.db"));
        db.Initialize();

        var organization = new Organization(Guid.NewGuid(), "ООО Тест", "6600000000");
        var source = new Location(Guid.NewGuid(), organization.Id, "Вороний Брод", "Советская, 14");
        var target = new Location(Guid.NewGuid(), organization.Id, "Мира 4", "Мира, 4");
        db.Save(organization);
        db.Save(source);
        db.Save(target);
        db.Save(new TerminalBinding(Guid.NewGuid(), organization.Id, source.Id, "Sber", "43151534", "", "POS"));
        db.Insert(new CashOperation("Sber.Acquiring", "op-1", organization.Id, source.Id,
            new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.FromHours(5)), SourceKind.Bank,
            OperationKind.Sale, PaymentKind.Electronic, 1000m));
        db.Save(new ShiftClosure("Test", "shift-1", organization.Id, source.Id,
            new DateTimeOffset(2026, 9, 1, 20, 0, 0, TimeSpan.FromHours(5)), 1200m, 200m, 1000m));

        var merged = db.MergeLocations(source.Id, target.Id, "test");

        Assert.True(merged);
        Assert.DoesNotContain(db.Locations(), x => x.Id == source.Id);
        Assert.Equal(target.Id, Assert.Single(db.TerminalBindings()).LocationId);
        var summary = Assert.Single(db.PointDaySummaries(organization.Id, 2026, 9, target.Id));
        Assert.Equal(1000m, summary.BankElectronic);
        Assert.Equal(1200m, summary.ShiftTotal);
    }
}
