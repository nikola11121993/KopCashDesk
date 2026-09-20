using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using KopCashDesk.Core;
using KopCashDesk.Data;
using KopCashDesk.Desktop;
using Microsoft.Data.Sqlite;
using Xunit;
using Location = KopCashDesk.Core.Location;

namespace KopCashDesk.Tests;

public sealed class RegisterBindingRegressionTests
{
    [Fact]
    public void TwoKkt_3457Plus100_SummaryAndBreakdownAre3557()
    {
        using var f = new Fixture();
        f.Bind("111", "991");
        f.Bind("222", "992");
        f.Import("Меркурий 180Ф", "111", "991", 3457, 415, "31.08.2026 15:00:00", point: "Столовая АТИ");
        f.Import("Меркурий 180Ф", "111", "991", 0, 416, "31.08.2026 15:10:00", point: "Столовая АТИ");
        f.Import("Кулинария Аппетит", "222", "992", 100, 10, "31.08.2026 15:11:00", point: "Столовая АТИ");
        var day = Assert.Single(f.Db.CanonicalPointDaySummaries());
        Assert.Equal(3557m, day.FiscalElectronic); Assert.Equal(3, day.ShiftCount); Assert.Equal(f.Point.Id, day.LocationId);
        var rows = f.Db.ShiftDetails(f.Org.Id, f.Point.Id, new DateOnly(2026, 8, 31));
        Assert.Equal(3, rows.Count); Assert.Equal(3557m, rows.Where(x => x.Included).Sum(x => x.Electronic));
        Assert.Contains(rows, x => x.Register == "Меркурий 180Ф" && x.KktSerial == "111");
        Assert.Contains(rows, x => x.Register == "Кулинария Аппетит" && x.Electronic == 100);
    }

    [Fact]
    public void UnknownRegister_SavesRawShiftAndAmounts_WithoutInventingLocation()
    {
        using var f = new Fixture();
        var result = f.Import("Новое название кассы", "111", "991", 500);
        Assert.Equal(0, result.LocationsCreated); Assert.Single(f.Db.Locations());
        Assert.Null(Assert.Single(f.Db.RegisterBindings()).LocationId);
        Assert.Empty(f.Db.PointDaySummaries());
        Assert.Equal(500m, Assert.Single(f.Db.ShiftDetails(f.Org.Id, null)).Electronic);
        Assert.Equal(2, f.Db.CountOperations());
    }

    [Fact]
    public void UnknownRegister_DoesNotTrustIncomingPointName()
    {
        using var f = new Fixture();
        f.Import("Неизвестная модель", "111", "991", 100, point: "Столовая АТИ");
        Assert.Null(Assert.Single(f.Db.RegisterBindings()).LocationId);
        Assert.Single(f.Db.Locations());
        Assert.Empty(f.Db.PointDaySummaries());
    }

    [Fact]
    public void ManualLocked_BeatsGresRule_InBindingAndImportedShift()
    {
        using var f = new Fixture();
        f.Db.SaveRegisterBinding(new(Guid.NewGuid(), f.Org.Id, f.Point.Id, "991", BindingSource: BindingSource.Manual, IsLocked: true, KktSerial: KnownBusinessRules.ReftinskayaRegisterSerial));
        f.Import("Касса", KnownBusinessRules.ReftinskayaRegisterSerial, "991", 100);
        Assert.Equal(f.Point.Id, Assert.Single(f.Db.PointDaySummaries()).LocationId);
        Assert.Equal(BindingSource.Manual, Assert.Single(f.Db.RegisterBindings()).BindingSource);
    }

    [Fact]
    public void SerialBinding_SurvivesFnReplacement()
    {
        using var f = new Fixture(); f.Bind("111", "991");
        f.Import("Касса", "111", "992", 100);
        Assert.Equal(f.Point.Id, Assert.Single(f.Db.PointDaySummaries()).LocationId);
    }

    [Fact]
    public void RnmBinding_ResolvesWhenFnAndSerialAreMissing()
    {
        using var f = new Fixture(); f.Db.SaveRegisterBinding(new(Guid.NewGuid(), f.Org.Id, f.Point.Id, "", RegisterNumber: "12345"));
        var b = RegisterBindingService.Resolve(f.Db, f.Org.Id, "", "", "12345", "Касса", "", new DateOnly(2026, 8, 31));
        Assert.Equal(f.Point.Id, b.LocationId);
    }

    [Fact]
    public void DatedMove_KeepsBeforeAtOldPoint_AndAfterAtNew_IncludingReimport()
    {
        using var f = new Fixture(); var old = new Location(Guid.NewGuid(), f.Org.Id, "Историческая точка 1"); var next = new Location(Guid.NewGuid(), f.Org.Id, "Историческая точка 2"); f.Db.Save(old); f.Db.Save(next);
        var b = new RegisterBinding(Guid.NewGuid(), f.Org.Id, old.Id, "991", KktSerial: "111"); f.Db.SaveRegisterBinding(b);
        f.Import("Историческая ККТ", "111", "991", 200, 1, "01.08.2026 15:00:00");
        RegisterBindingService.Assign(f.Db, b, next.Id, new DateOnly(2026, 8, 15), null);
        f.Import("Историческая ККТ", "111", "991", 100, 2, "31.08.2026 15:00:00");
        f.Import("Историческая ККТ", "111", "991", 200, 1, "01.08.2026 15:00:00");
        var days = f.Db.PointDaySummaries();
        Assert.Equal(old.Id, Assert.Single(days, x => x.Date.Day == 1).LocationId); Assert.Equal(next.Id, Assert.Single(days, x => x.Date.Day == 31).LocationId);
        Assert.Equal(2, f.Db.RegisterBindings().Count); Assert.Equal(3, RegisterBindingService.Read(f.Db, true).Count);
    }

    [Fact]
    public void GapInHistory_DoesNotUseTodaysRule()
    {
        using var f = new Fixture(); f.Db.SaveRegisterBinding(new(Guid.NewGuid(), f.Org.Id, f.Point.Id, "991", ValidFrom: new DateOnly(2026, 9, 1), KktSerial: "111"));
        f.Import("Меркурий 180Ф", "111", "991", 100);
        Assert.Empty(f.Db.PointDaySummaries()); Assert.Single(f.Db.ShiftDetails(f.Org.Id, null));
    }

    [Fact]
    public void RepeatedImport_DoesNotMultiplyRegistersLocationsShiftsOrLinks()
    {
        using var f = new Fixture(); f.Bind("111", "991");
        f.Import("Меркурий 180Ф", "111", "991", 100, point: "Столовая АТИ"); f.Import("Меркурий 180Ф", "111", "991", 100, point: "Столовая АТИ");
        Assert.Single(f.Db.RegisterBindings()); Assert.Single(f.Db.Locations()); Assert.Single(f.Db.ShiftDetails(f.Org.Id, f.Point.Id)); Assert.Empty(f.Db.CrossSourceShiftLinks());
    }

    [Fact]
    public void IndependentFrontolShift_DoesNotIncreaseOfficialTaxcomCash()
    {
        using var f = new Fixture(); f.Bind("111", "991");
        f.Import("Меркурий 180Ф", "111", "991", 3457, point: "Столовая АТИ");
        f.Shift("Frontol.Report", "frontol", new DateTimeOffset(2026, 8, 31, 18, 0, 0, TimeSpan.FromHours(5)), 100);
        f.Db.Insert(new("UBRiR", "historic", f.Org.Id, f.Point.Id, new DateTimeOffset(2026, 8, 31, 10, 0, 0, TimeSpan.FromHours(5)), SourceKind.Bank, OperationKind.Sale, PaymentKind.Electronic, 3557));
        Assert.Equal(3457m, Assert.Single(f.Db.PointDaySummaries()).FiscalElectronic);
        var fifo = Assert.Single(f.Db.ReconciliationDays());
        Assert.Equal(3457m, fifo.CashElectronic);
        Assert.Equal(100m, fifo.DayRemaining);
    }

    [Fact]
    public void FrontolMismatch_KeepsTaxcomAsOfficialCash()
    {
        using var f = new Fixture(); f.Bind("111", "991");
        f.Import("Меркурий 180Ф", "111", "991", 75952, 1, "09.06.2026 15:00:00", point: "Столовая АТИ");
        f.Shift("Frontol.Report", "frontol", new DateTimeOffset(2026, 6, 9, 15, 1, 0, TimeSpan.FromHours(5)), 96101);
        var day = Assert.Single(f.Db.PointDaySummaries());
        Assert.True(day.HasSourceConflict);
        Assert.Equal(75952m, day.FiscalElectronic);
        Assert.Equal(75952m, day.ShiftTotal);
        Assert.Equal(2, f.Db.ShiftDetails(f.Org.Id, f.Point.Id).Count);
        Assert.Empty(f.Db.ReconciliationAllocations(f.Org.Id, f.Point.Id));
    }

    [Fact]
    public void ExactCrossSourceDuplicate_CountsOnceInDayMonthYearAndFifo()
    {
        using var f = new Fixture(); f.Bind("111", "991");
        f.Import("Меркурий 180Ф", "111", "991", 59460, 1, "10.06.2026 15:00:00", point: "Столовая АТИ");
        f.Shift("Frontol.Report", "frontol", new DateTimeOffset(2026, 6, 10, 15, 1, 0, TimeSpan.FromHours(5)), 59460);
        Assert.Equal(59460m, Assert.Single(f.Db.PointDaySummaries(f.Org.Id, 2026)).FiscalElectronic);
        Assert.Equal(59460m, Assert.Single(f.Db.PointDaySummaries(f.Org.Id, 2026, 6)).FiscalElectronic);
        Assert.Single(f.Db.CrossSourceShiftLinks()); Assert.Equal(2, f.Db.ShiftDetails(f.Org.Id, f.Point.Id).Count);
        Assert.Equal(59460m, Assert.Single(f.Db.ReconciliationDays()).CashElectronic);
    }

    [Fact]
    public void Assignment_IsAudited_AndUnassignedOldObservationsBecomeVisible()
    {
        using var f = new Fixture(); f.Import("Неизвестная ККТ", "111", "991", 100);
        var b = Assert.Single(f.Db.RegisterBindings()); RegisterBindingService.Assign(f.Db, b, f.Point.Id, null, null);
        Assert.Equal(100m, Assert.Single(f.Db.PointDaySummaries()).FiscalElectronic);
        Assert.Contains(f.Db.AuditEntries(), x => x.Action == "register.rebind" && x.Details.Contains("source=Manual") && x.Details.Contains("new_loc=" + f.Point.Id));
    }

    [Fact]
    public void DisplayOnlyPseudoPoint_IsNotAutoMerged()
    {
        using var f = new Fixture();
        var pseudo = new Location(Guid.NewGuid(), f.Org.Id, "Чапаева/МЧС");
        f.Db.Save(pseudo);
        f.Db.SaveRegisterBinding(new(Guid.NewGuid(), f.Org.Id, pseudo.Id, "991"));
        Assert.Equal(0, f.Db.RepairRegisterLocations());
        Assert.True(Assert.Single(f.Db.Locations(true), x => x.Id == pseudo.Id).IsActive);
    }

    [Fact]
    public void HistoricVoroniyLeningradskayaMira_AreMergedIntoClosedReportingPoint()
    {
        using var f = new Fixture();
        var old = new Location(Guid.NewGuid(), f.Org.Id, "Вороний Брод");
        var mid = new Location(Guid.NewGuid(), f.Org.Id, "Ленинградская 1");
        var next = new Location(Guid.NewGuid(), f.Org.Id, "Мира 4");
        f.Db.Save(old); f.Db.Save(mid); f.Db.Save(next);
        f.Db.Insert(new("UBRiR", "old-terminal", f.Org.Id, old.Id, new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.FromHours(5)), SourceKind.Bank, OperationKind.Sale, PaymentKind.Electronic, 100));

        KnownBusinessRules.ApplyPending(f.Db);

        var active = f.Db.Locations().Where(x => x.OrganizationId == f.Org.Id).ToArray();
        Assert.Equal(7, active.Length);
        var mira = Assert.Single(active, x => x.Name == KnownBusinessRules.MiraPointName);
        var day = Assert.Single(f.Db.PointDaySummaries());
        Assert.Equal(mira.Id, day.LocationId);
        Assert.Equal(100m, day.BankElectronic);
        var all = f.Db.Locations(true).Where(x => x.OrganizationId == f.Org.Id).ToArray();
        Assert.All(all.Where(x => x.Id == old.Id || x.Id == mid.Id || x.Id == next.Id), x => Assert.False(x.IsActive));
    }

    [Fact]
    public void MigrationReentry_PreservesAmountsBindingsAndSourceRows()
    {
        using var f = new Fixture(); f.Bind("111", "991");
        f.Import("Меркурий 180Ф", "111", "991", 100, point: "Столовая АТИ");
        var count = f.Db.CountOperations(); var binding = Assert.Single(f.Db.RegisterBindings()); f.Db.Initialize(); f.Db.Initialize();
        Assert.Equal(count, f.Db.CountOperations()); Assert.Equal(binding.Id, Assert.Single(f.Db.RegisterBindings()).Id); Assert.Equal(100m, Assert.Single(f.Db.PointDaySummaries()).FiscalElectronic);
    }

    [Fact]
    public void UnknownRegister_AcrossDaysKeepsOnePendingBinding()
    {
        using var f = new Fixture();
        f.Import("Неизвестная", "111", "991", 100, 1, "01.08.2026 15:00:00");
        f.Import("Неизвестная", "111", "991", 200, 2, "02.08.2026 15:00:00");
        Assert.Single(f.Db.RegisterBindings());
        Assert.Equal(2, f.Db.ShiftDetails(f.Org.Id, null).Count);
        Assert.Empty(f.Db.PointDaySummaries());
    }

    [Fact]
    public void HardRule_DoesNotMoveLockedRegisterPoint()
    {
        using var f = new Fixture();
        f.Db.SaveRegisterBinding(new(Guid.NewGuid(), f.Org.Id, f.Point.Id, "991", BindingSource: BindingSource.Manual, IsLocked: true, KktSerial: KnownBusinessRules.ReftinskayaRegisterSerial));
        KnownBusinessRules.ApplyPending(f.Db);
        Assert.Equal(f.Point.Id, Assert.Single(f.Db.RegisterBindings()).LocationId);
        Assert.Equal(BindingSource.Manual, Assert.Single(f.Db.RegisterBindings()).BindingSource);
    }

    [Fact]
    public void DifferentKnownSerials_AreNotCrossSourceDuplicates()
    {
        using var f = new Fixture();
        var date = new DateTimeOffset(2026, 8, 31, 15, 0, 0, TimeSpan.Zero);
        f.Db.Save(new ShiftClosure("Taxcom.ShiftReport", "t", f.Org.Id, f.Point.Id, date, 100, 0, 100, KktSerial: "111"));
        f.Db.Save(new ShiftClosure("Frontol.Report", "f", f.Org.Id, f.Point.Id, date, 100, 0, 100, KktSerial: "222"));
        f.Db.RebuildCrossSourceShiftMatches();
        Assert.Empty(f.Db.CrossSourceShiftLinks());
        Assert.Equal(1, Assert.Single(f.Db.PointDaySummaries()).ShiftCount);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "kkt-tests-" + Guid.NewGuid().ToString("N"));
        public Database Db { get; }
        public Organization Org { get; }
        public Location Point { get; }
        public Fixture()
        {
            Directory.CreateDirectory(root);
            Db = new(Path.Combine(root, "cash.db"));
            Db.Initialize();
            Org = new(Guid.NewGuid(), "Тест", KnownBusinessRules.CopTaxId);
            Point = new(Guid.NewGuid(), Org.Id, KnownBusinessRules.AtiMercuryPointName, "Плеханова 64");
            Db.Save(Org); Db.Save(Point);
        }
        public void Bind(string serial, string fn) =>
            Db.SaveRegisterBinding(new RegisterBinding(Guid.NewGuid(), Org.Id, Point.Id, fn, KktSerial: serial));

        public TaxcomShiftImportSummary Import(string name, string serial, string fn, decimal amount, int shift = 1, string closed = "31.08.2026 15:00:00", string point = "Без торговой точки")
        {
            var path = Path.Combine(root, "ИНН 6683009222 report.xlsx");
            using (var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
            {
                var wb = doc.AddWorkbookPart(); wb.Workbook = new Workbook(); var sheet = wb.AddNewPart<WorksheetPart>(); var data = new SheetData(); sheet.Worksheet = new Worksheet(data);
                string[] headers = ["Дата закрытия", "№ смены", "Выручка нал.", "Выручка безнал.", "Выручка", "Торговая точка", "Название ККТ", "Зав. № ККТ", "Рег. № ККТ", "Зав. № ФН"];
                string[] values = [closed, shift.ToString(), "0", amount.ToString(System.Globalization.CultureInfo.InvariantCulture), amount.ToString(System.Globalization.CultureInfo.InvariantCulture), point, name, serial, "12345", fn];
                for (int row = 0; row < 2; row++) { var r = new Row { RowIndex = (uint)row + 1 }; var texts = row == 0 ? headers : values; for (int i = 0; i < texts.Length; i++) r.Append(new Cell { CellReference = $"{(char)('A' + i)}{row + 1}", DataType = CellValues.InlineString, InlineString = new InlineString(new Text(texts[i])) }); data.Append(r); }
                sheet.Worksheet.Save(); wb.Workbook.Append(new Sheets(new Sheet { Id = wb.GetIdOfPart(sheet), SheetId = 1, Name = "Смены" })); wb.Workbook.Save();
            }
            return new TaxcomShiftReportImporter(Db).ImportFiles([path]);
        }
        public void Shift(string source, string id, DateTimeOffset date, decimal electronic, Guid? location = null, string fn = "")
        {
            date = new DateTimeOffset(date.DateTime, TimeZoneInfo.Local.GetUtcOffset(date.DateTime));
            var loc = location ?? Point.Id; Db.Save(new ShiftClosure(source, id, Org.Id, loc, date, electronic, 0, electronic, fn, 1));
            Db.UpsertFiscalOperation(new(source, id + ":cash", Org.Id, loc, date, SourceKind.Fiscal, OperationKind.Sale, PaymentKind.Cash, 0));
            Db.UpsertFiscalOperation(new(source, id + ":electronic", Org.Id, loc, date, SourceKind.Fiscal, OperationKind.Sale, PaymentKind.Electronic, electronic));
        }
        public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(root, true); } catch { } }
    }
}
