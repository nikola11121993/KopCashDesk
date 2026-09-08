using KopCashDesk.Core;
using KopCashDesk.Data;
using System.Text.Json;

namespace KopCashDesk.Desktop;

public sealed class IntegrationService
{
    private readonly TaxcomCredentialStore _secrets;
    public IntegrationService(Database database, string dataDirectory)
    {
        _secrets = new TaxcomCredentialStore(dataDirectory);
    }

    public static bool IsSafeSettings(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && !ContainsSecret(doc.RootElement);
        }
        catch (JsonException) { return false; }
    }

    private static bool ContainsSecret(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
            return element.EnumerateArray().Any(ContainsSecret);
        if (element.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in element.EnumerateObject())
        {
            if (new[] { "password", "secret", "token", "apiKey", "privateKey", "credential", "authorization", "passwd", "pwd" }
                .Any(x => property.Name.Contains(x, StringComparison.OrdinalIgnoreCase))) return true;
            if (ContainsSecret(property.Value)) return true;
        }
        return false;
    }

    public Task<IntegrationCheck> CheckAsync(IntegrationProfile profile, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(profile.Kind switch
        {
            IntegrationKind.Taxcom => CheckTaxcom(profile),
            IntegrationKind.AstralOfd => new IntegrationCheck(false, "Астрал ОФД: сетевой адаптер ещё не подключён."),
            IntegrationKind.Email => new IntegrationCheck(false, "Почта: сетевой адаптер ещё не подключён."),
            _ => new IntegrationCheck(false, "Неизвестный провайдер")
        });
    }

    private IntegrationCheck CheckTaxcom(IntegrationProfile profile)
    {
        try
        {
            var credentials = _secrets.Load(profile.Id);
            if (credentials is null || string.IsNullOrWhiteSpace(credentials.Login) || string.IsNullOrEmpty(credentials.Password))
                return new(false, "Такском: сначала сохраните логин и пароль в настройках.");
            var settings = TaxcomConnectionSettings.FromJson(profile.SettingsJson);
            if (string.IsNullOrWhiteSpace(settings.IntegratorId))
                return new(false, "Логин и пароль сохранены. Для подключения API укажите Integrator-ID, выданный Такском.");
            if (!TaxcomConnectionSettings.TryNormalizeServer(settings.Server, out _))
                return new(false, "Логин и пароль сохранены. Укажите корректный HTTPS-адрес сервера Такском.");
            return new(false, "Параметры сохранены. Сетевая авторизация API в этой версии ещё не реализована.");
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or JsonException)
        {
            return new(false, "Не удалось прочитать параметры подключения. Проверьте защищённое хранилище Windows.");
        }
    }
}
