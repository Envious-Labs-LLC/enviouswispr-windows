using EnviousWispr.App.Composition;
using EnviousWispr.Audio;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Credentials;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.History;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Settings;
using EnviousWispr.Presentation;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// The window's presentation session, as the shell composes it: one settings writer every presenter
/// shares, a drain that stops and joins the presentation's own work before the shell disposes what
/// it runs against, a close that drains before it disposes and can be asked for twice, and a
/// microphone test that opens whatever capture it was handed - the production WASAPI one in the
/// app, a fake here.
/// </summary>
public sealed class WindowPresentationSessionTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ComposedPresentersShareOneSettingsWriter()
    {
        // ONE WRITER, TWO PAGES, THE SHELL'S OWN COMPOSITION. A word added on the Vocabulary page is
        // inside the store when the General page's Save is asked for; the Save waits behind it and
        // derives from what it wrote, so the stored record carries the word and the General values,
        // and the history page's next load asks the history store for the retention the Save just
        // stored - read off the same writer, not off a copy the window kept.
        var store = new HeldStore();
        var history = new RecordingHistoryStore();
        var session = World.Compose(store, history);

        var word = session.Vocabulary.AddWordAsync(new CustomWordEntry("envy wisper", "EnviousWispr"));
        await store.SaveStarted.Task.WaitAsync(Patience);
        var general = session.Settings.SaveGeneralAsync(General());
        store.LetSavesFinish();

        Assert.True((await word.WaitAsync(Patience)).Saved);
        Assert.True((await general.WaitAsync(Patience)).Saved);
        var stored = store.LastSaved!;
        Assert.Contains(stored.UserData.CustomWords, entry => entry.Replacement == "EnviousWispr");
        Assert.Equal(45, stored.Preferences.History.RetentionDays);
        Assert.Equal("mic-2", stored.PreferredMicrophoneId);
        Assert.Equal(stored, session.Settings.Current);

        var view = await session.History.LoadAsync().WaitAsync(Patience);
        Assert.Equal(HistorySummary.Empty, view.Summary);
        Assert.Equal(45, Assert.Single(history.LoadedWithRetention));
    }

    [Fact]
    public async Task DrainingStopsAndJoinsThePresentationsWork()
    {
        // THE DRAIN IS THE EXIT'S FIRST STEP, AND IT LEAVES NOTHING OF THE WINDOW'S INSIDE THE SHELL'S
        // STORES. A microphone test is listening, a model discovery is out, a history load is inside
        // the store. The drain closes the gate, stops what can be stopped - the test ends Cancelled
        // with its capture disposed, the discovery answers nothing - and waits for what cannot: the
        // load inside the store, which the drain does not finish ahead of. After it, every presenter
        // refuses without touching its store, the writer refuses, and a second drain is the first.
        var clock = new Deterministic.ManualClock();
        var history = new RecordingHistoryStore { HoldLoads = true };
        var models = new FakeModels { HoldDiscovery = true };
        var world = World.Create(new HeldStore(), clock, history, models);
        var session = world.Session;

        var test = session.MicrophoneTest.RunAsync(null, recordingInProgress: false);
        await world.Capture.Started.Task.WaitAsync(Patience);
        await clock.WhenRegistered(1).WaitAsync(Patience);
        var listing = session.Provider.ListModelsAsync(PolishProvider.Ollama, "http://localhost:11434");
        await models.DiscoveryStarted.Task.WaitAsync(Patience);
        var load = session.History.LoadAsync();
        await history.LoadStarted.Task.WaitAsync(Patience);
        Assert.Equal(3, session.Outstanding);
        Assert.False(session.Closing);

        var drain = session.DrainAsync();
        Assert.True(session.Closing);
        Assert.Equal(MicrophoneTestOutcome.Cancelled, (await test.WaitAsync(Patience)).Outcome);
        Assert.True(world.Capture.Disposed, "the stopped test did not dispose its capture");
        Assert.Null(await listing.WaitAsync(Patience));
        Assert.False(drain.IsCompleted, "the drain finished ahead of the load inside the history store");
        Assert.Equal(1, session.Outstanding);

        history.LetLoadsFinish();
        await drain.WaitAsync(Patience);
        Assert.Equal(HistorySummary.Empty, (await load).Summary);
        Assert.Equal(0, session.Outstanding);
        Assert.Same(drain, session.DrainAsync());

        // LATE CALLS ARE REFUSED WITHOUT REACHING THE STORE, THE SOURCE OR THE DEVICE.
        Assert.Equal(HistorySummary.Closing, (await session.History.LoadAsync()).Summary);
        Assert.True((await session.History.DeleteAsync(Guid.NewGuid())).Closing);
        Assert.True((await session.History.ClearAsync()).Closing);
        Assert.False(await session.History.DeleteRecoveryAsync());
        Assert.Equal(1, history.Loads);
        Assert.Equal(0, history.Commands);
        Assert.Equal(0, world.Recovery.Cleared);
        Assert.Equal(MicrophoneTestOutcome.Cancelled, (await session.MicrophoneTest.RunAsync(null, recordingInProgress: false)).Outcome);
        Assert.Equal(1, world.Capture.Opened);
        Assert.Null(await session.Provider.ListModelsAsync(PolishProvider.Ollama, null));
        Assert.Equal(1, models.Discoveries);
        Assert.Equal(SettingsSaveRefusal.Closing, (await session.Settings.SaveAsync(current => current with { LaunchCount = 4 })).Refusal);
    }

    [Fact]
    public async Task ClosingPresentationDrainsBeforeDisposing()
    {
        // THE CLOSE WAITS FOR THE SAVE INSIDE THE WRITER, then lets go of the writer's gate and the
        // catalogue it opened - once. A save that arrives after the close is refused quietly; the
        // catalogue cannot be opened again; a second close is the first's task and disposes nothing
        // twice.
        var store = new HeldStore();
        var world = World.Create(store);
        var session = world.Session;
        var catalog = session.DeviceCatalog();
        Assert.Same(catalog, session.DeviceCatalog());
        var save = session.Settings.SaveAsync(current => current with { LaunchCount = 3 });
        await store.SaveStarted.Task.WaitAsync(Patience);

        var closing = session.DisposeAsync().AsTask();
        Assert.False(closing.IsCompleted, "the close did not wait for the save in flight");
        Assert.Equal(0, world.Catalog.Disposals);
        store.LetSavesFinish();

        await closing.WaitAsync(Patience);
        Assert.True((await save).Saved);
        Assert.Equal(3, store.LastSaved!.LaunchCount);
        Assert.Equal(1, world.Catalog.Disposals);
        Assert.Equal(SettingsSaveRefusal.Closing, (await session.Settings.SaveAsync(current => current with { LaunchCount = 4 })).Refusal);
        Assert.Throws<ObjectDisposedException>(() => session.DeviceCatalog());
        Assert.True(session.Closing);

        var again = session.DisposeAsync().AsTask();
        await again.WaitAsync(Patience);
        Assert.Equal(1, world.Catalog.Disposals);
        Assert.False(session.DeviceCatalogOpened);
        Assert.Equal(HistorySummary.Closing, (await session.History.LoadAsync()).Summary);
    }

    [Fact]
    public async Task AShellOperationsLeaseIsJoinedByTheDrain()
    {
        // THE QUICK ADD'S SHAPE. A shell operation that borrows the delivery adapter - the thing the
        // session's teardown disposes - takes a lease like a presenter's operation. The drain, the
        // exit's first step, does not finish while the lease is held, so the session's shutdown (which
        // the lifetime asks for only after the drain) cannot dispose the adapter under the read; the
        // lease's token says the drain has begun, so the operation does nothing late; and once the
        // drain has begun a new lease is refused.
        var world = World.Create(new HeldStore());
        var session = world.Session;
        Assert.True(session.TryEnter(out var lease));
        Assert.Equal(1, session.Outstanding);
        Assert.False(lease!.Closing.IsCancellationRequested);

        var drain = session.DrainAsync();
        Assert.True(session.Closing);
        Assert.True(lease.Closing.IsCancellationRequested);
        Assert.False(drain.IsCompleted, "the drain finished ahead of the operation holding a lease");
        Assert.False(session.TryEnter(out var late));
        Assert.Null(late);

        lease.Dispose();
        await drain.WaitAsync(Patience);
        Assert.Equal(0, session.Outstanding);
    }

    [Fact]
    public async Task MicrophoneTestUsesTheInjectedCapture()
    {
        // THE TEST OPENS WHAT THE SESSION WAS HANDED. Here a fake capture: the test starts it, hears
        // its level, stops it.
        var clock = new Deterministic.ManualClock();
        var world = World.Create(new HeldStore(), clock);
        var frames = new List<MicrophoneTestFrame>();
        world.Session.MicrophoneTest.Frame += (_, frame) => frames.Add(frame);

        var run = world.Session.MicrophoneTest.RunAsync(null, recordingInProgress: false);
        await world.Capture.Started.Task.WaitAsync(Patience);
        world.Capture.RaiseLevel(0.4f);
        await clock.WhenRegistered(1).WaitAsync(Patience);
        clock.Advance(MicrophoneTestController.Duration);
        var result = await run.WaitAsync(Patience);

        Assert.Equal(1, world.Capture.Opened);
        Assert.True(world.Capture.Stopped);
        Assert.NotEmpty(frames);
        Assert.Equal(MicrophoneTestOutcome.Completed, result.Outcome);
        Assert.Equal(0, world.Session.Outstanding);
    }

    [Fact]
    public void ProductionCompositionNamesTheWasapiFactories()
    {
        // THE PRODUCTION FACTORIES ARE WINDOWCOMPOSITION'S, which names the WASAPI capture and the
        // WASAPI catalogue - and the window names neither any more, nor builds a presenter itself;
        // the shell composes the session there and drains and closes it through the lifetime.
        Assert.IsType<WasapiAudioCapture>(WindowComposition.OpenMicrophoneTestCapture());
        using (var productionCatalog = WindowComposition.OpenDeviceCatalog())
        {
            Assert.IsType<WasapiDeviceCatalog>(productionCatalog);
        }

        var window = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Production", "EnviousWispr.App", "MainWindow.xaml.cs"));
        Assert.DoesNotContain("new WasapiAudioCapture(", window, StringComparison.Ordinal);
        Assert.DoesNotContain("new WasapiDeviceCatalog(", window, StringComparison.Ordinal);
        Assert.DoesNotContain("new SettingsPresenter(", window, StringComparison.Ordinal);
        Assert.DoesNotContain("new HistoryPresenter(", window, StringComparison.Ordinal);
        Assert.DoesNotContain("new ProviderSettingsPresenter(", window, StringComparison.Ordinal);
        Assert.DoesNotContain("new MicrophoneTestController(", window, StringComparison.Ordinal);
        var shell = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Production", "EnviousWispr.App", "App.xaml.cs"));
        Assert.Contains("WindowComposition.Compose(", shell, StringComparison.Ordinal);
        Assert.Contains("DrainPresentation: () => _presentation?.DrainAsync()", shell, StringComparison.Ordinal);
        Assert.Contains("presentation.DisposeAsync()", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWindowDoesNotDrawAnAnswerThatArrivesAfterTheDrainBegan()
    {
        // A SUCCESSFUL ANSWER CAN LAND AFTER THE DRAIN BEGAN: a load inside a store that ignores its
        // token, a discovery that returned as the exit started, a test that completed under the
        // close. The presenters cannot tell the window not to draw those - they answer honestly -
        // so every continuation that renders asks the session whether the drain has begun, right
        // before it draws. Read at the source, because a WinUI handler cannot run under xunit.
        var window = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Production", "EnviousWispr.App", "MainWindow.xaml.cs"));
        Assert.Contains("if (view.Summary == HistorySummary.Closing || _session.Closing)", window, StringComparison.Ordinal);
        Assert.Equal(3, Occurrences(window, "if (result.Closing || _session.Closing)"));
        Assert.Contains("if (listing is null || _session.Closing || !_session.Provider.IsCurrent(listing.Ticket))", window, StringComparison.Ordinal);
        var testHandler = window[window.IndexOf("private async void MicrophoneTestButton_Click(", StringComparison.Ordinal)..];
        var ran = testHandler.IndexOf("await _session.MicrophoneTest.RunAsync(", StringComparison.Ordinal);
        var guarded = testHandler.IndexOf("if (_session.Closing)", StringComparison.Ordinal);
        var drawn = testHandler.IndexOf("switch (result.Outcome)", StringComparison.Ordinal);
        Assert.True(ran >= 0 && ran < guarded && guarded < drawn, "the microphone test's outcome is drawn before the session is asked whether the drain began");
        var recovery = window[window.IndexOf("private async void DeleteRecoveryButton_Click(", StringComparison.Ordinal)..];
        Assert.True(
            recovery.IndexOf("if (_session.Closing)", StringComparison.Ordinal) < recovery.IndexOf("if (recoveryDeleted)", StringComparison.Ordinal),
            "the recovery deletion's outcome is drawn before the session is asked whether the drain began");
    }

    private static int Occurrences(string text, string needle)
    {
        var count = 0;
        for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EnviousWispr.Windows.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }

    private static GeneralSettingsInput General() => new(
        "F8", "Escape", "Ctrl+Alt+W", 1, true, false, true, false, 2, 0, 1, true, true, 3.5, 2, "llama3", "http://localhost:11434",
        true, 45, 2, true, 1, true, true, RecordingSoundPairing.AirGlint, true, true, 30, true, true, "mic-2");

    private sealed class World
    {
        public required WindowPresentationSession Session { get; init; }
        public required FakeCapture Capture { get; init; }
        public required FakeCatalog Catalog { get; init; }
        public required FakeRecoveryStore Recovery { get; init; }

        /// <summary>The shell's own composition: production factories at the device leaves (never opened here), fakes at the stores.</summary>
        public static WindowPresentationSession Compose(ISettingsStore store, IHistoryStore history) =>
            WindowComposition.Compose(
                store,
                AppSettings.Default,
                history,
                new FakeRecoveryStore(),
                new FakeKeys(),
                new FakeModels(),
                new FakeProfiles(),
                new FakeDiagnostics());

        /// <summary>The session with fakes at the device leaves too, so a test can be driven.</summary>
        public static World Create(ISettingsStore store, TimeProvider? clock = null, IHistoryStore? history = null, IPolishModelSource? models = null)
        {
            var capture = new FakeCapture();
            var catalog = new FakeCatalog();
            var recovery = new FakeRecoveryStore();
            var session = new WindowPresentationSession(
                new WindowPresentationParts(
                    store,
                    AppSettings.Default,
                    history ?? new RecordingHistoryStore(),
                    recovery,
                    new FakeKeys(),
                    models ?? new FakeModels(),
                    new FakeProfiles(),
                    new FakeDiagnostics(),
                    () => capture,
                    () => catalog),
                clock);
            return new World { Session = session, Capture = capture, Catalog = catalog, Recovery = recovery };
        }
    }

    private sealed class HeldStore : ISettingsStore
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SaveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AppSettings? LastSaved { get; private set; }

        public void LetSavesFinish() => _release.TrySetResult();

        public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            SaveStarted.TrySetResult();
            await _release.Task.ConfigureAwait(false);
            LastSaved = settings;
        }

        public Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SettingsResetResult> ResetAsync(AppSettings replacement, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>A history store that records what it was asked and, when told to, holds a load inside itself regardless of the token - a file read part way.</summary>
    private sealed class RecordingHistoryStore : IHistoryStore
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool HoldLoads { get; init; }

        public TaskCompletionSource LoadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<int> LoadedWithRetention { get; } = [];

        public int Loads => LoadedWithRetention.Count;

        public int Commands { get; private set; }

        public void LetLoadsFinish() => _release.TrySetResult();

        public async Task<HistoryLoadResult> LoadAsync(int retentionDays, DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            LoadedWithRetention.Add(retentionDays);
            LoadStarted.TrySetResult();
            if (HoldLoads)
            {
                await _release.Task.ConfigureAwait(false);
            }

            return new HistoryLoadResult([], HistoryLoadStatus.Loaded);
        }

        public Task<HistoryOperationResult> AddAsync(DictationHistoryEntry entry, int retentionDays, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            Command();

        public Task<HistoryOperationResult> DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Command();

        public Task<HistoryOperationResult> KeepAsync(Guid id, CancellationToken cancellationToken = default) => Command();

        public Task<HistoryOperationResult> ClearAsync(CancellationToken cancellationToken = default) => Command();

        private Task<HistoryOperationResult> Command()
        {
            Commands++;
            return Task.FromResult(new HistoryOperationResult(true));
        }
    }

    private sealed class FakeCatalog : IAudioDeviceCatalog
    {
        public int Disposals { get; private set; }

        public event EventHandler<AudioDeviceChange>? DevicesChanged
        {
            add { }
            remove { }
        }

        public Task<IReadOnlyList<AudioDeviceInfo>> GetCaptureDevicesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AudioDeviceInfo>>([]);

        public void Dispose() => Disposals++;
    }

    private sealed class FakeCapture : IMicrophoneTestCapture
    {
        private static readonly float[] OneSample = [0.2f];
        private DictationSessionId _sessionId;

        public event EventHandler<AudioLevel>? LevelChanged;

        public bool IsCapturing { get; private set; }

        public int Opened { get; private set; }

        public bool Stopped { get; private set; }

        public bool Disposed { get; private set; }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int LastPacketCount => 10;

        public int LastSilentPacketCount => 0;

        public float LastPeak => 0.4f;

        public float LastRootMeanSquare => 0.4f;

        public long? LastStreamStartMilliseconds => Opened > 0 ? 5 : null;

        public void RaiseLevel(float rootMeanSquare) => LevelChanged?.Invoke(this, new AudioLevel(rootMeanSquare, rootMeanSquare));

        public Task<AudioOperationResult> StartAsync(AudioCaptureRequest request, CancellationToken cancellationToken = default)
        {
            Opened++;
            _sessionId = request.SessionId;
            IsCapturing = true;
            Started.TrySetResult();
            return Task.FromResult(new AudioOperationResult(Succeeded: true));
        }

        public Task<CapturedAudio> StopAsync(CancellationToken cancellationToken = default)
        {
            IsCapturing = false;
            Stopped = true;
            return Task.FromResult(new CapturedAudio(_sessionId, OneSample, SampleRate: 16_000, Channels: 1));
        }

        public Task<AudioOperationResult> CancelAsync(CancellationToken cancellationToken = default)
        {
            IsCapturing = false;
            return Task.FromResult(new AudioOperationResult(Succeeded: true));
        }

        public ValueTask DisposeAsync()
        {
            IsCapturing = false;
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeKeys : IApiKeyStore
    {
        public ApiKeyReadResult Read(PolishProvider provider) => new(ApiKeyReadStatus.Missing);

        public void Store(PolishProvider provider, string value)
        {
        }

        public void Delete(PolishProvider provider)
        {
        }
    }

    /// <summary>A model source whose discovery, when told to, stays out until its token is cancelled - a provider that does not answer.</summary>
    private sealed class FakeModels : IPolishModelSource
    {
        public bool HoldDiscovery { get; init; }

        public int Discoveries { get; private set; }

        public TaskCompletionSource DiscoveryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? RecommendedModel(PolishProvider provider) => null;

        public bool ModelIdBelongsTo(string? modelId, PolishProvider provider) => true;

        public async Task<PolishModelDiscovery> DiscoverAsync(PolishProvider provider, string? ollamaEndpoint, CancellationToken cancellationToken)
        {
            Discoveries++;
            DiscoveryStarted.TrySetResult();
            if (HoldDiscovery)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }

            return new PolishModelDiscovery(PolishModelDiscoveryStatus.Ready, ["llama3"]);
        }
    }

    private sealed class FakeProfiles : IPortableProfileService
    {
        public Task<PortableProfileExportResult> ExportAsync(PortableProfile profile, string destinationPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<PortableProfileImportResult> ImportAsync(string sourcePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeDiagnostics : IDiagnosticExportService
    {
        public Task<DiagnosticExportResult> ExportAsync(string destinationPath, int retentionDays, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
