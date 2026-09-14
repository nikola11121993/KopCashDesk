using Microsoft.Data.Sqlite;

namespace KopCashDesk.Data;

public static class V053DataFixes
{
    private sealed record ShiftRow(
        long RowId,
        string Source,
        string ExternalId,
        string OrganizationId,
        string LocationId,
        string Day,
        long Total,
        long Cash,
        long Electronic,
        string Fn,
        int ShiftNumber);

    public static int EnsureV053Fixes(this Database database)
    {
        var repaired = database.EnsureV052Fixes();

        using var db = Open(database);
        using var tx = db.BeginTransaction();
        var rows = ReadShiftRows(db, tx);
        var remove = new List<ShiftRow>();

        foreach (var group in rows.GroupBy(x => new
                 {
                     x.OrganizationId,
                     x.LocationId,
                     x.Day,
                     x.ShiftNumber,
                     x.Total,
                     x.Cash,
                     x.Electronic
                 }))
        {
            var candidates = group.ToArray();
            if (candidates.Length < 2) continue;

            var nonEmptyFns = candidates
                .Select(x => NormalizeFn(x.Fn))
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (nonEmptyFns.Length == 1)
            {
                RemoveAllButPreferred(candidates, remove);
                continue;
            }

            if (nonEmptyFns.Length > 1)
            {
                foreach (var fn in nonEmptyFns)
                {
                    var sameRegister = candidates
                        .Where(x => NormalizeFn(x.Fn) == fn)
                        .ToArray();
                    if (sameRegister.Length > 1) RemoveAllButPreferred(sameRegister, remove);
                }
                continue;
            }

            // Старые версии иногда сохраняли обе копии без ФН. В таком случае
            // удаляем только очевидные дубли одного и того же источника.
            foreach (var sourceGroup in candidates.GroupBy(x => x.Source, StringComparer.OrdinalIgnoreCase))
            {
                var sameSource = sourceGroup.ToArray();
                if (sameSource.Length > 1) RemoveAllButPreferred(sameSource, remove);
            }
        }

        foreach (var duplicate in remove.DistinctBy(x => x.RowId))
        {
            DeleteRelatedOperations(db, tx, duplicate);
            using var delete = db.CreateCommand();
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM shift_closures WHERE rowid=$rowid";
            delete.Parameters.AddWithValue("$rowid", duplicate.RowId);
            delete.ExecuteNonQuery();
        }

        var removed = remove.Select(x => x.RowId).Distinct().Count();
        if (removed > 0)
            Audit(db, tx, "fiscal.shift_duplicate_repair.v053", $"removed={removed}");

        tx.Commit();
        return repaired + removed;
    }

    private static void RemoveAllButPreferred(IReadOnlyList<ShiftRow> rows, ICollection<ShiftRow> remove)
    {
        var keep = rows
            .OrderBy(SourceRank)
            .ThenBy(x => string.IsNullOrWhiteSpace(x.Fn) ? 1 : 0)
            .ThenByDescending(x => x.RowId)
            .First();

        foreach (var row in rows)
            if (row.RowId != keep.RowId) remove.Add(row);
    }

    private static int SourceRank(ShiftRow row) => row.Source switch
    {
        "Taxcom.ShiftReport" => 0,
        "Frontol.Report" => 1,
        _ => 2
    };

    private static string NormalizeFn(string value) => new(value.Where(char.IsDigit).ToArray());

    private static IReadOnlyList<ShiftRow> ReadShiftRows(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT rowid,source,external_id,organization_id,location_id,
                   substr(closed_at,1,10),total_kopecks,cash_kopecks,electronic_kopecks,
                   COALESCE(fn,''),shift_number
            FROM shift_closures
            WHERE shift_number IS NOT NULL
            ORDER BY rowid
            """;
        using var reader = cmd.ExecuteReader();
        var result = new List<ShiftRow>();
        while (reader.Read())
        {
            result.Add(new ShiftRow(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetString(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8), reader.GetString(9), reader.GetInt32(10)));
        }
        return result;
    }

    private static void DeleteRelatedOperations(SqliteConnection db, SqliteTransaction tx, ShiftRow row)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            DELETE FROM operations
            WHERE source=$source
              AND organization_id=$org
              AND location_id=$loc
              AND (external_id=$cash OR external_id=$electronic)
            """;
        cmd.Parameters.AddWithValue("$source", row.Source);
        cmd.Parameters.AddWithValue("$org", row.OrganizationId);
        cmd.Parameters.AddWithValue("$loc", row.LocationId);
        cmd.Parameters.AddWithValue("$cash", row.ExternalId + ":cash");
        cmd.Parameters.AddWithValue("$electronic", row.ExternalId + ":electronic");
        cmd.ExecuteNonQuery();
    }

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

    private static void Audit(SqliteConnection db, SqliteTransaction tx, string action, string details)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "INSERT INTO audit_log(occurred_at,action,details) VALUES($t,$a,$d)";
        command.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$a", action);
        command.Parameters.AddWithValue("$d", details);
        command.ExecuteNonQuery();
    }
}
