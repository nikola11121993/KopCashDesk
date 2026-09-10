using KopCashDesk.Core;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace KopCashDesk.Data;

public sealed record ManualCashPosting(
    DateOnly Date,
    Guid OrganizationId,
    Guid LocationId,
    decimal Electronic,
    DateTimeOffset UpdatedAt);

public static class ManualCashPostingExtensions
{
    public static void EnsureManualCashPostings(this Database database)
    {
        using var db = Open(database);
        using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS manual_cash_postings(
                organization_id TEXT NOT NULL REFERENCES organizations(id),
                location_id TEXT NOT NULL REFERENCES locations(id),
                business_date TEXT NOT NULL,
                electronic_kopecks INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY(organization_id, location_id, business_date)
            );
            CREATE INDEX IF NOT EXISTS ix_manual_cash_postings_period
                ON manual_cash_postings(organization_id, location_id, business_date);
            """;
        command.ExecuteNonQuery();
    }

    public static IReadOnlyList<ManualCashPosting> ManualCashPostings(
        this Database database,
        Guid? organizationId = null,
        int? year = null,
        int? month = null,
        Guid? locationId = null)
    {
        database.EnsureManualCashPostings();
        using var db = Open(database);
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT business_date, organization_id, location_id, electronic_kopecks, updated_at
            FROM manual_cash_postings
            WHERE ($org IS NULL OR organization_id=$org)
              AND ($loc IS NULL OR location_id=$loc)
              AND ($year IS NULL OR CAST(substr(business_date,1,4) AS INTEGER)=$year)
              AND ($month IS NULL OR CAST(substr(business_date,6,2) AS INTEGER)=$month)
            ORDER BY business_date DESC
            """;
        command.Parameters.AddWithValue("$org", organizationId is null ? DBNull.Value : organizationId.Value.ToString());
        command.Parameters.AddWithValue("$loc", locationId is null ? DBNull.Value : locationId.Value.ToString());
        command.Parameters.AddWithValue("$year", year is null ? DBNull.Value : year.Value);
        command.Parameters.AddWithValue("$month", month is null ? DBNull.Value : month.Value);

        using var reader = command.ExecuteReader();
        var result = new List<ManualCashPosting>();
        while (reader.Read())
        {
            result.Add(new(
                DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                Guid.Parse(reader.GetString(1)),
                Guid.Parse(reader.GetString(2)),
                Money.FromKopecks(reader.GetInt64(3)),
                DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture)));
        }
        return result;
    }

    public static void SetManualCash(
        this Database database,
        Guid organizationId,
        Guid locationId,
        DateOnly date,
        decimal electronic)
    {
        database.EnsureManualCashPostings();
        using var db = Open(database);
        using var transaction = db.BeginTransaction();
        var now = DateTimeOffset.UtcNow.ToString("O");
        electronic = Money.Normalize(electronic);

        using (var command = db.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO manual_cash_postings(organization_id,location_id,business_date,electronic_kopecks,created_at,updated_at)
                VALUES($org,$loc,$date,$amount,$now,$now)
                ON CONFLICT(organization_id,location_id,business_date) DO UPDATE SET
                    electronic_kopecks=excluded.electronic_kopecks,
                    updated_at=excluded.updated_at
                """;
            command.Parameters.AddWithValue("$org", organizationId.ToString());
            command.Parameters.AddWithValue("$loc", locationId.ToString());
            command.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$amount", Money.ToKopecks(electronic));
            command.Parameters.AddWithValue("$now", now);
            command.ExecuteNonQuery();
        }

        Audit(db, transaction, "manual_cash.set",
            $"org={organizationId}; loc={locationId}; date={date:yyyy-MM-dd}; amount={electronic:0.00}");
        transaction.Commit();
    }

    public static void SetManualCashFromBank(
        this Database database,
        Guid organizationId,
        Guid locationId,
        DateOnly date,
        decimal electronic) =>
        database.SetManualCash(organizationId, locationId, date, electronic);

    public static void ClearManualCash(
        this Database database,
        Guid organizationId,
        Guid locationId,
        DateOnly date)
    {
        database.EnsureManualCashPostings();
        using var db = Open(database);
        using var transaction = db.BeginTransaction();

        using (var command = db.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM manual_cash_postings WHERE organization_id=$org AND location_id=$loc AND business_date=$date";
            command.Parameters.AddWithValue("$org", organizationId.ToString());
            command.Parameters.AddWithValue("$loc", locationId.ToString());
            command.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        Audit(db, transaction, "manual_cash.clear",
            $"org={organizationId}; loc={locationId}; date={date:yyyy-MM-dd}");
        transaction.Commit();
    }

    public static void ClearManualCashFromBank(
        this Database database,
        Guid organizationId,
        Guid locationId,
        DateOnly date) =>
        database.ClearManualCash(organizationId, locationId, date);

    private static SqliteConnection Open(Database database)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void Audit(SqliteConnection db, SqliteTransaction transaction, string action, string details)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO audit_log(occurred_at,action,details) VALUES($t,$a,$d)";
        command.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$a", action);
        command.Parameters.AddWithValue("$d", details);
        command.ExecuteNonQuery();
    }
}
