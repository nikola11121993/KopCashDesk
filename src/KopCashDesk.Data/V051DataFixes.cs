namespace KopCashDesk.Data;

public static class V051DataFixes
{
    // Compatibility entry point: preserve observations and derive canonical links, never delete source data.
    public static int EnsureV051Fixes(this Database database)
    {
        var result = database.RebuildCrossSourceShiftMatches();
        return result.NewMatches + result.NewConflicts;
    }
}
