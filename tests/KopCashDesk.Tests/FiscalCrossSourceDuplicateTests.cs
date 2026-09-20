using KopCashDesk.Core;
using KopCashDesk.Data;
using Xunit;

namespace KopCashDesk.Tests;

public sealed class FiscalCrossSourceDuplicateTests
{
    [Fact]
    public void Reftinskaya_0607_KeepsBothSources_ButFinancialSummaryCounts1233734Once()
    {
        var root = Path.Combine(Path.GetTempPath(), "KopCashDesk.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var db = new Database(Path.Combine(root, "cashdesk.db"));
        db.Initialize();

        var organization = new Organization(Guid.NewGuid(), "ООО ЦОП", "6683009222");
        var location = new Location(Guid.NewGuid(), organization.Id, "Рефтинская ГРЭС", "здание 6");
        db.Save(organization);
        db.Save(location);

        var taxcomClosedAt = new DateTimeOffset(2026, 7, 6, 14, 46, 0, TimeSpan.FromHours(5));
        var frontolClosedAt = taxcomClosedAt.AddSeconds(71);
        const string taxcomExternal = "taxcom-shift-2";
        var frontolExternal = $"{organization.Id:N}:{location.Id:N}:1:2";

        db.Save(new ShiftClosure(
            "Taxcom.ShiftReport", taxcomExternal, organization.Id, location.Id,
            taxcomClosedAt, 1233734m, 0m, 1233734m, "7381440800477733", 2));
        db.UpsertFiscalOperation(new CashOperation(
            "Taxcom.ShiftReport", taxcomExternal + ":electronic", organization.Id, location.Id,
            taxcomClosedAt, SourceKind.Fiscal, OperationKind.Sale, PaymentKind.Electronic, 1233734m));

        db.Save(new ShiftClosure(
            "Frontol.Report", frontolExternal, organization.Id, location.Id,
            frontolClosedAt, 1233734m, 0m, 1233734m, "", 2));
        db.UpsertFiscalOperation(new CashOperation(
            "Frontol.Report", frontolExternal + ":electronic", organization.Id, location.Id,
            frontolClosedAt, SourceKind.Fiscal, OperationKind.Sale, PaymentKind.Electronic, 1233734m));

        var matching = db.RebuildCrossSourceShiftMatches();

        Assert.Equal(1, matching.MatchedPairs);
        Assert.Equal(2, db.ShiftClosures(organization.Id, location.Id, new DateOnly(2026, 7, 6)).Count);
        Assert.Single(db.CrossSourceShiftLinks());
        var day = Assert.Single(db.CanonicalPointDaySummaries(organization.Id, 2026, 7, location.Id));
        Assert.Equal(1233734m, day.FiscalElectronic);
        Assert.Equal(1233734m, day.ShiftTotal);
        Assert.Equal(1, day.ShiftCount);
        Assert.Equal("Taxcom (основной) + Frontol (проверка)", day.FiscalSources);
    }
}
