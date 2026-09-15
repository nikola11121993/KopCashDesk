using KopCashDesk.Data;
using Microsoft.Data.Sqlite;
using Xunit;
namespace KopCashDesk.Tests;

public sealed class RegisterMigrationTests
{
    [Fact]
    public void V3Upgrade_BacksUpBeforeChange_PreservesRawRowsAndLinks_AndIsIdempotent()
    {
        var root = Path.Combine(Path.GetTempPath(), "kkt-migration-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "cash.db");
        try
        {
            using (var db = new SqliteConnection("Data Source=" + path))
            {
                db.Open(); using var c = db.CreateCommand(); c.CommandText = LegacySchema; c.ExecuteNonQuery();
                c.CommandText = """
                    INSERT INTO organizations VALUES('00000000-0000-0000-0000-000000000001','Test','123');
                    INSERT INTO locations(id,organization_id,name) VALUES('00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-000000000001','Point');
                    INSERT INTO register_bindings(id,organization_id,location_id,fn,binding_source,is_locked) VALUES('00000000-0000-0000-0000-000000000003','00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000002','991','Manual',1);
                    INSERT INTO source_documents VALUES('doc','Taxcom.ShiftReport','file','hash','2026-08-31','report.xlsx');
                    INSERT INTO shift_closures VALUES('s1','Taxcom.ShiftReport','s1','00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000002','2026-08-31T15:00:00+05:00',355700,0,355700,'991',415,'doc');
                    INSERT INTO shift_closures VALUES('s2','Frontol.Report','s2','00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000002','2026-08-31T15:01:00+05:00',355700,0,355700,'',415,'doc');
                    INSERT INTO shift_source_links VALUES('link','00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000002','2026-08-31','s1','s2','AutomaticExactMoneyWithinTolerance',1,60,'2026-08-31');
                    INSERT INTO operations VALUES('op','Taxcom.ShiftReport','s1:electronic','00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000002','2026-08-31T15:00:00+05:00','Fiscal','Sale','Electronic',355700,'doc');
                    """; c.ExecuteNonQuery();
            }
            var database = new Database(path); database.Initialize(); database.Initialize();
            Assert.True(Assert.Single(database.RegisterBindings()).IsLocked);
            Assert.Equal(1, database.CountOperations());
            using (var db = new SqliteConnection("Data Source=" + path))
            {
                db.Open(); using var c = db.CreateCommand(); c.CommandText = "PRAGMA foreign_key_check"; using (var r = c.ExecuteReader()) Assert.False(r.Read());
                c.CommandText = "SELECT COUNT(*) FROM shift_closures"; Assert.Equal(2L, c.ExecuteScalar());
                c.CommandText = "SELECT SUM(electronic_kopecks) FROM shift_closures"; Assert.Equal(711400L, c.ExecuteScalar());
                c.CommandText = "SELECT fn FROM operations WHERE id='op'"; Assert.Equal("991", c.ExecuteScalar());
                c.CommandText = "SELECT COUNT(*) FROM shift_source_links"; Assert.Equal(1L, c.ExecuteScalar());
            }
            var backup = Assert.Single(Directory.GetFiles(Path.Combine(root, "Backups"), "*.db"));
            using (var db = new SqliteConnection("Data Source=" + backup))
            {
                db.Open(); using var c = db.CreateCommand(); c.CommandText = "SELECT version FROM schema_version"; Assert.Equal(3L, c.ExecuteScalar());
                c.CommandText = "SELECT COUNT(*) FROM shift_closures"; Assert.Equal(2L, c.ExecuteScalar());
            }
        }
        finally { SqliteConnection.ClearAllPools(); try { Directory.Delete(root, true); } catch { } }
    }
    private const string LegacySchema = """

        PRAGMA journal_mode=WAL;
        PRAGMA foreign_keys=ON;
        CREATE TABLE IF NOT EXISTS schema_version(version INTEGER NOT NULL);
        INSERT INTO schema_version(version) SELECT 1 WHERE NOT EXISTS(SELECT 1 FROM schema_version);
        CREATE TABLE IF NOT EXISTS organizations(id TEXT PRIMARY KEY, name TEXT NOT NULL, tax_id TEXT NOT NULL DEFAULT '');
        CREATE TABLE IF NOT EXISTS locations(id TEXT PRIMARY KEY, organization_id TEXT NOT NULL REFERENCES organizations(id), name TEXT NOT NULL, address TEXT NOT NULL DEFAULT '', excluded INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS register_bindings(id TEXT PRIMARY KEY, organization_id TEXT NOT NULL REFERENCES organizations(id), location_id TEXT NOT NULL REFERENCES locations(id), fn TEXT NOT NULL, register_number TEXT NOT NULL DEFAULT '', UNIQUE(organization_id, fn));
        CREATE TABLE IF NOT EXISTS terminal_bindings(id TEXT PRIMARY KEY, organization_id TEXT NOT NULL REFERENCES organizations(id), location_id TEXT NOT NULL REFERENCES locations(id), provider TEXT NOT NULL, tid TEXT NOT NULL, mid TEXT NOT NULL DEFAULT '', payment_method TEXT NOT NULL DEFAULT 'POS', UNIQUE(organization_id, provider,tid));
        CREATE TABLE IF NOT EXISTS integrations(id TEXT PRIMARY KEY, organization_id TEXT NOT NULL REFERENCES organizations(id), kind TEXT NOT NULL, name TEXT NOT NULL, settings TEXT NOT NULL, enabled INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS source_documents(id TEXT PRIMARY KEY, source TEXT NOT NULL, external_id TEXT NOT NULL, sha256 TEXT NOT NULL, imported_at TEXT NOT NULL, original_name TEXT NOT NULL, UNIQUE(source, external_id));
        CREATE TABLE IF NOT EXISTS operations(id TEXT PRIMARY KEY, source TEXT NOT NULL, external_id TEXT NOT NULL, organization_id TEXT NOT NULL REFERENCES organizations(id), location_id TEXT REFERENCES locations(id), occurred_at TEXT NOT NULL, source_kind TEXT NOT NULL, kind TEXT NOT NULL, payment TEXT NOT NULL, amount_kopecks INTEGER NOT NULL, document_id TEXT REFERENCES source_documents(id), UNIQUE(source, external_id));
        CREATE TABLE IF NOT EXISTS shift_closures(id TEXT PRIMARY KEY, source TEXT NOT NULL, external_id TEXT NOT NULL, organization_id TEXT NOT NULL REFERENCES organizations(id), location_id TEXT NOT NULL REFERENCES locations(id), closed_at TEXT NOT NULL, total_kopecks INTEGER NOT NULL, cash_kopecks INTEGER NOT NULL, electronic_kopecks INTEGER NOT NULL, fn TEXT NOT NULL DEFAULT '', shift_number INTEGER, document_id TEXT REFERENCES source_documents(id), UNIQUE(source, external_id));
        CREATE INDEX IF NOT EXISTS ix_operations_summary ON operations(organization_id, location_id, occurred_at, source_kind, payment);
        CREATE INDEX IF NOT EXISTS ix_shift_closures_summary ON shift_closures(organization_id, location_id, closed_at);
        CREATE TABLE IF NOT EXISTS audit_log(id INTEGER PRIMARY KEY AUTOINCREMENT, occurred_at TEXT NOT NULL, action TEXT NOT NULL, details TEXT NOT NULL);
        ALTER TABLE locations ADD COLUMN is_active INTEGER NOT NULL DEFAULT 1;
        ALTER TABLE locations ADD COLUMN merged_into_location_id TEXT REFERENCES locations(id);
        ALTER TABLE register_bindings ADD COLUMN binding_source TEXT NOT NULL DEFAULT 'Automatic';
        ALTER TABLE register_bindings ADD COLUMN is_locked INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE register_bindings ADD COLUMN valid_from TEXT;
        ALTER TABLE register_bindings ADD COLUMN valid_to TEXT;
        ALTER TABLE terminal_bindings ADD COLUMN binding_source TEXT NOT NULL DEFAULT 'Automatic';
        ALTER TABLE terminal_bindings ADD COLUMN is_locked INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE terminal_bindings ADD COLUMN valid_from TEXT;
        ALTER TABLE terminal_bindings ADD COLUMN valid_to TEXT;

        CREATE TABLE IF NOT EXISTS manual_cash_postings(
        organization_id TEXT NOT NULL REFERENCES organizations(id),
        location_id TEXT NOT NULL REFERENCES locations(id),
        business_date TEXT NOT NULL,
        electronic_kopecks INTEGER NOT NULL,
        created_at TEXT NOT NULL,
        updated_at TEXT NOT NULL,
        PRIMARY KEY(organization_id, location_id, business_date)
        );
        CREATE INDEX IF NOT EXISTS ix_manual_cash_postings_period
        ON manual_cash_postings(organization_id, location_id, business_date);

        CREATE TABLE IF NOT EXISTS reconciliation_allocations(
        id TEXT PRIMARY KEY,
        organization_id TEXT NOT NULL REFERENCES organizations(id),
        location_id TEXT NOT NULL REFERENCES locations(id),
        terminal_date TEXT NOT NULL,
        settlement_kind TEXT NOT NULL,
        settlement_source TEXT NOT NULL,
        settlement_external_id TEXT NOT NULL,
        settlement_date TEXT NOT NULL,
        allocated_amount_kopecks INTEGER NOT NULL CHECK(allocated_amount_kopecks >= 0),
        algorithm_version INTEGER NOT NULL,
        created_at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_reconciliation_terminal
        ON reconciliation_allocations(organization_id, location_id, terminal_date);
        CREATE INDEX IF NOT EXISTS ix_reconciliation_settlement
        ON reconciliation_allocations(organization_id, location_id, settlement_date);

        CREATE TABLE IF NOT EXISTS applied_business_rules(
        rule_key TEXT PRIMARY KEY,
        applied_at TEXT NOT NULL,
        details TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS shift_source_links(
        id TEXT PRIMARY KEY,
        organization_id TEXT NOT NULL REFERENCES organizations(id),
        location_id TEXT NOT NULL REFERENCES locations(id),
        business_date TEXT NOT NULL,
        canonical_shift_id TEXT NOT NULL REFERENCES shift_closures(id),
        observed_shift_id TEXT NOT NULL REFERENCES shift_closures(id),
        match_kind TEXT NOT NULL,
        match_confidence REAL NOT NULL,
        time_difference_seconds INTEGER NOT NULL,
        created_at TEXT NOT NULL,
        UNIQUE(canonical_shift_id, observed_shift_id)
        );
        CREATE INDEX IF NOT EXISTS ix_shift_source_links_observed
        ON shift_source_links(observed_shift_id);
        CREATE INDEX IF NOT EXISTS ix_shift_source_links_period
        ON shift_source_links(organization_id, location_id, business_date);

        CREATE TABLE IF NOT EXISTS fiscal_source_conflicts(
        id TEXT PRIMARY KEY,
        organization_id TEXT NOT NULL REFERENCES organizations(id),
        location_id TEXT NOT NULL REFERENCES locations(id),
        business_date TEXT NOT NULL,
        taxcom_shift_id TEXT NOT NULL REFERENCES shift_closures(id),
        frontol_shift_id TEXT NOT NULL REFERENCES shift_closures(id),
        reason TEXT NOT NULL,
        time_difference_seconds INTEGER NOT NULL,
        created_at TEXT NOT NULL,
        UNIQUE(taxcom_shift_id, frontol_shift_id)
        );
        CREATE INDEX IF NOT EXISTS ix_fiscal_source_conflicts_period
        ON fiscal_source_conflicts(organization_id, location_id, business_date);

        UPDATE schema_version SET version=3;
        """;
}
