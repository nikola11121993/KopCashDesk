using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using KopCashDesk.Core;
using KopCashDesk.Data;
using KopCashDesk.Desktop;
using Xunit;

namespace KopCashDesk.Tests;

public sealed class TaxcomShiftReportImportTests
{
    private static readonly string[] Headers =
    [
        "Дата открытия",
        "Дата закрытия",
        "№ смены",
        "Выручка нал.",
        "Выручка безнал.",
        "Выручка",
        "Торговая точка",
        "Название ККТ",
        "Зав. № ККТ",
        "Рег. № ККТ",
        "Зав. № ФН"
    ];

    [Fact]
    public void ShiftReport_MapsKktToExistingPoint_AndDoesNotDoubleOnReimport()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"kopcashdesk-taxcom-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var report = Path.Combine(folder, "ИНН 6683009222 КПП 668301001 Сводный отчет по сменам.xlsx");
        var databasePath = Path.Combine(folder, "cashdesk.db");

        try
        {
            CreateWorkbook(report);
            var database = new Database(databasePath);
            database.Initialize();
            var organization = new Organization(Guid.NewGuid(), "ООО КОП", "6683009222");
            var location = new Location(Guid.NewGuid(), organization.Id, "Ладыженского 7", "Свердловская обл., Асбест, ул. Ладыженского, 7");
            database.Save(organization);
            database.Save(location);

            var first = new TaxcomShiftReportImporter(database).ImportFiles([report]);

            Assert.Equal(1, first.FilesProcessed);
            Assert.Equal(2, first.ShiftsProcessed);
            Assert.Equal(0, first.LocationsCreated);
            Assert.Equal(1, first.RegistersBound);
            Assert.Equal(4, first.FiscalOperationsInserted);
            Assert.Equal(0, first.FiscalOperationsUpdated);

            var binding = Assert.Single(database.RegisterBindings());
            Assert.Equal(location.Id, binding.LocationId);
            Assert.Equal("7381440901042300", binding.FiscalDriveNumber);

            var days = database.PointDaySummaries(organization.Id, 2026, 9, location.Id);
            Assert.Equal(2, days.Count);
            var september10 = Assert.Single(days.Where(x => x.Date == new DateOnly(2026, 9, 10)));
            Assert.Equal(17460m, september10.FiscalElectronic);
            Assert.Equal(17460m, september10.ShiftTotal);
            Assert.Equal(0m, september10.ShiftCash);
            Assert.Equal(17460m, september10.ShiftElectronic);
            Assert.Equal(1, september10.ShiftCount);

            var september9 = Assert.Single(days.Where(x => x.Date == new DateOnly(2026, 9, 9)));
            Assert.Equal(9715m, september9.FiscalElectronic);
            Assert.Equal(10055m, september9.ShiftTotal);
            Assert.Equal(340m, september9.ShiftCash);
            Assert.Equal(9715m, september9.ShiftElectronic);

            var second = new TaxcomShiftReportImporter(database).ImportFiles([report]);
            Assert.Equal(0, second.FiscalOperationsInserted);
            Assert.Equal(4, second.FiscalOperationsUpdated);

            var afterRepeat = database.PointDaySummaries(organization.Id, 2026, 9, location.Id);
            Assert.Equal(2, afterRepeat.Count);
            Assert.Equal(17460m, Assert.Single(afterRepeat.Where(x => x.Date == new DateOnly(2026, 9, 10))).FiscalElectronic);
            Assert.Equal(9715m, Assert.Single(afterRepeat.Where(x => x.Date == new DateOnly(2026, 9, 9))).FiscalElectronic);
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

        data.Append(TextRow(1, ["Такском-Касса"]));
        data.Append(TextRow(2, ["Сводный отчет по сменам"]));
        data.Append(TextRow(7, ["ККТ", "Ладыженского, 7"]));
        data.Append(TextRow(10, Headers));
        data.Append(MixedRow(11,
        [
            "10.09.2026 11:17:00", "10.09.2026 12:53:00", "140", "0", "17460", "17460",
            "Без торговой точки", "Ладыженского, 7", "00108202518113", "0009525635035583", "7381440901042300"
        ]));
        data.Append(MixedRow(12,
        [
            "09.09.2026 11:00:00", "09.09.2026 12:23:00", "139", "340", "9715", "10055",
            "Без торговой точки", "Ладыженского, 7", "00108202518113", "0009525635035583", "7381440901042300"
        ]));
        worksheetPart.Worksheet.Save();

        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1,
            Name = "Смены"
        });
        workbookPart.Workbook.Save();
    }

    private static Row TextRow(uint rowIndex, IReadOnlyList<string> values) => MixedRow(rowIndex, values);

    private static Row MixedRow(uint rowIndex, IReadOnlyList<string> values)
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
