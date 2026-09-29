using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using KopCashDesk.Core;
using KopCashDesk.Data;
using KopCashDesk.Desktop;
using Microsoft.Data.Sqlite;
using Xunit;
using CoreLocation = KopCashDesk.Core.Location;

namespace KopCashDesk.Tests;

public sealed class GarantMayCorrectionTests
{
    private static readonly string[] SberHeaders =
    [
        "Наименование юридического лица",
        "ИНН",
        "Наименование ТСТ",
        "Адрес ТСТ",
        "Номер терминала",
        "RRN",
        "Дата операции",
        "Сумма операции"
    ];

    [Fact]
    public void Dvvs_ReconciliationUsesFiscalCashlessInsteadOfInflatedRawSber()
    {
        var folder = NewFolder();
        try
        {
            var db = new Database(Path.Combine(folder, "cashdesk.db"));
            db.Initialize();

            var org = new Organization(Guid.NewGuid(), "ООО ГАРАНТ", KnownBusinessRules.GarantTaxId);
            var dvvs = new CoreLocation(Guid.NewGuid(), org.Id, KnownBusinessRules.GarantDvvsPointName, "Екатеринбург, ул. Универсиады, 11");
            db.Save(org);
            db.Save(dvvs);
            KnownBusinessRules.ApplyPending(db);

            var at = new DateTimeOffset(2026, 5, 11, 12, 0, 0, TimeSpan.FromHours(5));
            db.Insert(new CashOperation(
                "Sber.Acquiring", "raw-bank", org.Id, dvvs.Id, at,
                SourceKind.Bank, OperationKind.Sale, PaymentKind.Electronic, 62251m));
            db.UpsertFiscalOperation(new CashOperation(
                "Taxcom.FiscalDocuments", "fiscal", org.Id, dvvs.Id, at,
                SourceKind.Fiscal, OperationKind.Sale, PaymentKind.Electronic, 55421m,
                FiscalDriveNumber: "7384440900680170",
                KktSerial: KnownBusinessRules.GarantDvvs1Serial,
                RegistrationNumber: KnownBusinessRules.GarantDvvs1Rnm,
                RegisterDisplayName: "ДВВС Касса 1"));

            var row = Assert.Single(db.CanonicalPointDaySummaries(org.Id, 2026, 5, dvvs.Id));
            Assert.Equal(55421m, row.FiscalElectronic);
            Assert.Equal(55421m, row.BankElectronic);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(folder);
        }
    }

    [Fact]
    public void Sredneuralsk_ReimportMovesPreviouslyMisboundSberOperationOutOfDvvs()
    {
        var folder = NewFolder();
        var report = Path.Combine(folder, "may-sredneuralsk.xlsx");
        try
        {
            CreateSberWorkbook(report);

            var db = new Database(Path.Combine(folder, "cashdesk.db"));
            db.Initialize();

            var org = new Organization(Guid.NewGuid(), "ООО ГАРАНТ", KnownBusinessRules.GarantTaxId);
            var dvvs = new CoreLocation(Guid.NewGuid(), org.Id, KnownBusinessRules.GarantDvvsPointName, "Екатеринбург, ул. Универсиады, 11");
            db.Save(org);
            db.Save(dvvs);

            // Simulate the old database state where the Среднеуральск TID was attached to ДВВС.
            db.Save(new TerminalBinding(
                Guid.NewGuid(), org.Id, dvvs.Id, "Sber",
                KnownBusinessRules.GarantSredneuralskPosTid,
                "191000238936", "POS", BindingSource.Manual, true));

            var first = new SberAcquiringImporter(db).ImportFiles([report]);
            Assert.Equal(1, first.OperationsAdded);
            Assert.Equal(KnownBusinessRules.GarantDvvsPointName, Assert.Single(db.Operations(org.Id)).Location);

            KnownBusinessRules.ApplyPending(db);
            var sred = Assert.Single(db.Locations(), x => x.Name == KnownBusinessRules.GarantSredneuralskPointName);
            Assert.Equal(sred.Id,
                Assert.Single(db.TerminalBindings(), x => x.TerminalId == KnownBusinessRules.GarantSredneuralskPosTid).LocationId);

            var second = new SberAcquiringImporter(db).ImportFiles([report]);
            Assert.Equal(0, second.OperationsAdded);
            Assert.True(second.DuplicatesIgnored >= 1);

            var operation = Assert.Single(db.Operations(org.Id));
            Assert.Equal(KnownBusinessRules.GarantSredneuralskPointName, operation.Location);
            Assert.Equal(370m, operation.Amount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(folder);
        }
    }

    private static void CreateSberWorkbook(string path)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var data = new SheetData();
        worksheetPart.Worksheet = new Worksheet(data);

        data.Append(TextRow(1, SberHeaders));
        data.Append(TextRow(2,
        [
            "ОБЩЕСТВО С ОГРАНИЧЕННОЙ ОТВЕТСТВЕННОСТЬЮ \"ГАРАНТ\"",
            KnownBusinessRules.GarantTaxId,
            "кафеАппетит",
            "Свердловская область, Среднеуральск, ул Набережная, здание 8а",
            KnownBusinessRules.GarantSredneuralskPosTid,
            "613103602431",
            "11.05.2026 16:17:37",
            "370"
        ]));

        worksheetPart.Worksheet.Save();
        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1,
            Name = "Отчет"
        });
        workbookPart.Workbook.Save();
    }

    private static Row TextRow(uint rowIndex, IReadOnlyList<string> values)
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

    private static string NewFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), "garant-may-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string folder)
    {
        try { Directory.Delete(folder, true); } catch { }
    }
}
