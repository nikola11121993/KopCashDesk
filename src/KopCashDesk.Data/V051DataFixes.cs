using KopCashDesk.Core;
using Microsoft.Data.Sqlite;

namespace KopCashDesk.Data;

public static class V051DataFixes
{
    public static int EnsureV051Fixes(this Database database)
    {
        database.EnsureManualCashPostings();
        using var db = Open(database);
        using var tx = db.BeginTransaction();

        var duplicates = Scalar(db, tx, """
            SELECT COALESCE(SUM(cnt - 1),0)
            FROM (
                SELECT COUNT(*) cnt
                FROM shift_closures
                WHERE source='Taxcom.ShiftReport' AND fn<>'' AND shift_number IS NOT NULL
                GROUP BY organization_id,fn,shift_number
                HAVING COUNT(*)>1
            )
            """);

        Execute(db, tx, """
            DROP TRIGGER IF EXISTS trg_shift_closures_taxcom_business_upsert;
            DROP TRIGGER IF EXISTS trg_shift_closures_taxcom_rekey_operations;
            DROP INDEX IF EXISTS ux_shift_closures_taxcom_business;

            DELETE FROM shift_closures
            WHERE rowid IN (
                SELECT rowid FROM (
                    SELECT rowid,
                           ROW_NUMBER() OVER(
                               PARTITION BY organization_id,fn,shift_number
                               ORDER BY rowid DESC
                           ) duplicate_no
                    FROM shift_closures
                    WHERE source='Taxcom.ShiftReport' AND fn<>'' AND shift_number IS NOT NULL
                ) WHERE duplicate_no>1
            );

            DELETE FROM operations WHERE source='Taxcom.ShiftReport';

            INSERT INTO operations(
                id,source,external_id,organization_id,location_id,occurred_at,
                source_kind,kind,payment,amount_kopecks,document_id)
            SELECT lower(hex(randomblob(16))),'Taxcom.ShiftReport',external_id || ':cash',
                   organization_id,location_id,closed_at,'Fiscal',
                   CASE WHEN cash_kopecks<0 THEN 'Return' ELSE 'Sale' END,
                   'Cash',cash_kopecks,document_id
            FROM shift_closures WHERE source='Taxcom.ShiftReport';

            INSERT INTO operations(
                id,source,external_id,organization_id,location_id,occurred_at,
                source_kind,kind,payment,amount_kopecks,document_id)
            SELECT lower(hex(randomblob(16))),'Taxcom.ShiftReport',external_id || ':electronic',
                   organization_id,location_id,closed_at,'Fiscal',
                   CASE WHEN electronic_kopecks<0 THEN 'Return' ELSE 'Sale' END,
                   'Electronic',electronic_kopecks,document_id
            FROM shift_closures WHERE source='Taxcom.ShiftReport';

            CREATE UNIQUE INDEX ux_shift_closures_taxcom_business
                ON shift_closures(organization_id,fn,shift_number)
                WHERE source='Taxcom.ShiftReport' AND fn<>'' AND shift_number IS NOT NULL;

            CREATE TRIGGER trg_shift_closures_taxcom_rekey_operations
            AFTER UPDATE OF external_id,location_id,closed_at,cash_kopecks,electronic_kopecks,document_id
            ON shift_closures
            WHEN NEW.source='Taxcom.ShiftReport'
            BEGIN
                UPDATE operations SET
                    external_id=NEW.external_id || ':cash',
                    organization_id=NEW.organization_id,
                    location_id=NEW.location_id,
                    occurred_at=NEW.closed_at,
                    source_kind='Fiscal',
                    kind=CASE WHEN NEW.cash_kopecks<0 THEN 'Return' ELSE 'Sale' END,
                    payment='Cash',amount_kopecks=NEW.cash_kopecks,document_id=NEW.document_id
                WHERE source='Taxcom.ShiftReport' AND external_id=OLD.external_id || ':cash';

                UPDATE operations SET
                    external_id=NEW.external_id || ':electronic',
                    organization_id=NEW.organization_id,
                    location_id=NEW.location_id,
                    occurred_at=NEW.closed_at,
                    source_kind='Fiscal',
                    kind=CASE WHEN NEW.electronic_kopecks<0 THEN 'Return' ELSE 'Sale' END,
                    payment='Electronic',amount_kopecks=NEW.electronic_kopecks,document_id=NEW.document_id
                WHERE source='Taxcom.ShiftReport' AND external_id=OLD.external_id || ':electronic';
            END;

            CREATE TRIGGER trg_shift_closures_taxcom_business_upsert
            BEFORE INSERT ON shift_closures
            WHEN NEW.source='Taxcom.ShiftReport' AND NEW.fn<>'' AND NEW.shift_number IS NOT NULL
             AND EXISTS(
                SELECT 1 FROM shift_closures
                WHERE organization_id=NEW.organization_id AND source='Taxcom.ShiftReport'
                  AND fn=NEW.fn AND shift_number=NEW.shift_number
             )
            BEGIN
                UPDATE shift_closures SET
                    external_id=NEW.external_id,location_id=NEW.location_id,closed_at=NEW.closed_at,
                    total_kopecks=NEW.total_kopecks,cash_kopecks=NEW.cash_kopecks,
                    electronic_kopecks=NEW.electronic_kopecks,document_id=NEW.document_id
                WHERE organization_id=NEW.organization_id AND source='Taxcom.ShiftReport'
                  AND fn=NEW.fn AND shift_number=NEW.shift_number;
                SELECT RAISE(IGNORE);
            END;
            """);

        CreateManualOverrideTriggers(db, tx);
        CreateFiscalRefreshTriggers(db, tx);
        Execute(db, tx, "UPDATE manual_cash_postings SET electronic_kopecks=electronic_kopecks;");

        if (duplicates > 0)
            Audit(db, tx, "taxcom.duplicate_repair", $"removed={duplicates}");

        tx.Commit();
        return checked((int)duplicates);
    }

    private static void CreateManualOverrideTriggers(SqliteConnection db, SqliteTransaction tx)
    {
        const string refresh = """
            DELETE FROM operations
            WHERE source='Manual.CashOverride'
              AND external_id='manual-cash:' || NEW.organization_id || ':' || NEW.location_id || ':' || NEW.business_date;

            INSERT INTO operations(
                id,source,external_id,organization_id,location_id,occurred_at,
                source_kind,kind,payment,amount_kopecks,document_id)
            SELECT lower(hex(randomblob(16))),'Manual.CashOverride',
                   'manual-cash:' || NEW.organization_id || ':' || NEW.location_id || ':' || NEW.business_date,
                   NEW.organization_id,NEW.location_id,NEW.business_date || 'T23:59:59+00:00',
                   'Fiscal','Correction','Electronic',
                   NEW.electronic_kopecks - COALESCE((
                       SELECT SUM(amount_kopecks) FROM operations
                       WHERE organization_id=NEW.organization_id AND location_id=NEW.location_id
                         AND substr(occurred_at,1,10)=NEW.business_date
                         AND source_kind='Fiscal' AND payment='Electronic'
                         AND source<>'Manual.CashOverride'
                   ),0),NULL
            WHERE NEW.electronic_kopecks - COALESCE((
                SELECT SUM(amount_kopecks) FROM operations
                WHERE organization_id=NEW.organization_id AND location_id=NEW.location_id
                  AND substr(occurred_at,1,10)=NEW.business_date
                  AND source_kind='Fiscal' AND payment='Electronic'
                  AND source<>'Manual.CashOverride'
            ),0)<>0;
            """;

        Execute(db, tx, $"""
            DROP TRIGGER IF EXISTS trg_manual_cash_override_insert;
            DROP TRIGGER IF EXISTS trg_manual_cash_override_update;
            DROP TRIGGER IF EXISTS trg_manual_cash_override_delete;

            CREATE TRIGGER trg_manual_cash_override_insert AFTER INSERT ON manual_cash_postings
            BEGIN {refresh} END;

            CREATE TRIGGER trg_manual_cash_override_update AFTER UPDATE OF electronic_kopecks ON manual_cash_postings
            BEGIN {refresh} END;

            CREATE TRIGGER trg_manual_cash_override_delete AFTER DELETE ON manual_cash_postings
            BEGIN
                DELETE FROM operations WHERE source='Manual.CashOverride'
                  AND external_id='manual-cash:' || OLD.organization_id || ':' || OLD.location_id || ':' || OLD.business_date;
            END;
            """);
    }

    private static void CreateFiscalRefreshTriggers(SqliteConnection db, SqliteTransaction tx)
    {
        var refreshNew = RefreshManualOverride("NEW");
        var refreshOld = RefreshManualOverride("OLD");
        Execute(db, tx, $"""
            DROP TRIGGER IF EXISTS trg_fiscal_electronic_override_insert;
            DROP TRIGGER IF EXISTS trg_fiscal_electronic_override_update;
            DROP TRIGGER IF EXISTS trg_fiscal_electronic_override_delete;

            CREATE TRIGGER trg_fiscal_electronic_override_insert AFTER INSERT ON operations
            WHEN NEW.source_kind='Fiscal' AND NEW.payment='Electronic' AND NEW.source<>'Manual.CashOverride'
            BEGIN {refreshNew} END;

            CREATE TRIGGER trg_fiscal_electronic_override_update
            AFTER UPDATE OF organization_id,location_id,occurred_at,source_kind,payment,amount_kopecks ON operations
            WHEN (OLD.source_kind='Fiscal' AND OLD.payment='Electronic' AND OLD.source<>'Manual.CashOverride')
              OR (NEW.source_kind='Fiscal' AND NEW.payment='Electronic' AND NEW.source<>'Manual.CashOverride')
            BEGIN {refreshOld} {refreshNew} END;

            CREATE TRIGGER trg_fiscal_electronic_override_delete AFTER DELETE ON operations
            WHEN OLD.source_kind='Fiscal' AND OLD.payment='Electronic' AND OLD.source<>'Manual.CashOverride'
            BEGIN {refreshOld} END;
            """);
    }

    private static string RefreshManualOverride(string row) => $"""
        DELETE FROM operations WHERE source='Manual.CashOverride'
          AND external_id='manual-cash:' || {row}.organization_id || ':' || {row}.location_id || ':' || substr({row}.occurred_at,1,10);

        INSERT INTO operations(
            id,source,external_id,organization_id,location_id,occurred_at,
            source_kind,kind,payment,amount_kopecks,document_id)
        SELECT lower(hex(randomblob(16))),'Manual.CashOverride',
               'manual-cash:' || m.organization_id || ':' || m.location_id || ':' || m.business_date,
               m.organization_id,m.location_id,m.business_date || 'T23:59:59+00:00',
               'Fiscal','Correction','Electronic',
               m.electronic_kopecks - COALESCE((
                   SELECT SUM(amount_kopecks) FROM operations o
                   WHERE o.organization_id=m.organization_id AND o.location_id=m.location_id
                     AND substr(o.occurred_at,1,10)=m.business_date
                     AND o.source_kind='Fiscal' AND o.payment='Electronic'
                     AND o.source<>'Manual.CashOverride'
               ),0),NULL
        FROM manual_cash_postings m
        WHERE m.organization_id={row}.organization_id AND m.location_id={row}.location_id
          AND m.business_date=substr({row}.occurred_at,1,10)
          AND m.electronic_kopecks - COALESCE((
              SELECT SUM(amount_kopecks) FROM operations o
              WHERE o.organization_id=m.organization_id AND o.location_id=m.location_id
                AND substr(o.occurred_at,1,10)=m.business_date
                AND o.source_kind='Fiscal' AND o.payment='Electronic'
                AND o.source<>'Manual.CashOverride'
          ),0)<>0;
        """;

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
