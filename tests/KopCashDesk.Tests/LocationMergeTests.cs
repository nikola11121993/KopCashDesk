using KopCashDesk.Core;
using KopCashDesk.Data;
using Xunit;

namespace KopCashDesk.Tests;

public sealed class LocationMergeTests
{
    [Fact]
    public void MergeLocations_MovesOperationsTerminalsShiftsAndManualCashToTarget()
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
        db.SaveRegisterBinding(new RegisterBinding(Guid.NewGuid(), organization.Id, source.Id, "9287440300123456", "000123"));
        db.SetManualCashFromBank(organization.Id, source.Id, new DateOnly(2026, 9, 1), 1000m);
        db.Insert(new CashOperation("Sber.Acquiring", "op-1", organization.Id, source.Id,
            new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.FromHours(5)), SourceKind.Bank,
            OperationKind.Sale, PaymentKind.Electronic, 1000m));
        db.Save(new ShiftClosure("Test", "shift-1", organization.Id, source.Id,
            new DateTimeOffset(2026, 9, 1, 20, 0, 0, TimeSpan.FromHours(5)), 1200m, 200m, 1000m));

        var merged = db.MergeLocations(source.Id, target.Id, "test");

        Assert.True(merged);
        Assert.DoesNotContain(db.Locations(), x => x.Id == source.Id);
        Assert.Equal(target.Id, Assert.Single(db.TerminalBindings()).LocationId);
        Assert.Equal(target.Id, Assert.Single(db.RegisterBindings()).LocationId);
        Assert.Equal(target.Id, Assert.Single(db.ManualCashPostings(organization.Id, 2026, 9, target.Id)).LocationId);
        var summary = Assert.Single(db.PointDaySummaries(organization.Id, 2026, 9, target.Id));
        Assert.Equal(1000m, summary.BankElectronic);
        Assert.Equal(1200m, summary.ShiftTotal);
    }

    [Fact]
    public void MergeLocations_StopsWhenManualCashForSameDayConflicts()
    {
        var root = Path.Combine(Path.GetTempPath(), "KopCashDesk.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var db = new Database(Path.Combine(root, "cashdesk.db"));
        db.Initialize();

        var organization = new Organization(Guid.NewGuid(), "ООО Тест", "6600000000");
        var source = new Location(Guid.NewGuid(), organization.Id, "Дубль", "Адрес");
        var target = new Location(Guid.NewGuid(), organization.Id, "Основная", "Адрес");
        db.Save(organization);
        db.Save(source);
        db.Save(target);
        var date = new DateOnly(2026, 9, 2);
        db.SetManualCashFromBank(organization.Id, source.Id, date, 900m);
        db.SetManualCashFromBank(organization.Id, target.Id, date, 1000m);

        var error = Assert.Throws<InvalidOperationException>(() => db.MergeLocations(source.Id, target.Id, "test conflict"));

        Assert.Contains("разные ручные суммы", error.Message);
        Assert.Contains(db.Locations(), x => x.Id == source.Id);
        Assert.Contains(db.Locations(), x => x.Id == target.Id);
    }
}
