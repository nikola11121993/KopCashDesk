using KopCashDesk.Desktop;
using System.Text;
using Xunit;

namespace KopCashDesk.Tests;

public sealed class TaxcomSecurityTests
{
    [Fact]
    public void Credentials_RoundTrip_IsEncryptedAndIsolated()
    {
        var directory = Directory.CreateTempSubdirectory("kopcashdesk-security-").FullName;
        try
        {
            var store = new TaxcomCredentialStore(directory);
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();
            var password = "unit-test-" + Guid.NewGuid().ToString("N");
            store.Save(first, new TaxcomCredentials("test-login", password));
            Assert.True(store.HasCredentials(first));
            Assert.False(store.HasCredentials(second));
            Assert.Null(store.Load(second));
            Assert.Equal("test-login", store.Load(first)!.Login);
            Assert.Equal(password, store.Load(first)!.Password);
            var bytes = File.ReadAllBytes(Path.Combine(directory, "secrets", $"taxcom-{first:N}.bin"));
            Assert.DoesNotContain(password, Encoding.UTF8.GetString(bytes));
            store.Save(second, new TaxcomCredentials("second", "another-test-password"));
            store.Delete(first);
            Assert.Null(store.Load(first));
            Assert.NotNull(store.Load(second));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Credentials_RejectEmptyValues()
    {
        var directory = Directory.CreateTempSubdirectory("kopcashdesk-security-").FullName;
        try
        {
            var store = new TaxcomCredentialStore(directory);
            Assert.Throws<ArgumentException>(() => store.Save(Guid.NewGuid(), new TaxcomCredentials("", "password")));
            Assert.Throws<ArgumentException>(() => store.Save(Guid.NewGuid(), new TaxcomCredentials("login", "")));
            Assert.Throws<ArgumentException>(() => store.Save(Guid.Empty, new TaxcomCredentials("login", "password")));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("https://tlk-ofd.taxcom.ru", true)]
    [InlineData("https://api.taxcom.ru/", true)]
    [InlineData("http://tlk-ofd.taxcom.ru", false)]
    [InlineData("https://taxcom.ru.evil.example", false)]
    [InlineData("https://user:password@taxcom.ru", false)]
    [InlineData("https://taxcom.ru/API/v2/Login", false)]
    [InlineData("https://taxcom.ru/?password=secret", false)]
    public void ServerValidation_RejectsUnsafeAddresses(string value, bool expected)
    {
        Assert.Equal(expected, TaxcomConnectionSettings.TryNormalizeServer(value, out _));
    }
}
