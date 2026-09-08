using System.Text.Json;

namespace KopCashDesk.Desktop;

public sealed record TaxcomConnectionSettings(string Server, string IntegratorId)
{
    public static TaxcomConnectionSettings FromJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        static string Read(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        return new(Read(root, "server"), Read(root, "integratorId"));
    }

    public static bool TryNormalizeServer(string value, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var candidate)) return false;
        if (candidate.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(candidate.UserInfo) || !string.IsNullOrEmpty(candidate.Query) || !string.IsNullOrEmpty(candidate.Fragment)) return false;
        if (candidate.AbsolutePath.Trim('/') is not "") return false;
        var host = candidate.IdnHost;
        if (!host.Equals("taxcom.ru", StringComparison.OrdinalIgnoreCase) && !host.EndsWith(".taxcom.ru", StringComparison.OrdinalIgnoreCase)) return false;
        uri = new UriBuilder(candidate) { Path = "/", Query = "", Fragment = "" }.Uri;
        return true;
    }
}
