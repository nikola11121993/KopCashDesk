using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KopCashDesk.Desktop;

public sealed record TaxcomCredentials(string Login, string Password);

public sealed class TaxcomCredentialStore
{
    private readonly string _directory;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("KopCashDesk.Taxcom.Credentials.v1");

    public TaxcomCredentialStore(string dataDirectory)
    {
        _directory = Path.Combine(dataDirectory, "secrets");
    }

    private string FileName(Guid profileId) => Path.Combine(_directory, $"taxcom-{profileId:N}.bin");

    public bool HasCredentials(Guid profileId) => File.Exists(FileName(profileId));

    public void Save(Guid profileId, TaxcomCredentials credentials)
    {
        if (profileId == Guid.Empty || string.IsNullOrWhiteSpace(credentials.Login) || string.IsNullOrEmpty(credentials.Password))
            throw new ArgumentException("Укажите логин и пароль.");
        Directory.CreateDirectory(_directory);
        var plain = JsonSerializer.SerializeToUtf8Bytes(credentials);
        byte[]? encrypted = null;
        try
        {
            encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            var target = FileName(profileId);
            var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(encrypted);
                    stream.Flush(true);
                }
                File.Move(temp, target, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    public TaxcomCredentials? Load(Guid profileId)
    {
        var path = FileName(profileId);
        if (!File.Exists(path)) return null;
        var encrypted = File.ReadAllBytes(path);
        byte[]? plain = null;
        try
        {
            plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<TaxcomCredentials>(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            if (plain is not null) CryptographicOperations.ZeroMemory(plain);
        }
    }

    public void Delete(Guid profileId)
    {
        if (File.Exists(FileName(profileId))) File.Delete(FileName(profileId));
    }
}
