using KopCashDesk.Core;
using Microsoft.Data.Sqlite;

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
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS schema_version(version INTEGER NOT NULL);
            INSERT INTO schema_version(version) SELECT 1 WHERE NOT EXISTS(SELECT 1 FROM schema_version);
            CREATE TABLE IF NOT EXISTS organizations(id TEXT PRIMARY KEY, name TEXT NOT NULL, tax_id TEXT NOT NULL DEFAULT '');
            CREATE TABLE IF NOT EXISTS locations(id TEXT PRIMARY KEY, organization_id TEXT NOT NULL REFERENCES organizations(id), name TEXT NOT NULL, address TEXT NOT NULL DEFAULT '', excluded INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS register_bindings(id TEXT PRIMARY KEY, organization_id TEXT NOT NULL REFERENCES organizations(id), location_id TEXT NOT NULL REFERENCES locations(id), fn TEXT NOT NULL, register_number TEXT NOT NULL DEFAULT '', UNIQUE(organization_id, fn));
            CREATE TABLE IF NOT EXISTS terminal_bindings(id TEXT PRIMARY KEY, organization_id TEXT NOT NULL REFERENCES organizations(id), location_id TEXT NOT NULL REFERENCES locations(id), provider TEXT NOT NULL, tid TEXT NOT NULL, mid TEXT NOT NULL DEFAULT '', payment_method TEXT NOT NULL DEFAULT 'POS', UNIQUE(organization_id, provider, tid));
            CREATE TABLE IF NOT EXISTS integrations(id TEXT PRIMARY KEY, organization_id TEXT NOT NULL REFERENCES organizations(id), kind TEXT NOT NULL, name TEXT NOT NULL, settings TEXT NOT NULL, enabled INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS source_documents(id TEXT PRIMARY KEY, source TEXT NOT NULL, external_id TEXT NOT NULL, sha256 TEXT NOT NULL, imported_at TEXT NOT NULL, original_name TEXT NOT NULL, UNIQUE(source, external_id));
            CREATE TABLE IF NOT EXISTS operations(id TEXT PRIMARY KEY, source TEXT NOT NULL, external_id TEXT NOT NULL, organization_id TEXT NOT NULL REFERENCES organizations(id), location_id TEXT REFERENCES locations(id), occurred_at TEXT NOT NULL, source_kind TEXT NOT NULL, kind TEXT NOT NULL, payment TEXT NOT NULL, amount_kopecks INTEGER NOT NULL, document_id TEXT REFERENCES source_documents(id), UNIQUE(source, external_id));
            CREATE TABLE IF NOT EXISTS audit_log(id INTEGER PRIMARY KEY AUTOINCREMENT, occurred_at TEXT NOT NULL, action TEXT NOT NULL, details TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
        if (GetVersion(db) != 1)
            throw new InvalidOperationException("Версия базы данных новее этой программы. Обновите программу.");
    }

    private static long GetVersion(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT version FROM schema_version LIMIT 1";
        return (long)cmd.ExecuteScalar()!;
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

    public IReadOnlyList<Location> Locations()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id,organization_id,name,address,excluded FROM locations ORDER BY name";
        using var r = cmd.ExecuteReader();
        var result = new List<Location>();
        while (r.Read()) result.Add(new(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), r.GetString(2), r.GetString(3), r.GetInt64(4) != 0));
        return result;
    }

    public IReadOnlyList<TerminalBinding> TerminalBindings()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id,organization_id,location_id,provider,tid,mid,payment_method FROM terminal_bindings ORDER BY provider,tid";
        using var r = cmd.ExecuteReader();
        var result = new List<TerminalBinding>();
        while (r.Read())
            result.Add(new(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), Guid.Parse(r.GetString(2)), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6)));
        return result;
    }

    public void Save(Organization x)
    {
        using var db = Open();
        Execute(db,
            "INSERT INTO organizations(id,name,tax_id) VALUES($id,$n,$t) ON CONFLICT(id) DO UPDATE SET name=excluded.name,tax_id=excluded.tax_id",
            ("$id", x.Id.ToString()), ("$n", x.Name.Trim()), ("$t", x.TaxId.Trim()));
    }

    public void Save(Location x)
    {
        using var db = Open();
        Execute(db,
            "INSERT INTO locations(id,organization_id,name,address,excluded) VALUES($id,$o,$n,$a,$e) ON CONFLICT(id) DO UPDATE SET name=excluded.name,address=excluded.address,excluded=excluded.excluded",
            ("$id", x.Id.ToString()), ("$o", x.OrganizationId.ToString()), ("$n", x.Name.Trim()), ("$a", x.Address.Trim()), ("$e", x.IsExcluded ? 1 : 0));
    }

    public void Save(TerminalBinding x)
    {
        using var db = Open();
        Execute(db,
            """
            INSERT INTO terminal_bindings(id,organization_id,location_id,provider,tid,mid,payment_method)
            VALUES($id,$o,$l,$p,$tid,$mid,$pm)
            ON CONFLICT(organization_id,provider,tid) DO UPDATE SET
                location_id=excluded.location_id,
                mid=excluded.mid,
                payment_method=excluded.payment_method
            """,
            ("$id", x.Id.ToString()), ("$o", x.OrganizationId.ToString()), ("$l", x.LocationId.ToString()),
            ("$p", x.Provider.Trim()), ("$tid", x.TerminalId.Trim()), ("$mid", x.MerchantId.Trim()), ("$pm", x.PaymentMethod.Trim()));
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
}
