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

public sealed class AtiKnownKktBindingTests
{
    [Fact]
    public void Kkt_00301000370264_MapsTo_ZavodAti()
    {
        using var f = new Fixture();

        f.Import("Название ККТ уже изменилось", KnownBusinessRules.AtiAppetitRegisterSerial, "9287440300000001", 67517m, point: "Столовая АТИ");

        var binding = Assert.Single(f.Db.RegisterBindings());
        Assert.Equal(f.ZavodAti.Id, binding.LocationId);
        Assert.Equal(BindingSource.Rule, binding.BindingSource);
        var day = Assert.Single(f.Db.PointDaySummaries(f.Org.Id, 2026, 8));
        Assert.Equal(f.ZavodAti.Id, day.LocationId);
        Assert.Equal(67517m, day.FiscalElectronic);
    }

    [Fact]
    public void Kkt_08050950_MapsTo_StolovayaAti()
    {
        using var f = new Fixture();

        f.Import("Любое новое название", KnownBusinessRules.AtiMercuryRegisterSerial, "9287440300000002", 88646m, point: "ЗАВОД АТИ");

        var binding = Assert.Single(f.Db.RegisterBindings());
        Assert.Equal(f.StolovayaAti.Id, binding.LocationId);
        Assert.Equal(BindingSource.Rule, binding.BindingSource);
        var day = Assert.Single(f.Db.PointDaySummaries(f.Org.Id, 2026, 8));
        Assert.Equal(f.StolovayaAti.Id, day.LocationId);
        Assert.Equal(88646m, day.FiscalElectronic);
    }

    [Fact]
    public void KnownRule_DoesNotOverride_LockedManualBinding()
    {
        using var f = new Fixture();
        var manual = new RegisterBinding(
            Guid.NewGuid(), f.Org.Id, f.StolovayaAti.Id, "9287440300000099",
            BindingSource: BindingSource.Manual,
            IsLocked: true,
            KktSerial: KnownBusinessRules.AtiAppetitRegisterSerial,
            DisplayName: "Ручная привязка");
        f.Db.SaveRegisterBinding(manual);

        f.Import("Кулинария Аппетит переименована", KnownBusinessRules.AtiAppetitRegisterSerial, manual.FiscalDriveNumber, 123m);

        var binding = Assert.Single(f.Db.RegisterBindings());
        Assert.Equal(BindingSource.Manual, binding.BindingSource);
        Assert.True(binding.IsLocked);
        Assert.Equal(f.StolovayaAti.Id, binding.LocationId);
        Assert.Equal(f.StolovayaAti.Id, Assert.Single(f.Db.PointDaySummaries()).LocationId);
    }

    [Fact]
    public void Reimport_DoesNotChange_KnownKktLocation()
    {
        using var f = new Fixture();
        const string fn = "9287440300000011";

        f.Import("Кулинария Аппетит", KnownBusinessRules.AtiAppetitRegisterSerial, fn, 67517m, point: "Столовая АТИ");
        f.Import("Совсем другое название ККТ", KnownBusinessRules.AtiAppetitRegisterSerial, fn, 67517m, point: "Столовая АТИ");

        var binding = Assert.Single(f.Db.RegisterBindings());
        Assert.Equal(f.ZavodAti.Id, binding.LocationId);
        Assert.Equal(BindingSource.Rule, binding.BindingSource);
        Assert.Single(f.Db.ShiftDetails(f.Org.Id, f.ZavodAti.Id));
        Assert.Empty(f.Db.ShiftDetails(f.Org.Id, f.StolovayaAti.Id));
        Assert.Equal(67517m, Assert.Single(f.Db.PointDaySummaries()).FiscalElectronic);
    }

    [Fact]
    public void HistoricalFiscalData_IsMovedToCorrectLocation()
    {
        using var f = new Fixture();
        const string fn = "9287440300000021";
        f.SeedWrongHistoricalFiscalData(KnownBusinessRules.AtiAppetitRegisterSerial, fn, 67517m);

        f.Db.Initialize();

        var binding = Assert.Single(f.Db.RegisterBindings());
        Assert.Equal(f.ZavodAti.Id, binding.LocationId);
        Assert.Equal(BindingSource.Rule, binding.BindingSource);
        var day = Assert.Single(f.Db.PointDaySummaries(f.Org.Id, 2026, 8));
        Assert.Equal(f.ZavodAti.Id, day.LocationId);
        Assert.Equal(67517m, day.FiscalElectronic);
        Assert.Empty(f.Db.ShiftDetails(f.Org.Id, f.StolovayaAti.Id));
    }

    [Fact]
    public void KktReassignment_DoesNotMoveBankOperations()
    {
        using var f = new Fixture();
        const string fn = "9287440300000031";
        f.SeedWrongHistoricalFiscalData(KnownBusinessRules.AtiAppetitRegisterSerial, fn, 67517m);
        f.Db.Insert(new CashOperation(
            "Sber.Acquiring", "bank-ati", f.Org.Id, f.StolovayaAti.Id,
            new DateTimeOffset(2026, 8, 31, 12, 0, 0, TimeSpan.FromHours(5)),
            SourceKind.Bank, OperationKind.Sale, PaymentKind.Electronic, 500m,
            FiscalDriveNumber: fn,
            KktSerial: KnownBusinessRules.AtiAppetitRegisterSerial));
        var terminal = new TerminalBinding(Guid.NewGuid(), f.Org.Id, f.StolovayaAti.Id, "Sber", "ATI-TID");
        f.Db.Save(terminal);

        f.Db.Initialize();

        Assert.Equal(f.ZavodAti.Id, ReadOperationLocation(f.Db, "Taxcom.ShiftReport", "historic:electronic"));
        Assert.Equal(f.StolovayaAti.Id, ReadOperationLocation(f.Db, "Sber.Acquiring", "bank-ati"));
        Assert.Equal(f.StolovayaAti.Id, Assert.Single(f.Db.TerminalBindings()).LocationId);
    }

    [Fact]
    public void Ati_August2026_TotalsRemain156163_AndSplitCorrectly()
    {
        using var f = new Fixture();

        f.Import("Кулинария Аппетит", KnownBusinessRules.AtiAppetitRegisterSerial, "9287440300000041", 67517m, shift: 1);
        f.Import("Меркурий 180Ф", KnownBusinessRules.AtiMercuryRegisterSerial, "9287440300000042", 88646m, shift: 2);

        var days = f.Db.PointDaySummaries(f.Org.Id, 2026, 8);
        Assert.Equal(67517m, Assert.Single(days, x => x.LocationId == f.ZavodAti.Id).FiscalElectronic);
        Assert.Equal(88646m, Assert.Single(days, x => x.LocationId == f.StolovayaAti.Id).FiscalElectronic);
        Assert.Equal(156163m, days.Sum(x => x.FiscalElectronic ?? 0m));
    }

    private static Guid? ReadOperationLocation(Database database, string source, string externalId)
    {
        using var db = new SqliteConnection("Data Source=" + database.Path);
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT location_id FROM operations WHERE source=$source AND external_id=$external";
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$external", externalId);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Guid.Parse(Convert.ToString(value)!);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ati-kkt-tests-" + Guid.NewGuid().ToString("N"));
        public Database Db { get; }
        public Organization Org { get; }
        public Location StolovayaAti { get; }
        public Location ZavodAti { get; }

        public Fixture()
        {
            Directory.CreateDirectory(_root);
            Db = new Database(Path.Combine(_root, "cash.db"));
            Db.Initialize();
            Org = new Organization(Guid.NewGuid(), "ООО КОП", "6683009222");
            StolovayaAti = new Location(Guid.NewGuid(), Org.Id, KnownBusinessRules.AtiMercuryPointName, "Плеханова 64");
            ZavodAti = new Location(Guid.NewGuid(), Org.Id, KnownBusinessRules.AtiAppetitPointName, "Завод АТИ");
            Db.Save(Org);
            Db.Save(StolovayaAti);
            Db.Save(ZavodAti);
        }

        public TaxcomShiftImportSummary Import(
            string displayName,
            string serial,
            string fn,
            decimal electronic,
            int shift = 1,
            string point = "Без торговой точки")
        {
            var path = Path.Combine(_root, "ИНН 6683009222 Сводный отчет по сменам.xlsx");
            CreateWorkbook(path, displayName, serial, fn, electronic, shift, point);
            return new TaxcomShiftReportImporter(Db).ImportFiles([path]);
        }

        public void SeedWrongHistoricalFiscalData(string serial, string fn, decimal electronic)
        {
            Db.SaveRegisterBinding(new RegisterBinding(
                Guid.NewGuid(), Org.Id, StolovayaAti.Id, fn,
                BindingSource: BindingSource.Rule,
                KktSerial: serial,
                DisplayName: "Кулинария Аппетит"));

            var closed = new DateTimeOffset(2026, 8, 31, 15, 0, 0, TimeSpan.FromHours(5));
            Db.Save(new ShiftClosure(
                "Taxcom.ShiftReport", "historic", Org.Id, StolovayaAti.Id, closed,
                electronic, 0m, electronic,
                FiscalDriveNumber: fn,
                ShiftNumber: 1,
                KktSerial: serial,
                RegisterDisplayName: "Кулинария Аппетит"));
            Db.UpsertFiscalOperation(new CashOperation(
                "Taxcom.ShiftReport", "historic:electronic", Org.Id, StolovayaAti.Id, closed,
                SourceKind.Fiscal, OperationKind.Sale, PaymentKind.Electronic, electronic,
                FiscalDriveNumber: fn,
                KktSerial: serial,
                RegisterDisplayName: "Кулинария Аппетит"));
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_root, true); }
            catch { }
        }
    }

    private static void CreateWorkbook(
        string path,
        string displayName,
        string serial,
        string fn,
        decimal electronic,
        int shift,
        string point)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var data = new SheetData();
        worksheetPart.Worksheet = new Worksheet(data);

        data.Append(Row(1,
        [
            "Дата закрытия", "№ смены", "Выручка нал.", "Выручка безнал.", "Выручка",
            "Торговая точка", "Название ККТ", "Зав. № ККТ", "Рег. № ККТ", "Зав. № ФН"
        ]));
        data.Append(Row(2,
        [
            "31.08.2026 15:00:00", shift.ToString(), "0",
            electronic.ToString(System.Globalization.CultureInfo.InvariantCulture),
            electronic.ToString(System.Globalization.CultureInfo.InvariantCulture),
            point, displayName, serial, "0001234567890123", fn
        ]));
        worksheetPart.Worksheet.Save();

        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        sheets.Append(new Sheet { Id = workbookPart.GetIdOfPart(worksheetPart), SheetId = 1, Name = "Смены" });
        workbookPart.Workbook.Save();
    }

    private static Row Row(uint rowIndex, IReadOnlyList<string> values)
    {
        var row = new Row { RowIndex = rowIndex };
        for (var i = 0; i < values.Count; i++)
        {
            row.Append(new Cell
            {
                CellReference = ColumnName(i) + rowIndex,
                DataType = CellValues.InlineString,
                InlineString = new InlineString(new Text(values[i]))
            });
        }
        return row;
    }

    private static string ColumnName(int zeroBased)
    {
        var value = zeroBased + 1;
        var result = string.Empty;
        while (value > 0)
        {
            value--;
            result = (char)('A' + value % 26) + result;
            value /= 26;
        }
        return result;
    }
}
