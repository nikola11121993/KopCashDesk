using KopCashDesk.Core;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace KopCashDesk.Data;

public sealed class Database
{
    private readonly string _path;
    public string Path => _path;
    public Database(string path) => _path = path;

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true
        }.ToString());
        connection.Open();
        return connection;
    }

    public void Initialize()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        using (var db = Open())
        using (var command = db.CreateCommand())
        {
            command.CommandText = """
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
                """;
            command.ExecuteNonQuery();
        }

        DatabaseMigrations.Apply(this);

        // Refresh derived fiscal view on every start so business-rule changes
        // (for example Taxcom authoritative / Frontol verification-only) apply
        // to existing v4 databases without a destructive migration.
        using (var db = Open())
            CanonicalFiscalProjection.Create(db);
    }

    private static void Execute(SqliteConnection db, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<Organization> Organizations()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id,name,tax_id FROM organizations ORDER BY name";
        using var r = cmd.ExecuteReader();
        var result = new List<Organization>();
        while (r.Read()) result.Add(new(Guid.Parse(r.GetString(0)), r.GetString(1), r.GetString(2)));
        return result;
    }

    public IReadOnlyList<Location> Locations(bool includeInactive = false)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id,organization_id,name,address,excluded,is_active,merged_into_location_id
            FROM locations
            WHERE $all=1 OR is_active=1
            ORDER BY name
            """;
        cmd.Parameters.AddWithValue("$all", includeInactive ? 1 : 0);
        using var r = cmd.ExecuteReader();
        var result = new List<Location>();
        while (r.Read())
        {
            result.Add(new(
                Guid.Parse(r.GetString(0)),
                Guid.Parse(r.GetString(1)),
                r.GetString(2),
                r.GetString(3),
                r.GetInt64(4) != 0,
                r.GetInt64(5) != 0,
                r.IsDBNull(6) ? null : Guid.Parse(r.GetString(6))));
        }
        return result;
    }

    public IReadOnlyList<TerminalBinding> TerminalBindings()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id,organization_id,location_id,provider,tid,mid,payment_method,binding_source,is_locked,valid_from,valid_to
            FROM terminal_bindings
            ORDER BY provider,tid
            """;
        using var r = cmd.ExecuteReader();
        var result = new List<TerminalBinding>();
        while (r.Read())
        {
            result.Add(new(
                Guid.Parse(r.GetString(0)),
                Guid.Parse(r.GetString(1)),
                Guid.Parse(r.GetString(2)),
                r.GetString(3),
                r.GetString(4),
                r.GetString(5),
                r.GetString(6),
                ParseBindingSource(r.GetString(7)),
                r.GetInt64(8) != 0,
                r.IsDBNull(9) ? null : DateOnly.ParseExact(r.GetString(9), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                r.IsDBNull(10) ? null : DateOnly.ParseExact(r.GetString(10), "yyyy-MM-dd", CultureInfo.InvariantCulture)));
        }
        return result;
    }

    public void Save(Organization x)
    {
        var taxId = DigitsOnly(x.TaxId);
        using var db = Open();
        Execute(db,
            "INSERT INTO organizations(id,name,tax_id) VALUES($id,$n,$t) ON CONFLICT(id) DO UPDATE SET name=excluded.name,tax_id=excluded.tax_id",
            ("$id", x.Id.ToString()), ("$n", x.Name.Trim()), ("$t", taxId));
    }

    public void Save(Location x)
    {
        using var db = Open();
        Execute(db,
            """
            INSERT INTO locations(id,organization_id,name,address,excluded,is_active,merged_into_location_id)
            VALUES($id,$o,$n,$a,$e,$active,$merged)
            ON CONFLICT(id) DO UPDATE SET
                organization_id=excluded.organization_id,
                name=excluded.name,
                address=excluded.address,
                excluded=excluded.excluded,
                is_active=excluded.is_active,
                merged_into_location_id=excluded.merged_into_location_id
            """,
            ("$id", x.Id.ToString()), ("$o", x.OrganizationId.ToString()), ("$n", x.Name.Trim()),
            ("$a", x.Address.Trim()), ("$e", x.IsExcluded ? 1 : 0), ("$active", x.IsActive ? 1 : 0),
            ("$merged", x.MergedIntoLocationId?.ToString()));
    }

    public void Save(TerminalBinding x)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();

        TerminalBinding? existing = null;
        using (var read = db.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT id,organization_id,location_id,provider,tid,mid,payment_method,binding_source,is_locked,valid_from,valid_to
                FROM terminal_bindings
                WHERE organization_id=$org AND provider=$provider AND tid=$tid
                LIMIT 1
                """;
            read.Parameters.AddWithValue("$org", x.OrganizationId.ToString());
            read.Parameters.AddWithValue("$provider", x.Provider.Trim());
            read.Parameters.AddWithValue("$tid", x.TerminalId.Trim());
            using var reader = read.ExecuteReader();
            if (reader.Read()) existing = ReadTerminalBinding(reader);
        }

        if (existing is not null &&
            (existing.IsLocked || BindingRank(existing.BindingSource) > BindingRank(x.BindingSource)))
        {
            transaction.Rollback();
            return;
        }

        using (var command = db.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO terminal_bindings(id,organization_id,location_id,provider,tid,mid,payment_method,binding_source,is_locked,valid_from,valid_to)
                VALUES($id,$org,$loc,$provider,$tid,$mid,$method,$source,$locked,$from,$to)
                ON CONFLICT(organization_id,provider,tid) DO UPDATE SET
                    location_id=excluded.location_id,
                    mid=excluded.mid,
                    payment_method=excluded.payment_method,
                    binding_source=excluded.binding_source,
                    is_locked=excluded.is_locked,
                    valid_from=excluded.valid_from,
                    valid_to=excluded.valid_to
                """;
            command.Parameters.AddWithValue("$id", existing?.Id.ToString() ?? x.Id.ToString());
            command.Parameters.AddWithValue("$org", x.OrganizationId.ToString());
            command.Parameters.AddWithValue("$loc", x.LocationId.ToString());
            command.Parameters.AddWithValue("$provider", x.Provider.Trim());
            command.Parameters.AddWithValue("$tid", x.TerminalId.Trim());
            command.Parameters.AddWithValue("$mid", x.MerchantId.Trim());
            command.Parameters.AddWithValue("$method", string.IsNullOrWhiteSpace(x.PaymentMethod) ? "POS" : x.PaymentMethod.Trim());
            command.Parameters.AddWithValue("$source", x.BindingSource.ToString());
            command.Parameters.AddWithValue("$locked", x.IsLocked ? 1 : 0);
            command.Parameters.AddWithValue("$from", x.ValidFrom is null ? DBNull.Value : x.ValidFrom.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$to", x.ValidTo is null ? DBNull.Value : x.ValidTo.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void Save(ShiftClosure x)
    {
        using var db = Open();
        Execute(db,
            """
            INSERT INTO shift_closures(id,source,external_id,organization_id,location_id,closed_at,total_kopecks,cash_kopecks,electronic_kopecks,fn,shift_number,document_id,kkt_serial,registration_number,register_display_name)
            VALUES($id,$s,$e,$o,$l,$t,$total,$cash,$electronic,$fn,$shift,$d,$serial,$rnm,$name)
            ON CONFLICT(source,external_id) DO UPDATE SET
                organization_id=excluded.organization_id,
                location_id=excluded.location_id,
                closed_at=excluded.closed_at,
                total_kopecks=excluded.total_kopecks,
                cash_kopecks=excluded.cash_kopecks,
                electronic_kopecks=excluded.electronic_kopecks,
                fn=excluded.fn,
                shift_number=excluded.shift_number,
                document_id=excluded.document_id,
                kkt_serial=excluded.kkt_serial,registration_number=excluded.registration_number,register_display_name=excluded.register_display_name
            """,
            ("$id", Guid.NewGuid().ToString()), ("$s", x.Source), ("$e", x.ExternalId),
            ("$o", x.OrganizationId.ToString()), ("$l", x.LocationId?.ToString()), ("$t", x.ClosedAt.ToString("O")),
            ("$total", Money.ToKopecks(x.Total)), ("$cash", Money.ToKopecks(x.Cash)), ("$electronic", Money.ToKopecks(x.Electronic)),
            ("$fn", x.FiscalDriveNumber.Trim()), ("$shift", x.ShiftNumber), ("$d", x.SourceDocumentId),
            ("$serial", x.KktSerial), ("$rnm", x.RegistrationNumber), ("$name", x.RegisterDisplayName));
    }

    public IReadOnlyList<IntegrationProfile> Integrations()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id,organization_id,kind,name,settings,enabled FROM integrations ORDER BY name";
        using var r = cmd.ExecuteReader();
        var result = new List<IntegrationProfile>();
        while (r.Read()) result.Add(new(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), Enum.Parse<IntegrationKind>(r.GetString(2)), r.GetString(3), r.GetString(4), r.GetInt64(5) != 0));
        return result;
    }

    public void Save(IntegrationProfile x)
    {
        using var db = Open();
        Execute(db,
            "INSERT INTO integrations(id,organization_id,kind,name,settings,enabled) VALUES($id,$o,$k,$n,$s,$e) ON CONFLICT(id) DO UPDATE SET kind=excluded.kind,name=excluded.name,settings=excluded.settings,enabled=excluded.enabled",
            ("$id", x.Id.ToString()), ("$o", x.OrganizationId.ToString()), ("$k", x.Kind.ToString()), ("$n", x.Name), ("$s", x.SettingsJson), ("$e", x.Enabled ? 1 : 0));
    }

    public string RegisterSourceDocument(string source, string externalId, string sha256, string originalName)
    {
        using var db = Open();
        var newId = Guid.NewGuid().ToString();
        Execute(db,
            "INSERT OR IGNORE INTO source_documents(id,source,external_id,sha256,imported_at,original_name) VALUES($id,$s,$e,$h,$t,$n)",
            ("$id", newId), ("$s", source), ("$e", externalId), ("$h", sha256), ("$t", DateTimeOffset.UtcNow.ToString("O")), ("$n", originalName));
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id FROM source_documents WHERE source=$s AND external_id=$e LIMIT 1";
        cmd.Parameters.AddWithValue("$s", source);
        cmd.Parameters.AddWithValue("$e", externalId);
        return (string)cmd.ExecuteScalar()!;
    }

    public int CountOperations()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM operations";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public decimal SumOperations(string source, Guid? organizationId = null)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(amount_kopecks),0) FROM operations WHERE source=$source AND ($org IS NULL OR organization_id=$org)";
        cmd.Parameters.AddWithValue("$source", source);
        cmd.Parameters.AddWithValue("$org", organizationId is null ? DBNull.Value : organizationId.Value.ToString());
        return Money.FromKopecks(Convert.ToInt64(cmd.ExecuteScalar()));
    }

    public bool Insert(CashOperation x)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO operations(id,source,external_id,organization_id,location_id,occurred_at,source_kind,kind,payment,amount_kopecks,document_id) VALUES($id,$s,$e,$o,$l,$t,$sk,$k,$p,$a,$d)";
        cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        cmd.Parameters.AddWithValue("$s", x.Source);
        cmd.Parameters.AddWithValue("$e", x.ExternalId);
        cmd.Parameters.AddWithValue("$o", x.OrganizationId.ToString());
        cmd.Parameters.AddWithValue("$l", (object?)x.LocationId?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", x.OccurredAt.ToString("O"));
        cmd.Parameters.AddWithValue("$sk", x.SourceKind.ToString());
        cmd.Parameters.AddWithValue("$k", x.Kind.ToString());
        cmd.Parameters.AddWithValue("$p", x.Payment.ToString());
        cmd.Parameters.AddWithValue("$a", Money.ToKopecks(x.Amount));
        cmd.Parameters.AddWithValue("$d", (object?)x.SourceDocumentId ?? DBNull.Value);
        return cmd.ExecuteNonQuery() == 1;
    }

    public IReadOnlyList<PointDaySummary> PointDaySummaries(Guid? organizationId = null, int? year = null, int? month = null, Guid? locationId = null)
        => this.CanonicalPointDaySummaries(organizationId, year, month, locationId);

    private static decimal? ReadMoney(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Money.FromKopecks(reader.GetInt64(ordinal));

    public IReadOnlyList<ShiftClosure> ShiftClosures(Guid organizationId, Guid locationId, DateOnly date)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT source,external_id,organization_id,location_id,closed_at,total_kopecks,cash_kopecks,electronic_kopecks,fn,shift_number,document_id
            FROM shift_closures s
            WHERE organization_id=$org AND location_id=$loc AND substr(closed_at,1,10)=$day
              AND NOT EXISTS(SELECT 1 FROM shift_source_links l JOIN shift_closures canonical ON canonical.id=l.canonical_shift_id
                             WHERE l.observed_shift_id=s.id AND canonical.source=s.source)
            ORDER BY closed_at
            """;
        cmd.Parameters.AddWithValue("$org", organizationId.ToString());
        cmd.Parameters.AddWithValue("$loc", locationId.ToString());
        cmd.Parameters.AddWithValue("$day", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        using var r = cmd.ExecuteReader();
        var result = new List<ShiftClosure>();
        while (r.Read())
        {
            result.Add(new(
                r.GetString(0), r.GetString(1), Guid.Parse(r.GetString(2)), Guid.Parse(r.GetString(3)),
                DateTimeOffset.Parse(r.GetString(4), CultureInfo.InvariantCulture),
                Money.FromKopecks(r.GetInt64(5)), Money.FromKopecks(r.GetInt64(6)), Money.FromKopecks(r.GetInt64(7)),
                r.GetString(8), r.IsDBNull(9) ? null : r.GetInt32(9), r.IsDBNull(10) ? null : r.GetString(10)));
        }
        return result;
    }

    public IReadOnlyList<OperationView> Operations(Guid? organizationId = null, int limit = 5000)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT o.occurred_at, org.name, COALESCE(l.name,''), o.source, o.kind, o.payment, o.amount_kopecks
            FROM operations o
            JOIN organizations org ON org.id=o.organization_id
            LEFT JOIN locations l ON l.id=o.location_id
            WHERE ($org IS NULL OR o.organization_id=$org)
            ORDER BY o.occurred_at DESC
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$org", organizationId is null ? DBNull.Value : organizationId.Value.ToString());
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 50000));
        using var r = cmd.ExecuteReader();
        var result = new List<OperationView>();
        while (r.Read())
        {
            result.Add(new(
                DateTimeOffset.Parse(r.GetString(0)),
                r.GetString(1),
                r.GetString(2),
                r.GetString(3),
                Enum.Parse<OperationKind>(r.GetString(4)),
                Enum.Parse<PaymentKind>(r.GetString(5)),
                Money.FromKopecks(r.GetInt64(6))));
        }
        return result;
    }

    public void Audit(string action, string details)
    {
        using var db = Open();
        Execute(db, "INSERT INTO audit_log(occurred_at,action,details) VALUES($t,$a,$d)", ("$t", DateTimeOffset.UtcNow.ToString("O")), ("$a", action), ("$d", details));
    }

    public IReadOnlyList<(string Date, string Action, string Details)> AuditEntries()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT occurred_at,action,details FROM audit_log ORDER BY id DESC LIMIT 500";
        using var r = cmd.ExecuteReader();
        var result = new List<(string, string, string)>();
        while (r.Read()) result.Add((r.GetString(0), r.GetString(1), r.GetString(2)));
        return result;
    }

    public void Backup(string destination)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
        using var source = Open();
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        target.Open();
        source.BackupDatabase(target);
    }

    private static TerminalBinding ReadTerminalBinding(SqliteDataReader r) => new(
        Guid.Parse(r.GetString(0)),
        Guid.Parse(r.GetString(1)),
        Guid.Parse(r.GetString(2)),
        r.GetString(3),
        r.GetString(4),
        r.GetString(5),
        r.GetString(6),
        ParseBindingSource(r.GetString(7)),
        r.GetInt64(8) != 0,
        r.IsDBNull(9) ? null : DateOnly.ParseExact(r.GetString(9), "yyyy-MM-dd", CultureInfo.InvariantCulture),
        r.IsDBNull(10) ? null : DateOnly.ParseExact(r.GetString(10), "yyyy-MM-dd", CultureInfo.InvariantCulture));

    private static BindingSource ParseBindingSource(string value) =>
        Enum.TryParse<BindingSource>(value, true, out var source) ? source : BindingSource.Automatic;

    private static int BindingRank(BindingSource source) => source switch
    {
        BindingSource.Manual => 3,
        BindingSource.Rule => 2,
        _ => 1
    };

    private static string DigitsOnly(string value) => new(value.Where(char.IsDigit).ToArray());
}
