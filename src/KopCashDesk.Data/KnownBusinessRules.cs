using KopCashDesk.Core;
using Microsoft.Data.Sqlite;

namespace KopCashDesk.Data;

public static class KnownBusinessRules
{
    public const string ReftinskayaRegisterSerial = "00106900361561";
    public const string ReftinskayaPointName = "Рефтинская ГРЭС 6 столовая";

    public static string? PointNameForRegisterSerial(string serial)
    {
        var digits = DigitsOnly(serial);
        return digits == ReftinskayaRegisterSerial ? ReftinskayaPointName : null;
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
        var applied = 0;
        foreach (var organization in database.Organizations())
        {
            applied += TryAlias(database, organization.Id, "Вороний Брод", "Мира 4", "known.voroniy-to-mira", "касса перемещалась: Вороний Брод -> Ленинградская 1 -> Мира 4");
            applied += TryAlias(database, organization.Id, "Ленинградская 1", "Мира 4", "known.leningradskaya-to-mira", "касса перемещалась: Вороний Брод -> Ленинградская 1 -> Мира 4");
            applied += TryAlias(database, organization.Id, "Буфет", "Чапаева 28", "known.bufet-to-chapaeva28", "одна физическая точка по адресу Чапаева 28");
            applied += TryReftinskayaDuplicate(database, organization.Id);
        }
        return applied;
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
            if (database.MergeLocations(source.Id, target.Id, $"правило ККТ {ReftinskayaRegisterSerial} = {ReftinskayaPointName}")) merged++;

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
