using KopCashDesk.Core;
using KopCashDesk.Data;
using KopCashDesk.Desktop;
using System.Text;
using Xunit;
using CoreLocation = KopCashDesk.Core.Location;

namespace KopCashDesk.Tests;

public sealed class FrontolReportImportTests
{
    [Fact]
    public void FrontolReport_ImportsClosedShifts_ExcludesCancelledDocuments_AndDoesNotDoubleOnReimport()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"kopcashdesk-frontol-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var report = Path.Combine(folder, "report.txt");
        var databasePath = Path.Combine(folder, "cashdesk.db");

        try
        {
            File.WriteAllText(report, BuildReport(), Encoding.UTF8);
            var database = new Database(databasePath);
            database.Initialize();
            var organization = new Organization(Guid.NewGuid(), "ООО КОП", "6683009222");
            var location = new CoreLocation(Guid.NewGuid(), organization.Id, "Рефтинская ГРЭС 6 столовая", "пгт. Рефтинский");
            database.Save(organization);
            database.Save(location);

            var first = new FrontolReportImporter(database, organization.Id, location.Id).ImportFiles([report]);

            Assert.Equal(1, first.FilesProcessed);
            Assert.Equal(2, first.ShiftsProcessed);
            Assert.Equal(3, first.ClosedDocuments);
            Assert.Equal(1, first.CancelledDocuments);
            Assert.Equal(4, first.FiscalOperationsInserted);
            Assert.Equal(0, first.FiscalOperationsUpdated);
            Assert.Equal(100m, first.CashTotal);
            Assert.Equal(200m, first.ElectronicTotal);
            Assert.Equal(300m, first.RevenueTotal);

            var days = database.PointDaySummaries(organization.Id, 2026, 9, location.Id);
            Assert.Equal(2, days.Count);

            var september10 = Assert.Single(days, x => x.Date == new DateOnly(2026, 9, 10));
            Assert.Equal(250m, september10.FiscalElectronic);
            Assert.Equal(350m, september10.ShiftTotal);
            Assert.Equal(100m, september10.ShiftCash);
            Assert.Equal(250m, september10.ShiftElectronic);
            Assert.Equal(1, september10.ShiftCount);

            var september11 = Assert.Single(days, x => x.Date == new DateOnly(2026, 9, 11));
            Assert.Equal(-50m, september11.FiscalElectronic);
            Assert.Equal(-50m, september11.ShiftTotal);
            Assert.Equal(0m, september11.ShiftCash);
            Assert.Equal(-50m, september11.ShiftElectronic);

            var second = new FrontolReportImporter(database, organization.Id, location.Id).ImportFiles([report]);
            Assert.Equal(0, second.FiscalOperationsInserted);
            Assert.Equal(4, second.FiscalOperationsUpdated);

            var afterRepeat = database.PointDaySummaries(organization.Id, 2026, 9, location.Id);
            Assert.Equal(2, afterRepeat.Count);
            Assert.Equal(250m, Assert.Single(afterRepeat, x => x.Date == new DateOnly(2026, 9, 10)).FiscalElectronic);
            Assert.Equal(-50m, Assert.Single(afterRepeat, x => x.Date == new DateOnly(2026, 9, 11)).FiscalElectronic);
        }
        finally
        {
            try { Directory.Delete(folder, true); }
            catch { }
        }
    }

    private static string BuildReport()
    {
        string[] lines =
        [
            "#",
            "1",
            "2",
            // Смена 7: 100 наличными + 250 безналом. Чек на 500 отменён и не должен попасть в итог.
            "1;10.09.2026;10:00:00;40;1;101;3;;1;0;100;100;0;7;0;0;0;;;;;;;;;;;;;;;;;;;;;",
            "2;10.09.2026;10:00:01;55;1;101;3;;;0;100;0;1;7;100;0;0;;;100;1;0;1;0;;1/1/101;1;0;;;;;;;0;10.09.2026;0;0;;;;;;;",
            "3;10.09.2026;10:05:00;40;1;102;3;;2;7;250;250;0;7;0;0;0;;;;;;;;;;;;;;;;;;;;;",
            "4;10.09.2026;10:05:01;55;1;102;3;;;0;250;0;1;7;250;0;0;;;250;1;0;1;0;;1/1/102;1;0;;;;;;;0;10.09.2026;0;0;;;;;;;",
            "5;10.09.2026;10:10:00;40;1;103;3;;2;7;500;500;0;7;0;0;0;;;;;;;;;;;;;;;;;;;;;",
            "6;10.09.2026;10:10:01;56;1;103;3;;0;0;-1;-500;0;1;0;500;0;;;500;1;0;1;0;;1/1/103;1;0;;;;;;;;;;;;;;;;;",
            "7;10.09.2026;18:00:00;61;1;104;3;;7;350;0;350;10;7;0;0;0;;;350;1;0;1;0;;1/1/104;1;;;;;;;;;;;;;;;;;;",
            // Дублированное закрытие смены с тем же номером транзакции не должно удваивать смену.
            "7;10.09.2026;18:00:00;61;1;104;3;;7;350;0;350;10;7;0;0;0;;;350;1;0;1;0;;1/1/104;1;;;;;;;;;;;;;;;;;;",
            // Смена 8: возврат 50 по безналу.
            "8;11.09.2026;12:00:00;40;1;105;3;;2;7;-50;-50;1;8;0;0;0;;;;;;;;;;;;;;;;;;;;;",
            "9;11.09.2026;12:00:01;55;1;105;3;;;0;-50;0;1;8;-50;0;0;;;-50;1;0;1;0;;1/1/105;1;0;;;;;;;0;11.09.2026;0;0;;;;;;;",
            "10;11.09.2026;18:00:00;61;1;106;3;;8;-50;0;-50;10;8;0;0;0;;;-50;1;0;1;0;;1/1/106;1;;;;;;;;;;;;;;;;;;"
        ];
        return string.Join(Environment.NewLine, lines);
    }
}
