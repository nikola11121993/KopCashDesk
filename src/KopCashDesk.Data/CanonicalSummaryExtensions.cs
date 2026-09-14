using KopCashDesk.Core;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace KopCashDesk.Data;

public static class CanonicalSummaryExtensions
{
    public static IReadOnlyList<PointDaySummary> CanonicalPointDaySummaries(
        this Database database,
        Guid? organizationId = null,
        int? year = null,
        int? month = null,
        Guid? locationId = null)
    {
        database.EnsureManualTerminalPostings();
        using var db = Open(database);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            WITH operation_rows AS (
                SELECT
                    substr(occurred_at,1,10) AS day,
                    organization_id,
                    location_id,
                    source,
                    source_kind,
                    payment,
                    amount_kopecks,
                    CASE
                        WHEN source='Taxcom.ShiftReport' THEN 1
                        WHEN source='Taxcom.FiscalDocuments' THEN 2
                        WHEN source='Frontol.Report' THEN 3
                        ELSE 4
                    END AS fiscal_priority
                FROM operations
                WHERE location_id IS NOT NULL
            ),
            chosen_fiscal AS (
                SELECT day,organization_id,location_id,MIN(fiscal_priority) AS fiscal_priority
                FROM operation_rows
                WHERE source_kind='Fiscal' AND payment='Electronic'
                GROUP BY day,organization_id,location_id
            ),
            op AS (
                SELECT
                    r.day,
                    r.organization_id,
                    r.location_id,
                    SUM(CASE WHEN r.source_kind='Bank' AND r.payment='Electronic' THEN r.amount_kopecks ELSE 0 END) AS bank_sum,
                    SUM(CASE WHEN r.source_kind='Bank' AND r.payment='Electronic' THEN 1 ELSE 0 END) AS bank_count,
                    SUM(CASE WHEN r.source_kind='Fiscal' AND r.payment='Electronic' AND r.fiscal_priority=cf.fiscal_priority THEN r.amount_kopecks ELSE 0 END) AS fiscal_sum,
                    SUM(CASE WHEN r.source_kind='Fiscal' AND r.payment='Electronic' AND r.fiscal_priority=cf.fiscal_priority THEN 1 ELSE 0 END) AS fiscal_count
                FROM operation_rows r
                LEFT JOIN chosen_fiscal cf
                    ON cf.day=r.day AND cf.organization_id=r.organization_id AND cf.location_id=r.location_id
                GROUP BY r.day,r.organization_id,r.location_id
            ),
            canonical_shifts AS (
                SELECT s.*
                FROM shift_closures s
                WHERE NOT EXISTS(
                    SELECT 1 FROM shift_source_links l WHERE l.observed_shift_id=s.id
                )
                AND NOT EXISTS(
                    SELECT 1 FROM fiscal_source_conflicts c
                    WHERE c.taxcom_shift_id=s.id OR c.frontol_shift_id=s.id
                )
            ),
            shifts AS (
                SELECT
                    substr(closed_at,1,10) AS day,
                    organization_id,
                    location_id,
                    SUM(total_kopecks) AS total_sum,
                    SUM(cash_kopecks) AS cash_sum,
                    SUM(electronic_kopecks) AS electronic_sum,
                    COUNT(*) AS shift_count,
                    MAX(closed_at) AS last_closed_at
                FROM canonical_shifts
                GROUP BY substr(closed_at,1,10), organization_id, location_id
            ),
            raw_sources AS (
                SELECT
                    substr(closed_at,1,10) AS day,
                    organization_id,
                    location_id,
                    MAX(CASE WHEN source='Taxcom.ShiftReport' THEN 1 ELSE 0 END) AS has_taxcom,
                    MAX(CASE WHEN source='Frontol.Report' THEN 1 ELSE 0 END) AS has_frontol,
                    MAX(CASE WHEN source NOT IN ('Taxcom.ShiftReport','Frontol.Report') THEN 1 ELSE 0 END) AS has_other
                FROM shift_closures
                GROUP BY substr(closed_at,1,10), organization_id, location_id
            ),
            document_sources AS (
                SELECT
                    substr(occurred_at,1,10) AS day,
                    organization_id,
                    location_id,
                    MAX(CASE WHEN source='Taxcom.FiscalDocuments' THEN 1 ELSE 0 END) AS has_taxcom_documents
                FROM operations
                WHERE location_id IS NOT NULL AND source_kind='Fiscal'
                GROUP BY substr(occurred_at,1,10),organization_id,location_id
            ),
            conflicts AS (
                SELECT business_date AS day,organization_id,location_id,COUNT(*) AS conflict_count
                FROM fiscal_source_conflicts
                GROUP BY business_date,organization_id,location_id
            ),
            manual_cash AS (
                SELECT business_date AS day,organization_id,location_id
                FROM manual_cash_postings
            ),
            manual_terminal AS (
                SELECT business_date AS day,organization_id,location_id,electronic_kopecks
                FROM manual_terminal_postings
            ),
            keys AS (
                SELECT day,organization_id,location_id FROM op WHERE bank_count>0 OR fiscal_count>0
                UNION
                SELECT day,organization_id,location_id FROM shifts
                UNION
                SELECT day,organization_id,location_id FROM raw_sources
                UNION
                SELECT day,organization_id,location_id FROM document_sources
                UNION
                SELECT day,organization_id,location_id FROM conflicts
                UNION
                SELECT day,organization_id,location_id FROM manual_cash
                UNION
                SELECT day,organization_id,location_id FROM manual_terminal
            )
            SELECT
                k.day,
                k.organization_id,
                org.name,
                k.location_id,
                loc.name,
                CASE WHEN manual_terminal.electronic_kopecks IS NOT NULL THEN manual_terminal.electronic_kopecks
                     WHEN COALESCE(op.bank_count,0)>0 THEN op.bank_sum ELSE NULL END,
                CASE WHEN COALESCE(conflicts.conflict_count,0)>0 THEN NULL
                     WHEN COALESCE(op.fiscal_count,0)>0 THEN op.fiscal_sum ELSE NULL END,
                CASE WHEN COALESCE(conflicts.conflict_count,0)>0 THEN NULL ELSE shifts.total_sum END,
                CASE WHEN COALESCE(conflicts.conflict_count,0)>0 THEN NULL ELSE shifts.cash_sum END,
                CASE WHEN COALESCE(conflicts.conflict_count,0)>0 THEN NULL ELSE shifts.electronic_sum END,
                CASE WHEN COALESCE(conflicts.conflict_count,0)>0 THEN 0 ELSE COALESCE(shifts.shift_count,0) END,
                shifts.last_closed_at,
                CASE
                    WHEN COALESCE(raw_sources.has_taxcom,0)=1 AND COALESCE(raw_sources.has_frontol,0)=1 AND COALESCE(document_sources.has_taxcom_documents,0)=1 THEN 'Taxcom + Frontol + фискальные документы'
                    WHEN COALESCE(raw_sources.has_taxcom,0)=1 AND COALESCE(raw_sources.has_frontol,0)=1 THEN 'Taxcom + Frontol'
                    WHEN COALESCE(raw_sources.has_taxcom,0)=1 AND COALESCE(document_sources.has_taxcom_documents,0)=1 THEN 'Taxcom — смены + фискальные документы'
                    WHEN COALESCE(raw_sources.has_taxcom,0)=1 THEN 'Taxcom'
                    WHEN COALESCE(document_sources.has_taxcom_documents,0)=1 THEN 'Taxcom — фискальные документы'
                    WHEN COALESCE(raw_sources.has_frontol,0)=1 THEN 'Frontol'
                    WHEN COALESCE(raw_sources.has_other,0)=1 THEN 'Другой кассовый источник'
                    ELSE ''
                END,
                CASE WHEN COALESCE(conflicts.conflict_count,0)>0 THEN 1 ELSE 0 END
            FROM keys k
            JOIN organizations org ON org.id=k.organization_id
            JOIN locations loc ON loc.id=k.location_id
            LEFT JOIN op ON op.day=k.day AND op.organization_id=k.organization_id AND op.location_id=k.location_id
            LEFT JOIN shifts ON shifts.day=k.day AND shifts.organization_id=k.organization_id AND shifts.location_id=k.location_id
            LEFT JOIN raw_sources ON raw_sources.day=k.day AND raw_sources.organization_id=k.organization_id AND raw_sources.location_id=k.location_id
            LEFT JOIN document_sources ON document_sources.day=k.day AND document_sources.organization_id=k.organization_id AND document_sources.location_id=k.location_id
            LEFT JOIN conflicts ON conflicts.day=k.day AND conflicts.organization_id=k.organization_id AND conflicts.location_id=k.location_id
            LEFT JOIN manual_terminal ON manual_terminal.day=k.day AND manual_terminal.organization_id=k.organization_id AND manual_terminal.location_id=k.location_id
            WHERE ($org IS NULL OR k.organization_id=$org)
              AND ($loc IS NULL OR k.location_id=$loc)
              AND ($year IS NULL OR CAST(substr(k.day,1,4) AS INTEGER)=$year)
              AND ($month IS NULL OR CAST(substr(k.day,6,2) AS INTEGER)=$month)
            ORDER BY k.day DESC, org.name, loc.name
            """;
        cmd.Parameters.AddWithValue("$org", organizationId is null ? DBNull.Value : organizationId.Value.ToString());
        cmd.Parameters.AddWithValue("$loc", locationId is null ? DBNull.Value : locationId.Value.ToString());
        cmd.Parameters.AddWithValue("$year", year is null ? DBNull.Value : year.Value);
        cmd.Parameters.AddWithValue("$month", month is null ? DBNull.Value : month.Value);

        using var reader = cmd.ExecuteReader();
        var result = new List<PointDaySummary>();
        while (reader.Read())
        {
            result.Add(new(
                DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                Guid.Parse(reader.GetString(3)),
                reader.GetString(4),
                ReadMoney(reader, 5),
                ReadMoney(reader, 6),
                ReadMoney(reader, 7),
                ReadMoney(reader, 8),
                ReadMoney(reader, 9),
                reader.GetInt32(10),
                reader.IsDBNull(11) ? null : DateTimeOffset.Parse(reader.GetString(11), CultureInfo.InvariantCulture),
                reader.GetString(12),
                reader.GetInt64(13) != 0));
        }
        return result;
    }

    private static decimal? ReadMoney(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Money.FromKopecks(reader.GetInt64(ordinal));

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
}
