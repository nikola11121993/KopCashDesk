using KopCashDesk.Data;
using KopCashDesk.Desktop;
using Xunit;

namespace KopCashDesk.Tests;

public sealed class SmartImportArchiveFactsTests
{
    [Fact]
    public void Ati_Terminals_AreSplitBetweenConfirmedPoints()
    {
        Assert.Equal(KnownBusinessRules.AtiAppetitPointName, SmartSberAcquiringImporter.CanonicalPointNameForTerminal("42526205"));
        Assert.Equal(KnownBusinessRules.AtiAppetitPointName, SmartSberAcquiringImporter.CanonicalPointNameForTerminal("42526204"));
        Assert.Equal(KnownBusinessRules.AtiMercuryPointName, SmartSberAcquiringImporter.CanonicalPointNameForTerminal("34723825"));
        Assert.Equal(KnownBusinessRules.AtiMercuryPointName, SmartSberAcquiringImporter.CanonicalPointNameForTerminal("34723835"));
        Assert.Equal(KnownBusinessRules.AtiMercuryPointName, SmartSberAcquiringImporter.CanonicalPointNameForTerminal("34723837"));
    }

    [Fact]
    public void Replaced_Terminals_StayOnConfirmedPoints()
    {
        Assert.Equal(KnownBusinessRules.LadyzhenskogoPointName, SmartSberAcquiringImporter.CanonicalPointNameForTerminal("39413044"));
        Assert.Equal(KnownBusinessRules.LadyzhenskogoPointName, SmartSberAcquiringImporter.CanonicalPointNameForTerminal("45080359"));
        Assert.Equal(KnownBusinessRules.ChapaevaPointName, SmartSberAcquiringImporter.CanonicalPointNameForTerminal("42162000"));
        Assert.Equal(KnownBusinessRules.ChapaevaPointName, SmartSberAcquiringImporter.CanonicalPointNameForTerminal("45080612"));
        Assert.Equal(KnownBusinessRules.MusicCollegePointName, SmartSberAcquiringImporter.CanonicalPointNameForTerminal("39413112"));
    }

    [Fact]
    public void Voroniy_Leningradskaya_And_Mira_Terminals_AreOneHistoryPoint()
    {
        Assert.Equal(KnownBusinessRules.MiraPointName, SmartSberAcquiringImporter.CanonicalPointNameForTerminal("42638079"));
        Assert.Equal(KnownBusinessRules.MiraPointName, SmartSberAcquiringImporter.CanonicalPointNameForTerminal("39413189"));
        Assert.Equal(KnownBusinessRules.MiraPointName, SmartSberAcquiringImporter.CanonicalPointNameForTerminal("43151534"));
    }

    [Fact]
    public void Unknown_Terminal_HasNoAutomaticPoint()
    {
        Assert.Null(SmartSberAcquiringImporter.CanonicalPointNameForTerminal("99999999"));
        Assert.Null(SmartSberAcquiringImporter.CanonicalPointNameForTerminal("42830544"));
        Assert.Null(SmartSberAcquiringImporter.CanonicalPointNameForTerminal("44876543"));
    }
}
