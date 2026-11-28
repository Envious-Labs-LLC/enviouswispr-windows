using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;
using EnviousWispr.PostProcessing;

namespace EnviousWispr.Architecture.Tests;

public sealed class DictationLanguageResolverTests
{
    [Fact]
    public void LockedLanguageOutranksDetectingEngineAndText()
    {
        var resolution = DictationLanguageResolver.Resolve(
            new LanguageEvidence(
                LockedLanguage: "fr",
                EngineDetectsLanguage: true,
                EngineReportedLanguage: "de"),
            text: "Can you ask them to send it today?");

        Assert.Equal("fr", resolution.Language);
        Assert.Equal("fr", resolution.LearnLanguage);
        Assert.Equal(DictationLanguageSource.Locked, resolution.Source);
        Assert.False(resolution.EnglishVeto);
    }

    [Fact]
    public void ParakeetReportedLanguageIsIgnoredInFavorOfTextAndDocumentLadder()
    {
        var parakeetEvidence = new LanguageEvidence(
            LockedLanguage: null,
            EngineDetectsLanguage: false,
            EngineReportedLanguage: "de");

        var englishFromText = DictationLanguageResolver.Resolve(
            parakeetEvidence,
            text: "Can you ask them to send it today please?");
        Assert.Equal("en", englishFromText.Language);
        Assert.Equal(DictationLanguageSource.Dictation, englishFromText.Source);

        var whisperEvidence = new LanguageEvidence(
            LockedLanguage: null,
            EngineDetectsLanguage: true,
            EngineReportedLanguage: "de");
        var fromWhisper = DictationLanguageResolver.Resolve(
            whisperEvidence,
            text: "short");
        Assert.Equal("de", fromWhisper.Language);
        Assert.Equal(DictationLanguageSource.Engine, fromWhisper.Source);
    }

    [Fact]
    public void HybridDetectorUsesOsAgreementOnlyBetweenFiftyFiveAndEightyFive()
    {
        var identifier = LanguageIdentifier.Shared!;
        // "Complete your tests." hits ~0.73 in pure-C# L3 (below 0.85 solo floor, >= 0.55 agreement floor).
        var withoutOs = identifier.IdentifyHybrid("Complete your tests.", osDetector: null);
        Assert.Null(withoutOs.AcceptedLanguage);
        Assert.Equal("en", withoutOs.TopHypothesis);
        Assert.InRange(withoutOs.TopConfidence, 0.55, 0.85);

        var agreeingDetector = new StubOsDetector(["en-US"]);
        var withAgreeingOs = identifier.IdentifyHybrid("Complete your tests.", agreeingDetector);
        Assert.Equal("en", withAgreeingOs.AcceptedLanguage);
        Assert.True(withAgreeingOs.UsedOsAgreement);

        var disagreeingDetector = new StubOsDetector(["de"]);
        var withDisagreeingOs = identifier.IdentifyHybrid("Complete your tests.", disagreeingDetector);
        Assert.Null(withDisagreeingOs.AcceptedLanguage);
        Assert.False(withDisagreeingOs.UsedOsAgreement);
    }

    [Fact]
    public void SurroundingDocumentActsAsNonEnglishVetoOnly()
    {
        var lowSignalText = "ok";
        var englishDoc = DictationLanguageResolver.Resolve(
            LanguageEvidence.None,
            text: lowSignalText,
            surroundingText: "We are meeting tomorrow morning to review the design plan together.");

        Assert.Null(englishDoc.Language);
        Assert.Equal(DictationLanguageSource.None, englishDoc.Source);

        var germanDoc = DictationLanguageResolver.Resolve(
            LanguageEvidence.None,
            text: lowSignalText,
            surroundingText: "Wir treffen uns morgen früh im Büro und besprechen das Projekt.");

        Assert.Equal("de", germanDoc.Language);
        Assert.Null(germanDoc.LearnLanguage);
        Assert.Equal(DictationLanguageSource.Document, germanDoc.Source);
    }

    [Fact]
    public void AbstainedForeignTopHypothesisAtFiftyPercentSetsEnglishVeto()
    {
        var resolution = DictationLanguageResolver.Resolve(
            LanguageEvidence.None,
            text: "Pójść do sklepu jutro.",
            surroundingText: string.Empty,
            identifyTextOnAllPaths: false,
            osDetector: null,
            identifier: null,
            identifyOverride: static (_, _) =>
                new LanguageIdentificationResult(null, 0.62, "pl", 0.62));

        Assert.Null(resolution.Language);
        Assert.Null(resolution.LearnLanguage);
        Assert.True(resolution.EnglishVeto);
        Assert.Equal(DictationConfidenceBucket.From50To70, resolution.ConfidenceBucket);
        Assert.Equal(DictationLanguageSource.None, resolution.Source);
    }

    [Theory]
    [InlineData("今日はいい天気です", "ja")]
    [InlineData("今天天气很好", "zh")]
    [InlineData("오늘 날씨가 좋아요", "ko")]
    [InlineData("วันนี้อากาศดีมาก", "th")]
    public void ScriptGateDetectsNonLatinScriptsAuthoritatively(string text, string expectedLanguage)
    {
        var res = LanguageIdentifier.Shared!.IdentifyHybrid(text);
        Assert.Equal(expectedLanguage, res.AcceptedLanguage);
        Assert.Equal(1.0, res.AcceptedConfidence);
    }

    [Fact]
    public void BuildProtectedWordsIncludesCapitalizedCustomWordsAndPacksButSkipsLowercaseEntries()
    {
        var protectedWords = DictationLanguageResolver.BuildProtectedWords(
            [
                new CustomWordEntry("envious wispr", "EnviousWispr"),
                new CustomWordEntry("colour", "colour"),
            ],
            ["tech"]);

        Assert.Contains("EnviousWispr", protectedWords, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("colour", protectedWords, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("AngularJS", protectedWords, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("accept", protectedWords, StringComparer.OrdinalIgnoreCase);
    }

    private sealed class StubOsDetector(IReadOnlyList<string> tags) : IOsLanguageDetector
    {
        public IReadOnlyList<string> DetectBaseTags(string text) => tags;
    }
}
