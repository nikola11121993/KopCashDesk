using KopCashDesk.Core;
using Microsoft.Data.Sqlite;

namespace KopCashDesk.Data;

public static class LocationMergeExtensions
{
    public static bool MergeLocations(this Database database, Guid sourceLocationId, Guid targetLocationId, string reason = "")
    {
        if (sourceLocationId == targetLocationId) return false;
        database.EnsureManualCashPostings();
        database.EnsureManualTerminalPostings();

        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.Path,
            Mode = SqliteOpenMode.ReadWrite,
            ForeignKeys = true
        }.ToString());
        db.Open();

        using var transaction = db.BeginTransaction();
        var source = ReadLocation(db, transaction, sourceLocationId);
        var target = ReadLocation(db, transaction, targetLocationId);
        if (source is null || target is null) return false;
        if (!source.Value.IsActive) return false;
        if (!target.Value.IsActive) throw new InvalidOperationException("Нельзя объединять в неактивную точку.");
        if (source.Value.OrganizationId != target.Value.OrganizationId)
            throw new InvalidOperationException("Нельзя объединять точки разных организаций.");

        EnsureManualCashCanMerge(db, transaction, sourceLocationId, targetLocationId);
        using (var check = db.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = "SELECT COUNT(*) FROM manual_terminal_postings WHERE location_id=$loc";
            check.Parameters.AddWithValue("$loc", sourceLocationId.ToString());
            if (Convert.ToInt64(check.ExecuteScalar()) > 0) throw new InvalidOperationException("Сначала проверьте ручные суммы терминала исходной точки; автоматический перенос запрещён.");
        }
        using (var snapshot = db.CreateCommand())
        {
            snapshot.Transaction = transaction;
            snapshot.CommandText = """
                INSERT INTO register_binding_audit(binding_id,old_value,new_value,occurred_at)
                SELECT id,json_object('LocationId',location_id,'FN',fn,'Source',binding_source),
                       json_object('LocationId',$target,'FN',fn,'Source',binding_source),strftime('%Y-%m-%dT%H:%M:%fZ','now')
                FROM register_bindings WHERE location_id=$source AND is_active=1;
                """;
            snapshot.Parameters.AddWithValue("$target", targetLocationId.ToString()); snapshot.Parameters.AddWithValue("$source", sourceLocationId.ToString()); snapshot.ExecuteNonQuery();
        }

        var operationCount = CountReferences(db, transaction, "operations", sourceLocationId);
        var shiftCount = CountReferences(db, transaction, "shift_closures", sourceLocationId);
        var registerCount = CountReferences(db, transaction, "register_bindings", sourceLocationId);
        var terminalCount = CountReferences(db, transaction, "terminal_bindings", sourceLocationId);
        var manualCount = CountReferences(db, transaction, "manual_cash_postings", sourceLocationId);
        var allocationCount = CountReferences(db, transaction, "reconciliation_allocations", sourceLocationId) +
                              CountReferences(db, transaction, "reconciliation_allocations", targetLocationId);

        MoveManualCashPostings(db, transaction, sourceLocationId, targetLocationId);
        UpdateLocationReference(db, transaction, "operations", sourceLocationId, targetLocationId);
        UpdateLocationReference(db, transaction, "shift_closures", sourceLocationId, targetLocationId);
        UpdateLocationReference(db, transaction, "register_bindings", sourceLocationId, targetLocationId);
        UpdateLocationReference(db, transaction, "terminal_bindings", sourceLocationId, targetLocationId);

        using (var allocations = db.CreateCommand())
        {
            allocations.Transaction = transaction;
            allocations.CommandText = "DELETE FROM reconciliation_allocations WHERE location_id=$source OR location_id=$target";
            allocations.Parameters.AddWithValue("$source", sourceLocationId.ToString());
            allocations.Parameters.AddWithValue("$target", targetLocationId.ToString());
            allocations.ExecuteNonQuery();
        }

        using (var mark = db.CreateCommand())
        {
            mark.Transaction = transaction;
            mark.CommandText = """
                UPDATE locations
                SET is_active=0, merged_into_location_id=$target, excluded=1
                WHERE id=$source
                """;
            mark.Parameters.AddWithValue("$source", sourceLocationId.ToString());
            mark.Parameters.AddWithValue("$target", targetLocationId.ToString());
            mark.ExecuteNonQuery();
        }

        using (var audit = db.CreateCommand())
        {
            audit.Transaction = transaction;
            audit.CommandText = "INSERT INTO audit_log(occurred_at,action,details) VALUES($t,$a,$d)";
            audit.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
            audit.Parameters.AddWithValue("$a", "location.merge");
            var details =
                $"source={sourceLocationId} ({source.Value.Name}); target={targetLocationId} ({target.Value.Name}); " +
                $"operations={operationCount}; shifts={shiftCount}; terminals={terminalCount}; registers={registerCount}; manual={manualCount}; stale_allocations_cleared={allocationCount}";
            if (!string.IsNullOrWhiteSpace(reason)) details += $"; reason={reason.Trim()}";
            audit.Parameters.AddWithValue("$d", details);
            audit.ExecuteNonQuery();
        }

        transaction.Commit();
        database.RebuildCrossSourceShiftMatches();
        return true;
    }

    public static int MergeLocationsByName(this Database database, string sourceName, string targetName, string reason = "")
    {
        var locations = database.Locations();
        var merges = 0;

        foreach (var organizationGroup in locations.GroupBy(x => x.OrganizationId))
        {
            var sources = organizationGroup.Where(x => NameEquals(x.Name, sourceName)).ToArray();
            var targets = organizationGroup.Where(x => NameEquals(x.Name, targetName)).ToArray();
            if (sources.Length != 1 || targets.Length != 1) continue;
            if (database.MergeLocations(sources[0].Id, targets[0].Id, reason)) merges++;
        }

        return merges;
    }

    private static void EnsureManualCashCanMerge(SqliteConnection db, SqliteTransaction transaction, Guid source, Guid target)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
            FROM manual_cash_postings s
            JOIN manual_cash_postings t
              ON t.organization_id=s.organization_id
             AND t.business_date=s.business_date
             AND t.location_id=$target
            WHERE s.location_id=$source
              AND s.electronic_kopecks<>t.electronic_kopecks
            """;
        command.Parameters.AddWithValue("$source", source.ToString());
        command.Parameters.AddWithValue("$target", target.ToString());
        var conflicts = Convert.ToInt32(command.ExecuteScalar());
        if (conflicts > 0)
            throw new InvalidOperationException("Нельзя объединить точки: на одинаковые даты есть разные ручные суммы кассы. Сначала исправьте или очистите эти суммы в своде.");
    }

    private static void MoveManualCashPostings(SqliteConnection db, SqliteTransaction transaction, Guid source, Guid target)
    {
        using (var insert = db.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO manual_cash_postings(
                    organization_id,location_id,business_date,electronic_kopecks,created_at,updated_at)
                SELECT organization_id,$target,business_date,electronic_kopecks,created_at,updated_at
                FROM manual_cash_postings
                WHERE location_id=$source
                """;
            insert.Parameters.AddWithValue("$source", source.ToString());
            insert.Parameters.AddWithValue("$target", target.ToString());
            insert.ExecuteNonQuery();
        }

        using var delete = db.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM manual_cash_postings WHERE location_id=$source";
        delete.Parameters.AddWithValue("$source", source.ToString());
        delete.ExecuteNonQuery();
    }

    private static (Guid OrganizationId, string Name, bool IsActive)? ReadLocation(SqliteConnection db, SqliteTransaction transaction, Guid id)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT organization_id,name,is_active FROM locations WHERE id=$id LIMIT 1";
        command.Parameters.AddWithValue("$id", id.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read() ? (Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetInt64(2) != 0) : null;
    }

    private static int CountReferences(SqliteConnection db, SqliteTransaction transaction, string table, Guid location)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE location_id=$location";
        command.Parameters.AddWithValue("$location", location.ToString());
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void UpdateLocationReference(SqliteConnection db, SqliteTransaction transaction, string table, Guid source, Guid target)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"UPDATE {table} SET location_id=$target WHERE location_id=$source" + (table == "register_bindings" ? " AND is_active=1" : "");
        command.Parameters.AddWithValue("$target", target.ToString());
        command.Parameters.AddWithValue("$source", source.ToString());
        command.ExecuteNonQuery();
    }

    private static bool NameEquals(string left, string right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string value) =>
        string.Join(' ', value.Trim().Replace('ё', 'е').Replace('Ё', 'Е').Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
