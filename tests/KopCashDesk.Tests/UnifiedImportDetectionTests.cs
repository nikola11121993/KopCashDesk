using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using KopCashDesk.Desktop;
using Xunit;

namespace KopCashDesk.Tests;

public sealed class UnifiedImportDetectionTests
{
    [Fact]
    public void OrdinaryTaxcomShiftReport_IsDetectedAsTaxcom()
    {
        var folder = Path.Combine(Path.GetTempPath(), "KopCashDesk.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "ИНН 6683009222 КПП 668301001 Сводный отчет по сменам за период.xlsx");

        try
        {
            using (var document = SpreadsheetDocument.Create(path, DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook))
            {
                var workbookPart = document.AddWorkbookPart();
                workbookPart.Workbook = new Workbook();
                var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
                var data = new SheetData();
                worksheetPart.Worksheet = new Worksheet(data);

                data.Append(RowOf(1, "Такском-Касса"));
                data.Append(RowOf(2, "Сводный отчет по сменам"));
                data.Append(RowOf(10,
                    "Дата открытия", "Дата закрытия", "№ смены", "Кол-во чеков", "Средний чек",
                    "Выручка нал.", "Выручка безнал.", "Выручка",
                    "Торговая точка", "Название ККТ", "Зав. № ККТ", "Рег. № ККТ", "Зав. № ФН"));

                var sheets = workbookPart.Workbook.AppendChild(new Sheets());
                sheets.Append(new Sheet
                {
                    Id = workbookPart.GetIdOfPart(worksheetPart),
                    SheetId = 1,
                    Name = "Смены"
                });
                workbookPart.Workbook.Save();
            }

            Assert.Equal(UnifiedImportKind.Taxcom, UnifiedImportWindow.Detect(path));
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch { }
        }
    }

    [Fact]
    public void TaxcomShiftReport_WithWhitespaceVariants_IsStillDetected()
    {
        var folder = Path.Combine(Path.GetTempPath(), "KopCashDesk.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "Сводный отчет по сменам.xlsx");

        try
        {
            using (var document = SpreadsheetDocument.Create(path, DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook))
            {
                var workbookPart = document.AddWorkbookPart();
                workbookPart.Workbook = new Workbook();
                var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
                var data = new SheetData();
                worksheetPart.Worksheet = new Worksheet(data);

                data.Append(RowOf(1, "Сводный  отчет\nпо сменам"));
                data.Append(RowOf(10,
                    "Дата закрытия ", " № смены", "Выручка нал.\u00A0", "Выручка безнал.", "Рег. № ККТ"));

                var sheets = workbookPart.Workbook.AppendChild(new Sheets());
                sheets.Append(new Sheet
                {
                    Id = workbookPart.GetIdOfPart(worksheetPart),
                    SheetId = 1,
                    Name = "Смены"
                });
                workbookPart.Workbook.Save();
            }

            Assert.Equal(UnifiedImportKind.Taxcom, UnifiedImportWindow.Detect(path));
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch { }
        }
    }

    private static Row RowOf(uint index, params string[] values)
    {
        var row = new Row { RowIndex = index };
        for (var i = 0; i < values.Length; i++)
        {
            row.Append(new Cell
            {
                CellReference = Column(i) + index,
                DataType = CellValues.InlineString,
                InlineString = new InlineString(new Text(values[i]))
            });
        }
        return row;
    }

    private static string Column(int zeroBased)
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
}
