using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Pipeline;

namespace EnviousWispr.Architecture.Tests;

public sealed class DeliveryLanguagePolicyTests
{
    [Theory]
    [InlineData(ParakeetModelIds.Final)]
    [InlineData(ParakeetModelIds.Final + ":cpu")]
    [InlineData(ParakeetModelIds.Final + ":cuda:isolated")]
    [InlineData(ParakeetModelIds.LegacyV3)]
    [InlineData(ParakeetModelIds.LegacyV3 + ":cpu")]
    [InlineData(ParakeetModelIds.LegacyV3 + ":cuda:isolated")]
    public void ParakeetFinalAndLegacyEngineIdsAlwaysStripUntrustedDetectedLanguage(string engineId)
    {
        var transcript = new Transcript(
            DictationSessionId.Create(),
            "bonjour tout le monde",
            engineId,
            DetectedLanguage: "fr");

        Assert.Null(DeliveryLanguagePolicy.For(transcript));
    }

    [Theory]
    [InlineData(WhisperModelIds.Final)]
    [InlineData(WhisperModelIds.Final + ":cuda:isolated")]
    [InlineData(WhisperModelIds.Preview + ":cpu")]
    public void WhisperEngineIdsPreserveDetectedLanguage(string engineId)
    {
        var transcript = new Transcript(
            DictationSessionId.Create(),
            "bonjour tout le monde",
            engineId,
            DetectedLanguage: "fr");

        Assert.Equal("fr", DeliveryLanguagePolicy.For(transcript));
    }
}
