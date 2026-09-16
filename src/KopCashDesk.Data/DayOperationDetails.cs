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
                        WHEN o.source_kind='FiscalConflict' THEN 'Конфликт источников'
                        WHEN EXISTS(SELECT 1 FROM canonical_fiscal_operations f WHERE f.id=o.id) THEN 'Учтено в кассе'
                        ELSE 'Подтверждение / перекрыто сменой / конфликт — повторно не учтено' END,
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
