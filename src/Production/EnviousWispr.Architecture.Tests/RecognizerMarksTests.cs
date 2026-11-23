using EnviousWispr.Core.Dictation;
using EnviousWispr.Pipeline;
using EnviousWispr.PostProcessing;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// #3594: with Spoken punctuation on, the engine's own comma or period beside a mark the user speaks is
/// removed, and a command the engine wrote twice becomes one.
/// Ref: macOS RecognizerMarksTests.swift.
/// </summary>
public sealed class RecognizerMarksTests
{
    [Theory]
    [InlineData("Hello. Period", "Hello.")]
    [InlineData("hello period", "hello.")]
    [InlineData("period hello", ". Hello")]
    [InlineData("Mr. period", "Mr.")]
    [InlineData("3. period", "3.")]
    [InlineData("U.S. comma", "U.S.,")]
    [InlineData("Dr. question mark", "Dr.?")]
    [InlineData("e.g. comma", "e.g.,")]
    [InlineData("p.m. comma", "p.m.,")]
    [InlineData("end.) comma", "end),")]
    [InlineData("\"quote.\" period", "\"quote\".")]
    [InlineData("period.period", ".")]
    [InlineData("comma , comma", ",")]
    [InlineData("colon: colon", ":")]
    [InlineData("period. period. period.", ".")]
    [InlineData("period period", "..")]
    [InlineData("wait... period", "wait....")]
    [InlineData("example dot com period", "example.com.")]
    [InlineData("Meet John Jr. comma and Jane", "Meet John Jr., and Jane")]
    [InlineData("Use tools, etc. comma and supplies", "Use tools, etc., and supplies")]
    [InlineData("Ask J. comma next", "Ask J., next")]
    [InlineData("Maple Ave. comma", "Maple Ave.,")]
    [InlineData("Acme Inc. comma", "Acme Inc.,")]
    [InlineData("I said no. comma but", "I said no, but")]
    [InlineData("Tell Ed. comma now", "Tell Ed, now")]
    public void EnglishBoundaryRowsAbsorbRecognizerMarksBesideSpokenCommands(string input, string expected)
    {
        var result = InverseTextNormalizer.Normalize(input, spokenPunctuation: true);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Step4LiveParakeetTakeWithModelPunctuationNormalizesToSingleFullStop()
    {
        // Bug reported in UAT Step 4: Parakeet attaches model punctuation "Sounds good. See you tomorrow period."
        // Prior to absorption, ApplyPunctuation turned this into "Sounds good. See you tomorrow.."
        const string rawTake = "Sounds good. See you tomorrow period.";
        var normalized = InverseTextNormalizer.Normalize(rawTake, spokenPunctuation: true, out var spokenFinalFullStop);

        Assert.Equal("Sounds good. See you tomorrow.", normalized);
        Assert.True(spokenFinalFullStop);
    }

    [Fact]
    public void Step4LiveParakeetTakeWithPauseCommaAndDoubleDotNormalizesCleanly()
    {
        // Pause comma before spoken command: "Sounds good. See you tomorrow, period."
        var withComma = InverseTextNormalizer.Normalize("Sounds good. See you tomorrow, period.", spokenPunctuation: true, out var spoken1);
        Assert.Equal("Sounds good. See you tomorrow.", withComma);
        Assert.True(spoken1);

        // Trailing double dot: "Sounds good. See you tomorrow period.."
        var withDoubleDot = InverseTextNormalizer.Normalize("Sounds good. See you tomorrow period..", spokenPunctuation: true, out var spoken2);
        Assert.Equal("Sounds good. See you tomorrow.", withDoubleDot);
        Assert.True(spoken2);
    }

    [Fact]
    public async Task Step4DeterministicPipelineCasualModeKeepsSingleFullStopOnSpokenPeriodTake()
    {
        var pipeline = new DeterministicTextPipeline();
        var request = new DeterministicTextRequest(
            new Transcript(DictationSessionId.Create(), "Sounds good. See you tomorrow period.", "whisper", DetectedLanguage: "en"),
            CustomWords: [],
            Options: new DeterministicTextOptions(
                WordCorrectionEnabled: false,
                FillerRemovalEnabled: false,
                EmojiFormatterEnabled: false,
                SpokenPunctuationEnabled: true,
                CasualModeEnabled: true));

        var result = await pipeline.ProcessAsync(request);

        // In Casual Mode with spoken punctuation, user explicitly spoke "period", so the single period is preserved.
        Assert.Equal("Sounds good. See you tomorrow.", result.Output.Text);
        var casualReceipt = result.Receipts.Single(r => r.Stage == DeterministicTextStage.CasualEnding);
        Assert.Equal(DeterministicStageStatus.Completed, casualReceipt.Status);
        Assert.False(casualReceipt.Changed);
    }
}
