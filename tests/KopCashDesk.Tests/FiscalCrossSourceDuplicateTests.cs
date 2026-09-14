using KopCashDesk.Core;
using KopCashDesk.Data;
using Xunit;

namespace KopCashDesk.Tests;

public sealed class FiscalCrossSourceDuplicateTests
{
    [Fact]
    public void V052Fix_RemovesFrontolCopy_WhenSameShiftAlreadyExistsInTaxcom()
    {
        var root = Path.Combine(Path.GetTempPath(), "KopCashDesk.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var db = new Database(Path.Combine(root, "cashdesk.db"));
        db.Initialize();

        var organization = new Organization(Guid.NewGuid(), "ООО ЦОП", "6683009222");
        var location = new Location(Guid.NewGuid(), organization.Id, "Рефтинская ГРЭС", "здание 6");
        db.Save(organization);
        db.Save(location);

        var closedAt = new DateTimeOffset(2026, 7, 6, 14, 46, 0, TimeSpan.FromHours(5));
        var taxcomExternal = "taxcom-shift-2";
        var frontolExternal = $"{organization.Id:N}:{location.Id:N}:1:2";

        db.Save(new ShiftClosure(
            "Taxcom.ShiftReport", taxcomExternal, organization.Id, location.Id,
            closedAt, 1233734m, 0m, 1233734m, "7381440800477733", 2));

        db.UpsertFiscalOperation(new CashOperation(
            "Taxcom.ShiftReport", taxcomExternal + ":electronic", organization.Id, location.Id,
            closedAt, SourceKind.Fiscal, OperationKind.Sale, PaymentKind.Electronic, 1233734m));

        db.Save(new ShiftClosure(
            "Frontol.Report", frontolExternal, organization.Id, location.Id,
            closedAt, 1233734m, 0m, 1233734m, "", 2));

        db.UpsertFiscalOperation(new CashOperation(
            "Frontol.Report", frontolExternal + ":electronic", organization.Id, location.Id,
            closedAt, SourceKind.Fiscal, OperationKind.Sale, PaymentKind.Electronic, 1233734m));

        var repaired = db.EnsureV052Fixes();

        Assert.True(repaired >= 1);
        var day = Assert.Single(db.PointDaySummaries(organization.Id, 2026, 7, location.Id));
        Assert.Equal(1233734m, day.FiscalElectronic);
        Assert.Equal(1233734m, day.ShiftTotal);
        Assert.Equal(1, day.ShiftCount);
        Assert.Single(db.ShiftClosures(organization.Id, location.Id, new DateOnly(2026, 7, 6)));
    }
}
