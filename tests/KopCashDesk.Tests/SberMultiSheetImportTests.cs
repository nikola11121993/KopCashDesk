using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using KopCashDesk.Data;
using KopCashDesk.Desktop;
using Xunit;

namespace KopCashDesk.Tests;

public sealed class SberMultiSheetImportTests
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
    public void AnnualWorkbook_ImportsCompatibleRowsFromAllSheets()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"kopcashdesk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var workbookPath = Path.Combine(folder, "sber-year.xlsx");
        var databasePath = Path.Combine(folder, "cashdesk.db");

        try
        {
            CreateWorkbook(workbookPath);
            var database = new Database(databasePath);
            database.Initialize();

            var summary = new SberAcquiringImporter(database).ImportFiles([workbookPath]);

            Assert.Equal(1, summary.FilesProcessed);
            Assert.Equal(2, summary.SheetsProcessed);
            Assert.Equal(2, summary.OperationsAdded);
            var days = database.PointDaySummaries(year: 2026);
            Assert.Equal(2, days.Count);
            Assert.Contains(days, x => x.Date == new DateOnly(2026, 1, 10) && x.BankElectronic == 100m);
            Assert.Contains(days, x => x.Date == new DateOnly(2026, 2, 5) && x.BankElectronic == 250m);
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

        AddSheet(workbookPart, sheets, 1, "Январь", "10.01.2026 12:00:00", "100.00", "111111", "43151534");
        AddSheet(workbookPart, sheets, 2, "Февраль", "05.02.2026 13:00:00", "250.00", "222222", "43151534");
        workbookPart.Workbook.Save();
    }

    private static void AddSheet(WorkbookPart workbookPart, Sheets sheets, uint sheetId, string name, string date, string amount, string rrn, string terminal)
    {
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var sheetData = new SheetData();
        worksheetPart.Worksheet = new Worksheet(sheetData);
        sheetData.Append(CreateRow(1, Headers));
        sheetData.Append(CreateRow(2,
        [
            "ОБЩЕСТВО С ОГРАНИЧЕННОЙ ОТВЕТСТВЕННОСТЬЮ \"КОП\"",
            "6600000000",
            "Мира_4",
            "Свердловская обл., Асбест, ул. Мира, 4",
            terminal,
            rrn,
            date,
            amount
        ]));
        worksheetPart.Worksheet.Save();

        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = sheetId,
            Name = name
        });
    }

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
