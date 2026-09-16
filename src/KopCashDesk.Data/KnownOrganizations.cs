using KopCashDesk.Core;

namespace KopCashDesk.Data;

public static class KnownOrganizations
{
    public const string CenterTaxId = "6683009222";
    public const string GarantTaxId = "6683011158";
    public const string KopTaxId = "6603017238";

    public static IReadOnlyList<(string Name, string TaxId)> All { get; } =
    [
        ("ООО \"ЦОП\"", CenterTaxId),
        ("ООО \"ГАРАНТ\"", GarantTaxId),
        ("ООО \"КОП\"", KopTaxId)
    ];

    public static int Ensure(Database database)
    {
        var organizations = database.Organizations().ToList();
        var created = 0;

        foreach (var (name, taxId) in All)
        {
            if (organizations.Any(x => Digits(x.TaxId) == taxId)) continue;
            var organization = new Organization(Guid.NewGuid(), name, taxId);
            database.Save(organization);
            organizations.Add(organization);
            created++;
        }

        if (created > 0)
            database.Audit("organization.known.ensure", $"created={created}; expected={All.Count}");

        return created;
    }

    private static string Digits(string value) => new((value ?? string.Empty).Where(char.IsDigit).ToArray());
}
