using KopCashDesk.Core;
using KopCashDesk.Data;
using Xunit;

namespace KopCashDesk.Tests;

public sealed class SberDuplicateRepairTests
{
    [Fact]
    public void Repair_RemovesCopiesFromOverlappingSberDocuments()
    {
        var root = Path.Combine(Path.GetTempPath(), "KopCashDesk.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var db = new Database(Path.Combine(root, "cashdesk.db"));
        db.Initialize();

        var org = new Organization(Guid.NewGuid(), "ООО ЦОП", "6683009222");
        var loc = new Location(Guid.NewGuid(), org.Id, "Чапаева 28", "");
        db.Save(org);
        db.Save(loc);

        var oldDoc = db.RegisterSourceDocument("Sber.Acquiring", "old", "old", "old.xlsx");
        var newDoc = db.RegisterSourceDocument("Sber.Acquiring", "new", "new", "new.xlsx");
        var at = new DateTimeOffset(2026, 9, 10, 12, 30, 0, TimeSpan.FromHours(5));

        Assert.True(db.Insert(Bank("legacy-a", org.Id, loc.Id, at, 11325m, oldDoc)));
        Assert.True(db.Insert(Bank("new-a", org.Id, loc.Id, at, 11325m, newDoc)));

        Assert.Equal(2, db.Operations(org.Id).Count(x => x.Source == "Sber.Acquiring"));

        var removed = db.RepairSberOverlappingImports();

        Assert.Equal(1, removed);
        var remaining = db.Operations(org.Id).Where(x => x.Source == "Sber.Acquiring").ToArray();
        Assert.Single(remaining);
        Assert.Equal(11325m, remaining[0].Amount);
    }

    [Fact]
    public void Repair_PreservesMultiplicityInsideOneReport()
    {
        var root = Path.Combine(Path.GetTempPath(), "KopCashDesk.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var db = new Database(Path.Combine(root, "cashdesk.db"));
        db.Initialize();

        var org = new Organization(Guid.NewGuid(), "ООО ЦОП", "6683009222");
        var loc = new Location(Guid.NewGuid(), org.Id, "Точка", "");
        db.Save(org);
        db.Save(loc);

        var oldDoc = db.RegisterSourceDocument("Sber.Acquiring", "old2", "old2", "old.xlsx");
        var newDoc = db.RegisterSourceDocument("Sber.Acquiring", "new2", "new2", "new.xlsx");
        var at = new DateTimeOffset(2026, 9, 10, 12, 30, 0, TimeSpan.FromHours(5));

        // Two real equal payments happened in the same second. Both reports contain both.
        Assert.True(db.Insert(Bank("old-1", org.Id, loc.Id, at, 500m, oldDoc)));
        Assert.True(db.Insert(Bank("old-2", org.Id, loc.Id, at, 500m, oldDoc)));
        Assert.True(db.Insert(Bank("new-1", org.Id, loc.Id, at, 500m, newDoc)));
        Assert.True(db.Insert(Bank("new-2", org.Id, loc.Id, at, 500m, newDoc)));

        var removed = db.RepairSberOverlappingImports();

        Assert.Equal(2, removed);
        var remaining = db.Operations(org.Id).Where(x => x.Source == "Sber.Acquiring").ToArray();
        Assert.Equal(2, remaining.Length);
        Assert.Equal(1000m, remaining.Sum(x => x.Amount));
    }

    private static CashOperation Bank(
        string externalId,
        Guid organizationId,
        Guid locationId,
        DateTimeOffset occurredAt,
        decimal amount,
        string documentId) =>
        new(
            "Sber.Acquiring",
            externalId,
            organizationId,
            locationId,
            occurredAt,
            SourceKind.Bank,
            OperationKind.Sale,
            PaymentKind.Electronic,
            amount,
            documentId);
}
