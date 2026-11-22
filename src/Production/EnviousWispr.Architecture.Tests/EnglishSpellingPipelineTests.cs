using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;

namespace EnviousWispr.Architecture.Tests;

/// <summary>English (UK) in the production text pipeline: where British spelling applies, and where it must not.</summary>
public sealed class EnglishSpellingPipelineTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static readonly DeterministicTextOptions British =
        new(WordCorrectionEnabled: true, FillerRemovalEnabled: false, EmojiFormatterEnabled: false, SpokenPunctuationEnabled: false, EnglishSpelling.British);

    private static readonly DeterministicTextOptions American = British with { EnglishSpelling = EnglishSpelling.American };

    /// <summary>Parakeet reports no language and is English: the default engine gets British spelling.</summary>
    [Fact]
    public async Task ParakeetTextIsSpelledTheBritishWay()
    {
        var result = await ProcessWarmed("we moved the color printer to the center", "parakeet", language: null, British);

        AssertStageAndText(
            result,
            DeterministicTextStage.EnglishSpelling,
            DeterministicStageStatus.Completed,
            expectedChanged: true,
            expectedText: "we moved the colour printer to the centre");
    }

    [Fact]
    public async Task AmericanIsLeftAsItWasAndTheStepSaysItWasSkipped()
    {
        var result = await ProcessWarmed("we moved the color printer to the center", "parakeet", language: null, American);

        AssertStageAndText(
            result,
            DeterministicTextStage.EnglishSpelling,
            DeterministicStageStatus.Skipped,
            expectedChanged: false,
            expectedText: "we moved the color printer to the center");
    }

    /// <summary>Whisper converts when the take is English and never when it is another language.</summary>
    [Theory]
    [InlineData("en", "the colour of the centre", DeterministicStageStatus.Completed, true)]
    [InlineData("fr", "the color of the center", DeterministicStageStatus.Skipped, false)]
    public async Task WhisperFollowsTheLanguageOfTheTake(
        string language,
        string expected,
        DeterministicStageStatus expectedStatus,
        bool expectedChanged)
    {
        var result = await ProcessWarmed("the color of the center", "whisper", language, British);

        AssertStageAndText(
            result,
            DeterministicTextStage.EnglishSpelling,
            expectedStatus,
            expectedChanged,
            expected);
    }

    /// <summary>A person's own Custom Word is their spelling, whichever side of the Atlantic it is from.</summary>
    [Fact]
    public async Task ACustomWordKeepsItsOwnSpelling()
    {
        var pipeline = CreateWarmedSemanticPipeline();

        var result = await pipeline.ProcessAsync(new DeterministicTextRequest(
            new Transcript(DictationSessionId.Create(), "meet me at color street near the center", "parakeet"),
            [new CustomWordEntry("color street", "Color Street")],
            British));

        AssertStageAndText(
            result,
            DeterministicTextStage.EnglishSpelling,
            DeterministicStageStatus.Completed,
            expectedChanged: true,
            expectedText: "meet me at Color Street near the centre");
    }

    /// <summary>
    /// A polish that writes American spelling cannot undo the choice. Semantic spelling assertions warm
    /// stages and lift the 200 ms CI scheduling race; production-deadline fallback is tested separately (#255).
    /// </summary>
    [Fact]
    public async Task ThePolishedTextIsSpelledTheBritishWayToo()
    {
        var pipeline = CreateWarmedSemanticPipeline();
        var request = new DeterministicTextRequest(
            new Transcript(DictationSessionId.Create(), "we changed the color", "parakeet"),
            [],
            British);
        var deterministic = await pipeline.ProcessAsync(request);
        AssertStageAndText(
            deterministic,
            DeterministicTextStage.EnglishSpelling,
            DeterministicStageStatus.Completed,
            expectedChanged: true,
            expectedText: "we changed the colour");

        var polished = await pipeline.ApplyPolishedTextAsync(request, deterministic, "We changed the color of the logo.");

        AssertStageAndText(
            polished,
            DeterministicTextStage.EnglishSpellingAfterPolish,
            DeterministicStageStatus.Completed,
            expectedChanged: true,
            expectedText: "We changed the colour of the logo.",
            expectedDeterministicText: "we changed the colour");
    }

    /// <summary>With American chosen the polish is delivered exactly as the model wrote it.</summary>
    [Fact]
    public async Task AmericanLeavesThePolishAlone()
    {
        var pipeline = CreateWarmedSemanticPipeline();
        var request = new DeterministicTextRequest(
            new Transcript(DictationSessionId.Create(), "we changed the color", "parakeet"),
            [],
            American);
        var deterministic = await pipeline.ProcessAsync(request);

        var polished = await pipeline.ApplyPolishedTextAsync(request, deterministic, "We changed the color.");

        AssertStageAndText(
            polished,
            DeterministicTextStage.EnglishSpellingAfterPolish,
            DeterministicStageStatus.Skipped,
            expectedChanged: false,
            expectedText: "We changed the color.");
    }

    /// <summary>
    /// Deterministic reproduction of #255: when the post-polish spelling stage misses its exact 200 ms
    /// production deadline (e.g. cold-start or CI pool starvation), the pipeline stands down with TimedOut,
    /// preserves the model's American polish as last valid text, and surfaces the full receipt.
    /// </summary>
    [Fact]
    public async Task AfterPolishSpellingOverrunningProductionDeadlineFallsBackAndSurfacesReceipt()
    {
        var clock = new Deterministic.ManualClock();
        var scheduler = new Deterministic.ControlledTaskScheduler();
        var pipeline = new DeterministicTextPipeline(DeterministicTextPipeline.DefaultSteps(), clock, scheduler);
        Assert.Empty(pipeline.WarmStages());

        var request = new DeterministicTextRequest(
            new Transcript(DictationSessionId.Create(), "we changed the color", "parakeet"),
            [],
            British);
        var deterministic = await pipeline.ProcessAsync(request).WaitAsync(Patience);
        AssertStageAndText(
            deterministic,
            DeterministicTextStage.EnglishSpelling,
            DeterministicStageStatus.Completed,
            expectedChanged: true,
            expectedText: "we changed the colour");

        var queuedBeforePolish = scheduler.QueuedCount;
        var registeredBeforePolish = clock.Registered;
        scheduler.HoldNext(1);
        var pending = pipeline.ApplyPolishedTextAsync(request, deterministic, "We changed the color of the logo.");

        await scheduler.WhenQueued(queuedBeforePolish + 1).WaitAsync(Patience);
        await clock.WhenRegistered(registeredBeforePolish + 1).WaitAsync(Patience);
        Assert.Equal(TimeSpan.FromMilliseconds(200), clock.NextDue);

        clock.Advance(TimeSpan.FromMilliseconds(200));
        var polished = await pending.WaitAsync(Patience);

        var receipt = AssertStageAndText(
            polished,
            DeterministicTextStage.EnglishSpellingAfterPolish,
            DeterministicStageStatus.TimedOut,
            expectedChanged: false,
            expectedText: "We changed the color of the logo.",
            expectedDegraded: true,
            expectedDeterministicText: "we changed the colour");
        Assert.Equal(200, receipt.ElapsedMilliseconds);
        var drainedStatuses = await scheduler.DrainHeldAsync().WaitAsync(Patience);
        Assert.Equal([TaskStatus.Canceled], drainedStatuses);
    }

    /// <summary>
    /// When the pre-polish spelling stage misses its exact 200 ms production deadline, it stands down with
    /// TimedOut and preserves the raw transcript text (#255).
    /// </summary>
    [Fact]
    public async Task PrePolishSpellingOverrunningProductionDeadlineFallsBackAndSurfacesReceipt()
    {
        var request = new DeterministicTextRequest(
            new Transcript(DictationSessionId.Create(), "we changed the color", "parakeet"),
            [],
            British);
        var spellingOnly = DeterministicTextPipeline.DefaultSteps()
            .Where(step => step.Stage == DeterministicTextStage.EnglishSpelling)
            .ToArray();
        var clock = new Deterministic.ManualClock();
        var scheduler = new Deterministic.ControlledTaskScheduler(holdCount: 1);
        var pipeline = new DeterministicTextPipeline(spellingOnly, clock, scheduler);

        var pending = pipeline.ProcessAsync(request);
        await scheduler.WhenQueued(1).WaitAsync(Patience);
        await clock.WhenRegistered(1).WaitAsync(Patience);
        Assert.Equal(TimeSpan.FromMilliseconds(200), clock.NextDue);

        clock.Advance(TimeSpan.FromMilliseconds(200));
        var result = await pending.WaitAsync(Patience);

        var receipt = AssertStageAndText(
            result,
            DeterministicTextStage.EnglishSpelling,
            DeterministicStageStatus.TimedOut,
            expectedChanged: false,
            expectedText: "we changed the color",
            expectedDegraded: true);
        Assert.Equal(200, receipt.ElapsedMilliseconds);
        var drainedStatuses = await scheduler.DrainHeldAsync().WaitAsync(Patience);
        Assert.Equal([TaskStatus.Canceled], drainedStatuses);
    }

    /// <summary>The setting a person saves reaches the pipeline: the From the app uses carries it.</summary>
    [Fact]
    public void TheSavedPreferenceReachesTheOptions()
    {
        var options = DeterministicTextOptions.From(DictationPreferences.Default with { EnglishSpelling = EnglishSpelling.British });

        Assert.Equal(EnglishSpelling.British, options.EnglishSpelling);
        Assert.Equal(EnglishSpelling.American, DeterministicTextOptions.From(DictationPreferences.Default).EnglishSpelling);
    }

    internal static DeterministicTextPipeline CreateWarmedSemanticPipeline()
    {
        var pipeline = PatientPipeline.Create();
        Assert.Empty(pipeline.WarmStages());
        return pipeline;
    }

    internal static DeterministicStageReceipt AssertStageAndText(
        DeterministicTextResult result,
        DeterministicTextStage stage,
        DeterministicStageStatus expectedStatus,
        bool expectedChanged,
        string expectedText,
        bool expectedDegraded = false,
        string? expectedDeterministicText = null)
    {
        var receipt = Assert.Single(result.Receipts, item => item.Stage == stage);
        var receiptsSummary = string.Join(
            ", ",
            result.Receipts.Select(item => $"{item.Stage}:{item.Status}(Changed={item.Changed},{item.ElapsedMilliseconds}ms)"));
        var details =
            $"Stage={receipt.Stage}, Status={receipt.Status}, Changed={receipt.Changed}, ElapsedMs={receipt.ElapsedMilliseconds}, " +
            $"IsDegraded={result.IsDegraded}, Receipts=[{receiptsSummary}]";

        Assert.True(
            receipt.Status == expectedStatus &&
            receipt.Changed == expectedChanged &&
            result.IsDegraded == expectedDegraded &&
            string.Equals(expectedText, result.Output.Text, StringComparison.Ordinal) &&
            (expectedDeterministicText is null ||
             string.Equals(expectedDeterministicText, result.DeterministicText, StringComparison.Ordinal)),
            $"Expected {stage} Status={expectedStatus}, Changed={expectedChanged}, IsDegraded={expectedDegraded}, " +
            $"Text='{expectedText}'" +
            (expectedDeterministicText is null ? string.Empty : $", DeterministicText='{expectedDeterministicText}'") +
            $". Actual Text='{result.Output.Text}', DeterministicText='{result.DeterministicText}'; {details}.");

        return receipt;
    }

    private static Task<DeterministicTextResult> ProcessWarmed(
        string text,
        string engineId,
        string? language,
        DeterministicTextOptions options) =>
        CreateWarmedSemanticPipeline().ProcessAsync(new DeterministicTextRequest(
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
        var result = await EnglishSpellingPipelineTests.CreateWarmedSemanticPipeline().ProcessAsync(new DeterministicTextRequest(
            new Transcript(DictationSessionId.Create(), "El color del centro es un favor", "whisper", DetectedLanguage: "es"),
            [],
            new DeterministicTextOptions(true, false, false, false, EnglishSpelling.British)));

        EnglishSpellingPipelineTests.AssertStageAndText(
            result,
            DeterministicTextStage.EnglishSpelling,
            DeterministicStageStatus.Skipped,
            expectedChanged: false,
            expectedText: "El color del centro es un favor");
    }

    /// <summary>A short Parakeet take with no English cue is respelled all the same: the choice said English.</summary>
    [Fact]
    public async Task AParakeetTakeIsRespelledOnTheChoiceAlone()
    {
        var result = await EnglishSpellingPipelineTests.CreateWarmedSemanticPipeline().ProcessAsync(new DeterministicTextRequest(
            new Transcript(DictationSessionId.Create(), "gray color", "parakeet"),
            [],
            new DeterministicTextOptions(true, false, false, false, EnglishSpelling.British)));

        EnglishSpellingPipelineTests.AssertStageAndText(
            result,
            DeterministicTextStage.EnglishSpelling,
            DeterministicStageStatus.Completed,
            expectedChanged: true,
            expectedText: "grey colour");
    }
}
