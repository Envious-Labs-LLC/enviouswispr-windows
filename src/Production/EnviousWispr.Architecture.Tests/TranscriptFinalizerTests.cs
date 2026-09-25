using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// The finalizer decides what text is delivered. The real deterministic pipeline runs; polish and the
/// runtime resource are fakes; the shell is a fake that writes every effect down in order.
/// </summary>
public sealed class TranscriptFinalizerTests
{
    private static readonly DeterministicTextOptions AllOn = new(true, true, true, true);

    [Fact]
    public async Task WithoutAProviderTheDeterministicTextIsTheAnswer()
    {
        var (finalizer, effects, _) = Build();

        var finalized = await finalizer.FinalizeAsync(Spoken("um hello world"), [], AllOn, polish: null, CancellationToken.None);

        Assert.Equal("hello world", finalized.Processed.Output.Text);
        Assert.Null(finalized.Polish);
        Assert.Equal(PolishOutputVerdict.Accepted, finalized.PolishVerdict);
        Assert.False(finalized.WasPolished);
        Assert.Equal(
            ["DeterministicProcessingStarted", "StageReceipts:main", "SaveRecovery:hello world", "StageReceipts:restoration", "DeterministicProcessingFinished:degraded=False"],
            effects.Trace);
    }

    [Fact]
    public async Task AnAcceptedPolishIsUsedAndSavedForRecoveryASecondTime()
    {
        var provider = new FakePolishProvider(_ => "Hello, world.");
        var (finalizer, effects, _) = Build();

        var finalized = await finalizer.FinalizeAsync(
            Spoken("hello world"), [], AllOn, new PolishSetup(provider, UsesLocalRuntime: false, RuntimeResourceKind.Cpu), CancellationToken.None);

        Assert.Equal("Hello, world.", finalized.Processed.Output.Text);
        Assert.Equal("hello world", finalized.Processed.DeterministicText);
        Assert.True(finalized.WasPolished);
        Assert.Equal(
            ["DeterministicProcessingStarted", "StageReceipts:main", "SaveRecovery:hello world", "PolishStarted:fake", "PolishFinished:fake:Polished:local=False", "SaveRecovery:Hello, world.", "StageReceipts:restoration", "DeterministicProcessingFinished:degraded=False"],
            effects.Trace);
    }

    [Fact]
    public async Task APolishThatComesOffTheRailsIsRefusedAndTheDeterministicTextSurvives()
    {
        // A confident answer that is not the text: the guard refuses it, the log says so, and the
        // user keeps the cleaned words they already had.
        var provider = new FakePolishProvider(_ => string.Empty);
        var (finalizer, effects, _) = Build();

        var finalized = await finalizer.FinalizeAsync(
            Spoken("hello world"), [], AllOn, new PolishSetup(provider, UsesLocalRuntime: false, RuntimeResourceKind.Cpu), CancellationToken.None);

        Assert.Equal("hello world", finalized.Processed.Output.Text);
        Assert.NotEqual(PolishOutputVerdict.Accepted, finalized.PolishVerdict);
        Assert.False(finalized.WasPolished);
        Assert.Contains("PolishRefused", effects.Trace);
        Assert.Single(effects.Trace, entry => entry.StartsWith("SaveRecovery:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALocalProviderIsAskedOnlyWhileItHoldsItsResource()
    {
        var provider = new FakePolishProvider(_ => "Hello world.");
        var (finalizer, effects, admission) = Build();
        provider.Admission = admission;

        var finalized = await finalizer.FinalizeAsync(
            Spoken("hello world"), [], AllOn, new PolishSetup(provider, UsesLocalRuntime: true, RuntimeResourceKind.Accelerator), CancellationToken.None);

        Assert.True(finalized.WasPolished);
        Assert.Equal([RuntimeResourceKind.Accelerator], admission.Requested);
        Assert.Equal(RuntimeWorkloadKind.LocalPolish, admission.Workload);
        Assert.Equal(TimeSpan.FromSeconds(2), admission.Timeout);
        Assert.True(provider.HeldLeaseWhenAsked, "the provider ran before the lease was taken or after it was released");
        Assert.True(admission.Released);
        Assert.Contains("PolishFinished:fake:Polished:local=True", effects.Trace);
    }

    [Fact]
    public async Task ABusyResourceLeavesTheDeterministicTextAndNeverAsksTheProvider()
    {
        var provider = new FakePolishProvider(_ => "should never run");
        var (finalizer, effects, admission) = Build();
        admission.Refuse = true;

        var finalized = await finalizer.FinalizeAsync(
            Spoken("hello world"), [], AllOn, new PolishSetup(provider, UsesLocalRuntime: true, RuntimeResourceKind.Cpu), CancellationToken.None);

        Assert.Equal("hello world", finalized.Processed.Output.Text);
        Assert.Equal(PolishAttemptStatus.Unavailable, finalized.Polish?.Status);
        Assert.Equal(AppErrorCode.RuntimeResourceBusy, finalized.Polish?.Error?.Code);
        Assert.Equal(0, provider.Calls);
        Assert.Contains("PolishFinished:fake:Unavailable:local=True", effects.Trace);
        Assert.False(finalized.WasPolished);
    }

    [Fact]
    public async Task NothingToPolishMeansTheProviderIsNotAsked()
    {
        var provider = new FakePolishProvider(_ => "x");
        var (finalizer, effects, _) = Build();

        var finalized = await finalizer.FinalizeAsync(
            Spoken("um"), [], AllOn, new PolishSetup(provider, UsesLocalRuntime: false, RuntimeResourceKind.Cpu), CancellationToken.None);

        Assert.Equal(string.Empty, finalized.Processed.Output.Text.Trim());
        Assert.Null(finalized.Polish);
        Assert.Equal(0, provider.Calls);
        Assert.DoesNotContain(effects.Trace, entry => entry.StartsWith("PolishStarted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheCustomWordsReachBothTheStagesAndThePolishVocabulary()
    {
        IReadOnlyList<CustomWordEntry> words = [new CustomWordEntry("envy wisper", "EnviousWispr")];
        var provider = new FakePolishProvider(_ => "EnviousWispr is here.");
        var (finalizer, _, _) = Build(() => words);

        var finalized = await finalizer.FinalizeAsync(
            Spoken("envy wisper is here"),
            words,
            AllOn,
            new PolishSetup(provider, UsesLocalRuntime: false, RuntimeResourceKind.Cpu),
            CancellationToken.None);

        Assert.Equal("EnviousWispr is here", finalized.Processed.DeterministicText);
        Assert.Equal(["EnviousWispr"], provider.LastVocabulary);
        Assert.True(finalized.WasPolished);
    }

    private static Transcript Spoken(string text) =>
        new(DictationSessionId.Create(), text, "whisper", DetectedLanguage: "en");

    private static (TranscriptFinalizer Finalizer, FakeEffects Effects, FakeAdmission Admission) Build(
        Func<IReadOnlyList<CustomWordEntry>>? currentWords = null)
    {
        var effects = new FakeEffects();
        var admission = new FakeAdmission();
        var finalizer = new TranscriptFinalizer(
            PatientPipeline.Create(),
            new PolishExecutor(admission, effects, currentWords ?? (() => [])),
            effects);
        return (finalizer, effects, admission);
    }

    [Fact]
    public async Task ThePolishVocabularyIsReadAtTheCallNotWhenTheRecordingEnded()
    {
        // A word taught while the local provider was still waiting for its resource reaches the very
        // next polish, which is what the shell did by reading its settings at the call.
        IReadOnlyList<CustomWordEntry> words = [];
        var provider = new FakePolishProvider(_ => "Hello world.");
        var (finalizer, _, admission) = Build(() => words);
        admission.BeforeGranting = () => words = [new CustomWordEntry("hello", "Hello")];

        var finalized = await finalizer.FinalizeAsync(
            Spoken("hello world"), [], AllOn, new PolishSetup(provider, UsesLocalRuntime: true, RuntimeResourceKind.Cpu), CancellationToken.None);

        Assert.True(finalized.WasPolished);
        Assert.Equal(["Hello"], provider.LastVocabulary);
    }

    [Fact]
    public async Task ARefusedAdmissionNeverReadsTheVocabulary()
    {
        var reads = 0;
        var provider = new FakePolishProvider(_ => "never");
        var (finalizer, _, admission) = Build(() => { reads++; return []; });
        admission.Refuse = true;

        await finalizer.FinalizeAsync(
            Spoken("hello world"), [], AllOn, new PolishSetup(provider, UsesLocalRuntime: true, RuntimeResourceKind.Cpu), CancellationToken.None);

        Assert.Equal(0, reads);
    }

    [Fact]
    public async Task TheReviewedTextIsUsedNotWhatTheProviderSent()
    {
        var provider = new FakePolishProvider(_ => "Sure, here is the cleaned transcript:" + "\n" + "Hello, world.");
        var (finalizer, effects, _) = Build();

        var finalized = await finalizer.FinalizeAsync(
            Spoken("hello world"), [], AllOn, new PolishSetup(provider, UsesLocalRuntime: false, RuntimeResourceKind.Cpu), CancellationToken.None);

        Assert.True(finalized.WasPolished);
        Assert.Equal("Hello, world.", finalized.Processed.Output.Text);
        Assert.Contains("SaveRecovery:Hello, world.", effects.Trace);
        Assert.DoesNotContain(effects.Trace, entry => entry.Contains("Sure, here", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheMainReceiptsCarryTheStagesThatRanAndTheRestorationReceiptComesAlone()
    {
        var provider = new FakePolishProvider(_ => "Hello world.");
        var (finalizer, effects, _) = Build();

        await finalizer.FinalizeAsync(
            Spoken("um hello world"), [], AllOn, new PolishSetup(provider, UsesLocalRuntime: false, RuntimeResourceKind.Cpu), CancellationToken.None);

        Assert.Equal(
            [DeterministicTextStage.CustomWords, DeterministicTextStage.FillerAndFalseStarts, DeterministicTextStage.SpokenEmoji, DeterministicTextStage.InverseTextNormalization, DeterministicTextStage.EnglishSpelling, DeterministicTextStage.EnglishSpellingAfterPolish, DeterministicTextStage.EmojiRestoration],
            effects.MainReceipts.Select(receipt => receipt.Stage));
        // NOT "Completed". The filler and restoration stages carry 50 ms deadlines, and on a cold hosted
        // runner the first pass through a stage pays its compilation and can time out - main went red
        // on exactly that (#158). What this test is about is which receipts reach which emission, and a
        // stage that ran is any stage that was not skipped. Timing belongs to the pipeline's own tests.
        Assert.NotEqual(DeterministicStageStatus.Skipped, effects.MainReceipts.Single(r => r.Stage == DeterministicTextStage.FillerAndFalseStarts).Status);
        // The shell filters each emission to its half; the finalizer hands over the whole list both
        // times, so the restoration receipt in the first list is the not-yet-run Skipped one and the
        // one in the second list is the real thing.
        Assert.Equal(DeterministicStageStatus.Skipped, effects.MainReceipts.Single(r => r.Stage == DeterministicTextStage.EmojiRestoration).Status);
        Assert.NotEqual(DeterministicStageStatus.Skipped, effects.RestorationReceipts.Single(r => r.Stage == DeterministicTextStage.EmojiRestoration).Status);
    }

    private sealed class FakeEffects : ITranscriptFinalizationEffects
    {
        public List<string> Trace { get; } = [];

        public void RecordDeterministicProcessingStarted() => Trace.Add("DeterministicProcessingStarted");

        public List<DeterministicStageReceipt> MainReceipts { get; } = [];

        public List<DeterministicStageReceipt> RestorationReceipts { get; } = [];

        public void EmitStageReceipts(IReadOnlyList<DeterministicStageReceipt> receipts, bool emojiRestorationOnly)
        {
            Trace.Add(emojiRestorationOnly ? "StageReceipts:restoration" : "StageReceipts:main");
            (emojiRestorationOnly ? RestorationReceipts : MainReceipts).AddRange(receipts);
        }

        public Task SaveRecoveryTextAsync(ProcessedText output, CancellationToken cancellationToken)
        {
            Trace.Add($"SaveRecovery:{output.Text}");
            return Task.CompletedTask;
        }

        public void RecordPolishStarted(string providerId) => Trace.Add($"PolishStarted:{providerId}");

        public void RecordPolishFinished(string providerId, PolishResult result, bool usedLocalRuntime, long elapsedMilliseconds) =>
            Trace.Add($"PolishFinished:{providerId}:{result.Status}:local={usedLocalRuntime}");

        public void RecordPolishRefused() => Trace.Add("PolishRefused");

        public void RecordDeterministicProcessingFinished(bool degraded, long elapsedMilliseconds) =>
            Trace.Add($"DeterministicProcessingFinished:degraded={degraded}");
    }

    private sealed class FakeAdmission : IRuntimeResourceAdmission
    {
        public List<RuntimeResourceKind> Requested { get; } = [];

        public RuntimeWorkloadKind? Workload { get; private set; }

        public TimeSpan? Timeout { get; private set; }

        public Action? BeforeGranting { get; set; }

        public bool Refuse { get; set; }

        public bool Held { get; private set; }

        public bool Released { get; private set; }

        public Task<RuntimeResourceAcquireResult> AcquireAsync(
            RuntimeResourceKind resource,
            RuntimeWorkloadKind workload,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Requested.Add(resource);
            Workload = workload;
            Timeout = timeout;
            BeforeGranting?.Invoke();
            if (Refuse)
            {
                return Task.FromResult(new RuntimeResourceAcquireResult(
                    Succeeded: false,
                    Error: new AppError(AppErrorCode.RuntimeResourceBusy, AppErrorStage.RuntimeResource, CanRetry: true)));
            }

            Held = true;
            return Task.FromResult(new RuntimeResourceAcquireResult(Succeeded: true, new Lease(this)));
        }

        private sealed class Lease(FakeAdmission owner) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                owner.Held = false;
                owner.Released = true;
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class FakePolishProvider(Func<string, string> answer) : IPolishProvider
    {
        public string ProviderId => "fake";

        public int Calls { get; private set; }

        public bool HeldLeaseWhenAsked { get; private set; }

        public IReadOnlyList<string>? LastVocabulary { get; private set; }

        public FakeAdmission? Admission { get; set; }

        public Task<PolishResult> TryPolishAsync(PolishRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastVocabulary = request.Vocabulary;
            HeldLeaseWhenAsked = Admission?.Held ?? HeldLeaseWhenAsked;
            var output = answer(request.Input.Text);
            return Task.FromResult(new PolishResult(
                request.Input with { Text = output },
                PolishAttemptStatus.Polished));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
