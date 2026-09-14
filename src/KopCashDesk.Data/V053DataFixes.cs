namespace KopCashDesk.Data;

public static class V053DataFixes
{
    // Compatibility shim for 0.5.3. The former implementation deleted duplicate source rows.
    // Current behavior preserves Taxcom and Frontol observations and links them safely.
    public static int EnsureV053Fixes(this Database database)
    {
        var result = database.RebuildCrossSourceShiftMatches();
        return result.NewMatches + result.NewConflicts;
    }
}
