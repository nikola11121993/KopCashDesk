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

public sealed class ModernImportAndOrganizationTests
{
    [Fact]
    public void KnownOrganizations_EnsuresExactlyThreeExpectedBusinesses()
    {
        using var f = new Fixture();
        Assert.Equal(3, KnownOrganizations.Ensure(f.Db));
        Assert.Equal(0, KnownOrganizations.Ensure(f.Db));

        var organizations = f.Db.Organizations().OrderBy(x => x.TaxId).ToArray();
        Assert.Equal(3, organizations.Length);
        Assert.Contains(organizations, x => x.TaxId == "6683009222" && x.Name == "ООО \"ЦОП\"");
        Assert.Contains(organizations, x => x.TaxId == "6683011158" && x.Name == "ООО \"ГАРАНТ\"");
        Assert.Contains(organizations, x => x.TaxId == "6603017238" && x.Name == "ООО \"КОП\"");
    }

    [Fact]
    public void GarantUnknownToCopTerminal_CreatesTrustedGarantPointAndImportsOperation()
    {
        using var f = new Fixture();
        var report = Path.Combine(f.Root, "garant.xlsx");
        CreateSberWorkbook(report,
            "ООО ГАРАНТ", "6683011158", "КафеАппетит_SBP", "г. Екатеринбург, ул. Универсиады, 11",
            "37700439", "625141363356", "16.09.2026 12:00:00", "625");

        var result = new SmartSberAcquiringImporter(f.Db).ImportFiles([report]);

        Assert.Equal(1, result.OperationsAdded);
        var garant = Assert.Single(f.Db.Organizations(), x => x.TaxId == KnownOrganizations.GarantTaxId);
        var point = Assert.Single(f.Db.Locations(), x => x.OrganizationId == garant.Id);
        Assert.Equal("КафеАппетит", point.Name);
        Assert.Contains("Универсиады", point.Address);
        Assert.Equal(625m, f.Db.SumOperations("Sber.Acquiring", garant.Id));
    }

    [Fact]
    public void CopUnknownTerminal_DoesNotInventAnEighthPoint()
    {
        using var f = new Fixture();
        var report = Path.Combine(f.Root, "cop.xlsx");
        CreateSberWorkbook(report,
            "ООО ЦОП", "6683009222", "Столовая 5", "Рефтинская ГРЭС, здание 5",
            "42830544", "600000000001", "16.09.2026 12:00:00", "100");

        var result = new SmartSberAcquiringImporter(f.Db).ImportFiles([report]);

        Assert.Equal(0, result.OperationsAdded);
        var cop = Assert.Single(f.Db.Organizations(), x => x.TaxId == KnownOrganizations.CenterTaxId);
        Assert.DoesNotContain(f.Db.Locations(), x => x.OrganizationId == cop.Id);
    }

    [Fact]
    public void KopHrizotil_37446495_IsRoutedToSiestaForFiscalReconciliation()
    {
        using var f = new Fixture();
        var hrizotilReport = Path.Combine(f.Root, "hrizotil-main.xlsx");
        var siestaFiscalReport = Path.Combine(f.Root, "hrizotil-second-terminal.xlsx");

        CreateSberWorkbook(hrizotilReport,
            "ООО КОП", "6603017238", "Хризотил", "г. Асбест, ул. Королева, 30",
            "37446500", "700000000001", "19.09.2026 12:00:00", "7000");
        CreateSberWorkbook(siestaFiscalReport,
            "ООО КОП", "6603017238", "Хризотил", "г. Асбест, ул. Королева, 30",
            "37446495", "500000000001", "19.09.2026 12:05:00", "500");

        var result = new SmartSberAcquiringImporter(f.Db).ImportFiles([hrizotilReport, siestaFiscalReport]);

        Assert.Equal(2, result.OperationsAdded);
        var kop = Assert.Single(f.Db.Organizations(), x => x.TaxId == KnownOrganizations.KopTaxId);
        var locations = f.Db.Locations().Where(x => x.OrganizationId == kop.Id).ToArray();
        var hrizotil = Assert.Single(locations, x => x.Name == "Хризотил");
        var siesta = Assert.Single(locations, x => x.Name == "Кафе Сиеста");

        var day = f.Db.PointDaySummaries(kop.Id, 2026, 9).Where(x => x.Date == new DateOnly(2026, 9, 19)).ToArray();
        Assert.Equal(7000m, Assert.Single(day, x => x.LocationId == hrizotil.Id).BankElectronic);
        Assert.Equal(500m, Assert.Single(day, x => x.LocationId == siesta.Id).BankElectronic);

        var bindings = f.Db.TerminalBindings().Where(x => x.OrganizationId == kop.Id).ToArray();
        Assert.Equal(hrizotil.Id, Assert.Single(bindings, x => x.TerminalId == "37446500").LocationId);
        Assert.Equal(hrizotil.Id, Assert.Single(bindings, x => x.TerminalId == "37446495").LocationId);
    }

    [Fact]
    public void BatchInsert_DeduplicatesWithinOneTransaction()
    {
        using var f = new Fixture();
        KnownOrganizations.Ensure(f.Db);
        var org = Assert.Single(f.Db.Organizations(), x => x.TaxId == KnownOrganizations.GarantTaxId);
        var point = new Location(Guid.NewGuid(), org.Id, "Точка");
        f.Db.Save(point);
        var moment = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.FromHours(5));
        var operation = new CashOperation("Sber.Acquiring", "same", org.Id, point.Id, moment,
            SourceKind.Bank, OperationKind.Sale, PaymentKind.Electronic, 100m);

        Assert.Equal(1, f.Db.InsertOperationsBatch([operation, operation]));
        Assert.Equal(1, f.Db.CountOperations());
    }

    private static void CreateSberWorkbook(
        string path,
        string legalName,
        string taxId,
        string point,
        string address,
        string terminal,
        string rrn,
        string date,
        string amount)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var wb = document.AddWorkbookPart();
        wb.Workbook = new Workbook();
        var ws = wb.AddNewPart<WorksheetPart>();
        var data = new SheetData();
        ws.Worksheet = new Worksheet(data);
        data.Append(Row(1, ["Наименование юридического лица", "ИНН", "Наименование ТСТ", "Адрес ТСТ", "Номер терминала", "RRN", "Дата операции", "Сумма операции"]));
        data.Append(Row(2, [legalName, taxId, point, address, terminal, rrn, date, amount]));
        ws.Worksheet.Save();
        wb.Workbook.Append(new Sheets(new Sheet { Id = wb.GetIdOfPart(ws), SheetId = 1, Name = "Отчёт" }));
        wb.Workbook.Save();
    }

    private static Row Row(uint index, IReadOnlyList<string> values)
    {
        var row = new Row { RowIndex = index };
        for (var i = 0; i < values.Count; i++)
            row.Append(new Cell
            {
                CellReference = ColumnName(i) + index,
                DataType = CellValues.InlineString,
                InlineString = new InlineString(new Text(values[i]))
            });
        return row;
    }

    private static string ColumnName(int zeroBased)
    {
        var n = zeroBased + 1;
        var result = string.Empty;
        while (n > 0)
        {
            n--;
            result = (char)('A' + n % 26) + result;
            n /= 26;
        }
        return result;
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "modern-import-" + Guid.NewGuid().ToString("N"));
        public Database Db { get; }

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Db = new Database(Path.Combine(Root, "cash.db"));
            Db.Initialize();
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(Root, true); }
            catch { }
        }
    }
}
