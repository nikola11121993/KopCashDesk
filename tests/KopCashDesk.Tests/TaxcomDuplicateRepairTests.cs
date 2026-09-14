using KopCashDesk.Core;
using KopCashDesk.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace KopCashDesk.Tests;

public sealed class TaxcomDuplicateRepairTests
{
    [Fact]
    public void SameTaxcomShift_WithChangedExternalId_UpdatesInsteadOfDoubling()
    {
        var root = CreateRoot();
        try
        {
            var db = new Database(Path.Combine(root, "cashdesk.db"));
            db.Initialize();
            db.EnsureV051Fixes();
            var organization = new Organization(Guid.NewGuid(), "ООО ЦОП", "6683009222");
            var location = new Location(Guid.NewGuid(), organization.Id, "Рефтинская ГРЭС", "здание 6");
            db.Save(organization);
            db.Save(location);

            var firstClosed = new DateTimeOffset(2026, 7, 15, 14, 53, 0, TimeSpan.FromHours(5));
            SaveTaxcomShift(db, organization, location, "old-shift", firstClosed, 9, 53193m, 1500m, 51693m);

            var secondClosed = new DateTimeOffset(2026, 7, 15, 14, 54, 0, TimeSpan.FromHours(5));
            SaveTaxcomShift(db, organization, location, "new-shift", secondClosed, 9, 53193m, 1500m, 51693m);

            var day = Assert.Single(db.PointDaySummaries(organization.Id, 2026, 7, location.Id));
            Assert.Equal(1, day.ShiftCount);
            Assert.Equal(51693m, day.FiscalElectronic);
            Assert.Equal(53193m, day.ShiftTotal);
            Assert.Single(db.ShiftClosures(organization.Id, location.Id, new DateOnly(2026, 7, 15)));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void ExistingDuplicate_IsRepaired_AndImportedCashCanBeManuallyOverridden()
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "cashdesk.db");
            var db = new Database(path);
            db.Initialize();
            var organization = new Organization(Guid.NewGuid(), "ООО ЦОП", "6683009222");
            var location = new Location(Guid.NewGuid(), organization.Id, "Рефтинская ГРЭС", "здание 6");
            db.Save(organization);
            db.Save(location);

            using (var sql = Open(path))
            {
                Execute(sql, "DROP INDEX IF EXISTS ux_shift_closures_taxcom_business");
                Execute(sql, "DROP TRIGGER IF EXISTS trg_shift_closures_taxcom_business_upsert");
                Execute(sql, "DROP TRIGGER IF EXISTS trg_shift_closures_taxcom_rekey_operations");

                InsertRawShift(sql, organization.Id, location.Id, "old", "2026-07-06T14:46:00+05:00", 2, 1233734m, 0m, 1233734m);
                InsertRawShift(sql, organization.Id, location.Id, "new", "2026-07-06T14:47:00+05:00", 2, 1233734m, 0m, 1233734m);
                InsertRawFiscal(sql, organization.Id, location.Id, "old:electronic", "2026-07-06T14:46:00+05:00", 1233734m);
                InsertRawFiscal(sql, organization.Id, location.Id, "new:electronic", "2026-07-06T14:47:00+05:00", 1233734m);
            }

            var repairedCount = db.EnsureV051Fixes();
            Assert.Equal(1, repairedCount);

            var repaired = Assert.Single(db.PointDaySummaries(organization.Id, 2026, 7, location.Id));
            Assert.Equal(1, repaired.ShiftCount);
            Assert.Equal(1233734m, repaired.FiscalElectronic);
            Assert.Equal(1233734m, repaired.ShiftTotal);

            var date = new DateOnly(2026, 7, 6);
            db.SetManualCash(organization.Id, location.Id, date, 1200000m);
            Assert.Equal(1200000m, Assert.Single(db.PointDaySummaries(organization.Id, 2026, 7, location.Id)).FiscalElectronic);

            var existingShift = Assert.Single(db.ShiftClosures(organization.Id, location.Id, date));
            db.UpsertFiscalOperation(new CashOperation(
                "Taxcom.ShiftReport", existingShift.ExternalId + ":electronic",
                organization.Id, location.Id,
                new DateTimeOffset(2026, 7, 6, 14, 48, 0, TimeSpan.FromHours(5)),
                SourceKind.Fiscal, OperationKind.Sale, PaymentKind.Electronic, 1210000m));
            Assert.Equal(1200000m, Assert.Single(db.PointDaySummaries(organization.Id, 2026, 7, location.Id)).FiscalElectronic);

            db.ClearManualCash(organization.Id, location.Id, date);
            Assert.Equal(1210000m, Assert.Single(db.PointDaySummaries(organization.Id, 2026, 7, location.Id)).FiscalElectronic);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void SaveTaxcomShift(Database db, Organization organization, Location location, string externalId,
        DateTimeOffset closedAt, int shiftNumber, decimal total, decimal cash, decimal electronic)
    {
        const string fn = "7381440901042300";
        db.Save(new ShiftClosure("Taxcom.ShiftReport", externalId, organization.Id, location.Id,
            closedAt, total, cash, electronic, fn, shiftNumber));
        db.UpsertFiscalOperation(new CashOperation("Taxcom.ShiftReport", externalId + ":cash", organization.Id, location.Id,
            closedAt, SourceKind.Fiscal, OperationKind.Sale, PaymentKind.Cash, cash));
        db.UpsertFiscalOperation(new CashOperation("Taxcom.ShiftReport", externalId + ":electronic", organization.Id, location.Id,
            closedAt, SourceKind.Fiscal, OperationKind.Sale, PaymentKind.Electronic, electronic));
    }

    private static void InsertRawShift(SqliteConnection db, Guid organizationId, Guid locationId, string externalId,
        string closedAt, int shiftNumber, decimal total, decimal cash, decimal electronic)
    {
        using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO shift_closures(id,source,external_id,organization_id,location_id,closed_at,
                total_kopecks,cash_kopecks,electronic_kopecks,fn,shift_number,document_id)
            VALUES($id,'Taxcom.ShiftReport',$external,$org,$loc,$closed,$total,$cash,$electronic,$fn,$shift,NULL)
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("$external", externalId);
        command.Parameters.AddWithValue("$org", organizationId.ToString());
        command.Parameters.AddWithValue("$loc", locationId.ToString());
        command.Parameters.AddWithValue("$closed", closedAt);
        command.Parameters.AddWithValue("$total", Money.ToKopecks(total));
        command.Parameters.AddWithValue("$cash", Money.ToKopecks(cash));
        command.Parameters.AddWithValue("$electronic", Money.ToKopecks(electronic));
        command.Parameters.AddWithValue("$fn", "7381440901042300");
        command.Parameters.AddWithValue("$shift", shiftNumber);
        command.ExecuteNonQuery();
    }

    private static void InsertRawFiscal(SqliteConnection db, Guid organizationId, Guid locationId, string externalId, string occurredAt, decimal electronic)
    {
        using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO operations(id,source,external_id,organization_id,location_id,occurred_at,
                source_kind,kind,payment,amount_kopecks,document_id)
            VALUES($id,'Taxcom.ShiftReport',$external,$org,$loc,$time,'Fiscal','Sale','Electronic',$amount,NULL)
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("$external", externalId);
        command.Parameters.AddWithValue("$org", organizationId.ToString());
        command.Parameters.AddWithValue("$loc", locationId.ToString());
        command.Parameters.AddWithValue("$time", occurredAt);
        command.Parameters.AddWithValue("$amount", Money.ToKopecks(electronic));
        command.ExecuteNonQuery();
    }

    private static SqliteConnection Open(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, ForeignKeys = true }.ToString());
        db.Open();
        return db;
    }

    private static void Execute(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "KopCashDesk.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string root)
    {
        try { Directory.Delete(root, true); }
        catch { }
    }
}
