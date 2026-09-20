using KopCashDesk.Core;
using Microsoft.Data.Sqlite;

namespace KopCashDesk.Data;

public sealed record ShiftDetail(string Id, string Source, string ExternalId, string Register, string KktSerial, string Fn, string Rnm,
    int? ShiftNumber, DateTimeOffset ClosedAt, decimal Total, decimal Cash, decimal Electronic, string Status, bool Included, string Document);

public static class ShiftDetailExtensions
{
    public static IReadOnlyList<ShiftDetail> ShiftDetails(this Database database, Guid org, Guid? location, DateOnly? day = null)
    {
        database.RebuildCrossSourceShiftMatches();
        using var db = RegisterBindingService.Open(database); using var c = db.CreateCommand();
        c.CommandText = """
            SELECT s.id,s.source,s.external_id,s.register_display_name,s.kkt_serial,s.fn,s.registration_number,
                   s.shift_number,s.closed_at,s.total_kopecks,s.cash_kopecks,s.electronic_kopecks,
                   EXISTS(SELECT 1 FROM shift_source_links l WHERE l.observed_shift_id=s.id),
                   EXISTS(SELECT 1 FROM fiscal_source_conflicts c WHERE c.taxcom_shift_id=s.id OR c.frontol_shift_id=s.id),
                   COALESCE(d.original_name,'')
            FROM shift_closures s LEFT JOIN source_documents d ON d.id=s.document_id
            WHERE s.organization_id=$org AND (($loc IS NULL AND s.location_id IS NULL) OR s.location_id=$loc)
              AND ($day IS NULL OR substr(s.closed_at,1,10)=$day)
            ORDER BY s.closed_at,s.source,s.id
            """;
        c.Parameters.AddWithValue("$org", org.ToString()); c.Parameters.AddWithValue("$loc", (object?)location?.ToString() ?? DBNull.Value); c.Parameters.AddWithValue("$day", (object?)day?.ToString("yyyy-MM-dd") ?? DBNull.Value);
        var registers = database.RegisterBindings(); using var r = c.ExecuteReader(); var result = new List<ShiftDetail>();
        while (r.Read())
        {
            var closed = DateTimeOffset.Parse(r.GetString(8)); var fn = r.GetString(5); var name = r.GetString(3);
            if (name.Length == 0) name = registers.FirstOrDefault(b => b.OrganizationId == org && fn.Length > 0 && b.FiscalDriveNumber == fn &&
                (b.ValidFrom is null || b.ValidFrom <= DateOnly.FromDateTime(closed.DateTime)) && (b.ValidTo is null || b.ValidTo >= DateOnly.FromDateTime(closed.DateTime)))?.DisplayName ?? "";
            if (name.Length == 0) name = fn.Length > 0 ? $"ККТ, ФН {fn}" : r.GetString(1);

            var source = r.GetString(1);
            var isFrontol = source == "Frontol.Report";
            var duplicate = r.GetInt64(12) != 0;
            var mismatch = r.GetInt64(13) != 0;
            var included = location is not null && !duplicate && !isFrontol;

            var status = location is null
                ? "ККТ не привязана"
                : mismatch && isFrontol
                    ? "Расхождение с Такском — Frontol проверочный, в итог не включён"
                    : mismatch
                        ? "Такском — основной источник, учтён; Frontol отличается"
                        : duplicate && isFrontol
                            ? "Совпало с Такском — Frontol проверочный, повторно не учтён"
                            : isFrontol
                                ? "Есть в кассе Frontol, но нет подтверждения ОФД — в итог не включён"
                                : duplicate
                                    ? "Повторная запись источника — не учтена"
                                    : source == "Taxcom.ShiftReport"
                                        ? "Такском — учтено"
                                        : source == "FirstOFD.ShiftReport"
                                            ? "Первый ОФД — учтено"
                                            : "Учтено";

            result.Add(new(r.GetString(0), source, r.GetString(2), name, r.GetString(4), fn, r.GetString(6), r.IsDBNull(7) ? null : r.GetInt32(7), closed,
                Money.FromKopecks(r.GetInt64(9)), Money.FromKopecks(r.GetInt64(10)), Money.FromKopecks(r.GetInt64(11)), status, included, r.GetString(14)));
        }
        return result;
    }
}
