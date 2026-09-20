using KopCashDesk.Core;

namespace KopCashDesk.Data;

public static class KnownPointDirectory
{
    private static readonly IReadOnlyDictionary<string, string> Addresses =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [KnownBusinessRules.ReftinskayaPointName] = "п. Рефтинский, Рефтинская ГРЭС, здание 6",
            [KnownBusinessRules.LadyzhenskogoPointName] = "г. Асбест, ул. Ладыженского, 7",
            [KnownBusinessRules.MiraPointName] = "г. Асбест, ул. Мира, 4",
            [KnownBusinessRules.AtiAppetitPointName] = "г. Асбест, ул. Плеханова, 62",
            [KnownBusinessRules.AtiMercuryPointName] = "г. Асбест, ул. Плеханова, 64",
            [KnownBusinessRules.ChapaevaPointName] = "г. Асбест, ул. Чапаева, 28",
            [KnownBusinessRules.MusicCollegePointName] = "г. Асбест, ул. Войкова, 62"
        };

    public static string AddressFor(string pointName)
        => Addresses.TryGetValue(pointName, out var address) ? address : string.Empty;

    public static int Apply(Database database)
    {
        var organization = database.Organizations().FirstOrDefault(x => Digits(x.TaxId) == KnownOrganizations.CenterTaxId);
        if (organization is null) return 0;

        var changed = 0;
        foreach (var location in database.Locations().Where(x => x.OrganizationId == organization.Id && x.IsActive).ToArray())
        {
            if (!Addresses.TryGetValue(location.Name, out var address) || location.Address == address) continue;
            database.Save(location with { Address = address });
            changed++;
        }
        return changed;
    }

    private static string Digits(string value) => new((value ?? string.Empty).Where(char.IsDigit).ToArray());
}
