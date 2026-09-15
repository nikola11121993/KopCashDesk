using KopCashDesk.Core;
using Microsoft.Data.Sqlite;

namespace KopCashDesk.Data;

public static class RegisterDataRepair
{
    public static int RepairRegisterLocations(this Database database)
    {
        var repaired = 0;
        var locations = database.Locations().ToArray();
        foreach (var source in locations)
        {
            var target = RegisterBindingService.RuleLocation(database, locations.Where(x => x.OrganizationId == source.OrganizationId).ToArray(), "", source.Name);
            if (target is null || source.Id == target.Id) continue;
            var registers = database.RegisterBindings().Where(x => x.LocationId == source.Id).ToArray();
            using (var db = RegisterBindingService.Open(database))
            using (var c = db.CreateCommand())
            {
                c.CommandText = """
                    SELECT (SELECT COUNT(*) FROM operations WHERE location_id=$loc AND source_kind='Bank')
                         + (SELECT COUNT(*) FROM terminal_bindings WHERE location_id=$loc)
                         + (SELECT COUNT(*) FROM manual_cash_postings WHERE location_id=$loc)
                         + (SELECT COUNT(*) FROM manual_terminal_postings WHERE location_id=$loc)
                    """;
                database.EnsureManualTerminalPostings();
                c.Parameters.AddWithValue("$loc", source.Id.ToString());
                if (Convert.ToInt64(c.ExecuteScalar()) > 0 || registers.Length == 0 || registers.Any(x => x.IsLocked || x.BindingSource == BindingSource.Manual))
                {
                    c.CommandText = "INSERT OR IGNORE INTO register_repair_reviews(location_id,reason,created_at) VALUES($loc,'У точки есть банковская история, ручные данные или неустановленная ККТ. Автоматическое объединение запрещено.',strftime('%Y-%m-%dT%H:%M:%fZ','now'))";
                    c.ExecuteNonQuery(); continue;
                }
            }
            // The whole source point is a verified register-name alias and has no bank/manual evidence.
            if (repaired == 0) database.BackupBeforeMigration(4);
            foreach (var b in registers)
                database.SaveRegisterBinding(b with { DisplayName = b.DisplayName.Length > 0 ? b.DisplayName : source.Name, BindingSource = BindingSource.Rule });
            if (database.MergeLocations(source.Id, target.Id, "исправление псевдо-точки ККТ по явному правилу; банковских и ручных данных нет")) repaired++;
        }
        // Historic moves deliberately need dated source evidence, including closed UBRiR accounts.
        using (var db = RegisterBindingService.Open(database))
        using (var c = db.CreateCommand())
        {
            c.CommandText = """
                INSERT OR IGNORE INTO register_repair_reviews(location_id,reason,created_at)
                SELECT id,'Проверьте дату переезда ККТ: Белокаменный кафе → Ленинградская 1 → Мира 4. Прошлые операции УБРиР сохраняются. Прежнее объединение нельзя автоматически обратить без исходных дат.',strftime('%Y-%m-%dT%H:%M:%fZ','now')
                FROM locations WHERE merged_into_location_id IS NOT NULL
                  AND (name LIKE '%Ленинград%' OR name LIKE '%Белокамен%' OR name LIKE '%Вороний%');
                """; c.ExecuteNonQuery();
        }
        database.RebuildCrossSourceShiftMatches();
        return repaired;
    }
}
