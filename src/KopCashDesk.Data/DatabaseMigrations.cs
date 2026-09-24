using Microsoft.Data.Sqlite;

namespace KopCashDesk.Data;

internal static class DatabaseMigrations
{
    public const long CurrentVersion = 5;

    public static void Apply(Database database)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true
        }.ToString());
        db.Open();

        var version = ReadVersion(db);
        if (version > CurrentVersion)
            throw new InvalidOperationException("Версия базы данных новее этой программы. Обновите программу.");

        if (version < CurrentVersion)
            database.BackupBeforeMigration(version);

        if (version < 2)
            MigrateToV2(db);
        if (version < 3)
            MigrateToV3(db);
        if (version < 4)
            RegisterSchemaMigration.Apply(db);
        if (version < 5)
            MigrateToV5(db);

        // Refresh SQLite planner statistics after schema/index changes.
        using (var optimize = db.CreateCommand())
        {
            optimize.CommandText = "PRAGMA optimize;";
            optimize.ExecuteNonQuery();
        }

        // Data-only business rules are intentionally idempotent and remain outside the schema version.
        // This lets an already-v4 database receive corrected hard KKT bindings without rebuilding tables.
        KnownBusinessRules.ApplyPending(database);
        database.RepairSberOverlappingImports();
    }

    private static void MigrateToV2(SqliteConnection db)
    {
        using var tx = db.BeginTransaction();

        AddColumnIfMissing(db, tx, "locations", "is_active", "INTEGER NOT NULL DEFAULT 1");
        AddColumnIfMissing(db, tx, "locations", "merged_into_location_id", "TEXT REFERENCES locations(id)");

        AddColumnIfMissing(db, tx, "terminal_bindings", "binding_source", "TEXT NOT NULL DEFAULT 'Automatic'");
        AddColumnIfMissing(db, tx, "terminal_bindings", "is_locked", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(db, tx, "terminal_bindings", "valid_from", "TEXT");
        AddColumnIfMissing(db, tx, "terminal_bindings", "valid_to", "TEXT");

        AddColumnIfMissing(db, tx, "register_bindings", "binding_source", "TEXT NOT NULL DEFAULT 'Automatic'");
        AddColumnIfMissing(db, tx, "register_bindings", "is_locked", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(db, tx, "register_bindings", "valid_from", "TEXT");
        AddColumnIfMissing(db, tx, "register_bindings", "valid_to", "TEXT");

        Execute(db, tx, """
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
            """);

        NormalizeTaxIds(db, tx);
        PreserveKnownManualTerminalEdits(db, tx);

        Execute(db, tx, """
            CREATE TRIGGER IF NOT EXISTS trg_organizations_tax_id_unique_insert
            BEFORE INSERT ON organizations
            WHEN NEW.tax_id <> '' AND EXISTS(
                SELECT 1 FROM organizations o WHERE o.tax_id=NEW.tax_id AND o.id<>NEW.id
            )
            BEGIN
                SELECT RAISE(ABORT, 'Организация с таким ИНН уже существует');
            END;

            CREATE TRIGGER IF NOT EXISTS trg_organizations_tax_id_unique_update
            BEFORE UPDATE OF tax_id ON organizations
            WHEN NEW.tax_id <> '' AND EXISTS(
                SELECT 1 FROM organizations o WHERE o.tax_id=NEW.tax_id AND o.id<>NEW.id
            )
            BEGIN
                SELECT RAISE(ABORT, 'Организация с таким ИНН уже существует');
            END;
            """);

        using (var version = db.CreateCommand())
        {
            version.Transaction = tx;
            version.CommandText = "UPDATE schema_version SET version=2";
            version.ExecuteNonQuery();
        }

        using (var audit = db.CreateCommand())
        {
            audit.Transaction = tx;
            audit.CommandText = "INSERT INTO audit_log(occurred_at,action,details) VALUES($t,'schema.migrate','1 -> 2: FIFO reconciliation, binding provenance, soft merge')";
            audit.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
            audit.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private static void MigrateToV3(SqliteConnection db)
    {
        using var tx = db.BeginTransaction();
        Execute(db, tx, """
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
            """);

        using (var version = db.CreateCommand())
        {
            version.Transaction = tx;
            version.CommandText = "UPDATE schema_version SET version=3";
            version.ExecuteNonQuery();
        }

        using (var audit = db.CreateCommand())
        {
            audit.Transaction = tx;
            audit.CommandText = "INSERT INTO audit_log(occurred_at,action,details) VALUES($t,'schema.migrate','2 -> 3: cross-source fiscal shift links and conflicts')";
            audit.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
            audit.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private static void MigrateToV5(SqliteConnection db)
    {
        using var tx = db.BeginTransaction();

        // Large databases spend most of their time grouping ISO-8601 timestamps by day
        // and resolving source/external-id pairs. Expression/covering indexes avoid full scans
        // without changing the existing data model or deleting historical rows.
        Execute(db, tx, """
            CREATE INDEX IF NOT EXISTS ix_operations_day_summary
                ON operations(organization_id, location_id, substr(occurred_at,1,10), source_kind, payment, amount_kopecks);
            CREATE INDEX IF NOT EXISTS ix_operations_source_external_role
                ON operations(source, external_id, source_kind);
            CREATE INDEX IF NOT EXISTS ix_operations_location_time
                ON operations(organization_id, location_id, occurred_at);

            CREATE INDEX IF NOT EXISTS ix_shift_closures_day_summary
                ON shift_closures(organization_id, location_id, substr(closed_at,1,10), source);
            CREATE INDEX IF NOT EXISTS ix_shift_closures_source_external
                ON shift_closures(source, external_id);
            CREATE INDEX IF NOT EXISTS ix_shift_closures_register_match
                ON shift_closures(organization_id, location_id, source, fn, shift_number, closed_at);

            CREATE INDEX IF NOT EXISTS ix_source_documents_hash
                ON source_documents(sha256);
            """);

        using (var version = db.CreateCommand())
        {
            version.Transaction = tx;
            version.CommandText = "UPDATE schema_version SET version=5";
            version.ExecuteNonQuery();
        }

        using (var audit = db.CreateCommand())
        {
            audit.Transaction = tx;
            audit.CommandText = "INSERT INTO audit_log(occurred_at,action,details) VALUES($t,'schema.migrate','4 -> 5: large database indexes')";
            audit.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
            audit.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private static void NormalizeTaxIds(SqliteConnection db, SqliteTransaction tx)
    {
        var rows = new List<(string Id, string TaxId)>();
        using (var read = db.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT id,tax_id FROM organizations";
            using var reader = read.ExecuteReader();
            while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        foreach (var row in rows)
        {
            var normalized = DigitsOnly(row.TaxId);
            if (normalized == row.TaxId) continue;
            using var update = db.CreateCommand();
            update.Transaction = tx;
            update.CommandText = "UPDATE organizations SET tax_id=$tax WHERE id=$id";
            update.Parameters.AddWithValue("$tax", normalized);
            update.Parameters.AddWithValue("$id", row.Id);
            update.ExecuteNonQuery();
        }
    }

    private static void PreserveKnownManualTerminalEdits(SqliteConnection db, SqliteTransaction tx)
    {
        var editedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var read = db.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT details FROM audit_log WHERE action='terminal.save'";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                var details = reader.GetString(0);
                const string marker = "id=";
                var start = details.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (start < 0) continue;
                start += marker.Length;
                var end = details.IndexOf(';', start);
                var id = (end < 0 ? details[start..] : details[start..end]).Trim();
                if (Guid.TryParse(id, out _)) editedIds.Add(id);
            }
        }

        foreach (var id in editedIds)
        {
            using var update = db.CreateCommand();
            update.Transaction = tx;
            update.CommandText = "UPDATE terminal_bindings SET binding_source='Manual',is_locked=1 WHERE id=$id";
            update.Parameters.AddWithValue("$id", id);
            update.ExecuteNonQuery();
        }
    }

    private static void AddColumnIfMissing(SqliteConnection db, SqliteTransaction tx, string table, string column, string definition)
    {
        if (ColumnExists(db, tx, table, column)) return;
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        command.ExecuteNonQuery();
    }

    private static bool ColumnExists(SqliteConnection db, SqliteTransaction tx, string table, string column)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = $"PRAGMA table_info({table})";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static long ReadVersion(SqliteConnection db)
    {
        using var command = db.CreateCommand();
        command.CommandText = "SELECT version FROM schema_version LIMIT 1";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void Execute(SqliteConnection db, SqliteTransaction tx, string sql)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string DigitsOnly(string value) => new(value.Where(char.IsDigit).ToArray());
}
