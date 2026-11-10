using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Location = KopCashDesk.Core.Location;
using KopCashDesk.Data;
using Microsoft.Data.Sqlite;
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

    [Fact]
    public void GarantSambo_ReimportRepairsOnlyConfirmedTerminalRows()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"kopcashdesk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var report = Path.Combine(folder, "Балтым.xlsx");
        var path = Path.Combine(folder, "cashdesk.db");
        try
        {
            CreateWorkbook(report,
                Values("Балтым", "Верхняя Пышма, Успенский 2А", "37799127", "1", KnownBusinessRules.GarantTaxId),
                Values("Балтым", "Верхняя Пышма, Успенский 2А", "37799128", "2", KnownBusinessRules.GarantTaxId),
                Values("Балтым", "Верхняя Пышма, Успенский 2А", "37825027", "3", KnownBusinessRules.GarantTaxId),
                Values("Другая точка", "Другой адрес", "100001", "4", KnownBusinessRules.GarantTaxId));

            var database = new Database(path);
            database.Initialize();
            var importer = new SberAcquiringImporter(database);
            Assert.Equal(4, importer.ImportFiles([report]).OperationsAdded);
            var organization = Assert.Single(database.Organizations());
            var sambo = Assert.Single(database.Locations(), l => l.Name == KnownBusinessRules.GarantSamboPointName);
            var baltym = new Location(Guid.NewGuid(), organization.Id, "Балтым", "с. Балтым, ул. Первомайская, 50А");
            database.Save(baltym);

            using var connection = new SqliteConnection($"Data Source={path}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE terminal_bindings SET location_id=$baltym WHERE location_id=$sambo;
                UPDATE operations SET location_id=$baltym WHERE location_id=$sambo;
                """;
            command.Parameters.AddWithValue("$baltym", baltym.Id.ToString());
            command.Parameters.AddWithValue("$sambo", sambo.Id.ToString());
            command.ExecuteNonQuery();

            KnownBusinessRules.ApplyPending(database);
            Assert.Equal(3, database.TerminalBindings().Count(x => x.LocationId == sambo.Id && x.IsLocked));
            Assert.Equal(0, importer.ImportFiles([report]).OperationsAdded);
            Assert.Equal(300m, Assert.Single(database.PointDaySummaries(organization.Id, 2026, 9),
                x => x.LocationId == sambo.Id).BankElectronic);
            Assert.DoesNotContain(database.PointDaySummaries(organization.Id, 2026, 9), x => x.LocationId == baltym.Id);
            command.CommandText = "SELECT COUNT(*) FROM audit_log WHERE action='sber.reassign_operation'";
            Assert.Equal(3L, command.ExecuteScalar());
        }
        finally
        {
            try { Directory.Delete(folder, true); }
            catch { }
        }
    }

    private static void CreateWorkbook(string path, params string[][] rows)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var data = new SheetData();
        worksheetPart.Worksheet = new Worksheet(data);
        data.Append(CreateRow(1, Headers));
        if (rows.Length == 0)
        {
            rows =
            [
                Values("Кафе Аппетит", "Адрес кафе 1", "100001", "1"),
                Values("Кафе Аппетит", "Адрес кафе 2", "100002", "2"),
                Values("Пышма Верхняя", "Один общий адрес", "100003", "3"),
                Values("ЦХиЭГ", "Один общий адрес", "100004", "4")
            ];
        }
        for (var index = 0; index < rows.Length; index++)
            data.Append(CreateRow((uint)index + 2, rows[index]));
        worksheetPart.Worksheet.Save();
        sheets.Append(new Sheet { Id = workbookPart.GetIdOfPart(worksheetPart), SheetId = 1, Name = "Отчет" });
        workbookPart.Workbook.Save();
    }

    private static string[] Values(string point, string address, string terminal, string rrn, string taxId = "6600000000") =>
    [
        "ОБЩЕСТВО С ОГРАНИЧЕННОЙ ОТВЕТСТВЕННОСТЬЮ \"ГАРАНТ\"",
        taxId,
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
