using KopCashDesk.Core;

namespace KopCashDesk.Data;

public static class TerminalLocationResolver
{
    public const string Gres6Tid = "34773474";
    public const string Gres6Mid = "191000238983";

    public static bool IsGres6(string provider, string tid) =>
        provider.Equals("Sber", StringComparison.OrdinalIgnoreCase) && tid == Gres6Tid;

    public static TerminalBinding? Resolve(Database database, Guid organizationId, string provider,
        string tid, string mid, DateOnly day, out bool requiresReview)
    {
        requiresReview = false;
        var history = database.TerminalBindings().Where(x => x.OrganizationId == organizationId &&
            x.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase)).ToArray();
        bool Valid(TerminalBinding x) => (x.ValidFrom is null || x.ValidFrom <= day) &&
            (x.ValidTo is null || x.ValidTo >= day);
        var tidRows = history.Where(x => x.TerminalId == tid).ToArray();
        var valid = tidRows.Where(Valid).ToArray();
        var manual = valid.Where(x => x.BindingSource == BindingSource.Manual || x.IsLocked).ToArray();
        TerminalBinding? Unique(TerminalBinding[] rows)
        {
            if (rows.Select(x => x.LocationId).Distinct().Count() != 1) return null;
            return rows.OrderByDescending(x => x.BindingSource).First();
        }
        if (manual.Length > 0)
        {
            var selected = Unique(manual);
            requiresReview = selected is null;
            return selected;
        }
        if (IsGres6(provider, tid))
        {
            var target = KnownBusinessRules.FindKnownPoint(database.Locations().Where(x => x.OrganizationId == organizationId), KnownBusinessRules.ReftinskayaPointName);
            if (target is null) { requiresReview = true; return null; }
            var existing = valid.FirstOrDefault(x => x.LocationId == target.Id && x.BindingSource == BindingSource.Rule);
            if (existing is not null) return existing;
            // A separate dated rule must never erase manual history outside this date.
            var rule = new TerminalBinding(Guid.NewGuid(), organizationId, target.Id, provider, tid,
                Gres6Mid, "SBP", BindingSource.Rule, false, day, day);
            database.Save(rule);
            database.Audit("terminal.rule", $"tid={tid}; mid={Gres6Mid}; new_loc={target.Id}; date={day}; source=Rule; reason=Known GRES6 terminal");
            return rule;
        }
        if (valid.Length > 0)
        {
            var strongest = valid.Max(x => x.BindingSource);
            var selected = Unique(valid.Where(x => x.BindingSource == strongest).ToArray());
            requiresReview = selected is null;
            return selected;
        }
        if (tidRows.Length > 0) { requiresReview = true; return null; }
        if (mid.Length > 0)
        {
            var midRows = history.Where(x => x.MerchantId == mid && Valid(x)).ToArray();
            if (midRows.Length > 0)
            {
                var selected = Unique(midRows);
                requiresReview = selected is null;
                return selected;
            }
        }
        return null;
    }
}
