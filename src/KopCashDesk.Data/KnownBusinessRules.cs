using KopCashDesk.Core;
using Microsoft.Data.Sqlite;

namespace KopCashDesk.Data;

public static class KnownBusinessRules
{
    public const string CopTaxId = "6683009222";

    public const string ReftinskayaRegisterSerial = "00106900361561";
    public const string ReftinskayaPointName = "Рефтинская ГРЭС 6 столовая";

    public const string LadyzhenskogoRegisterSerial = "00108202518113";
    public const string LadyzhenskogoPointName = "Ладыженского 7";

    public const string MiraRegisterSerial = "00178945";
    public const string MiraPointName = "Мира 4 (неактив.)";

    public const string AtiAppetitRegisterSerial = "00301000370264";
    public const string AtiAppetitPointName = "Кулинария Аппетит";

    public const string AtiMercuryRegisterSerial = "08050950";
    public const string AtiMercuryPointName = "Столовая АТИ";

    public const string ChapaevaRegisterSerial = "08052160";
    public const string ChapaevaPointName = "Чапаева 28";

    public const string MusicCollegeRegisterSerial = "00178241";
    public const string MusicCollegePointName = "Музыкальный колледж";

    // Any register which is not one of the confirmed KKT must stay unassigned until the user binds it manually.
    private const string UnknownRegisterPoint = "__UNASSIGNED_UNKNOWN_KKT__";

    public static IReadOnlyList<string> FixedPointNames { get; } =
    [
        ReftinskayaPointName,
        LadyzhenskogoPointName,
        MiraPointName,
        AtiAppetitPointName,
        AtiMercuryPointName,
        ChapaevaPointName,
        MusicCollegePointName
    ];

    public static bool IsKnownRegisterSerial(string serial)
    {
        var digits = DigitsOnly(serial);
        return digits is ReftinskayaRegisterSerial or LadyzhenskogoRegisterSerial or MiraRegisterSerial or
            AtiAppetitRegisterSerial or AtiMercuryRegisterSerial or ChapaevaRegisterSerial or MusicCollegeRegisterSerial;
    }

    public static string? PointNameForRegisterSerial(string serial)
    {
        var digits = DigitsOnly(serial);
        return digits switch
        {
            ReftinskayaRegisterSerial => ReftinskayaPointName,
            LadyzhenskogoRegisterSerial => LadyzhenskogoPointName,
            MiraRegisterSerial => MiraPointName,
            AtiAppetitRegisterSerial => AtiAppetitPointName,
            AtiMercuryRegisterSerial => AtiMercuryPointName,
            ChapaevaRegisterSerial => ChapaevaPointName,
            MusicCollegeRegisterSerial => MusicCollegePointName,
            _ => UnknownRegisterPoint
        };
    }

    public static Location? FindKnownPoint(IEnumerable<Location> locations, string knownPoint)
    {
        if (knownPoint == UnknownRegisterPoint) return null;

        var list = locations.Where(x => x.IsActive).ToArray();
        var exact = list.Where(x => Normalize(x.Name) == Normalize(knownPoint)).ToArray();
        if (exact.Length == 1) return exact[0];

        // Compatibility only for an old database. A clean database is normalized to the seven names above.
        string[] aliases = knownPoint switch
        {
            AtiAppetitPointName => ["ЗАВОД АТИ"],
            AtiMercuryPointName => ["Столовая завода АТИ"],
            MusicCollegePointName => ["Колледж искусств", "Музыкальный колледж"],
            MiraPointName => ["Мира 4", "Ленинградская 1", "Вороний Брод", "Белокаменный Кафе", "BUFET"],
            _ => []
        };
        var aliasMatches = list.Where(x => aliases.Any(a => Normalize(x.Name) == Normalize(a))).ToArray();
        if (aliasMatches.Length == 1) return aliasMatches[0];

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
            if (DigitsOnly(organization.TaxId) != CopTaxId) continue;

            EnsureSevenLocations(database, organization.Id);
            MergeConfirmedAliases(database, organization.Id);
            RemoveNonexistent9015(database, organization.Id);

            applied += TryKnownRegisterLocation(database, organization.Id, ReftinskayaRegisterSerial, ReftinskayaPointName, ref backupTaken);
            applied += TryKnownRegisterLocation(database, organization.Id, LadyzhenskogoRegisterSerial, LadyzhenskogoPointName, ref backupTaken);
            applied += TryKnownRegisterLocation(database, organization.Id, MiraRegisterSerial, MiraPointName, ref backupTaken);
            applied += TryKnownRegisterLocation(database, organization.Id, AtiAppetitRegisterSerial, AtiAppetitPointName, ref backupTaken);
            applied += TryKnownRegisterLocation(database, organization.Id, AtiMercuryRegisterSerial, AtiMercuryPointName, ref backupTaken);
            applied += TryKnownRegisterLocation(database, organization.Id, ChapaevaRegisterSerial, ChapaevaPointName, ref backupTaken);
            applied += TryKnownRegisterLocation(database, organization.Id, MusicCollegeRegisterSerial, MusicCollegePointName, ref backupTaken);
        }

        if (applied > 0)
            database.RebuildCrossSourceShiftMatches();

        return applied;
    }

    private static void EnsureSevenLocations(Database database, Guid organizationId)
    {
        foreach (var pointName in FixedPointNames)
        {
            var all = database.Locations(includeInactive: true)
                .Where(x => x.OrganizationId == organizationId && Normalize(x.Name) == Normalize(pointName))
                .ToArray();

            var target = all.FirstOrDefault(x => x.IsActive) ?? all.FirstOrDefault();
            if (target is null)
            {
                database.Save(new Location(Guid.NewGuid(), organizationId, pointName));
                continue;
            }

            if (!target.IsActive || target.IsExcluded || target.MergedIntoLocationId is not null || target.Name != pointName)
            {
                target = target with
                {
                    Name = pointName,
                    IsActive = true,
                    IsExcluded = false,
                    MergedIntoLocationId = null
                };
                database.Save(target);
            }

            foreach (var duplicate in all.Where(x => x.Id != target.Id && x.IsActive))
                TryMerge(database, duplicate.Id, target.Id, $"дубликат фиксированной точки {pointName}");
        }
    }

    private static void MergeConfirmedAliases(Database database, Guid organizationId)
    {
        MergeAlias(database, organizationId, "ЗАВОД АТИ", AtiAppetitPointName,
            "старое название; ККТ 00301000370264 = Кулинария Аппетит");
        MergeAlias(database, organizationId, "Столовая завода АТИ", AtiMercuryPointName,
            "старое название; ККТ 08050950 = Столовая АТИ");
        MergeAlias(database, organizationId, "Колледж искусств", MusicCollegePointName,
            "подтверждено пользователем: точка называется Музыкальный колледж");

        // One KKT 00178945 moved: Вороний Брод -> Ленинградская 1 -> Мира 4.
        // For reconciliation the entire history is intentionally shown under the single closed point.
        MergeAlias(database, organizationId, "Мира 4", MiraPointName, "история ККТ 00178945");
        MergeAlias(database, organizationId, "Ленинградская 1", MiraPointName, "история ККТ 00178945");
        MergeAlias(database, organizationId, "Вороний Брод", MiraPointName, "история ККТ 00178945");
        MergeAlias(database, organizationId, "Белокаменный Кафе", MiraPointName, "история ККТ 00178945");
        MergeAlias(database, organizationId, "Белокаменный Кафе / BUFET", MiraPointName, "история ККТ 00178945");
        MergeAlias(database, organizationId, "BUFET", MiraPointName, "история ККТ 00178945");
    }

    private static void MergeAlias(Database database, Guid organizationId, string sourceName, string targetName, string reason)
    {
        var locations = database.Locations().Where(x => x.OrganizationId == organizationId && x.IsActive).ToArray();
        var target = locations.SingleOrDefault(x => Normalize(x.Name) == Normalize(targetName));
        if (target is null) return;

        foreach (var source in locations.Where(x => x.Id != target.Id && Normalize(x.Name) == Normalize(sourceName)).ToArray())
            TryMerge(database, source.Id, target.Id, reason);
    }

    private static void TryMerge(Database database, Guid source, Guid target, string reason)
    {
        try { database.MergeLocations(source, target, reason); }
        catch (InvalidOperationException ex)
        {
            database.Audit("location.merge.review", $"source={source}; target={target}; reason={reason}; error={ex.Message}");
        }
    }

    private static void RemoveNonexistent9015(Database database, Guid organizationId)
    {
        using var db = Open(database);
        using var tx = db.BeginTransaction();

        using var removeRules = db.CreateCommand();
        removeRules.Transaction = tx;
        removeRules.CommandText = "DELETE FROM register_location_rules WHERE identity_value='00179015'";
        removeRules.ExecuteNonQuery();

        using var removeBinding = db.CreateCommand();
        removeBinding.Transaction = tx;
        removeBinding.CommandText = "DELETE FROM register_bindings WHERE organization_id=$org AND kkt_serial='00179015'";
        removeBinding.Parameters.AddWithValue("$org", organizationId.ToString());
        var deleted = removeBinding.ExecuteNonQuery();

        if (deleted > 0)
        {
            using var audit = db.CreateCommand();
            audit.Transaction = tx;
            audit.CommandText = "INSERT INTO audit_log(occurred_at,action,details) VALUES($t,'register.rule.remove',$d)";
            audit.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
            audit.Parameters.AddWithValue("$d", $"removed nonexistent KKT 00179015 bindings={deleted}");
            audit.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static void EnsureRegisterLocationRules(Database database)
    {
        using var db = Open(database);
        using var tx = db.BeginTransaction();

        using (var clear = db.CreateCommand())
        {
            clear.Transaction = tx;
            // No name/display guesses: an unknown KKT must wait for manual assignment.
            clear.CommandText = "DELETE FROM register_location_rules WHERE identity_kind IN ('serial','display')";
            clear.ExecuteNonQuery();
        }

        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO register_location_rules(rule_key,identity_kind,identity_value,target_name,target_address)
            VALUES
                ('fixed-reftinskaya','serial',$reft,$reftPoint,''),
                ('fixed-ladyzhenskogo','serial',$lady,$ladyPoint,''),
                ('fixed-mira-history','serial',$mira,$miraPoint,''),
                ('fixed-appetit','serial',$appetit,$appetitPoint,''),
                ('fixed-ati','serial',$ati,$atiPoint,''),
                ('fixed-chapaeva','serial',$chapaeva,$chapaevaPoint,''),
                ('fixed-music-college','serial',$college,$collegePoint,'');
            """;
        command.Parameters.AddWithValue("$reft", ReftinskayaRegisterSerial);
        command.Parameters.AddWithValue("$reftPoint", ReftinskayaPointName);
        command.Parameters.AddWithValue("$lady", LadyzhenskogoRegisterSerial);
        command.Parameters.AddWithValue("$ladyPoint", LadyzhenskogoPointName);
        command.Parameters.AddWithValue("$mira", MiraRegisterSerial);
        command.Parameters.AddWithValue("$miraPoint", MiraPointName);
        command.Parameters.AddWithValue("$appetit", AtiAppetitRegisterSerial);
        command.Parameters.AddWithValue("$appetitPoint", AtiAppetitPointName);
        command.Parameters.AddWithValue("$ati", AtiMercuryRegisterSerial);
        command.Parameters.AddWithValue("$atiPoint", AtiMercuryPointName);
        command.Parameters.AddWithValue("$chapaeva", ChapaevaRegisterSerial);
        command.Parameters.AddWithValue("$chapaevaPoint", ChapaevaPointName);
        command.Parameters.AddWithValue("$college", MusicCollegeRegisterSerial);
        command.Parameters.AddWithValue("$collegePoint", MusicCollegePointName);
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
        var ruleKey = $"known.kkt-location.v3:{organizationId:N}:{serial}";
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
                            AND rb.kkt_serial=$serial
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
                            AND rb.kkt_serial=$serial
                            AND (rb.valid_from IS NULL OR rb.valid_from<=substr(operations.occurred_at,1,10))
                            AND (rb.valid_to IS NULL OR rb.valid_to>=substr(operations.occurred_at,1,10))
                      );
                    """;
                operations.Parameters.AddWithValue("$loc", target.Id.ToString());
                operations.Parameters.AddWithValue("$org", organizationId.ToString());
                operations.Parameters.AddWithValue("$serial", serial);
                changed += operations.ExecuteNonQuery();
            }

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

    private static string Normalize(string value) =>
        string.Join(' ', (value ?? string.Empty).Trim().ToLowerInvariant().Replace('ё', 'е').Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string DigitsOnly(string value) => new((value ?? string.Empty).Where(char.IsDigit).ToArray());

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
