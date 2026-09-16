using Microsoft.Data.Sqlite;

namespace KopCashDesk.Data;

internal static class RegisterSchemaMigration
{
    public static void Apply(SqliteConnection db)
    {
        // Rebuild nullable foreign keys without rewriting the references in the link tables.
        using (var off = db.CreateCommand()) { off.CommandText = "PRAGMA foreign_keys=OFF"; off.ExecuteNonQuery(); }
        try
        {
            using var tx = db.BeginTransaction();
            using var c = db.CreateCommand();
            c.Transaction = tx;
            c.CommandText = """
                CREATE TABLE register_bindings_v4(
                    id TEXT PRIMARY KEY, organization_id TEXT NOT NULL REFERENCES organizations(id),
                    location_id TEXT REFERENCES locations(id), fn TEXT NOT NULL DEFAULT '', register_number TEXT NOT NULL DEFAULT '',
                    binding_source TEXT NOT NULL DEFAULT 'Automatic', is_locked INTEGER NOT NULL DEFAULT 0,
                    valid_from TEXT, valid_to TEXT, kkt_serial TEXT NOT NULL DEFAULT '', display_name TEXT NOT NULL DEFAULT '',
                    created_at TEXT NOT NULL, updated_at TEXT NOT NULL, is_active INTEGER NOT NULL DEFAULT 1,
                    CHECK(valid_from IS NULL OR valid_to IS NULL OR valid_from<=valid_to));
                INSERT INTO register_bindings_v4
                    SELECT id,organization_id,location_id,fn,register_number,binding_source,is_locked,valid_from,valid_to,
                           '', '', strftime('%Y-%m-%dT%H:%M:%fZ','now'),strftime('%Y-%m-%dT%H:%M:%fZ','now'),1
                    FROM register_bindings;
                DROP TABLE register_bindings;
                ALTER TABLE register_bindings_v4 RENAME TO register_bindings;
                CREATE INDEX ix_register_identity ON register_bindings(organization_id,fn,kkt_serial,register_number,is_active);

                CREATE TABLE shift_closures_v4(
                    id TEXT PRIMARY KEY, source TEXT NOT NULL, external_id TEXT NOT NULL,
                    organization_id TEXT NOT NULL REFERENCES organizations(id), location_id TEXT REFERENCES locations(id),
                    closed_at TEXT NOT NULL, total_kopecks INTEGER NOT NULL, cash_kopecks INTEGER NOT NULL,
                    electronic_kopecks INTEGER NOT NULL, fn TEXT NOT NULL DEFAULT '', shift_number INTEGER,
                    document_id TEXT REFERENCES source_documents(id), kkt_serial TEXT NOT NULL DEFAULT '',
                    registration_number TEXT NOT NULL DEFAULT '', register_display_name TEXT NOT NULL DEFAULT '',
                    UNIQUE(source,external_id));
                INSERT INTO shift_closures_v4(id,source,external_id,organization_id,location_id,closed_at,total_kopecks,cash_kopecks,electronic_kopecks,fn,shift_number,document_id)
                    SELECT id,source,external_id,organization_id,location_id,closed_at,total_kopecks,cash_kopecks,electronic_kopecks,fn,shift_number,document_id FROM shift_closures;
                DROP TABLE shift_closures;
                ALTER TABLE shift_closures_v4 RENAME TO shift_closures;
                CREATE INDEX ix_shift_closures_summary ON shift_closures(organization_id,location_id,closed_at);
                ALTER TABLE operations ADD COLUMN fn TEXT NOT NULL DEFAULT '';
                ALTER TABLE operations ADD COLUMN kkt_serial TEXT NOT NULL DEFAULT '';
                ALTER TABLE operations ADD COLUMN registration_number TEXT NOT NULL DEFAULT '';
                ALTER TABLE operations ADD COLUMN register_display_name TEXT NOT NULL DEFAULT '';
                ALTER TABLE operations ADD COLUMN shift_number INTEGER;

                CREATE TABLE register_location_rules(
                    rule_key TEXT PRIMARY KEY, identity_kind TEXT NOT NULL, identity_value TEXT NOT NULL,
                    target_name TEXT NOT NULL, target_address TEXT NOT NULL DEFAULT '');
                INSERT INTO register_location_rules VALUES
                    ('reftinskaya-serial','serial','00106900361561','Рефтинская ГРЭС 6 столовая',''),
                    ('ati-mercury','display','Меркурий 180Ф','Столовая АТИ','Плеханова 64'),
                    ('ati-appetit','display','Кулинария Аппетит','Столовая АТИ','Плеханова 64'),
                    ('ladyzhenskogo','display','Ладыженского, 7','Ладыженского 7','Ладыженского 7'),
                    ('chapaeva','display','Чапаева/МЧС','Чапаева 28','Чапаева 28'),
                    ('college','display','Музыкальный колледж','Колледж искусств','Войкова 62');

                CREATE TABLE register_binding_audit(
                    id INTEGER PRIMARY KEY AUTOINCREMENT, binding_id TEXT NOT NULL, old_value TEXT NOT NULL,
                    new_value TEXT NOT NULL, occurred_at TEXT NOT NULL);
                CREATE TABLE register_repair_reviews(
                    location_id TEXT PRIMARY KEY REFERENCES locations(id), reason TEXT NOT NULL, created_at TEXT NOT NULL);
                DROP TRIGGER IF EXISTS trg_manual_cash_override_insert;
                DROP TRIGGER IF EXISTS trg_manual_cash_override_update;
                DROP TRIGGER IF EXISTS trg_manual_cash_override_delete;
                DROP TRIGGER IF EXISTS trg_fiscal_electronic_override_insert;
                DROP TRIGGER IF EXISTS trg_fiscal_electronic_override_update;
                DROP TRIGGER IF EXISTS trg_fiscal_electronic_override_delete;
                UPDATE operations SET
                    fn=COALESCE((SELECT s.fn FROM shift_closures s WHERE s.source=operations.source AND operations.external_id IN(s.external_id||':cash',s.external_id||':electronic')),''),
                    shift_number=(SELECT s.shift_number FROM shift_closures s WHERE s.source=operations.source AND operations.external_id IN(s.external_id||':cash',s.external_id||':electronic'))
                WHERE source IN('Taxcom.ShiftReport','Frontol.Report');
                UPDATE operations SET source_kind='LegacyDerived' WHERE source='Manual.CashOverride';
                UPDATE schema_version SET version=4;
                INSERT INTO audit_log(occurred_at,action,details)
                    VALUES(strftime('%Y-%m-%dT%H:%M:%fZ','now'),'schema.migrate','3 -> 4: KKT identities, dated bindings, unassigned observations; all source rows preserved');
                """;
            c.ExecuteNonQuery();
            c.CommandText = "PRAGMA foreign_key_check";
            using (var reader = c.ExecuteReader())
                if (reader.Read()) throw new InvalidOperationException("Миграция отменена: нарушены связи базы данных.");
            CanonicalFiscalProjection.Create(db, tx);
            tx.Commit();
        }
        finally
        {
            using var on = db.CreateCommand(); on.CommandText = "PRAGMA foreign_keys=ON"; on.ExecuteNonQuery();
        }

    }
}

public static class MigrationBackupExtensions
{
    public static void BackupBeforeMigration(this Database database, long version)
    {
        var folder = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(database.Path)!, "Backups");
        Directory.CreateDirectory(folder);
        var target = System.IO.Path.Combine(folder, $"{System.IO.Path.GetFileNameWithoutExtension(database.Path)}-before-v4-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db");
        database.Backup(target);
    }
}
