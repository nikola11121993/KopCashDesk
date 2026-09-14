using KopCashDesk.Core;
using KopCashDesk.Data;
using Xunit;

namespace KopCashDesk.Tests;

public sealed class V053DuplicateRepairTests
{
    [Fact]
    public void LegacyV053_DoesNotDeleteSameSourceTaxcomHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), "KopCashDesk.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var db = new Database(Path.Combine(root, "cashdesk.db"));
        db.Initialize();

        var org = new Organization(Guid.NewGuid(), "ООО ЦОП", "6683009222");
        var loc = new Location(Guid.NewGuid(), org.Id, "Рефтинская ГРЭС", "");
        db.Save(org);
        db.Save(loc);

        var closed = new DateTimeOffset(2026, 7, 6, 14, 46, 0, TimeSpan.FromHours(5));
        SaveShiftWithFiscal(db, org.Id, loc.Id, "old-copy", closed, 1233734m, "", 2);
        SaveShiftWithFiscal(db, org.Id, loc.Id, "real-copy", closed.AddMinutes(1), 1233734m, "7381440800477733", 2);

        var repaired = db.EnsureV053Fixes();

        Assert.Equal(0, repaired);
        Assert.Equal(2, db.ShiftClosures(org.Id, loc.Id, new DateOnly(2026, 7, 6)).Count);
        Assert.Empty(db.CrossSourceShiftLinks());
    }

    [Fact]
    public void LegacyV053_PreservesTwoDifferentTaxcomRegisters_WithSameAmounts()
    {
        var root = Path.Combine(Path.GetTempPath(), "KopCashDesk.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var db = new Database(Path.Combine(root, "cashdesk.db"));
        db.Initialize();

        var org = new Organization(Guid.NewGuid(), "ООО ЦОП", "6683009222");
        var loc = new Location(Guid.NewGuid(), org.Id, "Точка", "");
        db.Save(org);
        db.Save(loc);

        var closed = new DateTimeOffset(2026, 7, 6, 14, 46, 0, TimeSpan.FromHours(5));
        SaveShiftWithFiscal(db, org.Id, loc.Id, "reg-a", closed, 1000m, "1111111111111111", 2);
        SaveShiftWithFiscal(db, org.Id, loc.Id, "reg-b", closed.AddMinutes(2), 1000m, "2222222222222222", 2);

        db.EnsureV053Fixes();

        var after = Assert.Single(db.PointDaySummaries(org.Id, 2026, 7, loc.Id));
        Assert.Equal(2000m, after.FiscalElectronic);
        Assert.Equal(2000m, after.ShiftTotal);
        Assert.Equal(2, after.ShiftCount);
        Assert.Equal(2, db.ShiftClosures(org.Id, loc.Id, new DateOnly(2026, 7, 6)).Count);
    }

    private static void SaveShiftWithFiscal(
        Database db,
        Guid organizationId,
        Guid locationId,
        string externalId,
        DateTimeOffset closedAt,
        decimal electronic,
        string fn,
        int shiftNumber)
    {
        db.Save(new ShiftClosure(
            "Taxcom.ShiftReport",
            externalId,
            organizationId,
            locationId,
            closedAt,
            electronic,
            0m,
            electronic,
            fn,
            shiftNumber,
            null));

        db.UpsertFiscalOperation(new CashOperation(
            "Taxcom.ShiftReport",
            externalId + ":electronic",
            organizationId,
            locationId,
            closedAt,
            SourceKind.Fiscal,
            OperationKind.Sale,
            PaymentKind.Electronic,
            electronic,
            null));
    }
}
