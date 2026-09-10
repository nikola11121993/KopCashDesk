using KopCashDesk.Core;
using Microsoft.Data.Sqlite;

namespace KopCashDesk.Data;

public static class LocationMergeExtensions
{
    public static bool MergeLocations(this Database database, Guid sourceLocationId, Guid targetLocationId, string reason = "")
    {
        if (sourceLocationId == targetLocationId) return false;

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
        if (source.Value.OrganizationId != target.Value.OrganizationId)
            throw new InvalidOperationException("Нельзя объединять точки разных организаций.");

        UpdateLocationReference(db, transaction, "operations", sourceLocationId, targetLocationId);
        UpdateLocationReference(db, transaction, "shift_closures", sourceLocationId, targetLocationId);
        UpdateLocationReference(db, transaction, "register_bindings", sourceLocationId, targetLocationId);
        UpdateLocationReference(db, transaction, "terminal_bindings", sourceLocationId, targetLocationId);

        using (var delete = db.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM locations WHERE id=$source";
            delete.Parameters.AddWithValue("$source", sourceLocationId.ToString());
            delete.ExecuteNonQuery();
        }

        using (var audit = db.CreateCommand())
        {
            audit.Transaction = transaction;
            audit.CommandText = "INSERT INTO audit_log(occurred_at,action,details) VALUES($t,$a,$d)";
            audit.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
            audit.Parameters.AddWithValue("$a", "location.merge");
            var details = $"{source.Value.Name} -> {target.Value.Name}";
            if (!string.IsNullOrWhiteSpace(reason)) details += $"; {reason.Trim()}";
            audit.Parameters.AddWithValue("$d", details);
            audit.ExecuteNonQuery();
        }

        transaction.Commit();
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

    private static (Guid OrganizationId, string Name)? ReadLocation(SqliteConnection db, SqliteTransaction transaction, Guid id)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT organization_id,name FROM locations WHERE id=$id LIMIT 1";
        command.Parameters.AddWithValue("$id", id.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read() ? (Guid.Parse(reader.GetString(0)), reader.GetString(1)) : null;
    }

    private static void UpdateLocationReference(SqliteConnection db, SqliteTransaction transaction, string table, Guid source, Guid target)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"UPDATE {table} SET location_id=$target WHERE location_id=$source";
        command.Parameters.AddWithValue("$target", target.ToString());
        command.Parameters.AddWithValue("$source", source.ToString());
        command.ExecuteNonQuery();
    }

    private static bool NameEquals(string left, string right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string value) =>
        string.Join(' ', value.Trim().Replace('ё', 'е').Replace('Ё', 'Е').Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
