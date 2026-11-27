using EnviousWispr.App.Composition;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Credentials;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.History;
using EnviousWispr.Core.Polish;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;
using EnviousWispr.Presentation;
using Xunit;

namespace EnviousWispr.Architecture.Tests;

public sealed class AdmittedMishearingAdvisorTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    private sealed class NonAdvisorProvider : IPolishProvider
    {
        public string ProviderId => "non-advisor";

        public Task<PolishResult> TryPolishAsync(PolishRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PolishResult(request.Input, PolishAttemptStatus.Unchanged));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingAdvisorProvider : IPolishProvider, IMishearingAdvisor
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _unwindGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ProviderId => "blocking-advisor";

        public bool ThrowOnSuggest { get; set; }

        public bool ReturnFailedOnCancellation { get; set; }

        public CancellationToken ObservedToken { get; private set; }

        public Task Entered => _entered.Task;

        public void AllowUnwind() => _unwindGate.TrySetResult();

        public Task<PolishResult> TryPolishAsync(PolishRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PolishResult(request.Input, PolishAttemptStatus.Unchanged));

        public async Task<MishearingAdvice> SuggestAsync(
            string spokenForm,
            IReadOnlyList<string> existing,
            CancellationToken cancellationToken = default)
        {
            ObservedToken = cancellationToken;
            _entered.TrySetResult();
            await _unwindGate.Task.ConfigureAwait(false);
            if (ReturnFailedOnCancellation && cancellationToken.IsCancellationRequested)
            {
                return MishearingAdvice.None(MishearingAdviceStatus.Failed);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOnSuggest)
            {
                throw new InvalidOperationException("Simulated provider fault");
            }

            return new MishearingAdvice(MishearingAdviceStatus.Suggested, ["Saurabh", "Sore-ub"]);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingResourceLease : IAsyncDisposable
    {
        private readonly TaskCompletionSource _disposeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _disposeGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public bool DeferDispose { get; init; }

        public Task DisposeEntered => _disposeEntered.Task;

        public void AllowDispose() => _disposeGate.TrySetResult();

        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            _disposeEntered.TrySetResult();
            if (DeferDispose)
            {
                await _disposeGate.Task.ConfigureAwait(false);
            }
        }
    }

    private sealed class SpyAdmission(BlockingResourceLease lease) : IRuntimeResourceAdmission
    {
        public int AcquireCount { get; private set; }

        public bool ThrowObjectDisposed { get; set; }

        public CancellationToken ObservedToken { get; private set; }

        public Task<RuntimeResourceAcquireResult> AcquireAsync(
            RuntimeResourceKind resource,
            RuntimeWorkloadKind workload,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            AcquireCount++;
            ObservedToken = cancellationToken;
            ObjectDisposedException.ThrowIf(ThrowObjectDisposed, "arbiter");

            return Task.FromResult(new RuntimeResourceAcquireResult(Succeeded: true, lease));
        }
    }

    private sealed class FakeSettingsStore : ISettingsStore
    {
        public Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SettingsLoadResult(AppSettings.Default, SettingsLoadStatus.Loaded));

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<SettingsResetResult> ResetAsync(AppSettings replacement, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeHistoryStore : IHistoryStore
    {
        public Task<HistoryLoadResult> LoadAsync(int retentionDays, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HistoryLoadResult([], HistoryLoadStatus.Loaded));

        public Task<HistoryOperationResult> AddAsync(DictationHistoryEntry entry, int retentionDays, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HistoryOperationResult(true));

        public Task<HistoryOperationResult> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HistoryOperationResult(true));

        public Task<HistoryOperationResult> KeepAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HistoryOperationResult(true));

        public Task<HistoryOperationResult> ClearAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new HistoryOperationResult(true));
    }

    private sealed class FakeRecoveryStore : IRecoveryTextStore
    {
        public Task<RecoveryTextLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RecoveryTextLoadResult(RecoveryTextLoadStatus.Missing));

        public Task<bool> SaveAsync(RecoveryTextRecord record, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> ClearAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class FakeKeys : IApiKeyStore
    {
        public ApiKeyReadResult Read(PolishProvider provider) => new(ApiKeyReadStatus.Missing);
        public void Store(PolishProvider provider, string value) { }
        public void Delete(PolishProvider provider) { }
    }

    private sealed class FakeModels : IPolishModelSource
    {
        public string? RecommendedModel(PolishProvider provider) => null;
        public bool ModelIdBelongsTo(string? modelId, PolishProvider provider) => true;
        public Task<PolishModelDiscovery> DiscoverAsync(PolishProvider provider, string? ollamaEndpoint, CancellationToken cancellationToken) =>
            Task.FromResult(new PolishModelDiscovery(PolishModelDiscoveryStatus.Ready, ["llama3"]));
    }

    private sealed class FakeProfiles : IPortableProfileService
    {
        public Task<PortableProfileExportResult> ExportAsync(PortableProfile profile, string destinationPath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PortableProfileImportResult> ImportAsync(string sourcePath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeDiagnostics : IDiagnosticExportService
    {
        public Task<DiagnosticExportResult> ExportAsync(string destinationPath, int retentionDays, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeCapture : IMicrophoneTestCapture
    {
        public event EventHandler<AudioLevel>? LevelChanged { add { } remove { } }
        public bool IsCapturing => false;
        public int LastPacketCount => IsCapturing ? 10 : 0;
        public int LastSilentPacketCount => 0;
        public float LastPeak => 0f;
        public float LastRootMeanSquare => 0f;
        public long? LastStreamStartMilliseconds => IsCapturing ? 5 : null;
        public Task<AudioOperationResult> StartAsync(AudioCaptureRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AudioOperationResult(Succeeded: true));
        public Task<CapturedAudio> StopAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new CapturedAudio(DictationSessionId.Create(), new float[1600], 16_000, 1));
        public Task<AudioOperationResult> CancelAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AudioOperationResult(Succeeded: true));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeCatalog : IAudioDeviceCatalog
    {
        public event EventHandler<AudioDeviceChange>? DevicesChanged { add { } remove { } }
        public Task<IReadOnlyList<AudioDeviceInfo>> GetCaptureDevicesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AudioDeviceInfo>>([]);
        public void Dispose() { }
    }

    private static WindowPresentationSession CreateSession() =>
        new(new WindowPresentationParts(
            new FakeSettingsStore(),
            AppSettings.Default,
            new FakeHistoryStore(),
            new FakeRecoveryStore(),
            new FakeKeys(),
            new FakeModels(),
            new FakeProfiles(),
            new FakeDiagnostics(),
            () => new FakeCapture(),
            () => new FakeCatalog()));

    private static AdmittedMishearingAdvisor CreateAdvisor(
        WindowPresentationSession session,
        IPolishProvider? provider,
        bool usesLocalRuntime = false,
        IRuntimeResourceAdmission? admission = null) =>
        new(
            getPolishSetup: () => provider is null
                ? null
                : new PolishSetup(provider, usesLocalRuntime, RuntimeResourceKind.Cpu, "test-model"),
            resourceArbiter: admission ?? new SpyAdmission(new BlockingResourceLease()),
            presentation: session);

    [Fact]
    public async Task SuggestAsyncReturnsNotSupportedWhenProviderDoesNotImplementAdvisor()
    {
        await using var session = CreateSession();
        var advisor = CreateAdvisor(session, new NonAdvisorProvider());

        var advice = await advisor.SuggestAsync("EnviousWispr", []);

        Assert.Equal(MishearingAdviceStatus.NotSupported, advice.Status);
        Assert.Empty(advice.Suggestions);
    }

    [Fact]
    public async Task SuggestAsyncThrowsOperationCanceledAfterPresentationDrainBegins()
    {
        await using var session = CreateSession();
        var advisor = CreateAdvisor(session, new BlockingAdvisorProvider());

        await session.DrainAsync().WaitAsync(TestTimeout);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            advisor.SuggestAsync("EnviousWispr", []));
    }

    [Fact]
    public async Task SuggestAsyncLinksClosingCancellationAndHoldsDrainUntilResourceLeaseDisposes()
    {
        await using var session = CreateSession();
        var provider = new BlockingAdvisorProvider();
        var lease = new BlockingResourceLease { DeferDispose = true };
        var admission = new SpyAdmission(lease);
        var advisor = CreateAdvisor(session, provider, usesLocalRuntime: true, admission: admission);

        Task<MishearingAdvice> suggestTask;
        Task drainTask;
        try
        {
            suggestTask = advisor.SuggestAsync("EnviousWispr", []);
            await provider.Entered.WaitAsync(TestTimeout);

            Assert.True(admission.ObservedToken.CanBeCanceled);
            drainTask = session.DrainAsync();
            Assert.False(drainTask.IsCompleted);
            Assert.True(provider.ObservedToken.IsCancellationRequested);
            Assert.True(admission.ObservedToken.IsCancellationRequested);

            provider.AllowUnwind();
            await lease.DisposeEntered.WaitAsync(TestTimeout);
            Assert.False(drainTask.IsCompleted);
        }
        finally
        {
            provider.AllowUnwind();
            lease.AllowDispose();
        }

        await drainTask.WaitAsync(TestTimeout);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => suggestTask.WaitAsync(TestTimeout));
        Assert.Equal(1, lease.DisposeCount);
    }

    [Fact]
    public async Task SuggestAsyncPropagatesCallerCancellationAndRejectsSwallowedFailedOutcome()
    {
        await using var session = CreateSession();
        var provider = new BlockingAdvisorProvider { ReturnFailedOnCancellation = true };
        var advisor = CreateAdvisor(session, provider);

        using var cts = new CancellationTokenSource();
        Task<MishearingAdvice> suggestTask;
        try
        {
            suggestTask = advisor.SuggestAsync("EnviousWispr", [], cts.Token);
            await provider.Entered.WaitAsync(TestTimeout);

            cts.Cancel();
            Assert.True(provider.ObservedToken.IsCancellationRequested);
        }
        finally
        {
            provider.AllowUnwind();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => suggestTask.WaitAsync(TestTimeout));
    }

    [Fact]
    public async Task SuggestAsyncMapsDisposedAdmissionFailureToFailedWhenTokenIsLiveAndDisposesLeaseOnce()
    {
        await using var session = CreateSession();
        var provider = new BlockingAdvisorProvider { ThrowOnSuggest = true };
        var lease = new BlockingResourceLease();
        var admission = new SpyAdmission(lease);
        var advisor = CreateAdvisor(session, provider, usesLocalRuntime: true, admission: admission);

        provider.AllowUnwind();
        var advice = await advisor.SuggestAsync("EnviousWispr", []).WaitAsync(TestTimeout);

        Assert.Equal(MishearingAdviceStatus.Failed, advice.Status);
        Assert.Equal(1, admission.AcquireCount);
        Assert.Equal(1, lease.DisposeCount);

        admission.ThrowObjectDisposed = true;
        var disposedAdvice = await advisor.SuggestAsync("EnviousWispr", []).WaitAsync(TestTimeout);
        Assert.Equal(MishearingAdviceStatus.Failed, disposedAdvice.Status);
    }
}
