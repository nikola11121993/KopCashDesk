using Microsoft.Data.Sqlite;
using System.Globalization;

namespace KopCashDesk.Data;

public sealed record GarantDvvsRegisterDaySummary(
    Guid OrganizationId,
    Guid LocationId,
    DateOnly Date,
    decimal Kassa1Electronic,
    decimal Kassa2Electronic,
    decimal SpareElectronic)
{
    public decimal TotalElectronic => Money.Normalize(Kassa1Electronic + Kassa2Electronic + SpareElectronic);
}

public static class GarantDvvsRegisterSummaryExtensions
{
    public static IReadOnlyList<GarantDvvsRegisterDaySummary> GarantDvvsRegisterDaySummaries(
        this Database database,
        int year,
        int? month = null,
        Guid? selectedOrganizationId = null,
        Guid? selectedLocationId = null)
    {
        var organization = database.Organizations()
            .FirstOrDefault(x =>
                (selectedOrganizationId is null || x.Id == selectedOrganizationId) &&
                DigitsOnly(x.TaxId) == KnownBusinessRules.GarantTaxId);
        if (organization is null) return [];

        var locations = database.Locations()
            .Where(x => x.OrganizationId == organization.Id && x.IsActive)
            .ToArray();

        var location = selectedLocationId is Guid selected
            ? locations.FirstOrDefault(x => x.Id == selected)
            : locations.FirstOrDefault(x => Normalize(x.Name) == Normalize(KnownBusinessRules.GarantDvvsPointName))
              ?? locations.FirstOrDefault(x =>
                  Normalize(x.Address).Contains("универсиады", StringComparison.Ordinal) &&
                  Normalize(x.Address).Contains("11", StringComparison.Ordinal));

        if (location is null || !IsDvvs(location)) return [];

        var from = new DateOnly(year, month ?? 1, 1);
        var to = month is null ? from.AddYears(1) : from.AddMonths(1);

        using var db = Open(database);
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT
                substr(occurred_at,1,10) AS business_date,
                COALESCE(SUM(CASE
                    WHEN (kkt_serial=$serial1 OR (kkt_serial='' AND registration_number=$rnm1))
                         AND payment='Electronic'
                    THEN amount_kopecks ELSE 0 END),0) AS kassa1,
                COALESCE(SUM(CASE
                    WHEN (kkt_serial=$serial2 OR (kkt_serial='' AND registration_number=$rnm2))
                         AND payment='Electronic'
                    THEN amount_kopecks ELSE 0 END),0) AS kassa2,
                COALESCE(SUM(CASE
                    WHEN (kkt_serial=$spareSerial OR (kkt_serial='' AND registration_number=$spareRnm))
                         AND payment='Electronic'
                    THEN amount_kopecks ELSE 0 END),0) AS spare
            FROM operations
            WHERE organization_id=$org
              AND location_id=$loc
              AND source='Taxcom.FiscalDocuments'
              AND source_kind='Fiscal'
              AND occurred_at >= $from
              AND occurred_at < $to
              AND (
                    kkt_serial IN($serial1,$serial2,$spareSerial)
                    OR (kkt_serial='' AND registration_number IN($rnm1,$rnm2,$spareRnm))
              )
            GROUP BY substr(occurred_at,1,10)
            ORDER BY business_date;
            """;
        command.Parameters.AddWithValue("$org", organization.Id.ToString());
        command.Parameters.AddWithValue("$loc", location.Id.ToString());
        command.Parameters.AddWithValue("$from", from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$to", to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$serial1", KnownBusinessRules.GarantDvvs1Serial);
        command.Parameters.AddWithValue("$rnm1", KnownBusinessRules.GarantDvvs1Rnm);
        command.Parameters.AddWithValue("$serial2", KnownBusinessRules.GarantDvvs2Serial);
        command.Parameters.AddWithValue("$rnm2", KnownBusinessRules.GarantDvvs2Rnm);
        command.Parameters.AddWithValue("$spareSerial", KnownBusinessRules.GarantDvvsSpareSerial);
        command.Parameters.AddWithValue("$spareRnm", KnownBusinessRules.GarantDvvsSpareRnm);

        using var reader = command.ExecuteReader();
        var result = new List<GarantDvvsRegisterDaySummary>();
        while (reader.Read())
        {
            var date = DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture);
            result.Add(new(
                organization.Id,
                location.Id,
                date,
                Money.FromKopecks(reader.GetInt64(1)),
                Money.FromKopecks(reader.GetInt64(2)),
                Money.FromKopecks(reader.GetInt64(3))));
        }

        return result;
    }

    public static bool IsGarantDvvs(this Database database, Guid? organizationId, Guid? locationId)
    {
        if (organizationId is null || locationId is null) return false;
        var organization = database.Organizations().FirstOrDefault(x => x.Id == organizationId);
        if (organization is null || DigitsOnly(organization.TaxId) != KnownBusinessRules.GarantTaxId) return false;
        var location = database.Locations().FirstOrDefault(x => x.Id == locationId && x.OrganizationId == organizationId);
        return location is not null && IsDvvs(location);
    }

    private static bool IsDvvs(KopCashDesk.Core.Location location) =>
        Normalize(location.Name) == Normalize(KnownBusinessRules.GarantDvvsPointName) ||
        (Normalize(location.Address).Contains("универсиады", StringComparison.Ordinal) &&
         Normalize(location.Address).Contains("11", StringComparison.Ordinal));

    private static SqliteConnection Open(Database database)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true
        }.ToString());
        connection.Open();
        using var pragmas = connection.CreateCommand();
        pragmas.CommandText = """
            PRAGMA busy_timeout=5000;
            PRAGMA temp_store=MEMORY;
            PRAGMA cache_size=-32768;
            PRAGMA mmap_size=268435456;
            """;
        pragmas.ExecuteNonQuery();
        return connection;
    }

    private static string DigitsOnly(string value) => new((value ?? string.Empty).Where(char.IsDigit).ToArray());
    private static string Normalize(string value) => new((value ?? string.Empty).Trim().ToLowerInvariant().Replace('ё', 'е').Where(char.IsLetterOrDigit).ToArray());
}
