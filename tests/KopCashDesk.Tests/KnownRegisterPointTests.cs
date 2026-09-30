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

    [Fact]
    public void GarantSamboRegister_RepairsMistakenBaltymImportAndFutureResolution()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"kopcashdesk-sambo-repair-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var databasePath = Path.Combine(folder, "cashdesk.db");

        try
        {
            var db = new Database(databasePath);
            db.Initialize();

            var organization = new Organization(Guid.NewGuid(), "ООО ГАРАНТ", KnownBusinessRules.GarantTaxId);
            var sambo = new KopCashDesk.Core.Location(
                Guid.NewGuid(), organization.Id, KnownBusinessRules.GarantSamboPointName, KnownBusinessRules.GarantSamboAddress);
            var baltym = new KopCashDesk.Core.Location(
                Guid.NewGuid(), organization.Id, "Балтым", "с. Балтым, ул. Первомайская, 50А");

            db.Save(organization);
            db.Save(sambo);
            db.Save(baltym);

            RegisterBindingService.Save(db, new RegisterBinding(
                Guid.NewGuid(),
                organization.Id,
                baltym.Id,
                "7381440901260801",
                KnownBusinessRules.GarantSamboRnm,
                BindingSource.Automatic,
                false,
                null,
                null,
                KnownBusinessRules.GarantSamboRegisterSerial,
                "Балтым"));

            db.Save(new ShiftClosure(
                "Taxcom.ShiftReport",
                "mistaken-baltym-shift",
                organization.Id,
                baltym.Id,
                new DateTimeOffset(2026, 5, 30, 9, 46, 0, TimeSpan.FromHours(5)),
                1950m,
                0m,
                1950m,
                "7381440901260801",
                28,
                null,
                KnownBusinessRules.GarantSamboRegisterSerial,
                KnownBusinessRules.GarantSamboRnm,
                "Балтым"));

            KnownBusinessRules.ApplyPending(db);

            var binding = Assert.Single(
                db.RegisterBindings(),
                x => x.KktSerial == KnownBusinessRules.GarantSamboRegisterSerial);
            Assert.Equal(sambo.Id, binding.LocationId);
            Assert.Equal(KnownBusinessRules.GarantSamboPointName, binding.DisplayName);

            var shifted = Assert.Single(db.ShiftClosures(
                organization.Id,
                sambo.Id,
                new DateOnly(2026, 5, 30)));
            Assert.Equal(1950m, shifted.Electronic);

            var resolved = RegisterBindingService.Resolve(
                db,
                organization.Id,
                KnownBusinessRules.GarantSamboRegisterSerial,
                "7381440901260801",
                KnownBusinessRules.GarantSamboRnm,
                "Любая подпись",
                "Балтым",
                new DateOnly(2026, 5, 31));
            Assert.Equal(sambo.Id, resolved.LocationId);
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
