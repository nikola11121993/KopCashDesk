using Microsoft.Data.Sqlite;

namespace KopCashDesk.Data;

internal static class CanonicalFiscalProjection
{
    public static void Create(SqliteConnection db, SqliteTransaction? tx = null)
    {
        using var c = db.CreateCommand(); c.Transaction = tx;
        c.CommandText = """
            CREATE VIEW IF NOT EXISTS canonical_fiscal_operations AS
            SELECT o.* FROM operations o
            JOIN locations loc ON loc.id=o.location_id AND loc.is_active=1
            WHERE o.source_kind='Fiscal' AND o.source<>'Manual.CashOverride'
              AND NOT EXISTS(
                SELECT 1 FROM shift_closures s JOIN shift_source_links l ON l.observed_shift_id=s.id
                WHERE s.source=o.source AND o.external_id IN(s.external_id||':cash',s.external_id||':electronic'))
              AND NOT EXISTS(
                SELECT 1 FROM fiscal_source_conflicts c
                WHERE c.organization_id=o.organization_id AND c.location_id=o.location_id AND c.business_date=substr(o.occurred_at,1,10))
              AND NOT (o.source='Taxcom.FiscalDocuments' AND EXISTS(
                SELECT 1 FROM shift_closures s
                WHERE s.organization_id=o.organization_id AND s.location_id=o.location_id
                AND s.source='Taxcom.ShiftReport' AND substr(s.closed_at,1,10)=substr(o.occurred_at,1,10)
                AND ((o.fn<>'' AND s.fn=o.fn) OR (o.kkt_serial<>'' AND s.kkt_serial=o.kkt_serial))
                AND (o.shift_number IS NULL OR s.shift_number=o.shift_number)));
            """; c.ExecuteNonQuery();
    }
}
