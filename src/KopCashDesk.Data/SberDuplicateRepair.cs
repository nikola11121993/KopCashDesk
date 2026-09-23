using Microsoft.Data.Sqlite;

namespace KopCashDesk.Data;

public static class SberDuplicateRepair
{
    private sealed record Row(
        string Id,
        string OrganizationId,
        string LocationId,
        string OccurredAt,
        string Kind,
        string Payment,
        long AmountKopecks,
        string DocumentId,
        string ImportedAt);

    public static int RepairSberOverlappingImports(this Database database)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true
        }.ToString());
        db.Open();

        var rows = new List<Row>();
        using (var read = db.CreateCommand())
        {
            read.CommandText = """
                SELECT
                    o.id,
                    o.organization_id,
                    o.location_id,
                    o.occurred_at,
                    o.kind,
                    o.payment,
                    o.amount_kopecks,
                    COALESCE(o.document_id,''),
                    COALESCE(d.imported_at,'')
                FROM operations o
                LEFT JOIN source_documents d ON d.id=o.document_id
                WHERE o.source='Sber.Acquiring'
                  AND o.source_kind='Bank'
                  AND o.payment='Electronic'
                  AND o.location_id IS NOT NULL
                ORDER BY o.organization_id,o.location_id,o.occurred_at,o.kind,o.payment,o.amount_kopecks,d.imported_at,o.id
                """;

            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetInt64(6),
                    reader.GetString(7),
                    reader.GetString(8)));
            }
        }

        var toDelete = new List<string>();
        foreach (var group in rows.GroupBy(x => new
        {
            x.OrganizationId,
            x.LocationId,
            x.OccurredAt,
            x.Kind,
            x.Payment,
            x.AmountKopecks
        }))
        {
            if (group.Count() < 2) continue;

            var byDocument = group
                .GroupBy(x => string.IsNullOrWhiteSpace(x.DocumentId) ? $"row:{x.Id}" : x.DocumentId)
                .Select(g => new
                {
                    DocumentKey = g.Key,
                    Rows = g.ToArray(),
                    Count = g.Count(),
                    ImportedAt = g.Min(x => x.ImportedAt)
                })
                .ToArray();

            // Several equal operations inside ONE Sber report may be real transactions.
            // Duplicates caused by overlapping exports appear in different source documents.
            if (byDocument.Length < 2) continue;

            var keeper = byDocument
                .OrderByDescending(x => x.Count)
                .ThenBy(x => x.ImportedAt, StringComparer.Ordinal)
                .ThenBy(x => x.DocumentKey, StringComparer.Ordinal)
                .First();

            foreach (var document in byDocument)
            {
                if (ReferenceEquals(document, keeper)) continue;
                toDelete.AddRange(document.Rows.Select(x => x.Id));
            }
        }

        if (toDelete.Count == 0) return 0;

        using var transaction = db.BeginTransaction();
        foreach (var id in toDelete.Distinct(StringComparer.Ordinal))
        {
            using var delete = db.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM operations WHERE id=$id AND source='Sber.Acquiring'";
            delete.Parameters.AddWithValue("$id", id);
            delete.ExecuteNonQuery();
        }

        using (var audit = db.CreateCommand())
        {
            audit.Transaction = transaction;
            audit.CommandText = "INSERT INTO audit_log(occurred_at,action,details) VALUES($t,'sber.duplicate_repair',$d)";
            audit.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
            audit.Parameters.AddWithValue("$d", $"removed={toDelete.Count}");
            audit.ExecuteNonQuery();
        }

        transaction.Commit();
        return toDelete.Count;
    }
}
