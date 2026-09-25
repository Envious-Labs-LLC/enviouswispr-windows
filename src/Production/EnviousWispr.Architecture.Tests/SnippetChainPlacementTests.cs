using System.Globalization;
using System.Text.RegularExpressions;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;
using EnviousWispr.PostProcessing;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// Where the snippet stage sits and what the text pass does around it: masked first, never seen by
/// polish, carried through every deterministic stage, put back exactly. Ported from macOS
/// <c>SnippetChainPlacementTests</c> onto this app's finalizer, with the real deterministic stages.
/// </summary>
public sealed class SnippetChainPlacementTests
{
    private static readonly DeterministicTextOptions AllOn = new(true, true, true, true);

    private static readonly SnippetVocabulary Email =
        new([new SnippetEntry("my email", "sam@example.com")], SnippetVocabulary.DefaultKeyword);

    private static Transcript Spoken(string text) =>
        new(DictationSessionId.Create(), text, "parakeet", DetectedLanguage: null);

    private static PolishSetup Polishing(IPolishProvider provider) =>
        new(provider, UsesLocalRuntime: false, RuntimeResourceKind.Cpu);

    // Placement and masking.

    /// <summary>
    /// THE PROMISE THE FEATURE IS FOR: the provider is handed a placeholder where the saved text
    /// belongs, never the saved text, and the saved text arrives exactly as written, inside the polish.
    /// </summary>
    [Fact]
    public async Task ThePolishProviderNeverSeesTheSnippetAndTheSnippetArrivesExactlyInsideThePolish()
    {
        var provider = new RecordingPolish(input => input.Replace("please", "Please", StringComparison.Ordinal) + "!");
        var world = new World();

        var finalized = await world.Finalizer.FinalizeAsync(
            Spoken("please email backslash my sign off"),
            [],
            AllOn,
            new SnippetVocabulary([new SnippetEntry("my sign off", "Kind regards,\nSam SMITH  (Envious Labs)")], "backslash"),
            Polishing(provider),
            CancellationToken.None);

        var sent = Assert.Single(provider.Inputs);
        Assert.DoesNotContain("Kind regards", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("SMITH", sent, StringComparison.Ordinal);
        Assert.Matches("^please email EWSNIP[0-9A-F]{32}$", sent);
        Assert.True(finalized.WasPolished);
        Assert.Equal("Please email Kind regards,\nSam SMITH  (Envious Labs)!", finalized.Processed.Output.Text);
        Assert.Equal("please email Kind regards,\nSam SMITH  (Envious Labs)", finalized.Processed.DeterministicText);
        Assert.Equal(1, finalized.SnippetsExpanded);
        world.AssertNoPlaceholderEscaped(finalized);
    }

    [Fact]
    public async Task TheSnippetStageIsTheFirstReceiptAndSaysOnlyThatASnippetFired()
    {
        var world = new World();

        await world.Finalizer.FinalizeAsync(Spoken("write to backslash my email"), [], AllOn, Email, polish: null, CancellationToken.None);

        var first = world.MainReceipts[0];
        Assert.Equal(DeterministicTextStage.SnippetExpansion, first.Stage);
        Assert.Equal(DeterministicStageStatus.Completed, first.Status);
        Assert.True(first.Changed);
        Assert.Equal(DeterministicTextStage.CustomWords, world.MainReceipts[1].Stage);
    }

    /// <summary>
    /// FIRST ON PURPOSE: a custom word that would rewrite a trigger word must not decide whether the
    /// snippet fires. The same words with no keyword are corrected, which proves the word list is live.
    /// </summary>
    [Fact]
    public async Task ACustomWordCannotStopASnippetFromFiringBecauseTheSnippetIsReadFirst()
    {
        IReadOnlyList<CustomWordEntry> words = [new CustomWordEntry("email", "e-mail")];
        var world = new World();

        var fired = await world.Finalizer.FinalizeAsync(Spoken("write to backslash my email"), words, AllOn, Email, polish: null, CancellationToken.None);
        var control = await world.Finalizer.FinalizeAsync(Spoken("write to my email"), words, AllOn, Email, polish: null, CancellationToken.None);

        Assert.Equal("write to sam@example.com", fired.Processed.Output.Text);
        Assert.Equal("write to my e-mail", control.Processed.Output.Text);
    }

    /// <summary>
    /// The keyword survives spoken punctuation: this app's spoken-punctuation patterns have no
    /// "backslash", and the snippet is masked before them anyway. The stop the person said after the
    /// trigger is still turned into a full stop, around the snippet.
    /// </summary>
    [Fact]
    public async Task TheKeywordReachesTheSnippetStageWithSpokenPunctuationOn()
    {
        var world = new World();

        var finalized = await world.Finalizer.FinalizeAsync(
            Spoken("send it to backslash my email period thanks"),
            [],
            AllOn,
            Email,
            polish: null,
            CancellationToken.None);
        var unmatched = await world.Finalizer.FinalizeAsync(
            Spoken("the path is backslash users"),
            [],
            AllOn,
            Email,
            polish: null,
            CancellationToken.None);

        Assert.Equal("send it to sam@example.com. Thanks", finalized.Processed.Output.Text);
        Assert.Equal("the path is backslash users", unmatched.Processed.Output.Text);
    }

    /// <summary>
    /// THE SENTINEL MUST COME THROUGH EVERY DETERMINISTIC STAGE BYTE FOR BYTE: custom words (with a
    /// look-alike of the prefix among them), filler removal, spoken emoji, inverse text normalisation
    /// with spoken punctuation, and British spelling, all on, around numbers, punctuation and emoji
    /// words. Swept over many random sentinels so every hex digit meets the stages.
    /// </summary>
    [Fact]
    public async Task EverySentinelSurvivesEveryDeterministicStageUntouched()
    {
        var pipeline = PatientPipeline.Create();
        IReadOnlyList<CustomWordEntry> words =
        [
            new CustomWordEntry("ewsnip", "SNIPPED"),
            new CustomWordEntry("envy wisper", "EnviousWispr"),
            new CustomWordEntry("color", "colour"),
        ];
        var options = new DeterministicTextOptions(true, true, true, true, EnglishSpelling.British);
        for (var round = 0; round < 64; round++)
        {
            var first = SnippetExpander.RandomCandidate();
            var second = SnippetExpander.RandomCandidate();
            var third = SnippetExpander.RandomCandidate();
            var text =
                $"um {first} so like I paid twenty five dollars comma the color is {second} period " +
                $"smiley face ({third}) at five pm on march third 2026 question mark envy wisper {first}x";
            var result = await pipeline.ProcessAsync(new DeterministicTextRequest(Spoken(text), words, options));

            foreach (var sentinel in new[] { first, second, third })
            {
                Assert.Matches($"(^|[^0-9A-Za-z]){sentinel}([^0-9A-Za-z]|$)", result.Output.Text);
            }

            Assert.Equal(1, Regex.Count(result.Output.Text, $"{second}"));
            Assert.Contains($"({third})", result.Output.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AnEmptyStoreLeavesTheTakeByteIdenticalAndTheStageSkipped()
    {
        var world = new World();
        const string spoken = "backslash my email address please";

        var empty = await world.Finalizer.FinalizeAsync(Spoken(spoken), [], AllOn, SnippetVocabulary.Empty, polish: null, CancellationToken.None);
        var receipt = world.MainReceipts[0];
        var noSnippets = await world.Finalizer.FinalizeAsync(
            Spoken(spoken), [], AllOn, new SnippetVocabulary([], SnippetVocabulary.DefaultKeyword), polish: null, CancellationToken.None);

        Assert.Equal("backslash my email address please", empty.Processed.Output.Text);
        Assert.Equal(empty.Processed.Output.Text, noSnippets.Processed.Output.Text);
        Assert.Equal(DeterministicStageStatus.Skipped, receipt.Status);
        Assert.Equal(0, empty.SnippetsExpanded);
    }

    // Polish fallbacks.

    /// <summary>A polish that lost the placeholder is refused WHOLE; the cleaned text with the snippet in place is delivered.</summary>
    [Fact]
    public async Task APolishThatDropsTheSentinelIsRefusedWholeAndTheSnippetStillArrives()
    {
        var provider = new RecordingPolish(_ => "Please write to your address.");
        var world = new World();

        var finalized = await world.Finalizer.FinalizeAsync(
            Spoken("please write to backslash my email"), [], AllOn, Email, Polishing(provider), CancellationToken.None);

        Assert.Equal("please write to sam@example.com", finalized.Processed.Output.Text);
        Assert.Equal(PolishOutputVerdict.RefusedSnippetLost, finalized.PolishVerdict);
        Assert.False(finalized.WasPolished);
        Assert.Equal(1, world.PolishRefusals);
        Assert.Equal(["please write to sam@example.com"], world.RecoverySaves);
        world.AssertNoPlaceholderEscaped(finalized);
    }

    [Fact]
    public async Task APolishThatDuplicatesTheSentinelIsRefusedWholeToo()
    {
        var provider = new RecordingPolish(input => input + " " + input[input.IndexOf("EWSNIP", StringComparison.Ordinal)..]);
        var world = new World();

        var finalized = await world.Finalizer.FinalizeAsync(
            Spoken("please write to backslash my email"), [], AllOn, Email, Polishing(provider), CancellationToken.None);

        Assert.Equal("please write to sam@example.com", finalized.Processed.Output.Text);
        Assert.Equal(PolishOutputVerdict.RefusedSnippetLost, finalized.PolishVerdict);
        world.AssertNoPlaceholderEscaped(finalized);
    }

    /// <summary>A take that is nothing but a snippet has nothing a model could improve, so no model is asked.</summary>
    [Fact]
    public async Task AWholeDictationSnippetIsNeverSentToPolish()
    {
        var provider = new RecordingPolish(input => input);
        var world = new World();

        var finalized = await world.Finalizer.FinalizeAsync(
            Spoken("backslash my email."), [], AllOn, Email, Polishing(provider), CancellationToken.None);

        Assert.Empty(provider.Inputs);
        Assert.Equal("sam@example.com", finalized.Processed.Output.Text);
        Assert.Null(finalized.Polish);
    }

    // Fill-ins and the clipboard.

    /// <summary>
    /// THE CLIPBOARD IS READ ONLY WHEN A SNIPPET THAT USES IT FIRES: not for a take with no match, not
    /// for a snippet with no fill-in, not for a clipboard snippet merely saved, and once when it fires.
    /// </summary>
    [Fact]
    public async Task TheClipboardIsReadOnlyWhenASnippetThatUsesItActuallyFires()
    {
        var vocabulary = new SnippetVocabulary(
            [new SnippetEntry("my sign off", "thanks, sam"), new SnippetEntry("my link", "see {{clipboard}}")],
            SnippetVocabulary.DefaultKeyword);
        var world = new World { Clipboard = "https://x.dev" };

        var noMatch = await world.Finalizer.FinalizeAsync(Spoken("nothing to see here"), [], AllOn, vocabulary, polish: null, CancellationToken.None);
        Assert.Equal(0, world.ClipboardReads);
        Assert.Equal(0, noMatch.SnippetsExpanded);

        var literal = await world.Finalizer.FinalizeAsync(Spoken("ok backslash my sign off"), [], AllOn, vocabulary, polish: null, CancellationToken.None);
        Assert.Equal(0, world.ClipboardReads);
        Assert.Equal("ok thanks, sam", literal.Processed.Output.Text);

        var fired = await world.Finalizer.FinalizeAsync(Spoken("ok backslash my link"), [], AllOn, vocabulary, polish: null, CancellationToken.None);
        Assert.Equal(1, world.ClipboardReads);
        Assert.Equal("ok see https://x.dev", fired.Processed.Output.Text);
    }

    [Fact]
    public async Task AClipboardHoldingNoTextFillsInAsNothingAndIsStillReadOnce()
    {
        var vocabulary = new SnippetVocabulary([new SnippetEntry("my link", "see {{clipboard}}")], SnippetVocabulary.DefaultKeyword);
        var world = new World { Clipboard = null };

        var finalized = await world.Finalizer.FinalizeAsync(Spoken("ok backslash my link"), [], AllOn, vocabulary, polish: null, CancellationToken.None);

        Assert.Equal(1, world.ClipboardReads);
        Assert.Equal("ok see ", finalized.Processed.Output.Text);
    }

    /// <summary>NOT SILENCE: a snippet whose whole text is an empty fill-in delivers the spoken words, never nothing.</summary>
    [Fact]
    public async Task ASnippetThatIsOnlyAnEmptyFillInDeliversTheSpokenWords()
    {
        var vocabulary = new SnippetVocabulary([new SnippetEntry("my link", "{{clipboard}}")], SnippetVocabulary.DefaultKeyword);
        var world = new World { Clipboard = string.Empty };

        var finalized = await world.Finalizer.FinalizeAsync(Spoken("backslash my link"), [], AllOn, vocabulary, polish: null, CancellationToken.None);

        Assert.Equal("backslash my link", finalized.Processed.Output.Text);
        Assert.Equal(0, finalized.SnippetsExpanded);
        Assert.False(world.MainReceipts[0].Changed);
    }

    /// <summary>The instant, zone and culture the stage is given are the ones the delivered text carries.</summary>
    [Fact]
    public async Task TheInjectedInstantZoneAndCultureReachTheDeliveredText()
    {
        var vocabulary = new SnippetVocabulary([new SnippetEntry("my stamp", "Filed {{date}} at {{time}}")], SnippetVocabulary.DefaultKeyword);
        var world = new World
        {
            Clock = new FixedClock(DateTimeOffset.FromUnixTimeSeconds(1_789_584_300), TimeZoneInfo.FindSystemTimeZoneById("America/New_York")),
            Culture = CultureInfo.GetCultureInfo("en-GB"),
        };

        var finalized = await world.Finalizer.FinalizeAsync(Spoken("backslash my stamp"), [], AllOn, vocabulary, polish: null, CancellationToken.None);

        Assert.Equal("Filed 16 Sept 2026 at 14:45", finalized.Processed.Output.Text);
        Assert.Equal(0, world.ClipboardReads);
    }

    /// <summary>
    /// A STAND-DOWN LOSES THE SNIPPET, NEVER THE WORDS: a clipboard that never answers runs the stage out
    /// of time, the take is delivered as spoken with no placeholder in it, and the pass says it degraded.
    /// </summary>
    [Fact]
    public async Task AStageThatRunsOutOfTimeDeliversTheSpokenWordsAndSaysSo()
    {
        var vocabulary = new SnippetVocabulary([new SnippetEntry("my link", "see {{clipboard}}")], SnippetVocabulary.DefaultKeyword);
        var never = new TaskCompletionSource<string?>();
        var world = new World { Timeout = TimeSpan.FromMilliseconds(200), ClipboardReader = _ => never.Task };

        var finalized = await world.Finalizer.FinalizeAsync(Spoken("ok backslash my link"), [], AllOn, vocabulary, polish: null, CancellationToken.None);

        Assert.Equal("ok backslash my link", finalized.Processed.Output.Text);
        Assert.Equal(DeterministicStageStatus.TimedOut, world.MainReceipts[0].Status);
        Assert.True(finalized.Processed.IsDegraded);
        Assert.Equal(0, finalized.SnippetsExpanded);
    }

    /// <summary>
    /// A STAND-DOWN IS NOT POLISHED: the keyword and trigger are still in the text unmasked, so a
    /// model would see and could rewrite them. Without the rule the provider is asked here.
    /// </summary>
    [Fact]
    public async Task AStageThatStoodDownIsNeverSentToPolish()
    {
        var vocabulary = new SnippetVocabulary([new SnippetEntry("my link", "see {{clipboard}}")], SnippetVocabulary.DefaultKeyword);
        var never = new TaskCompletionSource<string?>();
        var provider = new RecordingPolish(input => input + "!");
        var world = new World { Timeout = TimeSpan.FromMilliseconds(200), ClipboardReader = _ => never.Task };

        var finalized = await world.Finalizer.FinalizeAsync(
            Spoken("ok send the backslash my link now"), [], AllOn, vocabulary, Polishing(provider), CancellationToken.None);

        Assert.Empty(provider.Inputs);
        Assert.Null(finalized.Polish);
        Assert.Equal("ok send the backslash my link now", finalized.Processed.Output.Text);
    }

    /// <summary>A rolled-back snippet (an empty fill-in, spoken words delivered instead) is not polished either.</summary>
    [Fact]
    public async Task ARolledBackSnippetIsNeverSentToPolish()
    {
        var vocabulary = new SnippetVocabulary([new SnippetEntry("my link", "{{clipboard}}")], SnippetVocabulary.DefaultKeyword);
        var provider = new RecordingPolish(input => input + "!");
        var world = new World { Clipboard = string.Empty };

        var finalized = await world.Finalizer.FinalizeAsync(
            Spoken("backslash my link"), [], AllOn, vocabulary, Polishing(provider), CancellationToken.None);

        Assert.Empty(provider.Inputs);
        Assert.Null(finalized.Polish);
        Assert.Equal("backslash my link", finalized.Processed.Output.Text);
    }

    /// <summary>The control for both: an ordinary take with the same provider IS polished, so the empty lists above are the rule.</summary>
    [Fact]
    public async Task AnOrdinaryTakeWithTheSameProviderIsPolished()
    {
        var vocabulary = new SnippetVocabulary([new SnippetEntry("my link", "see {{clipboard}}")], SnippetVocabulary.DefaultKeyword);
        var provider = new RecordingPolish(input => input + "!");
        var world = new World();

        var finalized = await world.Finalizer.FinalizeAsync(
            Spoken("ok send it now"), [], AllOn, vocabulary, Polishing(provider), CancellationToken.None);

        Assert.Single(provider.Inputs);
        Assert.Equal("ok send it now!", finalized.Processed.Output.Text);
    }

    // The world.

    private sealed class World : ITranscriptFinalizationEffects
    {
        private TranscriptFinalizer? _finalizer;

        public string? Clipboard { get; set; }

        public int ClipboardReads { get; private set; }

        public Func<CancellationToken, Task<string?>>? ClipboardReader { get; set; }

        public TimeProvider Clock { get; set; } = TimeProvider.System;

        public CultureInfo Culture { get; set; } = CultureInfo.GetCultureInfo("en-US");

        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

        public List<DeterministicStageReceipt> MainReceipts { get; } = [];

        public List<string> RecoverySaves { get; } = [];

        public int PolishRefusals { get; private set; }

        public TranscriptFinalizer Finalizer => _finalizer ??= new TranscriptFinalizer(
            PatientPipeline.Create(),
            new PolishExecutor(new RefusingAdmission(), this, () => []),
            this,
            new SnippetExpansionStage(
                new SnippetExpander(),
                Clock,
                () => Culture,
                token =>
                {
                    ClipboardReads++;
                    return ClipboardReader?.Invoke(token) ?? Task.FromResult(Clipboard);
                },
                Timeout));

        /// <summary>Nothing the finalizer hands on, stores or logs carries a placeholder.</summary>
        public void AssertNoPlaceholderEscaped(FinalizedTranscript finalized)
        {
            Assert.DoesNotContain("EWSNIP", finalized.Processed.Output.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("EWSNIP", finalized.Processed.DeterministicText, StringComparison.Ordinal);
            Assert.DoesNotContain("EWSNIP", finalized.Request.Transcript.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(RecoverySaves, saved => saved.Contains("EWSNIP", StringComparison.Ordinal));
        }

        public void RecordDeterministicProcessingStarted()
        {
            MainReceipts.Clear();
            RecoverySaves.Clear();
        }

        public void EmitStageReceipts(IReadOnlyList<DeterministicStageReceipt> receipts, bool emojiRestorationOnly)
        {
            if (!emojiRestorationOnly)
            {
                MainReceipts.AddRange(receipts);
            }
        }

        public Task SaveRecoveryTextAsync(ProcessedText output, CancellationToken cancellationToken)
        {
            RecoverySaves.Add(output.Text);
            return Task.CompletedTask;
        }

        public void RecordPolishRefused() => PolishRefusals++;

        public void RecordDeterministicProcessingFinished(bool degraded, long elapsedMilliseconds)
        {
        }

        public void RecordPolishStarted(string providerId)
        {
        }

        public void RecordPolishFinished(string providerId, PolishResult result, bool usedLocalRuntime, long elapsedMilliseconds)
        {
        }
    }

    private sealed class FixedClock(DateTimeOffset now, TimeZoneInfo zone) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => zone;
    }

    private sealed class RecordingPolish(Func<string, string> answer) : IPolishProvider
    {
        public string ProviderId => "fake";

        public List<string> Inputs { get; } = [];

        public Task<PolishResult> TryPolishAsync(PolishRequest request, CancellationToken cancellationToken = default)
        {
            Inputs.Add(request.Input.Text);
            return Task.FromResult(new PolishResult(request.Input with { Text = answer(request.Input.Text) }, PolishAttemptStatus.Polished));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>These tests polish through a remote provider, which never asks for the local resource.</summary>
    private sealed class RefusingAdmission : IRuntimeResourceAdmission
    {
        public Task<RuntimeResourceAcquireResult> AcquireAsync(
            RuntimeResourceKind resource,
            RuntimeWorkloadKind workload,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new RuntimeResourceAcquireResult(
                Succeeded: false,
                Error: new AppError(AppErrorCode.RuntimeResourceBusy, AppErrorStage.RuntimeResource, CanRetry: true)));
    }
}
