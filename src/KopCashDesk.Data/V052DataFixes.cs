using Microsoft.Data.Sqlite;

namespace KopCashDesk.Data;

public static class V052DataFixes
{
    public static int EnsureV052Fixes(this Database database)
    {
        var repaired = database.EnsureV051Fixes();

        using var db = Open(database);
        using var tx = db.BeginTransaction();

        var crossSourceShifts = Scalar(db, tx, """
            SELECT COUNT(*)
            FROM shift_closures f
            WHERE f.source='Frontol.Report'
              AND EXISTS(
                SELECT 1
                FROM shift_closures t
                WHERE t.source='Taxcom.ShiftReport'
                  AND t.organization_id=f.organization_id
                  AND t.location_id=f.location_id
                  AND t.shift_number=f.shift_number
                  AND substr(t.closed_at,1,10)=substr(f.closed_at,1,10)
                  AND t.total_kopecks=f.total_kopecks
                  AND t.cash_kopecks=f.cash_kopecks
                  AND t.electronic_kopecks=f.electronic_kopecks
              )
            """);

        Execute(db, tx, """
            DELETE FROM operations
            WHERE source='Frontol.Report'
              AND EXISTS(
                SELECT 1
                FROM shift_closures f
                JOIN shift_closures t
                  ON t.source='Taxcom.ShiftReport'
                 AND t.organization_id=f.organization_id
                 AND t.location_id=f.location_id
                 AND t.shift_number=f.shift_number
                 AND substr(t.closed_at,1,10)=substr(f.closed_at,1,10)
                 AND t.total_kopecks=f.total_kopecks
                 AND t.cash_kopecks=f.cash_kopecks
                 AND t.electronic_kopecks=f.electronic_kopecks
                WHERE f.source='Frontol.Report'
                  AND operations.organization_id=f.organization_id
                  AND operations.location_id=f.location_id
                  AND (operations.external_id=f.external_id || ':cash'
                       OR operations.external_id=f.external_id || ':electronic')
              );

            DELETE FROM shift_closures
            WHERE rowid IN(
                SELECT f.rowid
                FROM shift_closures f
                JOIN shift_closures t
                  ON t.source='Taxcom.ShiftReport'
                 AND t.organization_id=f.organization_id
                 AND t.location_id=f.location_id
                 AND t.shift_number=f.shift_number
                 AND substr(t.closed_at,1,10)=substr(f.closed_at,1,10)
                 AND t.total_kopecks=f.total_kopecks
                 AND t.cash_kopecks=f.cash_kopecks
                 AND t.electronic_kopecks=f.electronic_kopecks
                WHERE f.source='Frontol.Report'
            );
            """);

        var crptOperations = Scalar(db, tx, """
            SELECT COUNT(*)
            FROM operations o
            WHERE o.source='CRPT.Archive'
              AND EXISTS(
                SELECT 1
                FROM shift_closures t
                WHERE t.source='Taxcom.ShiftReport'
                  AND t.organization_id=o.organization_id
                  AND t.location_id=o.location_id
                  AND t.fn<>''
                  AND t.shift_number IS NOT NULL
                  AND o.external_id LIKE
                      'receipt:' || t.fn || ':' || CAST(t.shift_number AS TEXT) || ':%'
              )
            """);

        Execute(db, tx, """
            DELETE FROM operations
            WHERE source='CRPT.Archive'
              AND EXISTS(
                SELECT 1
                FROM shift_closures t
                WHERE t.source='Taxcom.ShiftReport'
                  AND t.organization_id=operations.organization_id
                  AND t.location_id=operations.location_id
                  AND t.fn<>''
                  AND t.shift_number IS NOT NULL
                  AND operations.external_id LIKE
                      'receipt:' || t.fn || ':' || CAST(t.shift_number AS TEXT) || ':%'
              );
            """);

        if (crossSourceShifts > 0 || crptOperations > 0)
        {
            Audit(db, tx, "fiscal.cross_source_duplicate_repair",
                $"frontol_shifts_removed={crossSourceShifts}; crpt_operations_removed={crptOperations}");
        }

        tx.Commit();
        return checked(repaired + (int)crossSourceShifts + (int)crptOperations);
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

    private static long Scalar(SqliteConnection db, SqliteTransaction tx, string sql)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void Execute(SqliteConnection db, SqliteTransaction tx, string sql)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        command.ExecuteNonQuery();
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
