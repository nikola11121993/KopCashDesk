using KopCashDesk.Core;
using KopCashDesk.Data;
using Xunit;

namespace KopCashDesk.Tests;

public sealed class ManualStandaloneDayTests
{
    [Fact]
    public void ManualCash_CreatesDayWithoutImportingFiscalSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "KopCashDesk.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var db = new Database(Path.Combine(root, "cashdesk.db"));
        db.Initialize();

        var organization = new Organization(Guid.NewGuid(), "ООО ЦОП", "6683009222");
        var location = new Location(Guid.NewGuid(), organization.Id, "Рефтинская ГРЭС", "здание 6");
        db.Save(organization);
        db.Save(location);

        var date = new DateOnly(2026, 7, 12);
        db.SetManualCash(organization.Id, location.Id, date, 76123.45m);

        var day = Assert.Single(db.CanonicalPointDaySummaries(organization.Id, 2026, 7, location.Id));
        Assert.Equal(date, day.Date);
        Assert.Null(day.BankElectronic);
        Assert.Null(day.FiscalElectronic);
        Assert.Null(day.ShiftTotal);
        Assert.Equal(0, day.ShiftCount);
        Assert.Equal(string.Empty, day.FiscalSources);

        var manual = Assert.Single(db.ManualCashPostings(organization.Id, 2026, 7, location.Id));
        Assert.Equal(date, manual.Date);
        Assert.Equal(76123.45m, manual.Electronic);
        Assert.Empty(db.ShiftClosures(organization.Id, location.Id, date));
        Assert.Empty(db.Operations(organization.Id));
    }
}
