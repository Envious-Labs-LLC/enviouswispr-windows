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
        var provider = new FakePolishProvider(_ => "EnviousWispr is here.");
        var (finalizer, _, _) = Build();

        var finalized = await finalizer.FinalizeAsync(
            Spoken("envy wisper is here"),
            [new CustomWordEntry("envy wisper", "EnviousWispr")],
            AllOn,
            new PolishSetup(provider, UsesLocalRuntime: false, RuntimeResourceKind.Cpu),
            CancellationToken.None);

        Assert.Equal("EnviousWispr is here", finalized.Processed.DeterministicText);
        Assert.Equal(["EnviousWispr"], provider.LastVocabulary);
        Assert.True(finalized.WasPolished);
    }

    private static Transcript Spoken(string text) =>
        new(DictationSessionId.Create(), text, "whisper", DetectedLanguage: "en");

    private static (TranscriptFinalizer Finalizer, FakeEffects Effects, FakeAdmission Admission) Build()
    {
        var effects = new FakeEffects();
        var admission = new FakeAdmission();
        var finalizer = new TranscriptFinalizer(
            new DeterministicTextPipeline(),
            new PolishExecutor(admission, effects),
            effects);
        return (finalizer, effects, admission);
    }

    private sealed class FakeEffects : ITranscriptFinalizationEffects
    {
        public List<string> Trace { get; } = [];

        public void RecordDeterministicProcessingStarted() => Trace.Add("DeterministicProcessingStarted");

        public void EmitStageReceipts(IReadOnlyList<DeterministicStageReceipt> receipts, bool emojiRestorationOnly) =>
            Trace.Add(emojiRestorationOnly ? "StageReceipts:restoration" : "StageReceipts:main");

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
