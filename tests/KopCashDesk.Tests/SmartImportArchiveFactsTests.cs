using KopCashDesk.Desktop;
using Xunit;

namespace KopCashDesk.Tests;

public sealed class SmartImportArchiveFactsTests
{
    [Fact]
    public void Ati_Terminals_AreSplitBetweenTwoPhysicalLocations()
    {
        Assert.Equal("ЗАВОД АТИ", SmartSberAcquiringImporter.CanonicalPointNameForTerminal("42526205"));
        Assert.Equal("ЗАВОД АТИ", SmartSberAcquiringImporter.CanonicalPointNameForTerminal("42526204"));
        Assert.Equal("Столовая АТИ", SmartSberAcquiringImporter.CanonicalPointNameForTerminal("34723825"));
        Assert.Equal("Столовая АТИ", SmartSberAcquiringImporter.CanonicalPointNameForTerminal("34723835"));
        Assert.Equal("Столовая АТИ", SmartSberAcquiringImporter.CanonicalPointNameForTerminal("34723837"));
    }

    [Fact]
    public void Replaced_Terminals_StayOnSamePhysicalPoints()
    {
        Assert.Equal("Ладыженского 7", SmartSberAcquiringImporter.CanonicalPointNameForTerminal("39413044"));
        Assert.Equal("Ладыженского 7", SmartSberAcquiringImporter.CanonicalPointNameForTerminal("45080359"));
        Assert.Equal("Чапаева 28", SmartSberAcquiringImporter.CanonicalPointNameForTerminal("42162000"));
        Assert.Equal("Чапаева 28", SmartSberAcquiringImporter.CanonicalPointNameForTerminal("45080612"));
    }
}
