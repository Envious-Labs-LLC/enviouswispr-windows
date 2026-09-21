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
/// shares, a close that drains before it disposes and can be asked for twice, and a microphone test
/// that opens whatever capture it was handed - the production WASAPI one in the app, a fake here.
/// </summary>
public sealed class WindowPresentationSessionTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task WindowPresentersShareOneSettingsWriter()
    {
        // ONE WRITER, TWO PAGES. A word added on the Vocabulary page is inside the store when the
        // General page's Save is asked for; the Save waits behind it and derives from what it wrote,
        // so the stored record carries the word and the General values, and the history page's
        // preferences - read off the same writer - are the retention the Save just stored.
        var store = new HeldStore();
        var world = World.Create(store);
        var session = world.Session;

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
        Assert.Equal(45, session.Settings.Current.Preferences.History.RetentionDays);
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

        var again = session.DisposeAsync().AsTask();
        await again.WaitAsync(Patience);
        Assert.Equal(1, world.Catalog.Disposals);
        Assert.False(session.DeviceCatalogOpened);
    }

    [Fact]
    public async Task MicrophoneTestUsesInjectedProductionFactory()
    {
        // THE TEST OPENS WHAT THE SESSION WAS HANDED. Here a fake capture: the test starts it, hears
        // its level, stops it. In the app the factory is WindowComposition's, which names the WASAPI
        // capture and the WASAPI catalogue - and the window names neither any more.
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
        Assert.NotEqual(MicrophoneTestOutcome.AlreadyRunning, result.Outcome);

        Assert.IsType<WasapiAudioCapture>(WindowComposition.OpenMicrophoneTestCapture());
        using (var productionCatalog = WindowComposition.OpenDeviceCatalog())
        {
            Assert.IsType<WasapiDeviceCatalog>(productionCatalog);
        }

        var window = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Production", "EnviousWispr.App", "MainWindow.xaml.cs"));
        Assert.DoesNotContain("new WasapiAudioCapture(", window, StringComparison.Ordinal);
        Assert.DoesNotContain("new WasapiDeviceCatalog(", window, StringComparison.Ordinal);
        Assert.DoesNotContain("new SettingsPresenter(", window, StringComparison.Ordinal);
        Assert.DoesNotContain("new MicrophoneTestController(", window, StringComparison.Ordinal);
        var shell = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Production", "EnviousWispr.App", "App.xaml.cs"));
        Assert.Contains("WindowComposition.Compose(", shell, StringComparison.Ordinal);
        Assert.Contains("presentation.DisposeAsync()", shell, StringComparison.Ordinal);
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
        "F8", "Escape", "Ctrl+Alt+W", 1, true, false, true, false, 2, 1, true, true, 3.5, 2, "llama3", "http://localhost:11434",
        true, 45, 2, true, 1, true, true, RecordingSoundPairing.AirGlint, true, true, 30, true, true, "mic-2");

    private sealed class World
    {
        public required WindowPresentationSession Session { get; init; }
        public required FakeCapture Capture { get; init; }
        public required FakeCatalog Catalog { get; init; }

        public static World Create(ISettingsStore store, TimeProvider? clock = null)
        {
            var capture = new FakeCapture();
            var catalog = new FakeCatalog();
            var session = new WindowPresentationSession(
                new WindowPresentationParts(
                    store,
                    AppSettings.Default,
                    new FakeHistoryStore(),
                    new FakeRecoveryStore(),
                    new FakeKeys(),
                    new FakeModels(),
                    new FakeProfiles(),
                    new FakeDiagnostics(),
                    () => capture,
                    () => catalog),
                clock);
            return new World { Session = session, Capture = capture, Catalog = catalog };
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

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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

    private sealed class FakeModels : IPolishModelSource
    {
        public string? RecommendedModel(PolishProvider provider) => null;

        public bool ModelIdBelongsTo(string? modelId, PolishProvider provider) => true;

        public Task<PolishModelDiscovery> DiscoverAsync(PolishProvider provider, string? ollamaEndpoint, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
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
