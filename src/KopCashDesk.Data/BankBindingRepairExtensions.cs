using Microsoft.Data.Sqlite;

namespace KopCashDesk.Data;

public static class BankBindingRepairExtensions
{
    public static void RepairImportedBankBinding(this Database database, string source, string externalId, Guid? location)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database.Path, ForeignKeys = true }.ToString());
        db.Open();
        using var tx = db.BeginTransaction();
        using var c = db.CreateCommand();
        c.Transaction = tx;
        c.CommandText = "SELECT location_id FROM operations WHERE source=$source AND external_id=$external AND source_kind='Bank'";
        c.Parameters.AddWithValue("$source", source);
        c.Parameters.AddWithValue("$external", externalId);
        var old = c.ExecuteScalar();
        var next = (object?)location?.ToString() ?? DBNull.Value;
        if (old is null || Equals(old, next)) return;
        c.Parameters.AddWithValue("$loc", next);
        c.CommandText = "UPDATE operations SET location_id=$loc WHERE source=$source AND external_id=$external AND source_kind='Bank'";
        c.ExecuteNonQuery();
        c.Parameters.Clear();
        c.CommandText = """
            DELETE FROM reconciliation_allocations;
            INSERT INTO audit_log(occurred_at,action,details) VALUES($time,'bank.binding.repair',$details);
            """;
        c.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
        c.Parameters.AddWithValue("$details", $"source={source}; external_id={externalId}; old_loc={old}; new_loc={location}; reason=Reimport using dated terminal binding");
        c.ExecuteNonQuery();
        tx.Commit();
    }
}
