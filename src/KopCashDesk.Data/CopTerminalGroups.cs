using KopCashDesk.Core;
using Microsoft.Data.Sqlite;

namespace KopCashDesk.Data;

public static class CopTerminalGroups
{
    private const string TaxId = "6683009222";

    private sealed record Group(string PointName, string HeadTid, string[] Tids, string LegacyName);

    private static readonly Group[] Groups =
    [
        new("Музыкальный колледж", "39413112", ["45697788", "45697789", "45697790"], "Музыкалка"),
        new("Кулинария Аппетит", "42526205", ["42526204", "42526205", "42603950"], "Кулинария Аппетит")
    ];

    public static Location? Resolve(string taxId, string tid, IReadOnlyList<Location> locations,
        IEnumerable<TerminalBinding> terminals, out string? pointName)
    {
        var group = taxId == TaxId ? Groups.FirstOrDefault(x => x.Tids.Contains(tid)) : null;
        pointName = group?.PointName;
        if (group is null) return null;

        var head = terminals.FirstOrDefault(x => x.Provider == "Sber" && x.TerminalId == group.HeadTid);
        return locations.FirstOrDefault(x => x.Id == head?.LocationId) ??
               KnownBusinessRules.FindKnownPoint(locations, group.PointName);
    }

    internal static int Repair(Database database)
    {
        var organization = database.Organizations().FirstOrDefault(x => x.TaxId == TaxId);
        if (organization is null) return 0;

        var locations = database.Locations().Where(x => x.OrganizationId == organization.Id).ToArray();
        var terminals = database.TerminalBindings().Where(x => x.OrganizationId == organization.Id).ToArray();
        var merged = 0;
        foreach (var group in Groups)
        {
            var head = terminals.SingleOrDefault(x => x.Provider == "Sber" && x.TerminalId == group.HeadTid);
            var target = locations.SingleOrDefault(x => x.Id == head?.LocationId && x.Name == group.PointName);
            if (target is null) continue;

            foreach (var source in locations.Where(x => x.Id != target.Id && x.Name == group.LegacyName))
            {
                var bindings = terminals.Where(x => x.LocationId == source.Id).ToArray();
                if (bindings.Length == 0 || bindings.Any(x => x.Provider != "Sber" ||
                        x.BindingSource != BindingSource.Automatic || x.IsLocked || !group.Tids.Contains(x.TerminalId)))
                    continue;
                if (database.MergeLocations(source.Id, target.Id,
                        $"ЦОП: подтверждено выгрузками Сбер; TID={string.Join(",", bindings.Select(x => x.TerminalId))}; головной TID={group.HeadTid}",
                        (db, tx) => HasOnlyVerifiedRows(db, tx, source.Id, target.Id, group)))
                    merged++;
            }
        }
        return merged;
    }

    private static bool HasOnlyVerifiedRows(SqliteConnection db, SqliteTransaction tx, Guid source, Guid target, Group group)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        // These two exports were reconciled against the imported rows and Taxcom shifts.
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM operations WHERE location_id=$loc)
              AND EXISTS(SELECT 1 FROM terminal_bindings WHERE location_id=$target AND provider='Sber' AND tid=$head)
              AND EXISTS(SELECT 1 FROM terminal_bindings WHERE location_id=$loc)
              AND NOT EXISTS(
                  SELECT 1 FROM terminal_bindings WHERE location_id=$loc AND
                    (provider<>'Sber' OR binding_source<>'Automatic' OR is_locked<>0 OR tid NOT IN ($tid1,$tid2,$tid3)))
              AND NOT EXISTS(
                  SELECT 1 FROM operations o LEFT JOIN source_documents d ON d.id=o.document_id
                  WHERE o.location_id=$loc AND (
                    o.source<>'Sber.Acquiring' OR o.source_kind<>'Bank' OR d.sha256 IS NULL OR
                    d.sha256 NOT IN (
                      '75a572bb1b6631111daa04395bc69683191861fab676be170c5881878efd685e',
                      'e416089145dd5b310445873a30a66c5b47ef6b0b597d1e70e7c2ee1731856992')))
              AND NOT EXISTS(SELECT 1 FROM shift_closures WHERE location_id=$loc)
              AND NOT EXISTS(SELECT 1 FROM register_bindings WHERE location_id=$loc)
              AND NOT EXISTS(SELECT 1 FROM manual_cash_postings WHERE location_id=$loc)
              AND NOT EXISTS(SELECT 1 FROM manual_terminal_postings WHERE location_id=$loc);
            """;
        command.Parameters.AddWithValue("$loc", source.ToString());
        command.Parameters.AddWithValue("$target", target.ToString());
        command.Parameters.AddWithValue("$head", group.HeadTid);
        for (var index = 0; index < group.Tids.Length; index++)
            command.Parameters.AddWithValue($"$tid{index + 1}", group.Tids[index]);
        return Convert.ToInt64(command.ExecuteScalar()) != 0;
    }
}
