using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using KopCashDesk.Core;
using KopCashDesk.Data;
using KopCashDesk.Desktop;
using Xunit;

namespace KopCashDesk.Tests;

public sealed class KnownRegisterPointTests
{
    [Fact]
    public void ReftinskayaRegisterSerial_UsesExistingCafeteria6Point()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"kopcashdesk-known-register-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var report = Path.Combine(folder, "ИНН 6683009222 Сводный отчет по сменам.xlsx");
        var databasePath = Path.Combine(folder, "cashdesk.db");

        try
        {
            CreateWorkbook(report);
            var db = new Database(databasePath);
            db.Initialize();
            var organization = new Organization(Guid.NewGuid(), "ООО КОП", "6683009222");
            var point = new KopCashDesk.Core.Location(Guid.NewGuid(), organization.Id, "Рефтинская ГРЭС", "Рефтинский");
            db.Save(organization);
            db.Save(point);

            var result = new TaxcomShiftReportImporter(db).ImportFiles([report]);

            Assert.Equal(1, result.ShiftsProcessed);
            Assert.Equal(0, result.LocationsCreated);
            Assert.Equal(point.Id, Assert.Single(db.RegisterBindings()).LocationId);
            Assert.Equal(point.Id, Assert.Single(db.PointDaySummaries(organization.Id, 2026, 9)).LocationId);
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
            "10.09.2026 15:00:00", "55", "100", "9900", "10000",
            "Без торговой точки", "Касса", "00106900361561", "0001234567890123", "9287440300123456"
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
