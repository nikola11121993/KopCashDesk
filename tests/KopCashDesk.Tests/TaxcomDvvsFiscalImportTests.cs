using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using KopCashDesk.Core;
using KopCashDesk.Data;
using KopCashDesk.Desktop;
using Microsoft.Data.Sqlite;
using Xunit;

namespace KopCashDesk.Tests;

public sealed class TaxcomDvvsFiscalImportTests
{
    private static readonly string[] Headers =
    [
        "Дата и время", "Документ", "№ смены", "№ за смену", "Тип операции",
        "Наличными", "Безналичными", "Сумма", "№ ФД", "ФПД",
        "Торговая точка", "Название ККТ", "Зав. № ККТ", "Рег. № ККТ", "Зав. № ФН"
    ];

    [Fact]
    public void SplitDvvsReports_WithOverlapAndFnReplacement_StayOnePhysicalKktAndDoNotDouble()
    {
        var folder = Path.Combine(Path.GetTempPath(), "dvvs-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var firstPath = Path.Combine(folder, "ИНН 6683011158 КПП 668301001 Сводный отчет по фискальным документам за период c 01.01.26 по 30.04.26.xlsx");
        var secondPath = Path.Combine(folder, "ИНН 6683011158 КПП 668301001 Сводный отчет по фискальным документам за период c 30.04.26 по 29.09.26.xlsx");
        var databasePath = Path.Combine(folder, "cashdesk.db");

        try
        {
            CreateReport(firstPath,
                Row("30.04.2026 20:50:00", "100", "1", "Приход", "0", "4390", "4390", "45754", "100001", "7384440900680170"));
            CreateReport(secondPath,
                Row("30.04.2026 20:50:00", "100", "1", "Приход", "0", "4390", "4390", "45754", "100001", "7384440900680170"),
                Row("20.08.2026 09:04:00", "2", "1", "Приход", "0", "5000", "5000", "2", "200002", "7384441001761900"));

            var database = new Database(databasePath);
            database.Initialize();
            var organization = new Organization(Guid.NewGuid(), "ООО ГАРАНТ", KnownBusinessRules.GarantTaxId);
            var location = new Location(Guid.NewGuid(), organization.Id, KnownBusinessRules.GarantDvvsPointName, "Екатеринбург, ул. Универсиады, 11");
            database.Save(organization);
            database.Save(location);
            KnownBusinessRules.ApplyPending(database);

            var importer = new TaxcomFiscalDocumentImporter(database);
            var result = importer.ImportFiles([firstPath, secondPath]);

            Assert.Equal(2, result.FilesProcessed);
            Assert.Equal(3, result.DocumentsProcessed);

            var activeKassa1 = database.RegisterBindings()
                .Where(x => x.OrganizationId == organization.Id && x.KktSerial == KnownBusinessRules.GarantDvvs1Serial)
                .ToArray();
            Assert.Single(activeKassa1);
            Assert.Equal(location.Id, activeKassa1[0].LocationId);

            var april = Assert.Single(database.GarantDvvsRegisterDaySummaries(2026, 4, organization.Id, location.Id));
            Assert.Equal(4390m, april.Kassa1Electronic);

            var august = Assert.Single(database.GarantDvvsRegisterDaySummaries(2026, 8, organization.Id, location.Id));
            Assert.Equal(5000m, august.Kassa1Electronic);

            var history = database.RegisterFiscalDriveHistory(organization.Id)
                .Where(x => x.KktSerial == KnownBusinessRules.GarantDvvs1Serial)
                .Select(x => x.FiscalDriveNumber)
                .ToHashSet();
            Assert.Contains("7384440900680170", history);
            Assert.Contains("7384441001761900", history);

            // The 30 April document is present in both files but upserts to one operation.
            Assert.Equal(9390m, database.SumOperations(TaxcomFiscalDocumentImporter.Source, organization.Id));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(folder, true); } catch { }
        }
    }

    private static void CreateReport(string path, params string[][] rows)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var data = new SheetData();
        worksheetPart.Worksheet = new Worksheet(data);

        data.Append(TextRow(1, ["Такском-Касса"]));
        data.Append(TextRow(2, ["Сводный отчет по фискальным документам"]));
        data.Append(TextRow(3, ["Торговая точка", "Без торговой точки"]));
        data.Append(TextRow(4, ["ККТ", KnownBusinessRules.GarantDvvs1Serial]));
        data.Append(TextRow(6, Headers));
        uint index = 7;
        foreach (var row in rows) data.Append(TextRow(index++, row));
        data.Append(TextRow(index, ["Итог"]));
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

    private static string[] Row(
        string date,
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
        Set("Документ", "Кассовый чек");
        Set("№ смены", shift);
        Set("№ за смену", numberInShift);
        Set("Тип операции", operation);
        Set("Наличными", cash);
        Set("Безналичными", electronic);
        Set("Сумма", total);
        Set("№ ФД", fd);
        Set("ФПД", fpd);
        Set("Торговая точка", "Без торговой точки");
        Set("Название ККТ", "ДВВС Касса 1");
        Set("Зав. № ККТ", KnownBusinessRules.GarantDvvs1Serial);
        Set("Рег. № ККТ", KnownBusinessRules.GarantDvvs1Rnm);
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
}
