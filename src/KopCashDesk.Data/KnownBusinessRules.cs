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
    public const string AtiMercuryPointName = "Столовая АТИ";

    public const string GarantTaxId = "6683011158";
    public const string GarantDvvsPointName = "ДВВС";
    public const string GarantDvvs1Serial = "00108722823598";
    public const string GarantDvvs1Rnm = "0009046158049705";
    public const string GarantDvvs2Serial = "00108720771767";
    public const string GarantDvvs2Rnm = "0009168058061681";
    public const string GarantDvvsSpareSerial = "00308302622940";
    public const string GarantDvvsSpareRnm = "0009075914046300";

    public const string GarantSredneuralskPointName = "ДК Среднеуральск";
    public const string GarantSredneuralskAddress = "Свердловская область, г. Среднеуральск, ул. Набережная, 8а";
    public const string GarantSredneuralskRegisterSerial = "04023212";
    public const string GarantSredneuralskRnm = "0007852946058578";
    public const string GarantSredneuralskPosTid = "34771891";
    public const string GarantSredneuralskQrTid = "34771897";

    public const string GarantSamboPointName = "Дворец самбо";
    public const string GarantSamboAddress = "Свердловская область, г. Верхняя Пышма, ул. Горняков, 1Б";
    public const string GarantSamboRegisterSerial = "00307401462012";
    public const string GarantSamboRnm = "0006988497032805";

    public static string? PointNameForRegisterSerial(string serial)
    {
        var digits = DigitsOnly(serial);
        return digits switch
        {
            ReftinskayaRegisterSerial => ReftinskayaPointName,
            AtiAppetitRegisterSerial => AtiAppetitPointName,
            AtiMercuryRegisterSerial => AtiMercuryPointName,
            GarantDvvs1Serial => GarantDvvsPointName,
            GarantDvvs2Serial => GarantDvvsPointName,
            GarantDvvsSpareSerial => GarantDvvsPointName,
            GarantSredneuralskRegisterSerial => GarantSredneuralskPointName,
            GarantSamboRegisterSerial => GarantSamboPointName,
            _ => null
        };
    }

    public static string? PointNameForTerminal(string taxId, string terminalId)
    {
        if (DigitsOnly(taxId) != GarantTaxId) return null;
        var tid = DigitsOnly(terminalId);
        return tid is GarantSredneuralskPosTid or GarantSredneuralskQrTid
            ? GarantSredneuralskPointName
            : null;
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
        EnsureGarantDvvsRegisters(database);
        EnsureGarantSredneuralsk(database);
        EnsureGarantSambo(database);
        RepairGarantSredneuralskLegacyMayBankRows(database);

        var applied = 0;
        var backupTaken = false;
        foreach (var organization in database.Organizations())
        {
            applied += TryKnownRegisterLocation(database, organization.Id, AtiAppetitRegisterSerial, AtiAppetitPointName, ref backupTaken);
            applied += TryKnownRegisterLocation(database, organization.Id, AtiMercuryRegisterSerial, AtiMercuryPointName, ref backupTaken);

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
                ('ati-appetit','serial',$appetit,$appetitPoint,''),
                ('garant-dvvs-1','serial',$dvvs1,$dvvsPoint,''),
                ('garant-dvvs-2','serial',$dvvs2,$dvvsPoint,''),
                ('garant-dvvs-spare','serial',$dvvsSpare,$dvvsPoint,''),
                ('garant-sredneuralsk','serial',$sredSerial,$sredPoint,$sredAddress),
                ('garant-sambo','serial',$samboSerial,$samboPoint,$samboAddress)
            ON CONFLICT(rule_key) DO UPDATE SET
                identity_kind=excluded.identity_kind,
                identity_value=excluded.identity_value,
                target_name=excluded.target_name,
                target_address=excluded.target_address;
            """;
        command.Parameters.AddWithValue("$mercury", AtiMercuryRegisterSerial);
        command.Parameters.AddWithValue("$mercuryPoint", AtiMercuryPointName);
        command.Parameters.AddWithValue("$appetit", AtiAppetitRegisterSerial);
        command.Parameters.AddWithValue("$appetitPoint", AtiAppetitPointName);
        command.Parameters.AddWithValue("$dvvs1", GarantDvvs1Serial);
        command.Parameters.AddWithValue("$dvvs2", GarantDvvs2Serial);
        command.Parameters.AddWithValue("$dvvsSpare", GarantDvvsSpareSerial);
        command.Parameters.AddWithValue("$dvvsPoint", GarantDvvsPointName);
        command.Parameters.AddWithValue("$sredSerial", GarantSredneuralskRegisterSerial);
        command.Parameters.AddWithValue("$sredPoint", GarantSredneuralskPointName);
        command.Parameters.AddWithValue("$sredAddress", GarantSredneuralskAddress);
        command.Parameters.AddWithValue("$samboSerial", GarantSamboRegisterSerial);
        command.Parameters.AddWithValue("$samboPoint", GarantSamboPointName);
        command.Parameters.AddWithValue("$samboAddress", GarantSamboAddress);
        command.ExecuteNonQuery();
        tx.Commit();
    }

    private static void EnsureGarantDvvsRegisters(Database database)
    {
        var organization = database.Organizations()
            .FirstOrDefault(x => DigitsOnly(x.TaxId) == GarantTaxId);
        if (organization is null) return;

        var locations = database.Locations()
            .Where(x => x.OrganizationId == organization.Id && x.IsActive)
            .ToArray();
        var dvvs = locations.FirstOrDefault(x => Normalize(x.Name) == Normalize(GarantDvvsPointName))
            ?? locations.FirstOrDefault(x =>
                Normalize(x.Address).Contains("универсиады", StringComparison.Ordinal) &&
                Normalize(x.Address).Contains("11", StringComparison.Ordinal));
        if (dvvs is null) return;

        var known = new[]
        {
            (Name: "ДВВС Касса 1", Serial: GarantDvvs1Serial, Rnm: GarantDvvs1Rnm),
            (Name: "ДВВС Касса 2", Serial: GarantDvvs2Serial, Rnm: GarantDvvs2Rnm),
            (Name: "Запасная ДВВС", Serial: GarantDvvsSpareSerial, Rnm: GarantDvvsSpareRnm)
        };

        var bindings = RegisterBindingService.Read(database, true);
        foreach (var item in known)
        {
            var current = bindings.FirstOrDefault(x =>
                x.OrganizationId == organization.Id &&
                x.IsActive &&
                (DigitsOnly(x.KktSerial) == item.Serial || DigitsOnly(x.RegisterNumber) == item.Rnm));

            if (current is null)
            {
                RegisterBindingService.Save(database, new RegisterBinding(
                    Guid.NewGuid(),
                    organization.Id,
                    dvvs.Id,
                    "",
                    item.Rnm,
                    BindingSource.Rule,
                    true,
                    null,
                    null,
                    item.Serial,
                    item.Name));
                continue;
            }

            if (current.LocationId == dvvs.Id &&
                current.DisplayName == item.Name &&
                DigitsOnly(current.KktSerial) == item.Serial &&
                DigitsOnly(current.RegisterNumber) == item.Rnm &&
                current.IsLocked)
                continue;

            RegisterBindingService.Save(database, current with
            {
                LocationId = dvvs.Id,
                KktSerial = item.Serial,
                RegisterNumber = item.Rnm,
                DisplayName = item.Name,
                BindingSource = current.BindingSource == BindingSource.Manual ? BindingSource.Manual : BindingSource.Rule,
                IsLocked = true
            });
        }
    }

    private static void EnsureGarantSredneuralsk(Database database)
    {
        var organization = database.Organizations()
            .FirstOrDefault(x => DigitsOnly(x.TaxId) == GarantTaxId);
        if (organization is null) return;

        var locations = database.Locations(true)
            .Where(x => x.OrganizationId == organization.Id)
            .ToArray();

        var point = locations.FirstOrDefault(x =>
                x.IsActive && Normalize(x.Name) == Normalize(GarantSredneuralskPointName))
            ?? locations.FirstOrDefault(x =>
                x.IsActive &&
                Normalize(x.Address).Contains("среднеуральск", StringComparison.Ordinal) &&
                Normalize(x.Address).Contains("набереж", StringComparison.Ordinal) &&
                Normalize(x.Address).Contains("8а", StringComparison.Ordinal));

        if (point is null)
        {
            point = new Location(
                Guid.NewGuid(),
                organization.Id,
                GarantSredneuralskPointName,
                GarantSredneuralskAddress);
            database.Save(point);
        }
        else if (Normalize(point.Name) != Normalize(GarantSredneuralskPointName) ||
                 string.IsNullOrWhiteSpace(point.Address))
        {
            point = point with
            {
                Name = GarantSredneuralskPointName,
                Address = string.IsNullOrWhiteSpace(point.Address) ? GarantSredneuralskAddress : point.Address
            };
            database.Save(point);
        }

        using var db = Open(database);
        using var tx = db.BeginTransaction();
        var now = DateTimeOffset.UtcNow.ToString("O");

        using (var binding = db.CreateCommand())
        {
            binding.Transaction = tx;
            binding.CommandText = """
                UPDATE register_bindings
                SET location_id=$loc,
                    kkt_serial=$serial,
                    register_number=$rnm,
                    display_name=$name,
                    binding_source='Rule',
                    is_locked=1,
                    updated_at=$now
                WHERE organization_id=$org AND is_active=1
                  AND (kkt_serial=$serial OR register_number=$rnm);
                """;
            binding.Parameters.AddWithValue("$loc", point.Id.ToString());
            binding.Parameters.AddWithValue("$serial", GarantSredneuralskRegisterSerial);
            binding.Parameters.AddWithValue("$rnm", GarantSredneuralskRnm);
            binding.Parameters.AddWithValue("$name", GarantSredneuralskPointName);
            binding.Parameters.AddWithValue("$now", now);
            binding.Parameters.AddWithValue("$org", organization.Id.ToString());
            var changed = binding.ExecuteNonQuery();

            if (changed == 0)
            {
                using var insert = db.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = """
                    INSERT INTO register_bindings(
                        id,organization_id,location_id,fn,register_number,binding_source,is_locked,
                        valid_from,valid_to,kkt_serial,display_name,created_at,updated_at,is_active)
                    VALUES($id,$org,$loc,'',$rnm,'Rule',1,NULL,NULL,$serial,$name,$now,$now,1);
                    """;
                insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
                insert.Parameters.AddWithValue("$org", organization.Id.ToString());
                insert.Parameters.AddWithValue("$loc", point.Id.ToString());
                insert.Parameters.AddWithValue("$rnm", GarantSredneuralskRnm);
                insert.Parameters.AddWithValue("$serial", GarantSredneuralskRegisterSerial);
                insert.Parameters.AddWithValue("$name", GarantSredneuralskPointName);
                insert.Parameters.AddWithValue("$now", now);
                insert.ExecuteNonQuery();
            }
        }

        foreach (var tid in new[] { GarantSredneuralskPosTid, GarantSredneuralskQrTid })
        {
            using var terminal = db.CreateCommand();
            terminal.Transaction = tx;
            terminal.CommandText = """
                INSERT INTO terminal_bindings(
                    id,organization_id,location_id,provider,tid,mid,payment_method,
                    binding_source,is_locked,valid_from,valid_to)
                VALUES($id,$org,$loc,'Sber',$tid,'',$method,'Rule',1,NULL,NULL)
                ON CONFLICT(organization_id,provider,tid) DO UPDATE SET
                    location_id=excluded.location_id,
                    payment_method=excluded.payment_method,
                    binding_source='Rule',
                    is_locked=1;
                """;
            terminal.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
            terminal.Parameters.AddWithValue("$org", organization.Id.ToString());
            terminal.Parameters.AddWithValue("$loc", point.Id.ToString());
            terminal.Parameters.AddWithValue("$tid", tid);
            terminal.Parameters.AddWithValue("$method", tid == GarantSredneuralskQrTid ? "QR" : "POS");
            terminal.ExecuteNonQuery();
        }

        using (var audit = db.CreateCommand())
        {
            audit.Transaction = tx;
            audit.CommandText = """
                INSERT INTO audit_log(occurred_at,action,details)
                SELECT $now,'business_rule.garant_sredneuralsk',$details
                WHERE NOT EXISTS(
                    SELECT 1 FROM audit_log
                    WHERE action='business_rule.garant_sredneuralsk' AND details=$details
                );
                """;
            audit.Parameters.AddWithValue("$now", now);
            audit.Parameters.AddWithValue("$details",
                $"point={point.Id}; KKT={GarantSredneuralskRegisterSerial}; RNM={GarantSredneuralskRnm}; TID={GarantSredneuralskPosTid},{GarantSredneuralskQrTid}");
            audit.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private static void EnsureGarantSambo(Database database)
    {
        var organization = database.Organizations()
            .FirstOrDefault(x => DigitsOnly(x.TaxId) == GarantTaxId);
        if (organization is null) return;

        var locations = database.Locations(true)
            .Where(x => x.OrganizationId == organization.Id)
            .ToArray();

        var point = locations.FirstOrDefault(x =>
                x.IsActive && Normalize(x.Name) == Normalize(GarantSamboPointName))
            ?? locations.FirstOrDefault(x =>
            {
                if (!x.IsActive) return false;
                var name = Normalize(x.Name);
                var address = Normalize(x.Address);
                return name.Contains("самбо", StringComparison.Ordinal) ||
                       (address.Contains("горняков", StringComparison.Ordinal) && address.Contains("1б", StringComparison.Ordinal)) ||
                       (address.Contains("успен", StringComparison.Ordinal) &&
                        (address.Contains("2а", StringComparison.Ordinal) || address.EndsWith("2", StringComparison.Ordinal)));
            });

        if (point is null)
        {
            point = new Location(
                Guid.NewGuid(),
                organization.Id,
                GarantSamboPointName,
                GarantSamboAddress);
            database.Save(point);
        }
        else if (Normalize(point.Name) != Normalize(GarantSamboPointName))
        {
            point = point with { Name = GarantSamboPointName };
            database.Save(point);
        }

        using var db = Open(database);
        using var tx = db.BeginTransaction();
        var now = DateTimeOffset.UtcNow.ToString("O");

        using (var binding = db.CreateCommand())
        {
            binding.Transaction = tx;
            binding.CommandText = """
                UPDATE register_bindings
                SET location_id=$loc,
                    kkt_serial=$serial,
                    register_number=$rnm,
                    display_name=$name,
                    binding_source='Rule',
                    is_locked=1,
                    updated_at=$now
                WHERE organization_id=$org AND is_active=1
                  AND (kkt_serial=$serial OR register_number=$rnm)
                  AND binding_source<>'Manual';
                """;
            binding.Parameters.AddWithValue("$loc", point.Id.ToString());
            binding.Parameters.AddWithValue("$serial", GarantSamboRegisterSerial);
            binding.Parameters.AddWithValue("$rnm", GarantSamboRnm);
            binding.Parameters.AddWithValue("$name", GarantSamboPointName);
            binding.Parameters.AddWithValue("$now", now);
            binding.Parameters.AddWithValue("$org", organization.Id.ToString());
            var changed = binding.ExecuteNonQuery();

            if (changed == 0)
            {
                using var insert = db.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = """
                    INSERT INTO register_bindings(
                        id,organization_id,location_id,fn,register_number,binding_source,is_locked,
                        valid_from,valid_to,kkt_serial,display_name,created_at,updated_at,is_active)
                    SELECT $id,$org,$loc,'',$rnm,'Rule',1,NULL,NULL,$serial,$name,$now,$now,1
                    WHERE NOT EXISTS(
                        SELECT 1 FROM register_bindings
                        WHERE organization_id=$org AND is_active=1
                          AND (kkt_serial=$serial OR register_number=$rnm)
                    );
                    """;
                insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
                insert.Parameters.AddWithValue("$org", organization.Id.ToString());
                insert.Parameters.AddWithValue("$loc", point.Id.ToString());
                insert.Parameters.AddWithValue("$rnm", GarantSamboRnm);
                insert.Parameters.AddWithValue("$serial", GarantSamboRegisterSerial);
                insert.Parameters.AddWithValue("$name", GarantSamboPointName);
                insert.Parameters.AddWithValue("$now", now);
                insert.ExecuteNonQuery();
            }
        }

        var moved = 0;
        using (var shifts = db.CreateCommand())
        {
            shifts.Transaction = tx;
            shifts.CommandText = """
                UPDATE shift_closures
                SET location_id=$loc
                WHERE organization_id=$org
                  AND kkt_serial=$serial
                  AND (location_id IS NULL OR location_id<>$loc);
                """;
            shifts.Parameters.AddWithValue("$loc", point.Id.ToString());
            shifts.Parameters.AddWithValue("$org", organization.Id.ToString());
            shifts.Parameters.AddWithValue("$serial", GarantSamboRegisterSerial);
            moved += shifts.ExecuteNonQuery();
        }

        using (var operations = db.CreateCommand())
        {
            operations.Transaction = tx;
            operations.CommandText = """
                UPDATE operations
                SET location_id=$loc
                WHERE organization_id=$org
                  AND source_kind<>'Bank'
                  AND kkt_serial=$serial
                  AND (location_id IS NULL OR location_id<>$loc);
                """;
            operations.Parameters.AddWithValue("$loc", point.Id.ToString());
            operations.Parameters.AddWithValue("$org", organization.Id.ToString());
            operations.Parameters.AddWithValue("$serial", GarantSamboRegisterSerial);
            moved += operations.ExecuteNonQuery();
        }

        // Legacy shift-derived rows can have an empty kkt_serial; follow the repaired parent shift.
        using (var legacy = db.CreateCommand())
        {
            legacy.Transaction = tx;
            legacy.CommandText = """
                UPDATE operations
                SET location_id=$loc
                WHERE organization_id=$org
                  AND source_kind<>'Bank'
                  AND EXISTS(
                      SELECT 1 FROM shift_closures s
                      WHERE s.organization_id=operations.organization_id
                        AND s.source=operations.source
                        AND s.kkt_serial=$serial
                        AND operations.external_id IN(s.external_id||':cash',s.external_id||':electronic')
                  )
                  AND (location_id IS NULL OR location_id<>$loc);
                """;
            legacy.Parameters.AddWithValue("$loc", point.Id.ToString());
            legacy.Parameters.AddWithValue("$org", organization.Id.ToString());
            legacy.Parameters.AddWithValue("$serial", GarantSamboRegisterSerial);
            moved += legacy.ExecuteNonQuery();
        }

        if (moved > 0)
        {
            using var reset = db.CreateCommand();
            reset.Transaction = tx;
            reset.CommandText = "DELETE FROM reconciliation_allocations;";
            reset.ExecuteNonQuery();
        }

        using (var audit = db.CreateCommand())
        {
            audit.Transaction = tx;
            audit.CommandText = """
                INSERT INTO audit_log(occurred_at,action,details)
                SELECT $now,'business_rule.garant_sambo',$details
                WHERE NOT EXISTS(
                    SELECT 1 FROM audit_log
                    WHERE action='business_rule.garant_sambo' AND details=$details
                );
                """;
            audit.Parameters.AddWithValue("$now", now);
            audit.Parameters.AddWithValue("$details",
                $"point={point.Id}; KKT={GarantSamboRegisterSerial}; RNM={GarantSamboRnm}; moved={moved}; address={GarantSamboAddress}");
            audit.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private static void RepairGarantSredneuralskLegacyMayBankRows(Database database)
    {
        var organization = database.Organizations()
            .FirstOrDefault(x => DigitsOnly(x.TaxId) == GarantTaxId);
        if (organization is null) return;

        var locations = database.Locations()
            .Where(x => x.OrganizationId == organization.Id && x.IsActive)
            .ToArray();
        var dvvs = locations.FirstOrDefault(x => Normalize(x.Name) == Normalize(GarantDvvsPointName));
        var sred = locations.FirstOrDefault(x => Normalize(x.Name) == Normalize(GarantSredneuralskPointName));
        if (dvvs is null || sred is null) return;

        database.EnsureManualTerminalPostings();

        // Verified against the fresh Sber May export (30.09.2026).
        // These 32 rows are the Среднеуральск terminal stream that an older database
        // could have placed under ДВВС before TID 34771891/34771897 had a hard point rule.
        // Time + amount is unique for every row inside ДВВС on 11.05.2026.
        var rows = new (string Time, long Kopecks)[]
        {
            ("2026-05-11T16:17:37", 37000L),
            ("2026-05-11T16:27:23", 111000L),
            ("2026-05-11T16:29:59", 39000L),
            ("2026-05-11T16:30:25", 10000L),
            ("2026-05-11T16:31:52", 15000L),
            ("2026-05-11T16:37:37", 38000L),
            ("2026-05-11T16:39:37", 7000L),
            ("2026-05-11T16:39:59", 7000L),
            ("2026-05-11T16:40:48", 44000L),
            ("2026-05-11T16:41:18", 7000L),
            ("2026-05-11T16:42:02", 27000L),
            ("2026-05-11T16:42:37", 7000L),
            ("2026-05-11T16:45:32", 10000L),
            ("2026-05-11T16:47:11", 10000L),
            ("2026-05-11T16:48:01", 16000L),
            ("2026-05-11T16:49:09", 5000L),
            ("2026-05-11T16:49:34", 7000L),
            ("2026-05-11T16:50:31", 26000L),
            ("2026-05-11T17:59:06", 7000L),
            ("2026-05-11T17:59:46", 14000L),
            ("2026-05-11T18:00:41", 10000L),
            ("2026-05-11T18:00:59", 7000L),
            ("2026-05-11T18:01:22", 10000L),
            ("2026-05-11T18:02:09", 15000L),
            ("2026-05-11T18:03:17", 23000L),
            ("2026-05-11T18:03:42", 12000L),
            ("2026-05-11T18:04:28", 67000L),
            ("2026-05-11T18:04:48", 12000L),
            ("2026-05-11T18:05:05", 12000L),
            ("2026-05-11T18:08:10", 36000L),
            ("2026-05-11T18:10:15", 3000L),
            ("2026-05-11T18:11:34", 32000L)
        };

        using var db = Open(database);
        using var tx = db.BeginTransaction();
        var moved = 0;

        foreach (var row in rows)
        {
            using var command = db.CreateCommand();
            command.Transaction = tx;
            command.CommandText = """
                UPDATE operations
                SET location_id=$sred
                WHERE source='Sber.Acquiring'
                  AND source_kind='Bank'
                  AND organization_id=$org
                  AND location_id=$dvvs
                  AND substr(occurred_at,1,19)=$time
                  AND amount_kopecks=$amount;
                """;
            command.Parameters.AddWithValue("$sred", sred.Id.ToString());
            command.Parameters.AddWithValue("$org", organization.Id.ToString());
            command.Parameters.AddWithValue("$dvvs", dvvs.Id.ToString());
            command.Parameters.AddWithValue("$time", row.Time);
            command.Parameters.AddWithValue("$amount", row.Kopecks);
            moved += command.ExecuteNonQuery();
        }

        // Remove the temporary manual workaround from earlier builds. The real bank rows
        // above are the source of truth now.
        using (var clearManual = db.CreateCommand())
        {
            clearManual.Transaction = tx;
            clearManual.CommandText = """
                DELETE FROM manual_terminal_postings
                WHERE organization_id=$org AND location_id=$sred
                  AND business_date IN('2026-03-26','2026-03-28','2026-03-29','2026-05-11');
                """;
            clearManual.Parameters.AddWithValue("$org", organization.Id.ToString());
            clearManual.Parameters.AddWithValue("$sred", sred.Id.ToString());
            clearManual.ExecuteNonQuery();
        }

        if (moved > 0)
        {
            using var audit = db.CreateCommand();
            audit.Transaction = tx;
            audit.CommandText = """
                INSERT INTO audit_log(occurred_at,action,details)
                VALUES($now,'business_rule.garant_sredneuralsk_may_repair',$details);
                """;
            audit.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            audit.Parameters.AddWithValue("$details",
                $"moved={moved}; date=2026-05-11; amount=6830.00; from={dvvs.Id}; to={sred.Id}; source=fresh Sber export");
            audit.ExecuteNonQuery();
        }

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
