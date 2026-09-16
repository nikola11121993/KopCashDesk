using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using KopCashDesk.Desktop;
using System.IO.Compression;
using Xunit;

namespace KopCashDesk.Tests;

public sealed class SmartImportDetectionTests
{
    [Fact]
    public void ShortSberReport_IsDetected()
    {
        var path = Workbook(
            ["Наименование юридического лица", "Номер расчётного счёта", "Адрес ТСТ", "Номер терминала", "RRN", "Дата операции", "Сумма операции"],
            ["ООО ЦОП", "1", "Адрес", "34765811", "600395651541", "46025.2", "95"]);
        try { Assert.Equal(SmartImportKind.Sber, SmartReportDetector.Detect(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReimbursementSberReport_IsDetected()
    {
        var path = Workbook(
            ["RRN", "Наименование организации", "Адрес", "Номер мерчанта", "Наименование ТСТ", "Номер терминала", "Дата операции", "Сумма операции", "Тип операции"],
            ["625141363356", "ООО ГАРАНТ", "Адрес", "191", "КафеАппетит_SBP", "37712391", "46273.3", "625", "Покупка СБП - стороннее"]);
        try { Assert.Equal(SmartImportKind.Sber, SmartReportDetector.Detect(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AlternateClosedShifts_IsDetected()
    {
        var path = Workbook(
            ["Торговая точка", "Номер смены", "Дата закрытия смены", "Время закрытия смены", "Номер ФН", "Выручка", "Получено наличными", "Получено безналичными", "Наименование ККТ", "Заводской номер ККТ", "РегНомер ККТ"],
            ["Столовая", "968", "2026-04-30", "14:21:00", "7280440500124356", "415", "0", "415", "Столовая", "00106900361561", "0007277427055386"]);
        try { Assert.Equal(SmartImportKind.ClosedShifts, SmartReportDetector.Detect(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void UbrdDailySummary_IsDetected()
    {
        var path = Workbook(
            ["Свод по дням (по опердню)", "", "", "", "Пояснение"],
            ["Точка", "Дата", "Сумма", "", "Терминал"],
            ["BUFET", "46024", "32490", "", "26204835"]);
        try { Assert.Equal(SmartImportKind.UbrdDaily, SmartReportDetector.Detect(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MixedZip_WithSberAndTaxcom_IsDetectedAsSber()
    {
        var sber = Workbook(
            ["Наименование юридического лица", "ИНН", "Наименование ТСТ", "Адрес ТСТ", "Номер терминала", "RRN", "Дата операции", "Сумма операции"],
            ["ООО ЦОП", "6683009222", "Точка", "Адрес", "1", "2", "46025", "100"]);
        var taxcom = Workbook(
            ["Дата и время", "Документ", "Тип операции", "Наличными", "Безналичными", "Сумма", "№ ФД", "ФПД", "Название ККТ"],
            ["2026-09-01", "Кассовый чек", "Приход", "0", "100", "100", "1", "2", "ККТ"]);
        var zip = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".zip");
        try
        {
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(sber, "sber.xlsx");
                archive.CreateEntryFromFile(taxcom, "taxcom.xlsx");
            }
            Assert.Equal(SmartImportKind.Sber, SmartReportDetector.Detect(zip));
        }
        finally { File.Delete(sber); File.Delete(taxcom); File.Delete(zip); }
    }

    [Theory]
    [InlineData("42526205", "ЗАВОД АТИ")]
    [InlineData("34723825", "Столовая АТИ")]
    [InlineData("34773474", "Рефтинская ГРЭС 6 столовая")]
    [InlineData("42162000", "Чапаева 28")]
    [InlineData("45080359", "Ладыженского 7")]
    public void KnownTerminal_UsesPhysicalPoint(string terminal, string expected)
        => Assert.Equal(expected, SmartSberAcquiringImporter.CanonicalPointNameForTerminal(terminal));

    private static string Workbook(params string[][] rows)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var sheetData = new SheetData();
        worksheetPart.Worksheet = new Worksheet(sheetData);
        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        sheets.Append(new Sheet { Id = workbookPart.GetIdOfPart(worksheetPart), SheetId = 1, Name = "Sheet1" });
        uint rowIndex = 1;
        foreach (var values in rows)
        {
            var row = new Row { RowIndex = rowIndex++ };
            foreach (var value in values)
                row.Append(new Cell { DataType = CellValues.InlineString, InlineString = new InlineString(new Text(value ?? string.Empty)) });
            sheetData.Append(row);
        }
        workbookPart.Workbook.Save();
        return path;
    }
}
