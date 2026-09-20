using KopCashDesk.Core;
using KopCashDesk.Data;
using Xunit;

namespace KopCashDesk.Tests;

public sealed class CrossSourceShiftMatchingTests
{
    private const string Taxcom = "Taxcom.ShiftReport";
    private const string Frontol = "Frontol.Report";

    [Fact]
    public void ExactMoney_71Seconds_CountsOnePhysicalShift_AndKeepsBothSources()
    {
        var (db, org, location) = CreateDb();
        AddShift(db, org, location, Taxcom, "tax-1", At(14, 57, 0), 60160m, 700m, 59460m, "fn-1", 10);
        AddShift(db, org, location, Frontol, "front-1", At(14, 58, 11), 60160m, 700m, 59460m, "", 10);

        var result = db.RebuildCrossSourceShiftMatches();

        Assert.Equal(1, result.MatchedPairs);
        Assert.Single(db.CrossSourceShiftLinks());
        Assert.Equal(2, db.ShiftClosures(org.Id, location.Id, Day).Count);
        var summary = Assert.Single(db.CanonicalPointDaySummaries(org.Id, 2026, 6, location.Id));
        Assert.Equal(60160m, summary.ShiftTotal);
        Assert.Equal(700m, summary.ShiftCash);
        Assert.Equal(59460m, summary.ShiftElectronic);
        Assert.Equal(59460m, summary.FiscalElectronic);
        Assert.Equal(1, summary.ShiftCount);
        Assert.Equal("Такском (основной) + Frontol (проверка)", summary.FiscalSources);
        Assert.False(summary.HasSourceConflict);
    }

    [Fact]
    public void Delta_4Minutes59Seconds_Matches()
    {
        var (db, org, location) = CreateDb();
        AddPair(db, org, location, TimeSpan.FromSeconds(299));
        Assert.Equal(1, db.RebuildCrossSourceShiftMatches().MatchedPairs);
    }

    [Fact]
    public void Delta_OverFiveMinutes_DoesNotMatch()
    {
        var (db, org, location) = CreateDb();
        AddPair(db, org, location, TimeSpan.FromSeconds(301));
        var result = db.RebuildCrossSourceShiftMatches();
        Assert.Equal(0, result.MatchedPairs);
        Assert.Empty(db.CrossSourceShiftLinks());
        Assert.Empty(db.FiscalSourceConflicts());
    }

    [Fact]
    public void SameTotalButDifferentElectronic_IsConflict_NotMatch()
    {
        var (db, org, location) = CreateDb();
        AddShift(db, org, location, Taxcom, "tax", At(14, 57, 0), 100m, 10m, 90m, "fn", 1);
        AddShift(db, org, location, Frontol, "front", At(14, 58, 0), 100m, 10m, 80m, "", 1);

        var result = db.RebuildCrossSourceShiftMatches();

        Assert.Equal(0, result.MatchedPairs);
        Assert.Equal(1, result.Conflicts);
        var summary = Assert.Single(db.CanonicalPointDaySummaries(org.Id, 2026, 6, location.Id));
        Assert.True(summary.HasSourceConflict);
        Assert.Equal(90m, summary.FiscalElectronic);
        Assert.Equal(100m, summary.ShiftTotal);
        Assert.Equal(10m, summary.ShiftCash);
        Assert.Equal(90m, summary.ShiftElectronic);
        Assert.Equal("Такском (основной) + Frontol (проверка)", summary.FiscalSources);
    }

    [Fact]
    public void DifferentLocation_NeverMatches()
    {
        var (db, org, location) = CreateDb();
        var other = new Location(Guid.NewGuid(), org.Id, "Другая точка", "другой адрес");
        db.Save(other);
        AddShift(db, org, location, Taxcom, "tax", At(14, 57, 0), 60160m, 700m, 59460m, "fn", 1);
        AddShift(db, org, other, Frontol, "front", At(14, 58, 0), 60160m, 700m, 59460m, "", 1);
        Assert.Equal(0, db.RebuildCrossSourceShiftMatches().MatchedPairs);
    }

    [Fact]
    public void DifferentOrganization_NeverMatches()
    {
        var (db, org, location) = CreateDb();
        var otherOrg = new Organization(Guid.NewGuid(), "ООО Другая", "6677000000");
        var otherLocation = new Location(Guid.NewGuid(), otherOrg.Id, location.Name, location.Address);
        db.Save(otherOrg);
        db.Save(otherLocation);
        AddShift(db, org, location, Taxcom, "tax", At(14, 57, 0), 60160m, 700m, 59460m, "fn", 1);
        AddShift(db, otherOrg, otherLocation, Frontol, "front", At(14, 58, 0), 60160m, 700m, 59460m, "", 1);
        Assert.Equal(0, db.RebuildCrossSourceShiftMatches().MatchedPairs);
    }

    [Fact]
    public void TaxcomThenFrontol_ProducesSameCanonicalResult()
    {
        var (db, org, location) = CreateDb();
        AddShift(db, org, location, Taxcom, "tax", At(14, 57, 0), 60160m, 700m, 59460m, "fn", 1);
        Assert.Equal(0, db.RebuildCrossSourceShiftMatches().MatchedPairs);
        AddShift(db, org, location, Frontol, "front", At(14, 58, 11), 60160m, 700m, 59460m, "", 1);
        Assert.Equal(1, db.RebuildCrossSourceShiftMatches().MatchedPairs);
        Assert.Equal(59460m, Assert.Single(db.CanonicalPointDaySummaries(org.Id, 2026, 6, location.Id)).FiscalElectronic);
    }

    [Fact]
    public void FrontolThenTaxcom_ProducesSameCanonicalResult()
    {
        var (db, org, location) = CreateDb();
        AddShift(db, org, location, Frontol, "front", At(14, 58, 11), 60160m, 700m, 59460m, "", 1);
        Assert.Equal(0, db.RebuildCrossSourceShiftMatches().MatchedPairs);
        AddShift(db, org, location, Taxcom, "tax", At(14, 57, 0), 60160m, 700m, 59460m, "fn", 1);
        Assert.Equal(1, db.RebuildCrossSourceShiftMatches().MatchedPairs);
        Assert.Equal(59460m, Assert.Single(db.CanonicalPointDaySummaries(org.Id, 2026, 6, location.Id)).FiscalElectronic);
    }

    [Fact]
    public void Reimport_IsIdempotent_NoDuplicateLinks()
    {
        var (db, org, location) = CreateDb();
        AddPair(db, org, location, TimeSpan.FromSeconds(71));
        var first = db.RebuildCrossSourceShiftMatches();
        var second = db.RebuildCrossSourceShiftMatches();
        Assert.Equal(1, first.NewMatches);
        Assert.Equal(0, second.NewMatches);
        Assert.Single(db.CrossSourceShiftLinks());
    }

    [Fact]
    public void Summary_CountsCanonicalShiftOnce()
    {
        var (db, org, location) = CreateDb();
        AddPair(db, org, location, TimeSpan.FromSeconds(71));
        db.RebuildCrossSourceShiftMatches();
        var day = Assert.Single(db.CanonicalPointDaySummaries(org.Id, 2026, 6, location.Id));
        Assert.Equal(1, day.ShiftCount);
        Assert.Equal(60160m, day.ShiftTotal);
        Assert.Equal(59460m, day.FiscalElectronic);
    }

    [Fact]
    public void Fifo_UsesCanonicalElectronicAmountOnce()
    {
        var (db, org, location) = CreateDb();
        AddPair(db, org, location, TimeSpan.FromSeconds(71));
        AddBank(db, org, location, 59460m);
        db.RebuildCrossSourceShiftMatches();

        var day = Assert.Single(db.ReconciliationDays(org.Id, 2026, 6, location.Id));
        Assert.Equal(59460m, day.BankElectronic);
        Assert.Equal(59460m, day.CashElectronic);
        Assert.Equal(0m, day.CumulativeOutstanding);
        Assert.Equal(59460m, day.CashAppliedOnDate);
    }

    [Fact]
    public void MonthAndYearTotals_CountPhysicalShiftOnce()
    {
        var (db, org, location) = CreateDb();
        AddPair(db, org, location, TimeSpan.FromSeconds(71));
        db.RebuildCrossSourceShiftMatches();
        var month = db.CanonicalPointDaySummaries(org.Id, 2026, 6, location.Id);
        var year = db.CanonicalPointDaySummaries(org.Id, 2026, null, location.Id);
        Assert.Equal(60160m, month.Sum(x => x.ShiftTotal ?? 0m));
        Assert.Equal(60160m, year.Sum(x => x.ShiftTotal ?? 0m));
        Assert.Equal(59460m, year.Sum(x => x.FiscalElectronic ?? 0m));
    }

    [Fact]
    public void SourceRecords_RemainInDatabaseAfterMatching()
    {
        var (db, org, location) = CreateDb();
        AddPair(db, org, location, TimeSpan.FromSeconds(71));
        db.RebuildCrossSourceShiftMatches();
        var shifts = db.ShiftClosures(org.Id, location.Id, Day);
        Assert.Equal(2, shifts.Count);
        Assert.Contains(shifts, x => x.Source == Taxcom);
        Assert.Contains(shifts, x => x.Source == Frontol);
        Assert.Equal(4, db.Operations(org.Id).Count(x => x.Source is Taxcom or Frontol));
    }

    [Fact]
    public void Conflict_IsAudited_AndRawRowsRemain()
    {
        var (db, org, location) = CreateDb();
        AddShift(db, org, location, Taxcom, "tax", At(14, 57, 0), 75952m, 0m, 75952m, "fn", 1);
        AddShift(db, org, location, Frontol, "front", At(14, 58, 0), 96101m, 0m, 96101m, "", 1);
        db.RebuildCrossSourceShiftMatches();

        Assert.Single(db.FiscalSourceConflicts());
        Assert.Equal(2, db.ShiftClosures(org.Id, location.Id, Day).Count);
        Assert.Contains(db.AuditEntries(), x => x.Action == "Taxcom / Frontol amount mismatch");
    }

    [Fact]
    public void FrontolOnly_IsVerificationOnly_AndDoesNotBecomeOfficialCash()
    {
        var (db, org, location) = CreateDb();
        AddShift(db, org, location, Frontol, "front", At(14, 58, 0), 60160m, 700m, 59460m, "", 1);
        db.RebuildCrossSourceShiftMatches();
        var day = Assert.Single(db.CanonicalPointDaySummaries(org.Id, 2026, 6, location.Id));
        Assert.Null(day.ShiftTotal);
        Assert.Null(day.FiscalElectronic);
        Assert.Equal(0, day.ShiftCount);
        Assert.Equal("Frontol — проверка, в итог не включён", day.FiscalSources);
    }

    [Fact]
    public void Mismatch_ReconciliationUsesTaxcom_AndMarksReview()
    {
        var (db, org, location) = CreateDb();
        AddShift(db, org, location, Taxcom, "tax", At(14, 57, 0), 100m, 10m, 90m, "fn", 1);
        AddShift(db, org, location, Frontol, "front", At(14, 58, 0), 120m, 10m, 110m, "", 1);
        AddBank(db, org, location, 90m);

        db.RebuildCrossSourceShiftMatches();
        var day = Assert.Single(db.ReconciliationDays(org.Id, 2026, 6, location.Id));

        Assert.Equal(90m, day.CashElectronic);
        Assert.Equal(90m, day.BankElectronic);
        Assert.Equal(0m, day.CumulativeOutstanding);
        Assert.True(day.RequiresReview);
        Assert.Equal("Расхождение Такском ↔ Frontol — в расчёт взят Такском", day.Status);
    }

    [Fact]
    public void OverlappingTaxcomRevision_KeepsNewestTaxcom_WithoutFrontolMismatch()
    {
        var (db, org, location) = CreateDb();
        AddShift(db, org, location, Taxcom, "tax-old", At(14, 57, 0), 100m, 10m, 90m, "fn", 1);
        AddShift(db, org, location, Taxcom, "tax-new", At(14, 58, 0), 120m, 20m, 100m, "fn", 1);

        var matching = db.RebuildCrossSourceShiftMatches();
        var day = Assert.Single(db.CanonicalPointDaySummaries(org.Id, 2026, 6, location.Id));

        Assert.Equal(0, matching.MatchedPairs);
        Assert.Equal(0, matching.Conflicts);
        Assert.Empty(db.FiscalSourceConflicts());
        Assert.Equal(120m, day.ShiftTotal);
        Assert.Equal(100m, day.FiscalElectronic);
        Assert.False(day.HasSourceConflict);
        Assert.Contains(db.AuditEntries(), x => x.Action == "Taxcom shift revision collapsed");
    }

    [Fact]
    public void TaxcomOnly_HistoryRemainsFinancial()
    {
        var (db, org, location) = CreateDb();
        AddShift(db, org, location, Taxcom, "tax", At(14, 57, 0), 60160m, 700m, 59460m, "fn", 1);
        db.RebuildCrossSourceShiftMatches();
        var day = Assert.Single(db.CanonicalPointDaySummaries(org.Id, 2026, 6, location.Id));
        Assert.Equal(60160m, day.ShiftTotal);
        Assert.Equal(59460m, day.FiscalElectronic);
        Assert.Equal("Такском", day.FiscalSources);
    }

    private static readonly DateOnly Day = new(2026, 6, 10);

    private static (Database Db, Organization Org, Location Location) CreateDb()
    {
        var root = Path.Combine(Path.GetTempPath(), "KopCashDesk.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var db = new Database(Path.Combine(root, "cashdesk.db"));
        db.Initialize();
        var org = new Organization(Guid.NewGuid(), "ООО ЦОП", "6683009222");
        var location = new Location(Guid.NewGuid(), org.Id, "Рефтинская ГРЭС", "Рефтинская ГРЭС, здание 6");
        db.Save(org);
        db.Save(location);
        return (db, org, location);
    }

    private static DateTimeOffset At(int hour, int minute, int second) =>
        new(2026, 6, 10, hour, minute, second, TimeSpan.FromHours(5));

    private static void AddPair(Database db, Organization org, Location location, TimeSpan delta)
    {
        var taxTime = At(14, 57, 0);
        AddShift(db, org, location, Taxcom, "tax", taxTime, 60160m, 700m, 59460m, "fn", 10);
        AddShift(db, org, location, Frontol, "front", taxTime.Add(delta), 60160m, 700m, 59460m, "", 10);
    }

    private static void AddShift(
        Database db,
        Organization org,
        Location location,
        string source,
        string externalId,
        DateTimeOffset closedAt,
        decimal total,
        decimal cash,
        decimal electronic,
        string fn,
        int shiftNumber)
    {
        db.Save(new ShiftClosure(source, externalId, org.Id, location.Id, closedAt, total, cash, electronic, fn, shiftNumber));
        db.UpsertFiscalOperation(new CashOperation(
            source, externalId + ":cash", org.Id, location.Id, closedAt,
            SourceKind.Fiscal, OperationKind.Sale, PaymentKind.Cash, cash));
        db.UpsertFiscalOperation(new CashOperation(
            source, externalId + ":electronic", org.Id, location.Id, closedAt,
            SourceKind.Fiscal, OperationKind.Sale, PaymentKind.Electronic, electronic));
    }

    private static void AddBank(Database db, Organization org, Location location, decimal amount)
    {
        db.Insert(new CashOperation(
            "Sber.Acquiring", "bank-1", org.Id, location.Id, At(12, 0, 0),
            SourceKind.Bank, OperationKind.Sale, PaymentKind.Electronic, amount));
    }
}
