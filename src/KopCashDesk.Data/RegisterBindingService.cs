using KopCashDesk.Core;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;

namespace KopCashDesk.Data;

public static class RegisterBindingService
{
    private const string Columns = "id,organization_id,location_id,fn,register_number,binding_source,is_locked,valid_from,valid_to,kkt_serial,display_name,created_at,updated_at,is_active";
    internal static SqliteConnection Open(Database database)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database.Path, ForeignKeys = true }.ToString());
        db.Open(); return db;
    }
    public static IReadOnlyList<RegisterBinding> Read(Database database, bool includeHistory = false)
    {
        using var db = Open(database);
        return Read(db, null, includeHistory);
    }
    private static List<RegisterBinding> Read(SqliteConnection db, SqliteTransaction? tx, bool all = false)
    {
        using var c = db.CreateCommand(); c.Transaction = tx;
        c.CommandText = $"SELECT {Columns} FROM register_bindings WHERE $all=1 OR is_active=1 ORDER BY display_name,fn,valid_from";
        c.Parameters.AddWithValue("$all", all ? 1 : 0);
        using var r = c.ExecuteReader(); var list = new List<RegisterBinding>();
        while (r.Read()) list.Add(new(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), r.IsDBNull(2) ? null : Guid.Parse(r.GetString(2)),
            r.GetString(3), r.GetString(4), Enum.Parse<BindingSource>(r.GetString(5)), r.GetInt64(6) != 0,
            Date(r, 7), Date(r, 8), r.GetString(9), r.GetString(10), DateTimeOffset.Parse(r.GetString(11)), DateTimeOffset.Parse(r.GetString(12)), r.GetInt64(13) != 0));
        return list;
    }
    private static DateOnly? Date(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : DateOnly.Parse(r.GetString(i), CultureInfo.InvariantCulture);
    private static string Digits(string s) => new(s.Where(char.IsDigit).ToArray());
    private static string Normalize(string s) => new(s.ToLowerInvariant().Replace('ё', 'е').Where(char.IsLetterOrDigit).ToArray());
    private static bool EqualId(string a, string b) => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && Digits(a) == Digits(b);
    private static int IdentityRank(RegisterBinding b, string serial, string fn, string rnm)
    {
        // Never join two known different physical registers merely because a weaker identifier matches.
        if (b.KktSerial.Length > 0 && serial.Length > 0 && !EqualId(b.KktSerial, serial)) return 0;
        if (EqualId(b.KktSerial, serial)) return 3;
        if (EqualId(b.FiscalDriveNumber, fn)) return 2;
        return EqualId(b.RegisterNumber, rnm) ? 1 : 0;
    }
    private static bool InPeriod(RegisterBinding b, DateOnly d) => (b.ValidFrom is null || b.ValidFrom <= d) && (b.ValidTo is null || b.ValidTo >= d);
    private static bool Overlaps(RegisterBinding a, RegisterBinding b) => (a.ValidFrom ?? DateOnly.MinValue) <= (b.ValidTo ?? DateOnly.MaxValue) && (b.ValidFrom ?? DateOnly.MinValue) <= (a.ValidTo ?? DateOnly.MaxValue);

    public static RegisterBinding Resolve(Database database, Guid org, string serial, string fn, string rnm, string display, string point, DateOnly day)
    {
        serial = Digits(serial); fn = Digits(fn); rnm = Digits(rnm);
        var candidates = Read(database).Where(b => b.OrganizationId == org && IdentityRank(b, serial, fn, rnm) > 0).ToArray();
        var valid = candidates.Where(b => InPeriod(b, day)).ToArray();
        RegisterBinding? selected = null;
        if (valid.Length > 0)
        {
            var locked = valid.Where(b => b.BindingSource == BindingSource.Manual && b.IsLocked).ToArray();
            var pool = locked.Length > 0 ? locked : valid;
            var rank = pool.Max(b => IdentityRank(b, serial, fn, rnm));
            pool = pool.Where(b => IdentityRank(b, serial, fn, rnm) == rank).ToArray();
            var source = pool.Max(b => (int)b.BindingSource);
            pool = pool.Where(b => (int)b.BindingSource == source).ToArray();
            if (pool.Select(b => b.LocationId).Distinct().Count() == 1) selected = pool[0];
            else return SavePending(database, org, serial, fn, rnm, display, day);
        }
        if (selected is not null)
        {
            // Enrich identity without altering location/provenance/validity. A new FN is a new observation identity.
            if (selected.FiscalDriveNumber.Length > 0 && fn.Length > 0 && selected.FiscalDriveNumber != fn)
                selected = selected with { Id = Guid.NewGuid(), FiscalDriveNumber = fn, CreatedAt = null, UpdatedAt = null };
            selected = selected with
            {
                KktSerial = serial.Length > 0 ? serial : selected.KktSerial,
                FiscalDriveNumber = fn.Length > 0 ? fn : selected.FiscalDriveNumber,
                RegisterNumber = rnm.Length > 0 ? rnm : selected.RegisterNumber,
                DisplayName = display.Length > 0 ? display : selected.DisplayName
            };
            Save(database, selected);
            return selected;
        }
        // A gap in known history is not authorization to apply today's business rule to an old shift.
        if (candidates.Any(b => b.LocationId is not null)) return SavePending(database, org, serial, fn, rnm, display, day);
        var locations = database.Locations().Where(l => l.OrganizationId == org).ToArray();
        var ruleLocation = RuleLocation(database, locations, serial, display);
        Location? location = ruleLocation;
        if (location is null && !IsGenericPoint(point))
        {
            var matches = locations.Where(l => Normalize(l.Name) == Normalize(point) || (l.Address.Length > 0 && Normalize(l.Address) == Normalize(point))).ToArray();
            if (matches.Length == 1) location = matches[0];
        }
        var b = new RegisterBinding(Guid.NewGuid(), org, location?.Id, fn, rnm, ruleLocation is null ? BindingSource.Automatic : BindingSource.Rule,
            false, null, null, serial, display);
        if (location is null) return SavePending(database, org, serial, fn, rnm, display, day);
        Save(database, b); return b;
    }
    public static bool IsGenericPoint(string name) => Normalize(name) is "" or "безторговойточки" or "ккт" or "касса" or "неопределеннаяккт";
    internal static Location? RuleLocation(Database database, Location[] locations, string serial, string display)
    {
        using var db = Open(database); using var c = db.CreateCommand();
        c.CommandText = "SELECT identity_kind,identity_value,target_name,target_address FROM register_location_rules ORDER BY identity_kind DESC";
        using var r = c.ExecuteReader();
        while (r.Read())
        {
            if (r.GetString(0) == "serial" ? !EqualId(serial, r.GetString(1)) : Normalize(display) != Normalize(r.GetString(1))) continue;
            if (r.GetString(0) == "serial") return KnownBusinessRules.FindKnownPoint(locations, r.GetString(2));
            var name = Normalize(r.GetString(2)); var address = Normalize(r.GetString(3));
            var matches = locations.Where(l => Normalize(l.Name) == name || (address.Length > 0 && Normalize(l.Address).EndsWith(address, StringComparison.Ordinal))).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }
        return null;
    }
    private static RegisterBinding SavePending(Database database, Guid org, string serial, string fn, string rnm, string display, DateOnly day)
    {
        var old = Read(database).FirstOrDefault(b => b.OrganizationId == org && b.LocationId is null && IdentityRank(b, serial, fn, rnm) > 0 && InPeriod(b, day));
        var hasHistory = Read(database).Any(b => b.OrganizationId == org && b.LocationId is not null && IdentityRank(b, serial, fn, rnm) > 0);
        var b = old ?? new RegisterBinding(Guid.NewGuid(), org, null, fn, rnm, ValidFrom: hasHistory ? day : null, ValidTo: hasHistory ? day : null, KktSerial: serial, DisplayName: display);
        Save(database, b); return b;
    }
    public static void Save(Database database, RegisterBinding requested)
    {
        using var db = Open(database); using var tx = db.BeginTransaction();
        var bindings = Read(db, tx);
        var b = requested with { FiscalDriveNumber = Digits(requested.FiscalDriveNumber), KktSerial = Digits(requested.KktSerial), RegisterNumber = Digits(requested.RegisterNumber) };
        Validate(db, tx, b);
        var existing = bindings.FirstOrDefault(x => x.Id == b.Id) ?? bindings.FirstOrDefault(x => x.OrganizationId == b.OrganizationId &&
            x.FiscalDriveNumber == b.FiscalDriveNumber && x.KktSerial == b.KktSerial && x.RegisterNumber == b.RegisterNumber && x.ValidFrom == b.ValidFrom && x.ValidTo == b.ValidTo);
        if (existing is not null && ((existing.IsLocked && b.BindingSource != BindingSource.Manual) || existing.BindingSource > b.BindingSource)) return;
        var overlapping = bindings.Where(x => x.Id != existing?.Id && x.OrganizationId == b.OrganizationId && IdentityRank(x, b.KktSerial, b.FiscalDriveNumber, b.RegisterNumber) > 0 && Overlaps(x, b) && x.LocationId is not null && b.LocationId is not null && x.LocationId != b.LocationId).ToArray();
        if (overlapping.Length > 0)
        {
            if (b.BindingSource != BindingSource.Manual) return;
            throw new InvalidOperationException("Периоды привязки пересекаются. Используйте изменение точки с датой переезда.");
        }
        if (existing is not null) b = b with { Id = existing.Id, CreatedAt = existing.CreatedAt };
        Write(db, tx, b, existing); tx.Commit();
    }
    public static void Assign(Database database, RegisterBinding original, Guid location, DateOnly? from, DateOnly? to)
    {
        using var db = Open(database); using var tx = db.BeginTransaction();
        var b = original with { Id = Guid.NewGuid(), FiscalDriveNumber = Digits(original.FiscalDriveNumber), KktSerial = Digits(original.KktSerial), RegisterNumber = Digits(original.RegisterNumber), LocationId = location, BindingSource = BindingSource.Manual, IsLocked = true, ValidFrom = from, ValidTo = to, CreatedAt = null, UpdatedAt = null, IsActive = true };
        Validate(db, tx, b);
        var rows = Read(db, tx).Where(x => x.OrganizationId == b.OrganizationId && IdentityRank(x, b.KktSerial, b.FiscalDriveNumber, b.RegisterNumber) > 0 && Overlaps(x, b)).ToArray();
        foreach (var old in rows)
        {
            Write(db, tx, old with { IsActive = false }, old);
            if (from is not null && (old.ValidFrom is null || old.ValidFrom < from))
                Write(db, tx, old with { Id = Guid.NewGuid(), ValidTo = from.Value.AddDays(-1), CreatedAt = null, UpdatedAt = null }, null);
            if (to is not null && (old.ValidTo is null || old.ValidTo > to))
                Write(db, tx, old with { Id = Guid.NewGuid(), ValidFrom = to.Value.AddDays(1), CreatedAt = null, UpdatedAt = null }, null);
        }
        Write(db, tx, b, original);
        // Apply only this register's dated observations. Bank history is deliberately not part of this update.
        foreach (var table in new[] { "shift_closures", "operations" })
        {
            using var c = db.CreateCommand(); c.Transaction = tx;
            var time = table == "shift_closures" ? "closed_at" : "occurred_at";
            c.CommandText = $"""
                UPDATE {table} SET location_id=$loc WHERE organization_id=$org
                AND ($from IS NULL OR substr({time},1,10)>=$from) AND ($to IS NULL OR substr({time},1,10)<=$to)
                AND (($serial<>'' AND kkt_serial=$serial) OR ($fn<>'' AND fn=$fn) OR ($rnm<>'' AND registration_number=$rnm))
                AND (kkt_serial='' OR $serial='' OR kkt_serial=$serial)
                {(table == "operations" ? "AND source_kind<>'Bank'" : "")}
                """;
            c.Parameters.AddWithValue("$loc", location.ToString()); c.Parameters.AddWithValue("$org", b.OrganizationId.ToString());
            c.Parameters.AddWithValue("$from", (object?)from?.ToString("yyyy-MM-dd") ?? DBNull.Value); c.Parameters.AddWithValue("$to", (object?)to?.ToString("yyyy-MM-dd") ?? DBNull.Value);
            c.Parameters.AddWithValue("$serial", b.KktSerial); c.Parameters.AddWithValue("$fn", b.FiscalDriveNumber); c.Parameters.AddWithValue("$rnm", b.RegisterNumber); c.ExecuteNonQuery();
        }
        // Legacy shift-derived operations may not yet carry a register identifier.
        using (var sync = db.CreateCommand())
        {
            sync.Transaction = tx; sync.CommandText = """
                UPDATE operations SET location_id=(SELECT s.location_id FROM shift_closures s WHERE s.source=operations.source AND operations.external_id IN(s.external_id||':cash',s.external_id||':electronic'))
                WHERE source IN('Taxcom.ShiftReport','Frontol.Report') AND EXISTS(SELECT 1 FROM shift_closures s WHERE s.source=operations.source AND operations.external_id IN(s.external_id||':cash',s.external_id||':electronic'));
                DELETE FROM reconciliation_allocations;
                """; sync.ExecuteNonQuery();
        }
        tx.Commit(); database.RebuildCrossSourceShiftMatches();
    }
    private static void Validate(SqliteConnection db, SqliteTransaction tx, RegisterBinding b)
    {
        if (b.FiscalDriveNumber.Length == 0 && b.KktSerial.Length == 0 && b.RegisterNumber.Length == 0) throw new InvalidOperationException("Укажите заводской номер ККТ, ФН или РНМ.");
        if (b.ValidFrom is not null && b.ValidTo is not null && b.ValidFrom > b.ValidTo) throw new InvalidOperationException("Начало периода позже окончания.");
        if (b.LocationId is null) return;
        using var c = db.CreateCommand(); c.Transaction = tx; c.CommandText = "SELECT COUNT(*) FROM locations WHERE id=$loc AND organization_id=$org AND is_active=1";
        c.Parameters.AddWithValue("$loc", b.LocationId.ToString()); c.Parameters.AddWithValue("$org", b.OrganizationId.ToString());
        if (Convert.ToInt64(c.ExecuteScalar()) != 1) throw new InvalidOperationException("Выберите действующую точку этой организации.");
    }
    private static void Write(SqliteConnection db, SqliteTransaction tx, RegisterBinding b, RegisterBinding? old)
    {
        if (old is not null && (b with { CreatedAt = old.CreatedAt, UpdatedAt = old.UpdatedAt }) == old) return;
        var now = DateTimeOffset.UtcNow; b = b with { CreatedAt = b.CreatedAt ?? now, UpdatedAt = now };
        using var c = db.CreateCommand(); c.Transaction = tx;
        c.CommandText = $"""
            INSERT INTO register_bindings({Columns}) VALUES($id,$org,$loc,$fn,$rnm,$source,$lock,$from,$to,$serial,$name,$created,$updated,$active)
            ON CONFLICT(id) DO UPDATE SET location_id=excluded.location_id,fn=excluded.fn,register_number=excluded.register_number,
                binding_source=excluded.binding_source,is_locked=excluded.is_locked,valid_from=excluded.valid_from,valid_to=excluded.valid_to,
                kkt_serial=excluded.kkt_serial,display_name=excluded.display_name,updated_at=excluded.updated_at,is_active=excluded.is_active
            """;
        c.Parameters.AddWithValue("$id", b.Id.ToString()); c.Parameters.AddWithValue("$org", b.OrganizationId.ToString()); c.Parameters.AddWithValue("$loc", (object?)b.LocationId?.ToString() ?? DBNull.Value);
        c.Parameters.AddWithValue("$fn", b.FiscalDriveNumber); c.Parameters.AddWithValue("$rnm", b.RegisterNumber); c.Parameters.AddWithValue("$serial", b.KktSerial); c.Parameters.AddWithValue("$name", b.DisplayName);
        c.Parameters.AddWithValue("$source", b.BindingSource.ToString()); c.Parameters.AddWithValue("$lock", b.IsLocked ? 1 : 0); c.Parameters.AddWithValue("$active", b.IsActive ? 1 : 0);
        c.Parameters.AddWithValue("$from", (object?)b.ValidFrom?.ToString("yyyy-MM-dd") ?? DBNull.Value); c.Parameters.AddWithValue("$to", (object?)b.ValidTo?.ToString("yyyy-MM-dd") ?? DBNull.Value);
        c.Parameters.AddWithValue("$created", b.CreatedAt!.Value.ToString("O")); c.Parameters.AddWithValue("$updated", now.ToString("O")); c.ExecuteNonQuery();
        c.Parameters.Clear(); c.CommandText = """
            INSERT INTO register_binding_audit(binding_id,old_value,new_value,occurred_at) VALUES($id,$old,$new,$t);
            INSERT INTO audit_log(occurred_at,action,details) VALUES($t,'register.rebind',$details);
            """;
        c.Parameters.AddWithValue("$id", b.Id.ToString()); c.Parameters.AddWithValue("$old", JsonSerializer.Serialize(old)); c.Parameters.AddWithValue("$new", JsonSerializer.Serialize(b)); c.Parameters.AddWithValue("$t", now.ToString("O"));
        c.Parameters.AddWithValue("$details", $"ККТ={b.KktSerial}; ФН={b.FiscalDriveNumber}; old_loc={old?.LocationId}; new_loc={b.LocationId}; source={b.BindingSource}; period={b.ValidFrom}..{b.ValidTo}; active={b.IsActive}"); c.ExecuteNonQuery();
    }
}
