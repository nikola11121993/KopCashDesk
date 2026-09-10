using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
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

    private static void CreateWorkbook(string path)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var data = new SheetData();
        worksheetPart.Worksheet = new Worksheet(data);
        data.Append(CreateRow(1, Headers));
        data.Append(CreateRow(2, Values("Кафе Аппетит", "Адрес кафе 1", "100001", "1")));
        data.Append(CreateRow(3, Values("Кафе Аппетит", "Адрес кафе 2", "100002", "2")));
        data.Append(CreateRow(4, Values("Пышма Верхняя", "Один общий адрес", "100003", "3")));
        data.Append(CreateRow(5, Values("ЦХиЭГ", "Один общий адрес", "100004", "4")));
        worksheetPart.Worksheet.Save();
        sheets.Append(new Sheet { Id = workbookPart.GetIdOfPart(worksheetPart), SheetId = 1, Name = "Отчет" });
        workbookPart.Workbook.Save();
    }

    private static string[] Values(string point, string address, string terminal, string rrn) =>
    [
        "ОБЩЕСТВО С ОГРАНИЧЕННОЙ ОТВЕТСТВЕННОСТЬЮ \"ГАРАНТ\"",
        "6600000000",
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
