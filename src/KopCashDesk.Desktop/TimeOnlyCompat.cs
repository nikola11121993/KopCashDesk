namespace KopCashDesk.Desktop;

// Keep a readable noon constant without breaking existing uses of TimeOnly.MinValue/MaxValue
// elsewhere in this namespace. All properties return the BCL System.TimeOnly type.
internal static class TimeOnly
{
    public static System.TimeOnly MinValue => System.TimeOnly.MinValue;
    public static System.TimeOnly MaxValue => System.TimeOnly.MaxValue;
    public static System.TimeOnly Noon { get; } = new(12, 0);
}
