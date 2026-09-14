namespace KopCashDesk.Data;

public static class V052DataFixes
{
    // Kept for compatibility with older call sites. Cross-source duplicates are no longer deleted:
    // both observations remain in the database and are linked by the current matcher.
    public static int EnsureV052Fixes(this Database database)
    {
        var result = database.RebuildCrossSourceShiftMatches();
        return result.NewMatches + result.NewConflicts;
    }
}
