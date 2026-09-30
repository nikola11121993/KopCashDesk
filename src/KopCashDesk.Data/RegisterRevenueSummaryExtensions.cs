using KopCashDesk.Core;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace KopCashDesk.Data;

public sealed record RegisterRevenueSummary(
    Guid OrganizationId,
    Guid LocationId,
    string Organization,
    string Point,
    string Register,
    string KktSerial,
    string Fn,
    string Rnm,
    int ShiftCount,
    decimal Cash,
    decimal Electronic,
    decimal Total,
    DateTimeOffset? LastClosedAt)
{
    public string LastClosed => LastClosedAt?.LocalDateTime.ToString("dd.MM.yyyy HH:mm") ?? "";
}

public static class RegisterRevenueSummaryExtensions
{
    public static IReadOnlyList<RegisterRevenueSummary> RegisterRevenueSummaries(
        this Database database,
        Guid? organizationId,
        int year,
        int? month = null,
        Guid? locationId = null)
    {
        var from = new DateOnly(year, month ?? 1, 1);
        var to = month is null ? from.AddYears(1) : from.AddMonths(1);
        var fromText = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var toText = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        using var db = Open(database);
        using var command = db.CreateCommand();
        command.CommandText = """
            WITH filtered_shifts AS (
                SELECT s.*
                FROM shift_closures s
                WHERE ($org IS NULL OR s.organization_id=$org)
                  AND ($loc IS NULL OR s.location_id=$loc)
                  AND s.closed_at >= $from AND s.closed_at < $to
                  AND NOT EXISTS(
                      SELECT 1 FROM shift_source_links l
                      WHERE l.observed_shift_id=s.id
                  )
                  AND NOT EXISTS(
                      SELECT 1 FROM fiscal_source_conflicts c
                      WHERE c.taxcom_shift_id=s.id OR c.frontol_shift_id=s.id
                  )
            ),
            bindings AS (
                SELECT rb.id,rb.organization_id,rb.location_id,rb.display_name,rb.kkt_serial,rb.fn,rb.register_number,
                       org.name AS organization,loc.name AS point
                FROM register_bindings rb
                JOIN organizations org ON org.id=rb.organization_id
                JOIN locations loc ON loc.id=rb.location_id
                WHERE rb.is_active=1 AND rb.location_id IS NOT NULL AND loc.is_active=1
                  AND ($org IS NULL OR rb.organization_id=$org)
                  AND ($loc IS NULL OR rb.location_id=$loc)
            ),
            bound AS (
                SELECT
                    b.organization_id,b.location_id,b.organization,b.point,
                    b.display_name,b.kkt_serial,
                    CASE
                        WHEN COUNT(NULLIF(s.fn,''))>0 THEN GROUP_CONCAT(DISTINCT NULLIF(s.fn,''))
                        ELSE b.fn
                    END AS fn,
                    b.register_number,
                    COUNT(s.id) AS shift_count,
                    COALESCE(SUM(s.cash_kopecks),0) AS cash_sum,
                    COALESCE(SUM(s.electronic_kopecks),0) AS electronic_sum,
                    COALESCE(SUM(s.total_kopecks),0) AS total_sum,
                    MAX(s.closed_at) AS last_closed_at
                FROM bindings b
                LEFT JOIN filtered_shifts s
                  ON s.organization_id=b.organization_id AND s.location_id=b.location_id
                 AND (
                      (b.kkt_serial<>'' AND s.kkt_serial=b.kkt_serial)
                      OR (s.kkt_serial='' AND b.fn<>'' AND s.fn=b.fn)
                      OR (s.kkt_serial='' AND s.fn='' AND b.register_number<>'' AND s.registration_number=b.register_number)
                 )
                GROUP BY b.id,b.organization_id,b.location_id,b.organization,b.point,
                         b.display_name,b.kkt_serial,b.fn,b.register_number
            ),
            unbound AS (
                SELECT
                    s.organization_id,s.location_id,org.name AS organization,loc.name AS point,
                    COALESCE(NULLIF(s.register_display_name,''),
                             CASE WHEN s.registration_number<>'' THEN 'ККТ '||s.registration_number
                                  WHEN s.fn<>'' THEN 'ККТ, ФН '||s.fn
                                  ELSE 'Непривязанная ККТ' END) AS display_name,
                    s.kkt_serial,s.fn,s.registration_number,
                    COUNT(*) AS shift_count,
                    SUM(s.cash_kopecks) AS cash_sum,
                    SUM(s.electronic_kopecks) AS electronic_sum,
                    SUM(s.total_kopecks) AS total_sum,
                    MAX(s.closed_at) AS last_closed_at
                FROM filtered_shifts s
                JOIN organizations org ON org.id=s.organization_id
                JOIN locations loc ON loc.id=s.location_id
                WHERE NOT EXISTS(
                    SELECT 1
                    FROM bindings b
                    WHERE b.organization_id=s.organization_id AND b.location_id=s.location_id
                      AND (
                           (b.kkt_serial<>'' AND s.kkt_serial=b.kkt_serial)
                           OR (s.kkt_serial='' AND b.fn<>'' AND s.fn=b.fn)
                           OR (s.kkt_serial='' AND s.fn='' AND b.register_number<>'' AND s.registration_number=b.register_number)
                      )
                )
                GROUP BY s.organization_id,s.location_id,org.name,loc.name,
                         s.register_display_name,s.kkt_serial,s.fn,s.registration_number
            )
            SELECT * FROM bound
            UNION ALL
            SELECT * FROM unbound
            ORDER BY point,display_name;
            """;
        command.Parameters.AddWithValue("$org", organizationId is null ? DBNull.Value : organizationId.Value.ToString());
        command.Parameters.AddWithValue("$loc", locationId is null ? DBNull.Value : locationId.Value.ToString());
        command.Parameters.AddWithValue("$from", fromText);
        command.Parameters.AddWithValue("$to", toText);

        using var reader = command.ExecuteReader();
        var result = new List<RegisterRevenueSummary>();
        while (reader.Read())
        {
            var name = reader.GetString(4);
            if (string.IsNullOrWhiteSpace(name))
            {
                if (!reader.IsDBNull(7) && reader.GetString(7).Length > 0) name = $"ККТ {reader.GetString(7)}";
                else if (!reader.IsDBNull(6) && reader.GetString(6).Length > 0) name = $"ККТ, ФН {reader.GetString(6)}";
                else name = "ККТ";
            }

            result.Add(new(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                name,
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetInt32(8),
                Money.FromKopecks(reader.GetInt64(9)),
                Money.FromKopecks(reader.GetInt64(10)),
                Money.FromKopecks(reader.GetInt64(11)),
                reader.IsDBNull(12) ? null : DateTimeOffset.Parse(reader.GetString(12), CultureInfo.InvariantCulture)));
        }

        return result;
    }

    private static SqliteConnection Open(Database database)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true
        }.ToString());
        connection.Open();
        using var pragmas = connection.CreateCommand();
        pragmas.CommandText = """
            PRAGMA busy_timeout=5000;
            PRAGMA temp_store=MEMORY;
            PRAGMA cache_size=-32768;
            PRAGMA mmap_size=268435456;
            """;
        pragmas.ExecuteNonQuery();
        return connection;
    }
}
