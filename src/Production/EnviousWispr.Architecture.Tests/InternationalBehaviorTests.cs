using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;

namespace EnviousWispr.Architecture.Tests;

public sealed class InternationalBehaviorTests
{
    [Theory]
    [InlineData("auto", "auto")]
    [InlineData("en", "en")]
    [InlineData("en-gb", "en")]
    [InlineData("fr", "fr")]
    [InlineData("de", "de")]
    [InlineData("es", "es")]
    [InlineData("zh", "zh")]
    [InlineData("ja", "ja")]
    [InlineData("haw", "haw")]
    [InlineData("yue", "yue")]
    [InlineData("es-419", "es")]
    public void SupportedWhisperLanguagesNormalizeToPinnedRuntimeCodes(
        string input,
        string expected)
    {
        Assert.True(WhisperLanguageCodes.TryNormalize(input, out var normalized));
        Assert.Equal(expected, normalized);
        Assert.Equal(expected, WhisperLanguageCodes.For(normalized));
    }

    [Theory]
    [InlineData("xx")]
    [InlineData("english-ish")]
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
        var itnReceipt = result.Receipts.Single(receipt =>
            receipt.Stage == DeterministicTextStage.InverseTextNormalization);
        Assert.Equal(DeterministicStageStatus.Completed, itnReceipt.Status);
        Assert.False(itnReceipt.Changed);
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
