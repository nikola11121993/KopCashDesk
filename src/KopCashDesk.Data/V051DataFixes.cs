namespace KopCashDesk.Data;

public static class V051DataFixes
{
    // Compatibility entry point: preserve observations and derive canonical links, never delete source data.
    public static int EnsureV051Fixes(this Database database)
    {
        var beforeLinks = database.CrossSourceShiftLinks().Count;
        var result = database.RebuildCrossSourceShiftMatches();
        var afterLinks = database.CrossSourceShiftLinks().Count;
        return Math.Max(0, afterLinks - beforeLinks) + result.NewConflicts;
    }
}
