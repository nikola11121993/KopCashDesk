using KopCashDesk.Core;
using Microsoft.Data.Sqlite;

namespace KopCashDesk.Data;

public static class KnownBusinessRules
{
    public const string ReftinskayaRegisterSerial = "00106900361561";
    public const string ReftinskayaPointName = "Рефтинская ГРЭС 6 столовая";

    public const string AtiAppetitRegisterSerial = "00301000370264";
    public const string AtiAppetitPointName = "ЗАВОД АТИ";
    public const string AtiMercuryRegisterSerial = "08050950";
    // Legacy name from older imports. It is the same physical point as ЗАВОД АТИ.
    public const string AtiMercuryPointName = "Столовая АТИ";

    public static string? PointNameForRegisterSerial(string serial)
    {
        var digits = DigitsOnly(serial);
        return digits switch
        {
            ReftinskayaRegisterSerial => ReftinskayaPointName,
            AtiAppetitRegisterSerial => AtiAppetitPointName,
            AtiMercuryRegisterSerial => AtiAppetitPointName,
            _ => null
        };
    }

    public static Location? FindKnownPoint(IEnumerable<Location> locations, string knownPoint)
    {
        var list = locations.Where(x => x.IsActive).ToArray();
        var exact = list.Where(x => Normalize(x.Name) == Normalize(knownPoint)).ToArray();
        if (exact.Length == 1) return exact[0];

        if (knownPoint == ReftinskayaPointName)
        {
            var cafeteria6 = list.Where(IsReftinskayaCafeteria6).ToArray();
            if (cafeteria6.Length == 1) return cafeteria6[0];

            var gres = list.Where(x =>
            {
                var name = Normalize(x.Name);
                return name.Contains("рефтин", StringComparison.Ordinal) && name.Contains("грэс", StringComparison.Ordinal);
            }).ToArray();
            if (gres.Length == 1) return gres[0];
        }

        return null;
    }

    public static int ApplyPending(Database database)
    {
        EnsureRegisterLocationRules(database);

        var applied = 0;
        var backupTaken = false;
        foreach (var organization in database.Organizations())
        {
            // User-confirmed physical identity: "ЗАВОД АТИ" and "Столовая АТИ" are one point.
            // Merge the legacy alias first so bank operations, terminal bindings and fiscal data
            // all end up on the same location, rather than merely rebinding the KKT.
            applied += TryAlias(
                database,
                organization.Id,
                AtiMercuryPointName,
                AtiAppetitPointName,
                "known.ati-single-physical-point.v1",
                "ЗАВОД АТИ и Столовая АТИ — одна физическая точка");

            applied += TryKnownRegisterLocation(database, organization.Id, AtiAppetitRegisterSerial, AtiAppetitPointName, ref backupTaken);
            applied += TryKnownRegisterLocation(database, organization.Id, AtiMercuryRegisterSerial, AtiAppetitPointName, ref backupTaken);

            // Белокаменный кафе -> Ленинградская 1 -> Мира 4 describes a moving KKT,
            // not aliases for one address. No date-free history merges, including bank/UBRiR rows.
            applied += TryReftinskayaDuplicate(database, organization.Id);
        }

        if (applied > 0)
            database.RebuildCrossSourceShiftMatches();

        return applied;
    }

    private static void EnsureRegisterLocationRules(Database database)
    {
        using var db = Open(database);
        using var tx = db.BeginTransaction();
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO register_location_rules(rule_key,identity_kind,identity_value,target_name,target_address)
            VALUES
                ('ati-mercury','serial',$mercury,$mercuryPoint,''),
                ('ati-appetit','serial',$appetit,$appetitPoint,'')
            ON CONFLICT(rule_key) DO UPDATE SET
                identity_kind=excluded.identity_kind,
                identity_value=excluded.identity_value,
                target_name=excluded.target_name,
                target_address=excluded.target_address;
            """;
        command.Parameters.AddWithValue("$mercury", AtiMercuryRegisterSerial);
        command.Parameters.AddWithValue("$mercuryPoint", AtiAppetitPointName);
        command.Parameters.AddWithValue("$appetit", AtiAppetitRegisterSerial);
        command.Parameters.AddWithValue("$appetitPoint", AtiAppetitPointName);
        command.ExecuteNonQuery();
        tx.Commit();
    }

    private static int TryKnownRegisterLocation(
        Database database,
        Guid organizationId,
        string serial,
        string targetName,
        ref bool backupTaken)
    {
        var ruleKey = $"known.kkt-location.v1:{organizationId:N}:{serial}";
        if (IsApplied(database, ruleKey)) return 0;

        var locations = database.Locations().Where(x => x.OrganizationId == organizationId && x.IsActive).ToArray();
        var exactTargets = locations.Where(x => Normalize(x.Name) == Normalize(targetName)).ToArray();
        if (exactTargets.Length != 1)
        {
            NoteReview(database, organizationId, serial, targetName,
                exactTargets.Length == 0
                    ? "целевая торговая точка не найдена"
                    : $"найдено несколько ({exactTargets.Length}) одноимённых целевых точек");
            return 0;
        }

        var target = exactTargets[0];
        if (HasRegisterEvidence(database, organizationId, serial) && !backupTaken)
        {
            database.BackupBeforeMigration(4);
            backupTaken = true;
        }

        var changed = 0;
        using (var db = Open(database))
        using (var tx = db.BeginTransaction())
        {
            var now = DateTimeOffset.UtcNow.ToString("O");

            using (var bindings = db.CreateCommand())
            {
                bindings.Transaction = tx;
                bindings.CommandText = """
                    UPDATE register_bindings
                    SET location_id=$loc,binding_source='Rule',updated_at=$now
                    WHERE organization_id=$org AND kkt_serial=$serial
                      AND binding_source<>'Manual' AND is_locked=0
                      AND (location_id IS NULL OR location_id<>$loc OR binding_source<>'Rule');
                    """;
                bindings.Parameters.AddWithValue("$loc", target.Id.ToString());
                bindings.Parameters.AddWithValue("$org", organizationId.ToString());
                bindings.Parameters.AddWithValue("$serial", serial);
                bindings.Parameters.AddWithValue("$now", now);
                changed += bindings.ExecuteNonQuery();
            }

            using (var shifts = db.CreateCommand())
            {
                shifts.Transaction = tx;
                shifts.CommandText = """
                    UPDATE shift_closures SET location_id=$loc
                    WHERE organization_id=$org AND kkt_serial=$serial
                      AND (location_id IS NULL OR location_id<>$loc)
                      AND NOT EXISTS(
                          SELECT 1 FROM register_bindings rb
                          WHERE rb.organization_id=shift_closures.organization_id
                            AND rb.is_active=1
                            AND (rb.binding_source='Manual' OR rb.is_locked=1)
                            AND (
                                rb.kkt_serial=$serial OR
                                (rb.kkt_serial='' AND shift_closures.fn<>'' AND rb.fn=shift_closures.fn) OR
                                (rb.kkt_serial='' AND shift_closures.registration_number<>'' AND rb.register_number=shift_closures.registration_number)
                            )
                            AND (rb.valid_from IS NULL OR rb.valid_from<=substr(shift_closures.closed_at,1,10))
                            AND (rb.valid_to IS NULL OR rb.valid_to>=substr(shift_closures.closed_at,1,10))
                      );
                    """;
                shifts.Parameters.AddWithValue("$loc", target.Id.ToString());
                shifts.Parameters.AddWithValue("$org", organizationId.ToString());
                shifts.Parameters.AddWithValue("$serial", serial);
                changed += shifts.ExecuteNonQuery();
            }

            using (var operations = db.CreateCommand())
            {
                operations.Transaction = tx;
                operations.CommandText = """
                    UPDATE operations SET location_id=$loc
                    WHERE organization_id=$org AND kkt_serial=$serial AND source_kind<>'Bank'
                      AND (location_id IS NULL OR location_id<>$loc)
                      AND NOT EXISTS(
                          SELECT 1 FROM register_bindings rb
                          WHERE rb.organization_id=operations.organization_id
                            AND rb.is_active=1
                            AND (rb.binding_source='Manual' OR rb.is_locked=1)
                            AND (
                                rb.kkt_serial=$serial OR
                                (rb.kkt_serial='' AND operations.fn<>'' AND rb.fn=operations.fn) OR
                                (rb.kkt_serial='' AND operations.registration_number<>'' AND rb.register_number=operations.registration_number)
                            )
                            AND (rb.valid_from IS NULL OR rb.valid_from<=substr(operations.occurred_at,1,10))
                            AND (rb.valid_to IS NULL OR rb.valid_to>=substr(operations.occurred_at,1,10))
                      );
                    """;
                operations.Parameters.AddWithValue("$loc", target.Id.ToString());
                operations.Parameters.AddWithValue("$org", organizationId.ToString());
                operations.Parameters.AddWithValue("$serial", serial);
                changed += operations.ExecuteNonQuery();
            }

            // Older shift-derived operation rows can lack a serial even when their parent shift has it.
            // Follow the already-repaired fiscal shift; bank operations are deliberately excluded.
            using (var legacyOperations = db.CreateCommand())
            {
                legacyOperations.Transaction = tx;
                legacyOperations.CommandText = """
                    UPDATE operations
                    SET location_id=(
                        SELECT s.location_id FROM shift_closures s
                        WHERE s.organization_id=operations.organization_id
                          AND s.source=operations.source
                          AND operations.external_id IN(s.external_id||':cash',s.external_id||':electronic')
                          AND s.kkt_serial=$serial
                        LIMIT 1)
                    WHERE organization_id=$org AND source_kind<>'Bank'
                      AND EXISTS(
                        SELECT 1 FROM shift_closures s
                        WHERE s.organization_id=operations.organization_id
                          AND s.source=operations.source
                          AND operations.external_id IN(s.external_id||':cash',s.external_id||':electronic')
                          AND s.kkt_serial=$serial
                          AND (operations.location_id IS NULL OR operations.location_id<>s.location_id));
                    """;
                legacyOperations.Parameters.AddWithValue("$org", organizationId.ToString());
                legacyOperations.Parameters.AddWithValue("$serial", serial);
                changed += legacyOperations.ExecuteNonQuery();
            }

            if (changed > 0)
            {
                using var reset = db.CreateCommand();
                reset.Transaction = tx;
                reset.CommandText = "DELETE FROM reconciliation_allocations;";
                reset.ExecuteNonQuery();
            }

            tx.Commit();
        }

        MarkApplied(database, ruleKey, $"serial={serial}; target={target.Id}; target_name={target.Name}; changed={changed}");
        return changed > 0 ? 1 : 0;
    }

    private static bool HasRegisterEvidence(Database database, Guid organizationId, string serial)
    {
        using var db = Open(database);
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM register_bindings WHERE organization_id=$org AND kkt_serial=$serial) +
                (SELECT COUNT(*) FROM shift_closures WHERE organization_id=$org AND kkt_serial=$serial) +
                (SELECT COUNT(*) FROM operations WHERE organization_id=$org AND kkt_serial=$serial AND source_kind<>'Bank');
            """;
        command.Parameters.AddWithValue("$org", organizationId.ToString());
        command.Parameters.AddWithValue("$serial", serial);
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    private static void NoteReview(Database database, Guid organizationId, string serial, string targetName, string reason)
    {
        var details = $"org={organizationId}; serial={serial}; target={targetName}; {reason}; автоматическая привязка и перенос истории пропущены";
        using var db = Open(database);
        using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO audit_log(occurred_at,action,details)
            SELECT $time,'register.rule.review',$details
            WHERE NOT EXISTS(
                SELECT 1 FROM audit_log WHERE action='register.rule.review' AND details=$details
            );
            """;
        command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$details", details);
        command.ExecuteNonQuery();
    }

    private static int TryAlias(Database database, Guid organizationId, string sourceName, string targetName, string ruleName, string reason)
    {
        var ruleKey = $"{ruleName}:{organizationId:N}";
        if (IsApplied(database, ruleKey)) return 0;

        var locations = database.Locations().Where(x => x.OrganizationId == organizationId).ToArray();
        var source = locations.Where(x => NameEquals(x.Name, sourceName)).ToArray();
        var target = locations.Where(x => NameEquals(x.Name, targetName)).ToArray();
        if (source.Length != 1 || target.Length != 1) return 0;

        if (!database.MergeLocations(source[0].Id, target[0].Id, reason)) return 0;
        MarkApplied(database, ruleKey, $"{source[0].Name} -> {target[0].Name}");
        return 1;
    }

    private static int TryReftinskayaDuplicate(Database database, Guid organizationId)
    {
        var ruleKey = $"known.reftinskaya-register:{organizationId:N}";
        if (IsApplied(database, ruleKey)) return 0;

        var locations = database.Locations().Where(x => x.OrganizationId == organizationId).ToArray();
        var target = FindKnownPoint(locations, ReftinskayaPointName);
        if (target is null) return 0;

        var sources = locations.Where(x => x.Id != target.Id && DigitsOnly(x.Name).Contains(ReftinskayaRegisterSerial, StringComparison.Ordinal)).ToArray();
        if (sources.Length == 0) return 0;

        var merged = 0;
        foreach (var source in sources)
        {
            if (database.RegisterBindings().Any(b => b.LocationId == source.Id && (b.IsLocked || b.BindingSource == BindingSource.Manual))) continue;
            using var db = Open(database);
            using var check = db.CreateCommand();
            database.EnsureManualTerminalPostings();
            check.CommandText = "SELECT (SELECT COUNT(*) FROM operations WHERE location_id=$loc AND source_kind='Bank') + (SELECT COUNT(*) FROM terminal_bindings WHERE location_id=$loc) + (SELECT COUNT(*) FROM manual_cash_postings WHERE location_id=$loc) + (SELECT COUNT(*) FROM manual_terminal_postings WHERE location_id=$loc)";
            check.Parameters.AddWithValue("$loc", source.Id.ToString());
            if (Convert.ToInt64(check.ExecuteScalar()) > 0) continue;
            if (merged == 0) database.BackupBeforeMigration(4);
            if (database.MergeLocations(source.Id, target.Id, $"правило ККТ {ReftinskayaRegisterSerial} = {ReftinskayaPointName}")) merged++;
        }

        if (merged > 0) MarkApplied(database, ruleKey, $"merged={merged}; target={target.Id}");
        return merged;
    }

    private static bool IsApplied(Database database, string ruleKey)
    {
        using var db = Open(database);
        using var command = db.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM applied_business_rules WHERE rule_key=$key)";
        command.Parameters.AddWithValue("$key", ruleKey);
        return Convert.ToInt64(command.ExecuteScalar()) != 0;
    }

    private static void MarkApplied(Database database, string ruleKey, string details)
    {
        using var db = Open(database);
        using var command = db.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO applied_business_rules(rule_key,applied_at,details) VALUES($key,$time,$details)";
        command.Parameters.AddWithValue("$key", ruleKey);
        command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$details", details);
        command.ExecuteNonQuery();
    }

    private static bool IsReftinskayaCafeteria6(Location location)
    {
        var name = Normalize(location.Name);
        return name.Contains("рефтин", StringComparison.Ordinal) &&
               name.Contains("грэс", StringComparison.Ordinal) &&
               (name.Contains("6 стол", StringComparison.Ordinal) || name.Contains("столовая 6", StringComparison.Ordinal));
    }

    private static bool NameEquals(string left, string right) => Normalize(left) == Normalize(right);

    private static string Normalize(string value) =>
        string.Join(' ', value.Trim().ToLowerInvariant().Replace('ё', 'е').Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string DigitsOnly(string value) => new(value.Where(char.IsDigit).ToArray());

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
}
