using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using KopCashDesk.Core;
using Location = KopCashDesk.Core.Location;
using KopCashDesk.Data;
using KopCashDesk.Desktop;
using Xunit;

namespace KopCashDesk.Tests;

public sealed class SberPointIdentityTests
{
    private static readonly string[] Headers =
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
    public void Import_DoesNotMergeSameNameDifferentAddressOrDifferentNameSameAddress()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"kopcashdesk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var workbookPath = Path.Combine(folder, "points.xlsx");
        var databasePath = Path.Combine(folder, "cashdesk.db");

        try
        {
            CreateWorkbook(workbookPath);
            var database = new Database(databasePath);
            database.Initialize();

            var summary = new SberAcquiringImporter(database).ImportFiles([workbookPath]);

            Assert.Equal(4, summary.OperationsAdded);
            var locations = database.Locations();
            Assert.Equal(4, locations.Count);
            Assert.Equal(2, locations.Count(x => x.Name == "Кафе Аппетит"));
            Assert.Contains(locations, x => x.Name == "Пышма Верхняя");
            Assert.Contains(locations, x => x.Name == "ЦХиЭГ");
        }
        finally
        {
            try { Directory.Delete(folder, true); }
            catch { }
        }
    }

    [Theory]
    [InlineData("Музыкальный колледж", "Музыкалка", "39413112", "45697790")]
    [InlineData("Кулинария Аппетит", "Кулинария Аппетит", "42526205", "42603950")]
    public void CopKnownTerminal_RepairsVerifiedLegacyPointAndPreventsItsReturn(
        string targetName, string legacyName, string headTid, string childTid)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"kopcashdesk-cop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var database = new Database(Path.Combine(folder, "cashdesk.db"));
            database.Initialize();
            var org = new Organization(Guid.NewGuid(), "ЦОП", "6683009222");
            var main = new Location(Guid.NewGuid(), org.Id, targetName, "Прежний адрес");
            var alias = new Location(Guid.NewGuid(), org.Id, legacyName, "Адрес в Сбере");
            database.Save(org);
            database.Save(main);
            database.Save(alias);
            database.Save(new TerminalBinding(Guid.NewGuid(), org.Id, main.Id, "Sber", headTid));
            database.Save(new TerminalBinding(Guid.NewGuid(), org.Id, alias.Id, "Sber", childTid));
            const string hash = "75a572bb1b6631111daa04395bc69683191861fab676be170c5881878efd685e";
            var documentId = database.RegisterSourceDocument("Sber.Acquiring", hash, hash, "sber.xlsx");
            database.UpsertBankOperation(new CashOperation("Sber.Acquiring", "legacy", org.Id, alias.Id,
                new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.FromHours(5)),
                SourceKind.Bank, OperationKind.Sale, PaymentKind.Electronic, 200m, documentId));

            Assert.Equal(1, KnownBusinessRules.ApplyPending(database));
            Assert.Equal(0, KnownBusinessRules.ApplyPending(database));
            Assert.Equal(main.Id, Assert.Single(database.Locations()).Id);
            Assert.Equal(200m, Assert.Single(database.PointDaySummaries(org.Id, 2026, 10)).BankElectronic);

            var report = Path.Combine(folder, "new.xlsx");
            CreateWorkbook(report, [Values(legacyName, "Адрес в Сбере", childTid, "1", org.TaxId)]);
            var imported = new SberAcquiringImporter(database).ImportFiles([report]);
            Assert.Equal(1, imported.OperationsAdded);
            Assert.Equal(0, imported.LocationsCreated);
            Assert.Equal(main.Id, Assert.Single(database.PointDaySummaries(org.Id, 2026, 9)).LocationId);
            Assert.All(database.TerminalBindings(), binding => Assert.Equal(main.Id, binding.LocationId));
        }
        finally
        {
            try { Directory.Delete(folder, true); }
            catch { }
        }
    }

    private static void CreateWorkbook(string path, string[][]? rows = null)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var data = new SheetData();
        worksheetPart.Worksheet = new Worksheet(data);
        data.Append(CreateRow(1, Headers));
        rows ??=
        [
            Values("Кафе Аппетит", "Адрес кафе 1", "100001", "1"),
            Values("Кафе Аппетит", "Адрес кафе 2", "100002", "2"),
            Values("Пышма Верхняя", "Один общий адрес", "100003", "3"),
            Values("ЦХиЭГ", "Один общий адрес", "100004", "4")
        ];
        for (var index = 0; index < rows.Length; index++)
            data.Append(CreateRow((uint)index + 2, rows[index]));
        worksheetPart.Worksheet.Save();
        sheets.Append(new Sheet { Id = workbookPart.GetIdOfPart(worksheetPart), SheetId = 1, Name = "Отчет" });
        workbookPart.Workbook.Save();
    }

    private static string[] Values(string point, string address, string terminal, string rrn, string taxId = "6600000000") =>
    [
        "ОБЩЕСТВО С ОГРАНИЧЕННОЙ ОТВЕТСТВЕННОСТЬЮ \"ГАРАНТ\"",
        taxId,
        point,
        address,
        terminal,
        rrn,
        "10.09.2026 12:00:00",
        "100.00"
    ];

    private static Row CreateRow(uint rowIndex, IReadOnlyList<string> values)
    {
        var row = new Row { RowIndex = rowIndex };
        for (var index = 0; index < values.Count; index++)
        {
            row.Append(new Cell
            {
                CellReference = ColumnName(index) + rowIndex,
                DataType = CellValues.InlineString,
                InlineString = new InlineString(new Text(values[index]))
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
