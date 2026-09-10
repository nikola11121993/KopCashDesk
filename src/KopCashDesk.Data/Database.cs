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

    public void Save(ShiftClosure x)
    {
        using var db = Open();
        Execute(db,
            """
            INSERT INTO shift_closures(id,source,external_id,organization_id,location_id,closed_at,total_kopecks,cash_kopecks,electronic_kopecks,fn,shift_number,document_id)
            VALUES($id,$s,$e,$o,$l,$t,$total,$cash,$electronic,$fn,$shift,$d)
            ON CONFLICT(source,external_id) DO UPDATE SET
                organization_id=excluded.organization_id,
                location_id=excluded.location_id,
                closed_at=excluded.closed_at,
                total_kopecks=excluded.total_kopecks,
                cash_kopecks=excluded.cash_kopecks,
                electronic_kopecks=excluded.electronic_kopecks,
                fn=excluded.fn,
                shift_number=excluded.shift_number,
                document_id=excluded.document_id
            """,
            ("$id", Guid.NewGuid().ToString()), ("$s", x.Source), ("$e", x.ExternalId),
            ("$o", x.OrganizationId.ToString()), ("$l", x.LocationId.ToString()), ("$t", x.ClosedAt.ToString("O")),
            ("$total", Money.ToKopecks(x.Total)), ("$cash", Money.ToKopecks(x.Cash)), ("$electronic", Money.ToKopecks(x.Electronic)),
            ("$fn", x.FiscalDriveNumber.Trim()), ("$shift", x.ShiftNumber), ("$d", x.SourceDocumentId));
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
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            WITH op AS (
                SELECT
                    substr(occurred_at,1,10) AS day,
                    organization_id,
                    location_id,
                    SUM(CASE WHEN source_kind='Bank' AND payment='Electronic' THEN amount_kopecks ELSE 0 END) AS bank_sum,
                    SUM(CASE WHEN source_kind='Bank' AND payment='Electronic' THEN 1 ELSE 0 END) AS bank_count,
                    SUM(CASE WHEN source_kind='Fiscal' AND payment='Electronic' THEN amount_kopecks ELSE 0 END) AS fiscal_sum,
                    SUM(CASE WHEN source_kind='Fiscal' AND payment='Electronic' THEN 1 ELSE 0 END) AS fiscal_count
                FROM operations
                WHERE location_id IS NOT NULL
                GROUP BY substr(occurred_at,1,10), organization_id, location_id
            ),
            shifts AS (
                SELECT
                    substr(closed_at,1,10) AS day,
                    organization_id,
                    location_id,
                    SUM(total_kopecks) AS total_sum,
                    SUM(cash_kopecks) AS cash_sum,
                    SUM(electronic_kopecks) AS electronic_sum,
                    COUNT(*) AS shift_count,
                    MAX(closed_at) AS last_closed_at
                FROM shift_closures
                GROUP BY substr(closed_at,1,10), organization_id, location_id
            ),
            keys AS (
                SELECT day,organization_id,location_id FROM op WHERE bank_count>0 OR fiscal_count>0
                UNION
                SELECT day,organization_id,location_id FROM shifts
            )
            SELECT
                k.day,
                k.organization_id,
                org.name,
                k.location_id,
                loc.name,
                CASE WHEN COALESCE(op.bank_count,0)>0 THEN op.bank_sum ELSE NULL END,
                CASE WHEN COALESCE(op.fiscal_count,0)>0 THEN op.fiscal_sum ELSE NULL END,
                shifts.total_sum,
                shifts.cash_sum,
                shifts.electronic_sum,
                COALESCE(shifts.shift_count,0),
                shifts.last_closed_at
            FROM keys k
            JOIN organizations org ON org.id=k.organization_id
            JOIN locations loc ON loc.id=k.location_id
            LEFT JOIN op ON op.day=k.day AND op.organization_id=k.organization_id AND op.location_id=k.location_id
            LEFT JOIN shifts ON shifts.day=k.day AND shifts.organization_id=k.organization_id AND shifts.location_id=k.location_id
            WHERE ($org IS NULL OR k.organization_id=$org)
              AND ($loc IS NULL OR k.location_id=$loc)
              AND ($year IS NULL OR CAST(substr(k.day,1,4) AS INTEGER)=$year)
              AND ($month IS NULL OR CAST(substr(k.day,6,2) AS INTEGER)=$month)
            ORDER BY k.day DESC, org.name, loc.name
            """;
        cmd.Parameters.AddWithValue("$org", organizationId is null ? DBNull.Value : organizationId.Value.ToString());
        cmd.Parameters.AddWithValue("$loc", locationId is null ? DBNull.Value : locationId.Value.ToString());
        cmd.Parameters.AddWithValue("$year", year is null ? DBNull.Value : year.Value);
        cmd.Parameters.AddWithValue("$month", month is null ? DBNull.Value : month.Value);

        using var r = cmd.ExecuteReader();
        var result = new List<PointDaySummary>();
        while (r.Read())
        {
            result.Add(new(
                DateOnly.ParseExact(r.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                Guid.Parse(r.GetString(1)),
                r.GetString(2),
                Guid.Parse(r.GetString(3)),
                r.GetString(4),
                ReadMoney(r, 5),
                ReadMoney(r, 6),
                ReadMoney(r, 7),
                ReadMoney(r, 8),
                ReadMoney(r, 9),
                r.GetInt32(10),
                r.IsDBNull(11) ? null : DateTimeOffset.Parse(r.GetString(11), CultureInfo.InvariantCulture)));
        }
        return result;
    }

    private static decimal? ReadMoney(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Money.FromKopecks(reader.GetInt64(ordinal));

    public IReadOnlyList<ShiftClosure> ShiftClosures(Guid organizationId, Guid locationId, DateOnly date)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT source,external_id,organization_id,location_id,closed_at,total_kopecks,cash_kopecks,electronic_kopecks,fn,shift_number,document_id
            FROM shift_closures
            WHERE organization_id=$org AND location_id=$loc AND substr(closed_at,1,10)=$day
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
}
