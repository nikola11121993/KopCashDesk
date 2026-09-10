using KopCashDesk.Core;
using KopCashDesk.Data;
using Xunit;

namespace KopCashDesk.Tests;

public sealed class ManualCashPostingTests
{
    [Fact]
    public void ManualPosting_CanBeSetUpdatedAndCleared()
    {
        var root = Path.Combine(Path.GetTempPath(), "KopCashDesk.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var db = new Database(Path.Combine(root, "cashdesk.db"));
        db.Initialize();

        var organization = new Organization(Guid.NewGuid(), "ООО Гарант", "6600000000");
        var location = new Location(Guid.NewGuid(), organization.Id, "Пышма Верхняя", "Адрес 1");
        db.Save(organization);
        db.Save(location);

        var date = new DateOnly(2026, 9, 10);
        db.SetManualCash(organization.Id, location.Id, date, 1234.567m);
        var first = Assert.Single(db.ManualCashPostings(organization.Id, 2026, 9, location.Id));
        Assert.Equal(1234.57m, first.Electronic);

        db.SetManualCash(organization.Id, location.Id, date, 1500m);
        var updated = Assert.Single(db.ManualCashPostings(organization.Id, 2026, 9, location.Id));
        Assert.Equal(1500m, updated.Electronic);

        db.ClearManualCash(organization.Id, location.Id, date);
        Assert.Empty(db.ManualCashPostings(organization.Id, 2026, 9, location.Id));
    }
}
