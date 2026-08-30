using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;

namespace EnviousWispr.Architecture.Tests;

public sealed class InternationalBehaviorTests
{
    [Theory]
    [InlineData(WhisperLanguagePreference.Automatic, "auto")]
    [InlineData(WhisperLanguagePreference.English, "en")]
    [InlineData(WhisperLanguagePreference.French, "fr")]
    [InlineData(WhisperLanguagePreference.German, "de")]
    [InlineData(WhisperLanguagePreference.Spanish, "es")]
    public void AdvertisedWhisperPreferencesMapToPinnedRuntimeCodes(
        WhisperLanguagePreference preference,
        string expected)
    {
        Assert.Equal(expected, WhisperLanguageCodes.For(preference));
        Assert.True(WhisperLanguageCodes.TryNormalize(expected, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("zh")]
    [InlineData("../../unsafe")]
    [InlineData("")]
    public void UnadvertisedOrUnsafeLanguageOverridesAreRejected(string value)
    {
        Assert.False(WhisperLanguageCodes.TryNormalize(value, out _));
    }

    [Theory]
    [InlineData("مرحبا بالعالم ١٢٣ 👋🏽")]
    [InlineData("שלום עולם 123 🌍")]
    [InlineData("नमस्ते दुनिया 👨‍👩‍👧‍👦")]
    [InlineData("Cafe\u0301 déjà — 東京")]
    public async Task UnicodeAndRightToLeftTextRemainByteExactThroughDeterministicPipeline(string text)
    {
        var request = new DeterministicTextRequest(
            new Transcript(
                DictationSessionId.Create(),
                text,
                "whisper",
                DetectedLanguage: "ar"),
            [],
            new DeterministicTextOptions(false, false, true, true));

        var result = await new DeterministicTextPipeline().ProcessAsync(request);

        Assert.Equal(text, result.Output.Text);
        Assert.Equal(DeterministicStageStatus.Skipped, result.Receipts.Single(receipt =>
            receipt.Stage == DeterministicTextStage.SpokenEmoji).Status);
        Assert.Equal(DeterministicStageStatus.Skipped, result.Receipts.Single(receipt =>
            receipt.Stage == DeterministicTextStage.InverseTextNormalization).Status);
    }

    [Fact]
    public async Task NonEnglishSpeechDoesNotRunEnglishEmojiOrNumberCommands()
    {
        const string text = "rocket emoji vingt trois janvier";
        var request = new DeterministicTextRequest(
            new Transcript(
                DictationSessionId.Create(),
                text,
                "whisper",
                DetectedLanguage: "fr"),
            [],
            new DeterministicTextOptions(false, false, true, true));

        var result = await new DeterministicTextPipeline().ProcessAsync(request);

        Assert.Equal(text, result.Output.Text);
    }
}
