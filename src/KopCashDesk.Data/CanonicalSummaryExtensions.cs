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
        database.RebuildCrossSourceShiftMatches();

        var garant = database.Organizations().FirstOrDefault(x =>
            new string((x.TaxId ?? string.Empty).Where(char.IsDigit).ToArray()) == KnownBusinessRules.GarantTaxId);
        var garantDvvs = garant is null
            ? null
            : KnownBusinessRules.FindKnownPoint(
                database.Locations().Where(x => x.OrganizationId == garant.Id),
                KnownBusinessRules.GarantDvvsPointName);

        DateOnly? fromDate = year is null ? null : new DateOnly(year.Value, month ?? 1, 1);
        DateOnly? toDate = fromDate is null
            ? null
            : month is null ? fromDate.Value.AddYears(1) : fromDate.Value.AddMonths(1);
        var fromText = fromDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var toText = toDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        using var db = Open(database);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            WITH operation_rows AS (
                SELECT substr(occurred_at,1,10) AS day,organization_id,location_id,source_kind,payment,amount_kopecks
                FROM operations
                WHERE source_kind='Bank' AND location_id IS NOT NULL
                  AND ($org IS NULL OR organization_id=$org)
                  AND ($loc IS NULL OR location_id=$loc)
                  AND ($from IS NULL OR occurred_at>=$from)
                  AND ($to IS NULL OR occurred_at<$to)
                UNION ALL
                SELECT substr(occurred_at,1,10),organization_id,location_id,source_kind,payment,amount_kopecks
                FROM canonical_fiscal_operations
                WHERE ($org IS NULL OR organization_id=$org)
                  AND ($loc IS NULL OR location_id=$loc)
                  AND ($from IS NULL OR occurred_at>=$from)
                  AND ($to IS NULL OR occurred_at<$to)
            ),
            op AS (
                SELECT day,organization_id,location_id,
                    SUM(CASE WHEN source_kind='Bank' AND payment='Electronic' THEN amount_kopecks ELSE 0 END) AS bank_sum,
                    SUM(CASE WHEN source_kind='Bank' AND payment='Electronic' THEN 1 ELSE 0 END) AS bank_count,
                    SUM(CASE WHEN source_kind='Fiscal' AND payment='Electronic' THEN amount_kopecks ELSE 0 END) AS fiscal_sum,
                    SUM(CASE WHEN source_kind='Fiscal' AND payment='Electronic' THEN 1 ELSE 0 END) AS fiscal_count
                FROM operation_rows GROUP BY day,organization_id,location_id
            ),
            canonical_shifts AS (
                SELECT s.*
                FROM shift_closures s
                WHERE ($org IS NULL OR s.organization_id=$org)
                  AND ($loc IS NULL OR s.location_id=$loc)
                  AND ($from IS NULL OR s.closed_at>=$from)
                  AND ($to IS NULL OR s.closed_at<$to)
                  AND NOT EXISTS(
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
                WHERE ($org IS NULL OR organization_id=$org)
                  AND ($loc IS NULL OR location_id=$loc)
                  AND ($from IS NULL OR closed_at>=$from)
                  AND ($to IS NULL OR closed_at<$to)
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
                  AND ($org IS NULL OR organization_id=$org)
                  AND ($loc IS NULL OR location_id=$loc)
                  AND ($from IS NULL OR occurred_at>=$from)
                  AND ($to IS NULL OR occurred_at<$to)
                GROUP BY substr(occurred_at,1,10),organization_id,location_id
            ),
            conflicts AS (
                SELECT business_date AS day,organization_id,location_id,COUNT(*) AS conflict_count
                FROM fiscal_source_conflicts
                WHERE ($org IS NULL OR organization_id=$org)
                  AND ($loc IS NULL OR location_id=$loc)
                  AND ($from IS NULL OR business_date>=$from)
                  AND ($to IS NULL OR business_date<$to)
                GROUP BY business_date,organization_id,location_id
            ),
            manual_cash AS (
                SELECT business_date AS day,organization_id,location_id,electronic_kopecks
                FROM manual_cash_postings
                WHERE ($org IS NULL OR organization_id=$org)
                  AND ($loc IS NULL OR location_id=$loc)
                  AND ($from IS NULL OR business_date>=$from)
                  AND ($to IS NULL OR business_date<$to)
            ),
            manual_terminal AS (
                SELECT business_date AS day,organization_id,location_id,electronic_kopecks
                FROM manual_terminal_postings
                WHERE ($org IS NULL OR organization_id=$org)
                  AND ($loc IS NULL OR location_id=$loc)
                  AND ($from IS NULL OR business_date>=$from)
                  AND ($to IS NULL OR business_date<$to)
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
                     WHEN COALESCE(op.fiscal_count,0)>0 AND manual_cash.electronic_kopecks IS NOT NULL THEN manual_cash.electronic_kopecks
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
            LEFT JOIN manual_cash ON manual_cash.day=k.day AND manual_cash.organization_id=k.organization_id AND manual_cash.location_id=k.location_id
            LEFT JOIN manual_terminal ON manual_terminal.day=k.day AND manual_terminal.organization_id=k.organization_id AND manual_terminal.location_id=k.location_id
            WHERE loc.is_active=1 AND ($org IS NULL OR k.organization_id=$org)
              AND ($loc IS NULL OR k.location_id=$loc)
              AND ($from IS NULL OR k.day>=$from)
              AND ($to IS NULL OR k.day<$to)
            ORDER BY k.day DESC, org.name, loc.name
            """;
        cmd.Parameters.AddWithValue("$org", organizationId is null ? DBNull.Value : organizationId.Value.ToString());
        cmd.Parameters.AddWithValue("$loc", locationId is null ? DBNull.Value : locationId.Value.ToString());
        cmd.Parameters.AddWithValue("$from", (object?)fromText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$to", (object?)toText ?? DBNull.Value);

        using var reader = cmd.ExecuteReader();
        var result = new List<PointDaySummary>();
        while (reader.Read())
        {
            var rowOrganizationId = Guid.Parse(reader.GetString(1));
            var rowLocationId = Guid.Parse(reader.GetString(3));
            var bankElectronic = ReadMoney(reader, 5);
            var fiscalElectronic = ReadMoney(reader, 6);
            var shiftElectronic = ReadMoney(reader, 9);

            // ДВВС uses two iiko cash registers (plus the rarely used spare KKT).
            // For the working reconciliation the terminal side is the fiscal cashless amount:
            // if iiko recorded the payment, the integrated terminal recorded the same sale.
            // Raw Sber streams remain in operations for audit but must not inflate the accountant view.
            if (garant is not null &&
                garantDvvs is not null &&
                rowOrganizationId == garant.Id &&
                rowLocationId == garantDvvs.Id)
            {
                bankElectronic = fiscalElectronic ?? shiftElectronic;
            }

            result.Add(new(
                DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                rowOrganizationId,
                reader.GetString(2),
                rowLocationId,
                reader.GetString(4),
                bankElectronic,
                fiscalElectronic,
                ReadMoney(reader, 7),
                ReadMoney(reader, 8),
                shiftElectronic,
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
        using (var pragmas = connection.CreateCommand())
        {
            pragmas.CommandText = """
                PRAGMA busy_timeout=5000;
                PRAGMA temp_store=MEMORY;
                PRAGMA cache_size=-32768;
                PRAGMA mmap_size=268435456;
                """;
            pragmas.ExecuteNonQuery();
        }
        return connection;
    }
}
