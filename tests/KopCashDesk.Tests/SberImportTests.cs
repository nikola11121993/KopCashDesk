using KopCashDesk.Desktop;
using Xunit;

namespace KopCashDesk.Tests;

public sealed class SberImportTests
{
    [Theory]
    [InlineData("Школа_17_P_QR", "Школа 17")]
    [InlineData("Школа_17_SBP", "Школа 17")]
    [InlineData("SP_Столовая Аппетит", "Столовая Аппетит")]
    [InlineData("Школа 6.", "Школа 6")]
    public void CleanPointName_GroupsSberVariants(string source, string expected)
    {
        Assert.Equal(expected, SberAcquiringImporter.CleanPointName(source));
    }

    [Theory]
    [InlineData("КафеАппетит", "POS")]
    [InlineData("КафеАппетит_P_QR", "QR")]
    [InlineData("КафеАппетит_SBP", "SBP")]
    [InlineData("Столовая Аппетит_S_QR", "QR")]
    [InlineData("Столовая Аппетит_S_SBP", "SBP")]
    public void DetectPaymentMethod_RecognizesReportNaming(string source, string expected)
    {
        Assert.Equal(expected, SberAcquiringImporter.DetectPaymentMethod(source));
    }

    [Fact]
    public void FriendlyOrganizationName_MakesCopShortName()
    {
        var source = "ОБЩЕСТВО С ОГРАНИЧЕННОЙ ОТВЕТСТВЕННОСТЬЮ \"ЦЕНТР ОБЩЕСТВЕННОГО ПИТАНИЯ\"";
        Assert.Equal("ООО \"ЦОП\"", SberAcquiringImporter.FriendlyOrganizationName(source));
    }

    [Fact]
    public void NormalizeForMatch_IgnoresCasePunctuationAndYo()
    {
        Assert.Equal(
            SberAcquiringImporter.NormalizeForMatch("Рефтинский, Молодёжная улица, 5"),
            SberAcquiringImporter.NormalizeForMatch("рефтинский  молодежная улица 5"));
    }
}
