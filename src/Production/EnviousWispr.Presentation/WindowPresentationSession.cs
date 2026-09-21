using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Credentials;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Distribution;
using EnviousWispr.Core.History;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Presentation;

/// <summary>What the window is told once, at launch, and never changes: the facts of this build and this start.</summary>
/// <param name="SettingsLoadStatus">How the settings were read at launch: loaded, created, migrated, recovered.</param>
/// <param name="TelemetryAvailable">Whether this build can share anonymous telemetry at all.</param>
/// <param name="ReleaseIdentity">The product name and channel this build carries.</param>
/// <param name="UpdateConfigured">Whether an update endpoint is configured for this channel.</param>
/// <param name="CurrentVersion">The version installed, as the updater reports it.</param>
public sealed record WindowLaunch(
    SettingsLoadStatus SettingsLoadStatus,
    bool TelemetryAvailable,
    ReleaseIdentity ReleaseIdentity,
    bool UpdateConfigured,
    string CurrentVersion);

/// <summary>The services the shell hands the window's presentation session: stores it does not own, and the factories for what the session opens itself.</summary>
/// <param name="SettingsStore">The settings file; the session's one writer sits over it.</param>
/// <param name="Settings">The settings as loaded, which the writer starts from.</param>
/// <param name="HistoryStore">The dictation history.</param>
/// <param name="RecoveryStore">The recovery copy of the last dictation.</param>
/// <param name="ApiKeys">The credential store the provider page reads and writes.</param>
/// <param name="PolishModels">The model catalogue the provider page consults; owned by the shell.</param>
/// <param name="Profiles">Portable profile import and export; owned by the shell.</param>
/// <param name="Diagnostics">The diagnostic export; owned by the shell.</param>
/// <param name="OpenMicrophoneTestCapture">Opens a capture for the microphone test: the production WASAPI capture in the app, a fake in a test.</param>
/// <param name="OpenDeviceCatalog">Opens the device catalogue the microphone list reads; the session opens it once and owns it.</param>
public sealed record WindowPresentationParts(
    ISettingsStore SettingsStore,
    AppSettings Settings,
    IHistoryStore HistoryStore,
    IRecoveryTextStore RecoveryStore,
    IApiKeyStore ApiKeys,
    IPolishModelSource PolishModels,
    IPortableProfileService Profiles,
    IDiagnosticExportService Diagnostics,
    Func<IMicrophoneTestCapture> OpenMicrophoneTestCapture,
    Func<IAudioDeviceCatalog> OpenDeviceCatalog);

/// <summary>
/// The window's presentation for the life of the window: one settings writer every presenter shares,
/// the presenters built over it, the microphone test and the device catalogue the window would
/// otherwise open itself, and one coordinated drain and disposal at the end.
/// </summary>
/// <remarks>
/// ONE OWNER, NAMED (plan-2 step 12). The window used to construct its presenters in its constructor,
/// open its microphone capture and device catalogue where it first needed them, and dispose some of
/// them in a synchronous shutdown that could not wait for a save in flight. The session owns what it
/// builds - the writer, the presenters, the catalogue it opened - and only that: the stores, the model
/// catalogue, the profile and diagnostic services are the shell's and are handed in, never disposed
/// here. The window keeps WinUI: its controls, its layout, its navigation, its overlay.
///
/// THE DRAIN COMES BEFORE THE DISPOSAL, AND EITHER MAY BE ASKED FOR TWICE. Disposing the writer's gate
/// under a save in flight made the release throw; so the close waits for the writer to drain - the
/// save that was inside it finishes and is kept - and only then lets go of the gate and the catalogue.
/// A second close is a no-op, and a save that arrives after the close is refused as
/// <see cref="SettingsSaveRefusal.Closing"/>, quietly.
/// </remarks>
public sealed class WindowPresentationSession : IAsyncDisposable
{
    private readonly WindowPresentationParts _parts;
    private readonly object _lock = new();
    private IAudioDeviceCatalog? _deviceCatalog;
    private Task? _closing;

    public WindowPresentationSession(WindowPresentationParts parts, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(parts);
        _parts = parts;
        Settings = new SettingsPresenter(parts.SettingsStore, parts.Settings);
        Vocabulary = new VocabularyPresenter(Settings);
        VocabularyImport = new VocabularyImportController(Vocabulary);
        // THE HISTORY PAGE READS THE PREFERENCES AS LAST WRITTEN, from the shared writer, so a
        // retention saved a moment ago is the retention the next load prunes by.
        History = new HistoryPresenter(parts.HistoryStore, parts.RecoveryStore, () => Settings.Current.Preferences.History, clock);
        Provider = new ProviderSettingsPresenter(parts.ApiKeys, parts.PolishModels);
        MicrophoneTest = new MicrophoneTestController(parts.OpenMicrophoneTestCapture, clock);
    }

    /// <summary>The one settings writer, and the General page's decisions.</summary>
    public SettingsPresenter Settings { get; }

    public VocabularyPresenter Vocabulary { get; }

    public VocabularyImportController VocabularyImport { get; }

    public HistoryPresenter History { get; }

    public ProviderSettingsPresenter Provider { get; }

    public MicrophoneTestController MicrophoneTest { get; }

    /// <summary>Portable profile import and export: the shell's service, reachable through the session so the window takes one thing.</summary>
    public IPortableProfileService Profiles => _parts.Profiles;

    /// <summary>The diagnostic export: the shell's service, likewise.</summary>
    public IDiagnosticExportService Diagnostics => _parts.Diagnostics;

    /// <summary>The device catalogue, opened on first use and owned from then on; the window subscribes to its changes and reads its devices.</summary>
    public IAudioDeviceCatalog DeviceCatalog()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_closing is not null, this);
            return _deviceCatalog ??= _parts.OpenDeviceCatalog();
        }
    }

    /// <summary>Whether the catalogue has been opened; a window that never showed its microphone list never opened one.</summary>
    public bool DeviceCatalogOpened
    {
        get
        {
            lock (_lock)
            {
                return _deviceCatalog is not null;
            }
        }
    }

    /// <summary>Waits for the settings write in flight, if any, and stops taking new ones. The exit's first step; safe to ask for again.</summary>
    public Task DrainAsync() => Settings.DrainAsync();

    /// <summary>The close: the drain first, then the writer's gate and the catalogue let go of. Shared by every caller; a second call is the first's task.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            return new ValueTask(_closing ??= CloseAsync());
        }
    }

    private async Task CloseAsync()
    {
        await Settings.DrainAsync().ConfigureAwait(false);
        Settings.Dispose();
        IAudioDeviceCatalog? catalog;
        lock (_lock)
        {
            catalog = _deviceCatalog;
            _deviceCatalog = null;
        }

        catalog?.Dispose();
    }
}
