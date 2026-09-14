using KopCashDesk.Core;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace KopCashDesk.Data;

public sealed record ManualTerminalPosting(
    DateOnly Date,
    Guid OrganizationId,
    Guid LocationId,
    decimal Electronic,
    DateTimeOffset UpdatedAt);

public static class ManualTerminalPostingExtensions
{
    public static void EnsureManualTerminalPostings(this Database database)
    {
        using var db = Open(database);
        using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS manual_terminal_postings(
                organization_id TEXT NOT NULL REFERENCES organizations(id),
                location_id TEXT NOT NULL REFERENCES locations(id),
                business_date TEXT NOT NULL,
                electronic_kopecks INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY(organization_id, location_id, business_date)
            );
            CREATE INDEX IF NOT EXISTS ix_manual_terminal_postings_period
                ON manual_terminal_postings(organization_id, location_id, business_date);
            """;
        command.ExecuteNonQuery();
    }

    public static IReadOnlyList<ManualTerminalPosting> ManualTerminalPostings(
        this Database database,
        Guid? organizationId = null,
        int? year = null,
        int? month = null,
        Guid? locationId = null)
    {
        database.EnsureManualTerminalPostings();
        using var db = Open(database);
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT business_date, organization_id, location_id, electronic_kopecks, updated_at
            FROM manual_terminal_postings
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
        var result = new List<ManualTerminalPosting>();
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

    public static void SetManualTerminal(
        this Database database,
        Guid organizationId,
        Guid locationId,
        DateOnly date,
        decimal electronic)
    {
        database.EnsureManualTerminalPostings();
        using var db = Open(database);
        using var transaction = db.BeginTransaction();
        var old = ReadAmount(db, transaction, organizationId, locationId, date);
        var now = DateTimeOffset.UtcNow.ToString("O");
        electronic = Money.Normalize(electronic);

        using (var command = db.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO manual_terminal_postings(organization_id,location_id,business_date,electronic_kopecks,created_at,updated_at)
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

        Audit(db, transaction, "manual_terminal.set",
            $"org={organizationId}; loc={locationId}; date={date:yyyy-MM-dd}; old={MoneyText(old)}; new={electronic:0.00}");
        transaction.Commit();
    }

    public static void ClearManualTerminal(
        this Database database,
        Guid organizationId,
        Guid locationId,
        DateOnly date)
    {
        database.EnsureManualTerminalPostings();
        using var db = Open(database);
        using var transaction = db.BeginTransaction();
        var old = ReadAmount(db, transaction, organizationId, locationId, date);

        using (var command = db.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM manual_terminal_postings WHERE organization_id=$org AND location_id=$loc AND business_date=$date";
            command.Parameters.AddWithValue("$org", organizationId.ToString());
            command.Parameters.AddWithValue("$loc", locationId.ToString());
            command.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        Audit(db, transaction, "manual_terminal.clear",
            $"org={organizationId}; loc={locationId}; date={date:yyyy-MM-dd}; old={MoneyText(old)}; new=<none>");
        transaction.Commit();
    }

    private static decimal? ReadAmount(SqliteConnection db, SqliteTransaction tx, Guid organizationId, Guid locationId, DateOnly date)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT electronic_kopecks FROM manual_terminal_postings WHERE organization_id=$org AND location_id=$loc AND business_date=$date";
        command.Parameters.AddWithValue("$org", organizationId.ToString());
        command.Parameters.AddWithValue("$loc", locationId.ToString());
        command.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var value = command.ExecuteScalar();
        return value is null || value is DBNull ? null : Money.FromKopecks(Convert.ToInt64(value));
    }

    private static string MoneyText(decimal? value) => value is null ? "<none>" : value.Value.ToString("0.00", CultureInfo.InvariantCulture);

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
