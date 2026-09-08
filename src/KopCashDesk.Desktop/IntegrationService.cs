using KopCashDesk.Core;
using KopCashDesk.Data;
using System.Text.Json;

namespace KopCashDesk.Desktop;

public sealed class IntegrationService
{
    private readonly Database _database;
    private readonly string _dataDirectory;
    public IntegrationService(Database database, string dataDirectory) { _database = database; _dataDirectory = dataDirectory; }
    public static bool IsSafeSettings(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            return !ContainsSecret(doc.RootElement);
        }
        catch (JsonException) { return false; }
    }
    private static bool ContainsSecret(JsonElement element)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Contains("password", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("secret", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("token", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("apiKey", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("privateKey", StringComparison.OrdinalIgnoreCase)) return true;
            if (property.Value.ValueKind == JsonValueKind.Object && ContainsSecret(property.Value)) return true;
        }
        return false;
    }
    public Task<IntegrationCheck> CheckAsync(IntegrationProfile profile, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(profile.Kind switch
        {
            IntegrationKind.Taxcom => new IntegrationCheck(false, "Такском: параметры сохранены. Реальная авторизация будет доступна после получения Integrator-ID и проверки официальной схемы API. Сетевой запрос не выполнялся."),
            IntegrationKind.AstralOfd => new IntegrationCheck(false, "Астрал ОФД: параметры сохранены. Для подключения необходима официальная документация и разрешённый доступ. Сетевой запрос не выполнялся."),
            IntegrationKind.Email => new IntegrationCheck(false, "Почтовый профиль сохранён. Проверка IMAP и получение вложений будут добавлены на следующем этапе. Сетевой запрос не выполнялся."),
            _ => new IntegrationCheck(false, "Неизвестный провайдер")
        });
    }
}
