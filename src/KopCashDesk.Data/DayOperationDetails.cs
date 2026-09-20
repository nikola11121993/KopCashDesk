using KopCashDesk.Core;

namespace KopCashDesk.Data;

public sealed record DayOperationDetail(DateTimeOffset Date, string Source, string Kind, string Payment, decimal Amount, string Register, string Fn, string Serial, string Rnm, int? Shift, string Status, string Document);

public static class DayOperationDetails
{
    public static IReadOnlyList<DayOperationDetail> DayOperations(this Database database, Guid org, Guid? location, DateOnly? day)
    {
        using var db = RegisterBindingService.Open(database); using var c = db.CreateCommand();
        c.CommandText = """
            SELECT o.occurred_at,o.source,o.kind,o.payment,o.amount_kopecks,o.register_display_name,o.fn,o.kkt_serial,o.registration_number,o.shift_number,
                   CASE WHEN o.location_id IS NULL THEN 'ККТ не привязана'
                        WHEN o.source_kind='Bank' THEN 'Банковская операция'
                        WHEN o.source='Frontol.Report' AND EXISTS(
                            SELECT 1 FROM fiscal_source_conflicts c
                            WHERE c.organization_id=o.organization_id AND c.location_id=o.location_id
                              AND c.business_date=substr(o.occurred_at,1,10))
                            THEN 'Расхождение с Такском — Frontol проверочный, не учтён'
                        WHEN o.source='Frontol.Report' AND EXISTS(
                            SELECT 1 FROM shift_source_links l
                            JOIN shift_closures s ON s.id=l.observed_shift_id
                            WHERE s.source=o.source AND o.external_id IN(s.external_id||':cash',s.external_id||':electronic'))
                            THEN 'Совпало с Такском — Frontol проверочный, не учтён повторно'
                        WHEN o.source='Frontol.Report' THEN 'Frontol без подтверждения ОФД — в итог не включён'
                        WHEN EXISTS(SELECT 1 FROM canonical_fiscal_operations f WHERE f.id=o.id)
                            THEN CASE WHEN o.source LIKE 'Taxcom.%' THEN 'Такском — учтено в кассе' ELSE 'Учтено в кассе' END
                        ELSE 'Подтверждение / перекрыто сменой — повторно не учтено' END,
                   COALESCE(d.original_name,'')
            FROM operations o LEFT JOIN source_documents d ON d.id=o.document_id
            WHERE o.organization_id=$org AND (($loc IS NULL AND o.location_id IS NULL) OR o.location_id=$loc)
              AND ($day IS NULL OR substr(o.occurred_at,1,10)=$day)
            ORDER BY o.occurred_at,o.source,o.external_id
            """;
        c.Parameters.AddWithValue("$org", org.ToString()); c.Parameters.AddWithValue("$loc", (object?)location?.ToString() ?? DBNull.Value); c.Parameters.AddWithValue("$day", (object?)day?.ToString("yyyy-MM-dd") ?? DBNull.Value);
        using var r = c.ExecuteReader(); var result = new List<DayOperationDetail>();
        while (r.Read()) result.Add(new(DateTimeOffset.Parse(r.GetString(0)), r.GetString(1), r.GetString(2), r.GetString(3), Money.FromKopecks(r.GetInt64(4)), r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8), r.IsDBNull(9) ? null : r.GetInt32(9), r.GetString(10), r.GetString(11)));
        return result;
    }
    public static IReadOnlyList<string> RegisterRepairReviews(this Database database, Guid? organization)
    {
        using var db = RegisterBindingService.Open(database); using var c = db.CreateCommand();
        c.CommandText = "SELECT l.name,r.reason FROM register_repair_reviews r JOIN locations l ON l.id=r.location_id WHERE ($org IS NULL OR l.organization_id=$org)";
        c.Parameters.AddWithValue("$org", (object?)organization?.ToString() ?? DBNull.Value); using var r = c.ExecuteReader(); var result = new List<string>();
        while (r.Read()) result.Add(r.GetString(0) + ": " + r.GetString(1)); return result;
    }
}
