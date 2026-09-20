using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using KopCashDesk.Data;
using KopCashDesk.Desktop;
using Microsoft.Data.Sqlite;
using Xunit;

namespace KopCashDesk.Tests;

public sealed class FirstOfdShiftReportImportTests
{
    [Fact]
    public void FirstOfd_KopReport_ImportsThreeKnownRegisters_AndDoesNotDoubleOnReimport()
    {
        var root = Path.Combine(Path.GetTempPath(), "firstofd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "Отчет по сменам с налогами 2026-01-01-2026-09-20.xlsx");

        try
        {
            CreateWorkbook(path);

            var db = new Database(Path.Combine(root, "cash.db"));
            db.Initialize();

            var first = new FirstOfdShiftReportImporter(db).ImportFiles([path]);

            Assert.Equal(1, first.FilesProcessed);
            Assert.Equal(3, first.Shifts);
            Assert.Equal(6, first.FiscalInserted);
            Assert.Equal(0, first.FiscalUpdated);
            Assert.Equal(2847m, first.Cash);
            Assert.Equal(9663m + 20000m + 18136m, first.Electronic);
            Assert.Equal(12510m + 20000m + 18136m, first.Revenue);

            var kop = Assert.Single(db.Organizations(), x => x.TaxId == KnownOrganizations.KopTaxId);
            var locations = db.Locations().Where(x => x.OrganizationId == kop.Id).OrderBy(x => x.Name).ToArray();

            Assert.Contains(locations, x => x.Name == "Лакомка");
            Assert.Contains(locations, x => x.Name == "Кафе Сиеста");
            Assert.Contains(locations, x => x.Name == "Хризотил");

            var bindings = db.RegisterBindings().Where(x => x.OrganizationId == kop.Id).ToArray();
            Assert.Contains(bindings, x => x.KktSerial == "04207570" && x.RegisterNumber == "0006435906064640" && x.DisplayName == "Лакомка");
            Assert.Contains(bindings, x => x.KktSerial == "0014943" && x.RegisterNumber == "0001113145061553" && x.DisplayName == "Сиеста");
            Assert.Contains(bindings, x => x.KktSerial == "00180494" && x.RegisterNumber == "0006220550041581" && x.DisplayName == "Хризотил");

            var day = db.CanonicalPointDaySummaries(kop.Id, 2026, 1)
                .Where(x => x.Date == new DateOnly(2026, 1, 4))
                .ToArray();
            Assert.Single(day);
            Assert.Equal(9663m, day[0].FiscalElectronic);
            Assert.Equal(12510m, day[0].ShiftTotal);
            Assert.Equal("Первый ОФД", day[0].FiscalSources);

            var second = new FirstOfdShiftReportImporter(db).ImportFiles([path]);
            Assert.Equal(0, second.FiscalInserted);
            Assert.Equal(6, second.FiscalUpdated);

            var all = db.CanonicalPointDaySummaries(kop.Id, 2026);
            Assert.Equal(3, all.Sum(x => x.ShiftCount));
            Assert.Equal(9663m + 20000m + 18136m, all.Sum(x => x.FiscalElectronic ?? 0m));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); }
            catch { }
        }
    }

    private static void CreateWorkbook(string path)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var wb = document.AddWorkbookPart();
        wb.Workbook = new Workbook();
        var ws = wb.AddNewPart<WorksheetPart>();
        var data = new SheetData();
        ws.Worksheet = new Worksheet(data);

        data.Append(Row(1, ["Отчет по сменам с налогами с 01.01.2026 00:00 по 20.09.2026 23:59"]));
        data.Append(Row(2, [""]));
        data.Append(Row(3, ["В столбце \"Сумма выручки\" - сумма денежных средств,"]));
        data.Append(Row(4, ["принятых в наличном виде и при оплате по карте,"]));
        data.Append(Row(5, ["с учетом признака расчета."]));
        data.Append(Row(6, [""]));
        data.Append(Row(7,
        [
            "ИНН", "Группа", "Наименование ККТ", "Адрес места установки ККТ", "Смена",
            "Кассир (открытие смены)", "Кассир (закрытие смены)", "Открыта", "Закрыта",
            "Сумма выручки", "Сумма выручки наличными", "Сумма выручки безналичными"
        ]));

        data.Append(Row(8,
        [
            "6603017238", "", "Лакомка", "624285, Свердловская обл., п. Рефтинский, ул. Молодежная, 23А", "171",
            "", "", "46026.40902777778", "46026.79236111111", "12510", "2847", "9663"
        ]));
        data.Append(Row(9,
        [
            "6603017238", "", "Сиеста", "624285,PEФTИHCKИЙ,ГAГAPИHA,18A/1", "345",
            "", "", "46034.4", "46034.75", "20000", "0", "20000"
        ]));
        data.Append(Row(10,
        [
            "6603017238", "", "Хризотил", "624260, Свердловская обл., г. Асбест, ул. Королева, 30", "205",
            "", "", "46032.4", "46032.8", "18136", "0", "18136"
        ]));

        ws.Worksheet.Save();
        wb.Workbook.Append(new Sheets(new Sheet { Id = wb.GetIdOfPart(ws), SheetId = 1, Name = "Лист 0" }));
        wb.Workbook.Save();
    }

    private static Row Row(uint index, IReadOnlyList<string> values)
    {
        var row = new Row { RowIndex = index };
        for (var i = 0; i < values.Count; i++)
        {
            row.Append(new Cell
            {
                CellReference = ColumnName(i) + index,
                DataType = CellValues.InlineString,
                InlineString = new InlineString(new Text(values[i] ?? string.Empty))
            });
        }
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
}
