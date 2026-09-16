namespace KopCashDesk.Desktop;

// .NET's TimeOnly type has MinValue/MaxValue but no Noon constant.
// Keep the call site readable while returning the BCL TimeOnly value expected by DateOnly/DateTimeOffset APIs.
internal static class TimeOnly
{
    public static System.TimeOnly Noon { get; } = new(12, 0);
}
