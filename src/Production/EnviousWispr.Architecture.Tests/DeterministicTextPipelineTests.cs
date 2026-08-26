using System.Text.Json;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;
using EnviousWispr.PostProcessing;

namespace EnviousWispr.Architecture.Tests;

public sealed class DeterministicTextPipelineTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public async Task SharedParityFixtureMatchesPinnedMacBehaviorAndDocumentedDifferences()
    {
        var cases = await LoadParityCasesAsync();
        var pipeline = new DeterministicTextPipeline();

        Assert.True(cases.Count >= 35, "The deterministic parity fixture was unexpectedly reduced.");
        Assert.Contains(cases, item => item.Category == "international-filler");
        Assert.Contains(cases, item => item.Category == "international-itn");
        Assert.All(cases.Where(item => item.Difference is not null), item =>
            Assert.False(string.IsNullOrWhiteSpace(item.Difference)));

        var mismatches = new List<string>();
        foreach (var item in cases)
        {
            var result = await pipeline.ProcessAsync(CreateRequest(item));
            if (!string.Equals(item.Expected, result.Output.Text, StringComparison.Ordinal))
            {
                mismatches.Add($"{item.Name}: expected '{item.Expected}', got '{result.Output.Text}'");
            }
        }

        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches));
    }

    [Theory]
    [InlineData("macos-itn-parity.jsonl", 2_084)]
    [InlineData("macos-itn-parity-holdout.jsonl", 3_756)]
    public void InverseTextNormalizerMatchesPinnedMacOracleByteForByte(
        string fileName,
        int expectedRows)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        var failures = new List<string>();
        var rowCount = 0;
        foreach (var line in File.ReadLines(path))
        {
            var row = JsonSerializer.Deserialize<MacParityRow>(line, SerializerOptions)
                ?? throw new InvalidOperationException($"Invalid parity row in {fileName}.");
            var actual = InverseTextNormalizer.Normalize(row.Input, spokenPunctuation: true);
            if (!string.Equals(row.Expected, actual, StringComparison.Ordinal) && failures.Count < 10)
            {
                failures.Add(
                    $"[{row.Category}/{row.Slice}] expected '{row.Expected}', got '{actual}'");
            }

            rowCount++;
        }

        Assert.Equal(expectedRows, rowCount);
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public async Task DefaultPipelineUsesBindingOrderAndContentFreeReceipts()
    {
        var pipeline = new DeterministicTextPipeline();
        var transcript = new Transcript(
            DictationSessionId.Create(),
            "um send an envy wisper thumbs up emoji period done",
            "whisper",
            DetectedLanguage: "en");
        var result = await pipeline.ProcessAsync(new DeterministicTextRequest(
            transcript,
            [new CustomWordEntry("envy wisper", "EnviousWispr")],
            new DeterministicTextOptions(true, true, true, true)));

        Assert.Equal(
            [
                DeterministicTextStage.CustomWords,
                DeterministicTextStage.FillerAndFalseStarts,
                DeterministicTextStage.SpokenEmoji,
                DeterministicTextStage.InverseTextNormalization,
                DeterministicTextStage.EmojiRestoration,
            ],
            result.Receipts.Select(receipt => receipt.Stage));
        Assert.Equal("send an EnviousWispr 👍. Done", result.Output.Text);
        Assert.False(result.IsDegraded);

        var receiptProperties = typeof(DeterministicStageReceipt).GetProperties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("text", receiptProperties);
        Assert.DoesNotContain("transcript", receiptProperties);
        Assert.DoesNotContain("content", receiptProperties);
    }

    [Fact]
    public async Task PolishRunsAfterDeterministicWorkAndBeforeEmojiRestoration()
    {
        var pipeline = new DeterministicTextPipeline();
        var request = new DeterministicTextRequest(
            new Transcript(
                DictationSessionId.Create(),
                "thumbs up emoji we shipped it",
                "test",
                DetectedLanguage: "en"),
            [],
            new DeterministicTextOptions(false, false, true, false));
        var deterministic = await pipeline.ProcessAsync(request);

        var completed = await pipeline.ApplyPolishedTextAsync(
            request,
            deterministic,
            "We shipped it.");

        Assert.Equal("👍 We shipped it.", completed.Output.Text);
        Assert.Equal("👍 we shipped it", completed.DeterministicText);
        Assert.Equal(
            DeterministicStageStatus.Completed,
            completed.Receipts.Single(receipt =>
                receipt.Stage == DeterministicTextStage.EmojiRestoration).Status);
    }

    [Fact]
    public async Task FailedStageReturnsLastValidTextAndContinues()
    {
        IDeterministicTextStep[] steps =
        [
            new DelegateStep(
                DeterministicTextStage.CustomWords,
                context => context with { Text = "last valid" }),
            new DelegateStep(
                DeterministicTextStage.FillerAndFalseStarts,
                _ => throw new InvalidOperationException("synthetic failure")),
            new DelegateStep(
                DeterministicTextStage.InverseTextNormalization,
                context => context with { Text = context.Text + " output" }),
        ];
        var result = await new DeterministicTextPipeline(steps).ProcessAsync(CreateRequest("raw"));

        Assert.Equal("last valid output", result.Output.Text);
        Assert.True(result.IsDegraded);
        Assert.Equal(DeterministicStageStatus.Failed, result.Receipts[1].Status);
    }

    [Fact]
    public async Task TimedOutStageReturnsLastValidText()
    {
        var step = new DelegateStep(
            DeterministicTextStage.CustomWords,
            context =>
            {
                Thread.Sleep(100);
                return context with { Text = "too late" };
            },
            TimeSpan.FromMilliseconds(5));
        var result = await new DeterministicTextPipeline([step]).ProcessAsync(CreateRequest("safe"));

        Assert.Equal("safe", result.Output.Text);
        Assert.True(result.IsDegraded);
        Assert.Equal(DeterministicStageStatus.TimedOut, Assert.Single(result.Receipts).Status);
    }

    [Fact]
    public async Task CallerCancellationStopsThePipeline()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new DeterministicTextPipeline().ProcessAsync(
                CreateRequest("safe"),
                cancellation.Token));
    }

    [Fact]
    public void EmojiDictionaryLoadsAndFormatterIsIdempotent()
    {
        var formatter = SpokenEmojiFormatter.LoadBundled();
        var once = formatter.Format("thumbs up emoji and rocket emoji");

        Assert.Equal("👍 and 🚀", once);
        Assert.Equal(once, formatter.Format(once));
    }

    [Theory]
    [InlineData("thumbs up emoji", "👍")]
    [InlineData("send a fire emoji", "send a 🔥")]
    [InlineData("rocket emoji we shipped it", "🚀 we shipped it")]
    [InlineData("smiling face emoticon thanks", "🙂 thanks")]
    [InlineData("THUMBS UP EMOJI!", "👍!")]
    [InlineData("happy birthday Emma red heart emoji", "happy birthday Emma ❤️")]
    [InlineData("thumbs up, emoji", "👍")]
    [InlineData("thumbs up — emoji", "👍")]
    [InlineData("\"thumbs up emoji\"", "\"👍\"")]
    [InlineData("thumbs up emoji and rocket emoji", "👍 and 🚀")]
    [InlineData("sod face emoji", "😢")]
    [InlineData("sod emoji", "😢")]
    public void SpokenEmojiFormatterMatchesMacPositiveCases(string input, string expected)
    {
        Assert.Equal(expected, SpokenEmojiFormatter.LoadBundled().Format(input));
    }

    [Theory]
    [InlineData("I want a rocket ride to the moon")]
    [InlineData("I sent three thumbs up emojis to the team")]
    [InlineData("the red heart emoji category is confusing")]
    [InlineData("the fire emoji feature is great")]
    [InlineData("the smiling face emoji symbol")]
    [InlineData("the red heart emoji meaning is universal")]
    [InlineData("thumbs 👍 up emoji")]
    public void SpokenEmojiFormatterDeclinesUnsafeOrLiteralCases(string input)
    {
        Assert.Equal(input, SpokenEmojiFormatter.LoadBundled().Format(input));
    }

    [Fact]
    public void EmojiRestorerReinsertsDroppedGlyphWithoutChangingKeptGlyphs()
    {
        var restored = EmojiRestorer.Restore(
            "Ship it today.",
            "Ship it 🚀 today.");
        var kept = EmojiRestorer.Restore(
            "Ship it 🚀 today.",
            "Ship it 🚀 today.");

        Assert.Equal("Ship it 🚀 today.", restored.Text);
        Assert.Equal(1, restored.Dropped);
        Assert.Equal(1, restored.Restored);
        Assert.Equal("Ship it 🚀 today.", kept.Text);
        Assert.Equal(0, kept.Dropped);
    }

    public static TheoryData<string, string, string> EmojiRestoreParityCases => new()
    {
        { "Shipped it.", "Shipped it 🚀.", "Shipped it 🚀." },
        { "Wait, that is wrong.", "👀 wait that is wrong.", "👀 Wait, that is wrong." },
        { "This launch is huge.", "This launch 🔥🔥🔥 is huge.", "This launch 🔥🔥🔥 is huge." },
        { "Miami trip.", "Miami ☀️ 🌴 trip.", "Miami ☀️ 🌴 trip." },
        { "Très bien, fini le projet.", "Très bien 🎉 fini le projet.", "Très bien 🎉, fini le projet." },
        { "Check example.com for details.", "Check example.com 🔥 for details.", "Check example.com 🔥 for details." },
        { "First we update the dashboard, and the dashboard shows metrics.", "First we update the dashboard 🔥. The dashboard shows metrics.", "First we update the dashboard 🔥, and the dashboard shows metrics." },
        { "I shipped the auth refactor. The batch job is next.", "I shipped the auth refactor and the batch job is next 🚀", "I shipped the auth refactor. The batch job is next 🚀." },
        { "We shipped it. Users love it.", "We shipped it 🚀 and users love it.", "We shipped it 🚀. Users love it." },
        { "Actually, is more accurate.", "Actually, 😢 is more accurate.", "Actually, 😢 is more accurate." },
        { "Mike, can you take the Figma review? Link is in the channel.", "Mike can you take the Figma review 👍 link is in the channel", "Mike, can you take the Figma review? 👍 Link is in the channel." },
        { "Conversion hit 12.5%.", "Conversion hit 12.5% 🔥", "Conversion hit 12.5% 🔥." },
        { "The launch went well and the demo 🔥 crushed it.", "The launch 🔥 went well and the demo 🔥 crushed it.", "The launch 🔥 went well and the demo 🔥 crushed it." },
        { "Happy birthday, bro 🎉.", "Happy birthday bro 🎉 🎂.", "Happy birthday, bro 🎉 🎂." },
        { "Party A 🎉 🚀 B end.", "Party A 🎉 🎂 🚀 B end.", "Party A 🎉 🎂 🚀 B end." },
        { "Done.", "Done. 🚀", "Done. 🚀" },
        { "I really think I can’t.", "I really think I can't 🔥.", "I really think I can’t 🔥." },
        { "", "hi 🔥", "🔥" },
        { "Done. Onto the next thing.", "Done. 🚀 onto the next thing.", "Done. 🚀 Onto the next thing." },
    };

    [Theory]
    [MemberData(nameof(EmojiRestoreParityCases))]
    public void EmojiRestorerMatchesPinnedMacPlacementCases(
        string polished,
        string prePolish,
        string expected)
    {
        var result = EmojiRestorer.Restore(polished, prePolish);

        Assert.Equal(expected, result.Text);
        Assert.Equal(result.Dropped, result.Restored);
    }

    [Fact]
    public void EmojiRestorerTreatsPresentationAndSkinToneVariantsAsKept()
    {
        var presentation = EmojiRestorer.Restore("Love you ❤ so much.", "Love you ❤️ so much.");
        var skinTone = EmojiRestorer.Restore("Nice work 👍 everyone.", "Nice work 👍🏽 everyone.");

        Assert.Equal(0, presentation.Dropped);
        Assert.Equal("Love you ❤ so much.", presentation.Text);
        Assert.Equal(0, skinTone.Dropped);
        Assert.Equal("Nice work 👍 everyone.", skinTone.Text);
    }

    [Fact]
    public void CustomWordFuzzyFallbackAndHardeningThresholdsMatchMacContract()
    {
        var result = CustomWordCorrector.Correct(
            "deployed to kuberntes",
            [new CustomWordEntry("Kubernetes", "Kubernetes")]);

        Assert.Equal("deployed to Kubernetes", result.Text);
        Assert.Equal(1, result.ReplacementCount);
        Assert.Equal(0, CustomWordCorrector.LargeVocabularyPenalty(101));
        Assert.Equal(0.02, CustomWordCorrector.LargeVocabularyPenalty(600), precision: 5);
        Assert.Equal(0.06, CustomWordCorrector.LargeVocabularyPenalty(5_000), precision: 5);
        Assert.Equal(0.04, CustomWordCorrector.LengthAwareAdjustment(16), precision: 5);
    }

    private static DeterministicTextRequest CreateRequest(ParityCase item) => new(
        new Transcript(
            DictationSessionId.Create(),
            item.Input,
            item.EngineId,
            DetectedLanguage: item.Language),
        item.CustomWords,
        item.Options);

    private static DeterministicTextRequest CreateRequest(string text) => new(
        new Transcript(DictationSessionId.Create(), text, "test", DetectedLanguage: "en"),
        [],
        new DeterministicTextOptions(false, false, false, false));

    private static async Task<IReadOnlyList<ParityCase>> LoadParityCasesAsync()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "deterministic-parity.json");
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<ParityCase>>(stream, SerializerOptions)
            ?? throw new InvalidOperationException("The deterministic parity fixture is invalid.");
    }

    private sealed record ParityCase(
        string Name,
        string Category,
        string Input,
        string Expected,
        string? Language,
        string EngineId,
        IReadOnlyList<CustomWordEntry> CustomWords,
        DeterministicTextOptions Options,
        string? Difference = null);

    private sealed record MacParityRow(
        string Input,
        string Expected,
        string Category,
        string Slice);

    private sealed class DelegateStep(
        DeterministicTextStage stage,
        Func<DeterministicTextContext, DeterministicTextContext> process,
        TimeSpan? timeout = null) : IDeterministicTextStep
    {
        public DeterministicTextStage Stage => stage;

        public TimeSpan Timeout => timeout ?? TimeSpan.FromSeconds(1);

        public bool IsEnabled(DeterministicTextContext context) => true;

        public DeterministicTextContext Process(DeterministicTextContext context) => process(context);
    }
}
