using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;

namespace EnviousWispr.Architecture.Tests;

/// <summary>English (UK) in the production text pipeline: where British spelling applies, and where it must not.</summary>
public sealed class EnglishSpellingPipelineTests
{
    private static readonly DeterministicTextOptions British =
        new(WordCorrectionEnabled: true, FillerRemovalEnabled: false, EmojiFormatterEnabled: false, SpokenPunctuationEnabled: false, EnglishSpelling.British);

    private static readonly DeterministicTextOptions American = British with { EnglishSpelling = EnglishSpelling.American };

    /// <summary>Parakeet reports no language and is English: the default engine gets British spelling.</summary>
    [Fact]
    public async Task ParakeetTextIsSpelledTheBritishWay()
    {
        var result = await Process("we moved the color printer to the center", "parakeet", language: null, British);

        Assert.Equal("we moved the colour printer to the centre", result.Output.Text);
        var receipt = result.Receipts.Single(item => item.Stage == DeterministicTextStage.EnglishSpelling);
        Assert.Equal(DeterministicStageStatus.Completed, receipt.Status);
        Assert.True(receipt.Changed);
    }

    [Fact]
    public async Task AmericanIsLeftAsItWasAndTheStepSaysItWasSkipped()
    {
        var result = await Process("we moved the color printer to the center", "parakeet", language: null, American);

        Assert.Equal("we moved the color printer to the center", result.Output.Text);
        Assert.Equal(
            DeterministicStageStatus.Skipped,
            result.Receipts.Single(item => item.Stage == DeterministicTextStage.EnglishSpelling).Status);
    }

    /// <summary>Whisper converts when the take is English and never when it is another language.</summary>
    [Theory]
    [InlineData("en", "the colour of the centre")]
    [InlineData("fr", "the color of the center")]
    public async Task WhisperFollowsTheLanguageOfTheTake(string language, string expected)
    {
        var result = await Process("the color of the center", "whisper", language, British);

        Assert.Equal(expected, result.Output.Text);
    }

    /// <summary>A person's own Custom Word is their spelling, whichever side of the Atlantic it is from.</summary>
    [Fact]
    public async Task ACustomWordKeepsItsOwnSpelling()
    {
        var pipeline = new DeterministicTextPipeline();

        var result = await pipeline.ProcessAsync(new DeterministicTextRequest(
            new Transcript(DictationSessionId.Create(), "meet me at color street near the center", "parakeet"),
            [new CustomWordEntry("color street", "Color Street")],
            British));

        Assert.Equal("meet me at Color Street near the centre", result.Output.Text);
    }

    /// <summary>A polish that writes American spelling cannot undo the choice.</summary>
    [Fact]
    public async Task ThePolishedTextIsSpelledTheBritishWayToo()
    {
        var pipeline = new DeterministicTextPipeline();
        var request = new DeterministicTextRequest(
            new Transcript(DictationSessionId.Create(), "we changed the color", "parakeet"),
            [],
            British);
        var deterministic = await pipeline.ProcessAsync(request);

        var polished = await pipeline.ApplyPolishedTextAsync(request, deterministic, "We changed the color of the logo.");

        Assert.Equal("we changed the colour", polished.DeterministicText);
        Assert.Equal("We changed the colour of the logo.", polished.Output.Text);
        var receipt = polished.Receipts.Single(item => item.Stage == DeterministicTextStage.EnglishSpellingAfterPolish);
        Assert.Equal(DeterministicStageStatus.Completed, receipt.Status);
        Assert.True(receipt.Changed);
    }

    /// <summary>With American chosen the polish is delivered exactly as the model wrote it.</summary>
    [Fact]
    public async Task AmericanLeavesThePolishAlone()
    {
        var pipeline = new DeterministicTextPipeline();
        var request = new DeterministicTextRequest(
            new Transcript(DictationSessionId.Create(), "we changed the color", "parakeet"),
            [],
            American);
        var deterministic = await pipeline.ProcessAsync(request);

        var polished = await pipeline.ApplyPolishedTextAsync(request, deterministic, "We changed the color.");

        Assert.Equal("We changed the color.", polished.Output.Text);
    }

    /// <summary>The setting a person saves reaches the pipeline: the From the app uses carries it.</summary>
    [Fact]
    public void TheSavedPreferenceReachesTheOptions()
    {
        var options = DeterministicTextOptions.From(DictationPreferences.Default with { EnglishSpelling = EnglishSpelling.British });

        Assert.Equal(EnglishSpelling.British, options.EnglishSpelling);
        Assert.Equal(EnglishSpelling.American, DeterministicTextOptions.From(DictationPreferences.Default).EnglishSpelling);
    }

    private static Task<DeterministicTextResult> Process(
        string text,
        string engineId,
        string? language,
        DeterministicTextOptions options) =>
        new DeterministicTextPipeline().ProcessAsync(new DeterministicTextRequest(
            new Transcript(DictationSessionId.Create(), text, engineId, DetectedLanguage: language),
            [],
            options));
}

/// <summary>The spelling over the polish is logged with the polish half, where its outcome is known.</summary>
public sealed class EnglishSpellingReceiptEmissionTests
{
    [Fact]
    public void ThePolishHalfCarriesBothPolishStagesAndTheMainHalfNeither()
    {
        var logger = new RecordingLogger();
        var effects = new EnviousWispr.App.Composition.TranscriptFinalizationEffects(logger, null!);
        DeterministicStageReceipt[] receipts =
        [
            new(DeterministicTextStage.InverseTextNormalization, DeterministicStageStatus.Completed, false, 1),
            new(DeterministicTextStage.EnglishSpelling, DeterministicStageStatus.Completed, true, 1),
            new(DeterministicTextStage.EnglishSpellingAfterPolish, DeterministicStageStatus.Completed, true, 1),
            new(DeterministicTextStage.EmojiRestoration, DeterministicStageStatus.Completed, false, 1),
        ];

        effects.EmitStageReceipts(receipts, emojiRestorationOnly: false);
        var main = logger.Entries.Select(entry => entry.Stage).ToArray();
        effects.EmitStageReceipts(receipts, emojiRestorationOnly: true);
        var polish = logger.Entries.Skip(main.Length).Select(entry => entry.Stage).ToArray();

        Assert.Equal([DeterministicTextStage.InverseTextNormalization, DeterministicTextStage.EnglishSpelling], main);
        Assert.Equal([DeterministicTextStage.EnglishSpellingAfterPolish, DeterministicTextStage.EmojiRestoration], polish);
    }
}

/// <summary>A reported language decides; Parakeet reports none, and choosing British is the statement.</summary>
public sealed class EnglishSpellingUnreportedLanguageTests
{
    /// <summary>Whisper said Spanish: nothing is respelled, whatever the setting.</summary>
    [Fact]
    public async Task AWhisperTakeReportedAsSpanishKeepsItsWords()
    {
        var result = await new DeterministicTextPipeline().ProcessAsync(new DeterministicTextRequest(
            new Transcript(DictationSessionId.Create(), "El color del centro es un favor", "whisper", DetectedLanguage: "es"),
            [],
            new DeterministicTextOptions(true, false, false, false, EnglishSpelling.British)));

        Assert.Equal("El color del centro es un favor", result.Output.Text);
        Assert.Equal(
            DeterministicStageStatus.Skipped,
            result.Receipts.Single(item => item.Stage == DeterministicTextStage.EnglishSpelling).Status);
    }

    /// <summary>A short Parakeet take with no English cue is respelled all the same: the choice said English.</summary>
    [Fact]
    public async Task AParakeetTakeIsRespelledOnTheChoiceAlone()
    {
        var result = await new DeterministicTextPipeline().ProcessAsync(new DeterministicTextRequest(
            new Transcript(DictationSessionId.Create(), "gray color", "parakeet"),
            [],
            new DeterministicTextOptions(true, false, false, false, EnglishSpelling.British)));

        Assert.Equal("grey colour", result.Output.Text);
    }
}
