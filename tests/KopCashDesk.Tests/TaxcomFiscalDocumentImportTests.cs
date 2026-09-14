using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using KopCashDesk.Core;
using KopCashDesk.Data;
using KopCashDesk.Desktop;
using Xunit;
using CoreLocation = KopCashDesk.Core.Location;

namespace KopCashDesk.Tests;

public sealed class TaxcomFiscalDocumentImportTests
{
    private static readonly string[] Headers =
    [
        "Дата и время", "Документ", "№ смены", "№ за смену", "Система налогообложения", "Тип операции",
        "Наличными", "Безналичными", "Аванс", "В кредит", "Обмен", "Сумма", "Сумма без НДС",
        "Сумма с НДС 0%", "НДС 5%", "НДС 7%", "НДС 10%", "НДС 18%", "НДС 20%", "НДС 22%",
        "НДС 10/110", "НДС 18/118", "НДС 20/120", "НДС 22/122", "НДС 5/105", "НДС 7/107",
        "Кассир", "№ ФД", "ФПД", "Торговая точка", "Код торговой точки", "Название ККТ", "Зав. № ККТ",
        "Рег. № ККТ", "Зав. № ФН", "Предоплата 100%", "Частичная предоплата", "Аванс расчет", "Полный расчет",
        "Частичный расчет и кредит", "Передача в кредит", "Оплата кредита", "Дополнительный реквизит чека (БСО)",
        "Наименование дополнительного реквизита пользователя", "Значение дополнительного реквизита пользователя", "Ссылка на просмотр чека"
    ];

    [Fact]
    public void EmptyFiscalDocumentReport_FromUserFormat_IsDetected()
    {
        var folder = NewFolder();
        var report = Path.Combine(folder, "ИНН 6683009222 КПП 668301001 Сводный отчет по фискальным документам за период c 01.09.26 по 14.09.26.xlsx");
        try
        {
            CreateWorkbook(report, includeDocuments: false);
            Assert.Equal(UnifiedImportKind.TaxcomFiscalDocuments, UnifiedImportWindow.Detect(report));
        }
        finally
        {
            TryDelete(folder);
        }
    }

    [Fact]
    public void FiscalDocuments_ImportSaleAndReturn_AndDoNotDoubleOnReimport()
    {
        var folder = NewFolder();
        var report = Path.Combine(folder, "ИНН 6683009222 КПП 668301001 Сводный отчет по фискальным документам.xlsx");
        var databasePath = Path.Combine(folder, "cashdesk.db");

        try
        {
            CreateWorkbook(report, includeDocuments: true);
            var database = new Database(databasePath);
            database.Initialize();
            var organization = new Organization(Guid.NewGuid(), "ООО ЦОП", "6683009222");
            var location = new CoreLocation(Guid.NewGuid(), organization.Id, "Рефтинская ГРЭС", "здание 6");
            database.Save(organization);
            database.Save(location);

            var first = new TaxcomFiscalDocumentImporter(database).ImportFiles([report]);
            Assert.Equal(1, first.FilesProcessed);
            Assert.Equal(1, first.SheetsProcessed);
            Assert.Equal(2, first.DocumentsProcessed);
            Assert.Equal(2, first.FiscalOperationsInserted);
            Assert.Equal(0, first.FiscalOperationsUpdated);
            Assert.Equal(800m, first.ElectronicTotal);
            Assert.Equal(0m, first.CashTotal);
            Assert.Equal(800m, first.RevenueTotal);

            var day = Assert.Single(database.CanonicalPointDaySummaries(organization.Id, 2026, 9, location.Id));
            Assert.Equal(new DateOnly(2026, 9, 10), day.Date);
            Assert.Equal(800m, day.FiscalElectronic);
            Assert.Equal(0, day.ShiftCount);

            var second = new TaxcomFiscalDocumentImporter(database).ImportFiles([report]);
            Assert.Equal(0, second.FiscalOperationsInserted);
            Assert.Equal(2, second.FiscalOperationsUpdated);
            Assert.Equal(800m, Assert.Single(database.CanonicalPointDaySummaries(organization.Id, 2026, 9, location.Id)).FiscalElectronic);
        }
        finally
        {
            TryDelete(folder);
        }
    }

    private static void CreateWorkbook(string path, bool includeDocuments)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var data = new SheetData();
        worksheetPart.Worksheet = new Worksheet(data);

        data.Append(TextRow(1, ["Такском-Касса"]));
        data.Append(TextRow(2, ["Сводный отчет по фискальным документам"]));
        data.Append(TextRow(3, ["Дата формирования", "14.09.2026 11:50"]));
        data.Append(TextRow(4, ["Период", "с 01.09.2026 00:00 по 14.09.2026 23:59"]));
        data.Append(TextRow(5, ["Торговая точка", "Без торговой точки"]));
        data.Append(TextRow(6, ["ККТ", "00106900361561"]));
        data.Append(TextRow(7, ["Тип документа", "Все"]));
        data.Append(TextRow(8, ["Признак расчета", "Все"]));
        data.Append(TextRow(9, ["Тип оплаты", "Все"]));
        data.Append(TextRow(11, Headers));

        if (includeDocuments)
        {
            data.Append(TextRow(12, DocumentRow("10.09.2026 12:00:00", "Кассовый чек", "140", "1", "Приход", "0", "1000", "1000", "9001", "111111", "7381440901042300")));
            data.Append(TextRow(13, DocumentRow("10.09.2026 12:15:00", "Кассовый чек", "140", "2", "Возврат прихода", "0", "200", "200", "9002", "222222", "7381440901042300")));
        }

        data.Append(TextRow(includeDocuments ? 14u : 12u, ["Итог"]));
        worksheetPart.Worksheet.Save();

        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1,
            Name = "Фискальные документы"
        });
        workbookPart.Workbook.Save();
    }

    private static string[] DocumentRow(
        string date,
        string document,
        string shift,
        string numberInShift,
        string operation,
        string cash,
        string electronic,
        string total,
        string fd,
        string fpd,
        string fn)
    {
        var values = Enumerable.Repeat(string.Empty, Headers.Length).ToArray();
        void Set(string header, string value) => values[Array.IndexOf(Headers, header)] = value;
        Set("Дата и время", date);
        Set("Документ", document);
        Set("№ смены", shift);
        Set("№ за смену", numberInShift);
        Set("Тип операции", operation);
        Set("Наличными", cash);
        Set("Безналичными", electronic);
        Set("Сумма", total);
        Set("№ ФД", fd);
        Set("ФПД", fpd);
        Set("Торговая точка", "Без торговой точки");
        Set("Зав. № ККТ", "00106900361561");
        Set("Рег. № ККТ", "0009525635035583");
        Set("Зав. № ФН", fn);
        return values;
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
                InlineString = new InlineString(new Text(values[i] ?? string.Empty))
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
        var path = Path.Combine(Path.GetTempPath(), $"kopcashdesk-taxcom-docs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string folder)
    {
        try { Directory.Delete(folder, true); }
        catch { }
    }
}
