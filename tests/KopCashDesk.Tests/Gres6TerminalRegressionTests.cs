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

public sealed class Gres6TerminalRegressionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "gres6-" + Guid.NewGuid().ToString("N"));
    private readonly Database db;
    private readonly Organization org = new(Guid.NewGuid(), "ЦОП", "6683009222");
    private readonly Location gres;
    private readonly Location other;
    private static readonly DateOnly Day = new(2026, 10, 23);

    public Gres6TerminalRegressionTests()
    {
        db = new Database(Path.Combine(root, "cash.db")); db.Initialize(); db.Save(org);
        gres = new(Guid.NewGuid(), org.Id, KnownBusinessRules.ReftinskayaPointName);
        other = new(Guid.NewGuid(), org.Id, "Другая точка");
        db.Save(gres); db.Save(other);
    }

    private TerminalBinding? Resolve(DateOnly? day = null) => TerminalLocationResolver.Resolve(db, org.Id,
        "Sber", TerminalLocationResolver.Gres6Tid, TerminalLocationResolver.Gres6Mid, day ?? Day, out _);

    [Fact]
    public void Tid_34773474_MapsTo_ReftinskayaGRES6()
    {
        var binding = Assert.IsType<TerminalBinding>(Resolve());
        Assert.Equal(gres.Id, binding.LocationId);
        Assert.Equal(BindingSource.Rule, binding.BindingSource);
        Assert.Equal("191000238983", binding.MerchantId);
        Assert.Equal(binding.Id, Resolve()!.Id);
    }

    [Fact]
    public void KnownRule_DoesNotOverride_LockedManualBinding()
    {
        db.SaveEditableTerminal(new(Guid.NewGuid(), org.Id, other.Id, "Sber", "34773474"));
        Assert.Equal(other.Id, Resolve()!.LocationId);
    }

    [Fact]
    public void AutomaticBinding_DoesNotOverride_KnownRule()
    {
        var rule = Resolve()!;
        db.Save(rule with { LocationId = other.Id, BindingSource = BindingSource.Automatic });
        Assert.Equal(gres.Id, Resolve()!.LocationId);
    }

    [Fact]
    public void Tid_34773474_AddressBuilding1_DoesNotCreateAnotherLocation_AndReconciles48200()
    {
        var path = BankFile();
        db.Save(new TerminalBinding(Guid.NewGuid(), org.Id, gres.Id, "Sber", "111"));
        var imported = new SberAcquiringImporter(db).ImportFiles([path]);
        Assert.Equal(2, imported.OperationsAdded);
        Assert.Equal(0, imported.LocationsCreated);
        Assert.DoesNotContain(db.Locations(), x => x.Address.Contains("Здание 1"));
        var binding = RegisterBindingService.Resolve(db, org.Id, "00106900361561", "991", "", "ККТ", "", Day);
        Assert.Equal(gres.Id, binding.LocationId);
        var time = new DateTimeOffset(2026, 10, 23, 18, 0, 0, TimeSpan.FromHours(5));
        db.Save(new ShiftClosure("Taxcom.ShiftReport", "shift", org.Id, gres.Id, time, 51200m, 3000m, 48200m,
            "991", 1, KktSerial: "00106900361561"));
        db.UpsertFiscalOperation(new("Taxcom.ShiftReport", "shift:electronic", org.Id, gres.Id, time,
            SourceKind.Fiscal, OperationKind.Sale, PaymentKind.Electronic, 48200m, FiscalDriveNumber: "991", KktSerial: "00106900361561"));
        var day = Assert.Single(db.PointDaySummaries(org.Id, 2026, 10, gres.Id));
        Assert.Equal(48200m, day.BankElectronic);
        Assert.Equal(48200m, day.FiscalElectronic);
        Assert.Equal(3000m, day.ShiftCash);
        Assert.Equal(51200m, day.ShiftTotal);
        Assert.Equal("Сошлось", Assert.Single(db.ReconciliationDays(org.Id, 2026, 10, gres.Id)).Status);
        Assert.Equal(0, new SberAcquiringImporter(db).ImportFiles([path]).OperationsAdded);
        Assert.Equal(48200m, Assert.Single(db.PointDaySummaries(org.Id, 2026, 10, gres.Id)).BankElectronic);
    }

    [Fact]
    public void Reimport_RepairsWrongBankLocation_WithoutChangingMoneyOrDates()
    {
        var path = BankFile();
        db.SaveEditableTerminal(new(Guid.NewGuid(), org.Id, other.Id, "Sber", "34773474"));
        var importer = new SberAcquiringImporter(db);
        importer.ImportFiles([path]);
        var terminal = Assert.Single(db.TerminalBindings(), x => x.TerminalId == "34773474");
        db.SaveEditableTerminal(terminal with { LocationId = gres.Id });
        importer.ImportFiles([path]);
        Assert.Equal(493m, Assert.Single(db.PointDaySummaries(org.Id, 2026, 10, gres.Id)).BankElectronic);
        Assert.Contains(db.AuditEntries(), x => x.Action == "bank.binding.repair");
    }

    [Fact]
    public void DatedTerminalHistory_ResolvesOldAndNewPoints_AndGapRequiresReview()
    {
        db.SaveEditableTerminal(new(Guid.NewGuid(), org.Id, gres.Id, "Sber", "222", ValidTo: new(2026, 9, 30)));
        db.SaveEditableTerminal(new(Guid.NewGuid(), org.Id, other.Id, "Sber", "222", ValidFrom: new(2026, 10, 2)));
        var old = TerminalLocationResolver.Resolve(db, org.Id, "Sber", "222", "", new(2026, 9, 30), out var review);
        Assert.False(review); Assert.Equal(gres.Id, old!.LocationId);
        Assert.Null(TerminalLocationResolver.Resolve(db, org.Id, "Sber", "222", "", new(2026, 10, 1), out review));
        Assert.True(review);
        Assert.Equal(other.Id, TerminalLocationResolver.Resolve(db, org.Id, "Sber", "222", "", new(2026, 10, 2), out _)!.LocationId);
        Assert.Throws<InvalidOperationException>(() => db.SaveEditableTerminal(new(Guid.NewGuid(), org.Id, other.Id, "Sber", "222")));
        db.Initialize();
        Assert.Equal(2, db.TerminalBindings().Count);
    }

    [Fact]
    public void SummaryRefresh_WithoutChanges_DoesNotRewriteSourceLinks()
    {
        db.PointDaySummaries();
        long Revision()
        {
            using var c = new SqliteConnection("Data Source=" + db.Path); c.Open();
            using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT revision FROM cross_source_revision";
            return (long)cmd.ExecuteScalar()!;
        }
        var before = Revision(); db.PointDaySummaries(); Assert.Equal(before, Revision());
        db.Insert(new("Bank", "new", org.Id, gres.Id, DateTimeOffset.Now, SourceKind.Bank, OperationKind.Sale, PaymentKind.Electronic, 10m));
        Assert.True(Revision() > before);
        Assert.Single(db.PointDaySummaries());
    }

    [Fact]
    public void PeriodSummary_MissingCash_IsNotZeroOrMatched()
    {
        var row = new PointDaySummary(Day, org.Id, org.Name, gres.Id, gres.Name, 0m, null, null, null, null, 0, null);
        Assert.False(PeriodSummary.Calculate([row]).IsComplete);
        Assert.Null(PeriodSummary.Calculate([row]).Cash);
        Assert.Equal("Сошлось", PeriodSummary.Calculate([row with { FiscalElectronic = 0m }]).Status);
        Assert.False(PeriodSummary.Calculate([row with { FiscalElectronic = 0m, HasSourceConflict = true }]).IsComplete);
    }

    [Fact]
    public void MissingCanonicalGresPoint_RequiresReview_WithoutInventingLocation()
    {
        using var empty = new SqliteConnection("Data Source=" + db.Path); empty.Open();
        using var c = empty.CreateCommand(); c.CommandText = "UPDATE locations SET is_active=0 WHERE id=$id";
        c.Parameters.AddWithValue("$id", gres.Id.ToString()); c.ExecuteNonQuery();
        Assert.Null(TerminalLocationResolver.Resolve(db, org.Id, "Sber", "34773474", "191000238983", Day, out var review));
        Assert.True(review); Assert.Empty(db.TerminalBindings());
    }

    [Fact]
    public void V4Upgrade_PreservesLockedTerminalAndAmounts_WithBackupAndIdempotence()
    {
        db.SaveEditableTerminal(new(Guid.NewGuid(), org.Id, gres.Id, "Sber", "123"));
        db.Insert(new("Bank", "legacy", org.Id, gres.Id, DateTimeOffset.Now, SourceKind.Bank, OperationKind.Sale, PaymentKind.Electronic, 493m));
        using (var connection = new SqliteConnection("Data Source=" + db.Path))
        {
            connection.Open(); using var c = connection.CreateCommand();
            c.CommandText = "SELECT name FROM sqlite_master WHERE type='trigger' AND name LIKE 'revision_%'";
            var triggers = new List<string>(); using (var reader = c.ExecuteReader()) while (reader.Read()) triggers.Add(reader.GetString(0));
            foreach (var trigger in triggers) { c.CommandText = $"DROP TRIGGER {trigger}"; c.ExecuteNonQuery(); }
            c.CommandText = "DROP TABLE cross_source_revision; UPDATE schema_version SET version=4"; c.ExecuteNonQuery();
        }
        var backupsBefore = Directory.GetFiles(Path.Combine(root, "Backups"), "*.db").Length;
        db.Initialize(); db.Initialize();
        Assert.True(Assert.Single(db.TerminalBindings()).IsLocked);
        Assert.Equal(493m, db.SumOperations("Bank", org.Id));
        Assert.Equal(backupsBefore + 1, Directory.GetFiles(Path.Combine(root, "Backups"), "*.db").Length);
        using var migrated = new SqliteConnection("Data Source=" + db.Path); migrated.Open();
        using var check = migrated.CreateCommand(); check.CommandText = "PRAGMA foreign_key_check";
        using var rows = check.ExecuteReader(); Assert.False(rows.Read());
    }

    private string BankFile()
    {
        var path = Path.Combine(root, "sber.xlsx");
        using var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var wb = doc.AddWorkbookPart(); wb.Workbook = new Workbook();
        var part = wb.AddNewPart<WorksheetPart>(); var data = new SheetData(); part.Worksheet = new Worksheet(data);
        string[][] rows = [
            ["Наименование юридического лица", "ИНН", "Наименование ТСТ", "Адрес ТСТ", "Номер терминала", "Номер мерчанта", "RRN", "Дата операции", "Сумма операции"],
            ["ЦОП", "6683009222", "Рефтинская ГРЭС", "здание 6", "111", "100", "r1", "23.10.2026 12:00:00", "47707"],
            ["ЦОП", "6683009222", "Рефтинская ГРЭС_SBP", "Рефтенская ГЭС, Здание 1", "34773474", "191000238983", "r2", "23.10.2026 12:01:00", "493"]];
        for (var i = 0; i < rows.Length; i++)
        {
            var row = new Row { RowIndex = (uint)i + 1 };
            for (var j = 0; j < rows[i].Length; j++) row.Append(new Cell {
                CellReference = $"{(char)('A' + j)}{i + 1}", DataType = CellValues.InlineString,
                InlineString = new InlineString(new Text(rows[i][j])) });
            data.Append(row);
        }
        var sheets = wb.Workbook.AppendChild(new Sheets());
        sheets.Append(new Sheet { Id = wb.GetIdOfPart(part), SheetId = 1, Name = "Отчет" });
        part.Worksheet.Save(); wb.Workbook.Save(); return path;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(root, true); } catch (IOException) { }
    }
}
