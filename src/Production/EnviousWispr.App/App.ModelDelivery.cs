using System.Globalization;
using System.Reflection;
using EnviousWispr.ASR;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.ModelDelivery;
using EnviousWispr.Pipeline;
using EnviousWispr.Presentation;

namespace EnviousWispr.App;

/// <summary>What the window shows about the speech model: a sentence, optional progress, which buttons apply, and where it stands.</summary>
/// <param name="Stage">
/// Where the delivery stands, said by the code that knows rather than read back out of the sentence. The first-run
/// checklist gates on it; the graphics runtime, which shares this record, leaves it unknown.
/// </param>
public sealed record ModelDeliveryPresentation(
    string Text,
    double? Percent = null,
    bool CanDownload = false,
    bool CanCancel = false,
    SpeechModelDelivery Stage = SpeechModelDelivery.Unknown);

/// <summary>
/// The first-run path a new install was missing: find the model this configuration needs, and if it
/// is absent, fetch, verify and activate it through the store.
/// </summary>
/// <remarks>
/// THE STORE WAS BUILT, TESTED, AND CONSTRUCTED NOWHERE. A fresh install could say "model is not
/// installed" and offer nothing; the only fix was copying files by hand from another computer.
/// This file is the wiring, and only the wiring - manifests are bundled, admission is the store's,
/// and the engine is brought up by the same <c>ConfigureTranscriptionAsync</c> that runs at launch,
/// so a downloaded model and a pre-installed one take the same path from here on. Ref: #92.
/// </remarks>
public partial class App
{
    private static readonly HttpClient ModelDeliveryHttpClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly Dictionary<string, SpeechEngineTransientDelivery> _speechEngineDeliveries = new(StringComparer.Ordinal);
    private CancellationTokenSource? _modelDownload;
    private Task? _modelDelivery;
    private IReadOnlyList<string> _missingModelIds = [];
    private string? _activeDownloadModelId;
    private bool _activeDeliveryMirrorsSelectedEngine;
    private bool _livePreviewReconfigurePending;
    private bool _transcriptionReconfigurePending;

    private sealed record SpeechEngineTransientDelivery(
        SpeechModelDelivery Stage,
        double? Percent = null,
        string? Detail = null);

    private bool IsSpeechEngineBusy =>
        _sessionController?.CurrentSession is not null ||
        _sessionCoordinator?.IsProcessing == true ||
        _sessionCoordinator?.HeldBy is not null;

    private string ModelStoreRoot => Path.Combine(_dataDirectory, "models");

    private ModelStore CreateModelStore() =>
        CreateModelStore(new ModelDeliveryProgressObserver(ReportModelDeliveryProgress));

    private ModelStore CreateModelStore(IModelDeliveryObserver observer) => new(
        ModelStoreRoot,
        ModelDeliveryHttpClient,
        new ModelManifestVerifier(new Dictionary<string, string>()),
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0),
        observer: observer);

    /// <summary>
    /// Where a model is loaded from, or null when this configuration cannot run it.
    /// </summary>
    /// <remarks>
    /// The order is <see cref="InstalledModelLocator"/>'s: an explicit override, then the version the
    /// store admitted, then a hand-copied directory when it is COMPLETE, then the development checkout.
    /// </remarks>
    private async Task<string?> ResolveModelDirectoryAsync(
        string modelId,
        string environmentVariable = "ENVIOUSWISPR_MODEL_DIRECTORY")
    {
        var active = await CreateModelStore().OpenActiveOfflineAsync(modelId).ConfigureAwait(true);
        var development = DevelopmentModelDirectory(modelId);
        return InstalledModelLocator.Resolve(
            Environment.GetEnvironmentVariable(environmentVariable),
            active.Succeeded ? active.Installed?.DirectoryPath : null,
            Path.Combine(ModelStoreRoot, modelId),
            development,
            directory => ModelIsComplete(modelId, directory));
    }

    private static string? DevelopmentModelDirectory(string modelId)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EnviousWispr.Windows.slnx")))
        {
            directory = directory.Parent;
        }

        return directory is null ? null : Path.Combine(directory.FullName, "models", modelId);
    }

    private static bool ModelIsComplete(string modelId, string directory)
    {
        if (string.Equals(modelId, ParakeetTranscriptionEngine.ModelId, StringComparison.Ordinal))
        {
            var parakeet = new LocalParakeetModelProbe().Probe(directory);
            return parakeet.Int8Complete || parakeet.Fp32Complete;
        }

        var whisper = new LocalWhisperModelProbe().Probe(directory);
        return string.Equals(modelId, WhisperTranscriptionEngine.PreviewModelId, StringComparison.Ordinal)
            ? whisper.PreviewSmallComplete
            : whisper.QuantizedComplete || whisper.FullPrecisionComplete;
    }

    /// <summary>Which model pack ID the chosen Live Preview engine requires (#127).</summary>
    internal static string RequiredPreviewModelId(LivePreviewEngineChoice engine) =>
        EnviousWispr.Services.Runtime.LivePreviewRuntimeResolver.RequiredModelId(engine);

    /// <summary>The models this configuration needs and does not have, final engine first.</summary>
    private async Task<IReadOnlyList<string>> MissingModelIdsAsync(UserPreferences preferences)
    {
        var wanted = new List<string>
        {
            preferences.Dictation.FinalEngine == FinalAsrEngine.Whisper
                ? WhisperTranscriptionEngine.ModelId
                : ParakeetTranscriptionEngine.ModelId,
        };
        if (preferences.LivePreviewEnabled &&
            preferences.LivePreviewEngine == LivePreviewEngineChoice.Parakeet)
        {
            var previewModelId = RequiredPreviewModelId(preferences.LivePreviewEngine);
            if (!wanted.Contains(previewModelId, StringComparer.Ordinal))
            {
                wanted.Add(previewModelId);
            }
        }

        var missing = new List<string>();
        foreach (var modelId in wanted)
        {
            var variable = modelId == WhisperTranscriptionEngine.PreviewModelId
                ? "ENVIOUSWISPR_PREVIEW_MODEL_DIRECTORY"
                : "ENVIOUSWISPR_MODEL_DIRECTORY";
            if (await ResolveModelDirectoryAsync(modelId, variable).ConfigureAwait(true) is null)
            {
                missing.Add(modelId);
            }
        }

        return missing;
    }

    private async Task RefreshLivePreviewModelInventoryAsync()
    {
        var parakeetDirectory = await ResolveModelDirectoryAsync(ParakeetTranscriptionEngine.ModelId).ConfigureAwait(true);
        var parakeetInstalled = parakeetDirectory is not null &&
            new LocalParakeetModelProbe().Probe(parakeetDirectory).Int8Complete;
        var whisperPreviewDirectory = await ResolveModelDirectoryAsync(
            WhisperTranscriptionEngine.PreviewModelId,
            "ENVIOUSWISPR_PREVIEW_MODEL_DIRECTORY").ConfigureAwait(true);
        var whisperPreviewInstalled = whisperPreviewDirectory is not null &&
            new LocalWhisperModelProbe().Probe(whisperPreviewDirectory).PreviewSmallComplete;
        var storeInstalled = whisperPreviewInstalled &&
            (await CreateModelStore().ListInstalledAsync(WhisperTranscriptionEngine.PreviewModelId).ConfigureAwait(true)).Count > 0;
        var legacyWhisperRoot = Path.Combine(ModelStoreRoot, WhisperTranscriptionEngine.PreviewModelId);
        var canRemoveWhisper = storeInstalled || (whisperPreviewInstalled && Directory.Exists(legacyWhisperRoot));
        _window?.SetLivePreviewModelInventory(parakeetInstalled, whisperPreviewInstalled, canRemoveWhisper);
    }

    private static string SpeechEngineModelId(FinalAsrEngine engine) =>
        engine == FinalAsrEngine.Whisper
            ? WhisperTranscriptionEngine.ModelId
            : ParakeetTranscriptionEngine.ModelId;

    private static string SpeechEngineBundleSizeText(FinalAsrEngine engine)
    {
        var modelId = SpeechEngineModelId(engine);
        if (BundledModelManifests.TryRead(modelId) is null)
        {
            return EnginePolishPresentation.DefaultBundleSizeLabel(engine);
        }

        var manifest = BundledModelManifests.Load(modelId, new ModelManifestVerifier(new Dictionary<string, string>()));
        if (!manifest.Succeeded || manifest.Manifest is null)
        {
            return EnginePolishPresentation.DefaultBundleSizeLabel(engine);
        }

        var totalBytes = manifest.Manifest.Payload.Files.Sum(file => file.SizeBytes);
        return $"{Megabytes(totalBytes)} MB";
    }

    private async Task<SpeechEngineModelState> ProbeSpeechEngineModelAsync(FinalAsrEngine engine)
    {
        var modelId = SpeechEngineModelId(engine);
        var directory = await ResolveModelDirectoryAsync(modelId).ConfigureAwait(true);
        var installed = directory is not null && ModelIsComplete(modelId, directory);
        var storeInstalled = installed &&
            (await CreateModelStore().ListInstalledAsync(modelId).ConfigureAwait(true)).Count > 0;
        var legacyRoot = Path.Combine(ModelStoreRoot, modelId);
        var canRemove = storeInstalled || (installed && Directory.Exists(legacyRoot));
        var canDownload = BundledModelManifests.TryRead(modelId) is not null;
        var sizeLabel = SpeechEngineBundleSizeText(engine);

        if (installed)
        {
            if (_speechEngineDeliveries.TryGetValue(modelId, out var activating) &&
                activating.Stage == SpeechModelDelivery.Activating)
            {
                return new SpeechEngineModelState(
                    IsInstalled: true,
                    CanRemove: canRemove,
                    CanDownload: canDownload,
                    DeliveryStage: SpeechModelDelivery.Activating,
                    StatusDetail: activating.Detail,
                    BundleSizeText: sizeLabel);
            }

            _speechEngineDeliveries.Remove(modelId);
            return new SpeechEngineModelState(
                IsInstalled: true,
                CanRemove: canRemove,
                CanDownload: canDownload,
                DeliveryStage: SpeechModelDelivery.Installed,
                BundleSizeText: sizeLabel);
        }

        if (_speechEngineDeliveries.TryGetValue(modelId, out var delivery))
        {
            return new SpeechEngineModelState(
                IsInstalled: false,
                CanRemove: false,
                CanDownload: canDownload,
                DeliveryStage: delivery.Stage,
                DownloadPercent: delivery.Percent,
                StatusDetail: delivery.Detail,
                BundleSizeText: sizeLabel);
        }

        return new SpeechEngineModelState(
            IsInstalled: false,
            CanRemove: false,
            CanDownload: canDownload,
            DeliveryStage: canDownload ? SpeechModelDelivery.Missing : SpeechModelDelivery.CannotDownload,
            BundleSizeText: sizeLabel);
    }

    private async Task RefreshSpeechEngineInventoryAsync()
    {
        var parakeet = await ProbeSpeechEngineModelAsync(FinalAsrEngine.Parakeet).ConfigureAwait(true);
        var whisper = await ProbeSpeechEngineModelAsync(FinalAsrEngine.Whisper).ConfigureAwait(true);
        FinalAsrEngine? activeDownloadEngine = _modelDownload is not null
            ? _activeDownloadModelId switch
            {
                ParakeetTranscriptionEngine.ModelId => FinalAsrEngine.Parakeet,
                WhisperTranscriptionEngine.ModelId => FinalAsrEngine.Whisper,
                _ => null,
            }
            : null;
        _window?.SetSpeechEngineModels(parakeet, whisper, activeDownloadEngine);
    }

    /// <summary>Tells the window what the speech model situation is, after transcription was configured.</summary>
    private async Task PresentModelDeliveryAsync()
    {
        if (_modelDownload is not null)
        {
            return;
        }

        while (_livePreviewReconfigurePending || _transcriptionReconfigurePending)
        {
            if (IsSpeechEngineBusy || Leaving)
            {
                break;
            }

            var attempt = _sessionCoordinator?.TryHold(SessionHolder.SpeechEngineSwitch)
                ?? new SessionHoldAttempt(NoScope.Instance);
            if (attempt.Hold is not { } hold)
            {
                break;
            }

            using (hold)
            {
                var reconfigureFinalEngine = _transcriptionReconfigurePending;
                _livePreviewReconfigurePending = false;
                _transcriptionReconfigurePending = false;
                if (reconfigureFinalEngine || _configuredTranscription is not null)
                {
                    await ReconfigureTranscriptionAsync().ConfigureAwait(true);
                    if (Leaving)
                    {
                        return;
                    }

                    PresentGraphicsRuntime();
                }
            }
        }

        await RefreshLivePreviewModelInventoryAsync().ConfigureAwait(true);
        await RefreshSpeechEngineInventoryAsync().ConfigureAwait(true);
        _missingModelIds = await MissingModelIdsAsync(_settings.Preferences).ConfigureAwait(true);
        if (_missingModelIds.Count == 0)
        {
            _window?.SetModelDelivery(new(
                "The speech model this build pins is installed and verified.",
                Stage: SpeechModelDelivery.Installed));
            return;
        }

        var installable = _missingModelIds.Where(id => BundledModelManifests.TryRead(id) is not null).ToArray();
        if (installable.Length == 0)
        {
            _window?.SetModelDelivery(new(
                "This build cannot download the model it needs. Reinstall EnviousWispr.",
                Stage: SpeechModelDelivery.CannotDownload));
            return;
        }

        var totalBytes = installable.Sum(id =>
            BundledModelManifests.Load(id, new ModelManifestVerifier(new Dictionary<string, string>()))
                .Manifest?.Payload.Files.Sum(file => file.SizeBytes) ?? 0);
        _window?.SetModelDelivery(new(
            $"{SentenceStart(DescribeModels(installable))} {(installable.Length == 1 ? "is" : "are")} not installed on this PC. About {Megabytes(totalBytes)} MB to download, verified file by file.",
            CanDownload: true,
            Stage: SpeechModelDelivery.Missing));
    }

    private void OnModelDownloadRequested()
    {
        // ONE AT A TIME, READ OFF THE TASK ITSELF: a delivery still running refuses a second request;
        // one that has ended - including one that refused synchronously because a dictation was in
        // flight - does not, so "finish the current dictation, then download" can be followed.
        if (_modelDelivery is { IsCompleted: false } || Leaving)
        {
            return;
        }

        // ONE TRACKED TASK, from the request to the replaced engines: the exit policy cancels its
        // download and the lifetime joins the whole of it under Quiesce, so a delivery is never
        // tearing engines down or building them beside the exit's own disposals.
        _modelDelivery = DeliverModelsAsync();
    }

    private async Task DeliverModelsAsync()
    {

        if (_sessionController?.CurrentSession is not null)
        {
            _window?.SetModelDelivery(new(
                "Finish the current dictation, then download.",
                CanDownload: true,
                Stage: SpeechModelDelivery.Failed));
            return;
        }

        var wanted = _missingModelIds.Where(id => BundledModelManifests.TryRead(id) is not null).ToArray();
        if (wanted.Length == 0)
        {
            await PresentModelDeliveryAsync().ConfigureAwait(true);
            return;
        }

        using var download = new CancellationTokenSource();
        _modelDownload = download;
        _activeDeliveryMirrorsSelectedEngine = true;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        _logger.Write(new AppLogEntry(DateTimeOffset.UtcNow, AppEventCode.ModelDeliveryStarted));
        _window?.SetModelDelivery(new(
            $"Downloading {DescribeModels(wanted)}…",
            Percent: 0,
            CanCancel: true,
            Stage: SpeechModelDelivery.Downloading));
        try
        {
            var verifier = new ModelManifestVerifier(new Dictionary<string, string>());
            var provisioner = new ModelProvisioner(
                CreateModelStore(),
                modelId => BundledModelManifests.Load(modelId, verifier));
            foreach (var modelId in wanted)
            {
                _activeDownloadModelId = modelId;
                _speechEngineDeliveries[modelId] = new(
                    SpeechModelDelivery.Downloading,
                    Percent: 0,
                    Detail: $"Downloading {ModelDisplayName(modelId)}…");
                await RefreshSpeechEngineInventoryAsync().ConfigureAwait(true);
                var result = await provisioner.ProvisionAsync(modelId, download.Token).ConfigureAwait(true);
                if (!result.Succeeded)
                {
                    _logger.Write(new AppLogEntry(
                        DateTimeOffset.UtcNow,
                        AppEventCode.ModelDeliveryFailed,
                        AppFailureCategory.ModelDelivery,
                        clock.ElapsedMilliseconds));
                    _modelDownload = null;
                    _activeDownloadModelId = null;
                    _activeDeliveryMirrorsSelectedEngine = false;
                    var failureStage = result.Failure == ModelDeliveryFailure.Cancelled
                        ? SpeechModelDelivery.Cancelled
                        : SpeechModelDelivery.Failed;
                    var failureSentence = FailureSentence(result);
                    _speechEngineDeliveries[modelId] = new(failureStage, Detail: failureSentence);
                    await RefreshSpeechEngineInventoryAsync().ConfigureAwait(true);
                    _window?.SetModelDelivery(new(
                        failureSentence,
                        CanDownload: true,
                        Stage: failureStage));
                    return;
                }

                _speechEngineDeliveries.Remove(modelId);
            }

            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.ModelDeliveryCompleted,
                ElapsedMilliseconds: clock.ElapsedMilliseconds));
        }
        finally
        {
            _modelDownload = null;
            _activeDownloadModelId = null;
            _activeDeliveryMirrorsSelectedEngine = false;
        }

        // THE DOWNLOADED MODEL TAKES THE LAUNCH PATH. Tearing the engines down and re-running the
        // same configuration the app runs at startup means there is exactly one way a model becomes
        // "ready", and a download cannot drift from it. NOT UNDER AN EXIT: the engines are the
        // lifetime's to dispose once leaving has begun, and none are built after it.
        if (Leaving)
        {
            return;
        }

        _window?.SetModelDelivery(new("Download verified. Starting local transcription…", Stage: SpeechModelDelivery.Activating));
        await ReconfigureTranscriptionAsync().ConfigureAwait(true);
        if (Leaving)
        {
            return;
        }

        PresentGraphicsRuntime();
        await PresentModelDeliveryAsync().ConfigureAwait(true);
    }

    private void OnModelDownloadCancelRequested() => _modelDownload?.Cancel();

    private void OnSpeechEngineDownloadRequested(FinalAsrEngine engine)
    {
        if (_modelDelivery is { IsCompleted: false } || Leaving)
        {
            return;
        }

        _modelDelivery = DeliverSpeechEngineModelAsync(engine);
    }

    private async Task DeliverSpeechEngineModelAsync(FinalAsrEngine engine)
    {
        var modelId = SpeechEngineModelId(engine);
        if (IsSpeechEngineBusy)
        {
            const string busyMessage = "Finish the current dictation or file transcription, then download.";
            _speechEngineDeliveries[modelId] = new(SpeechModelDelivery.Failed, Detail: busyMessage);
            await RefreshSpeechEngineInventoryAsync().ConfigureAwait(true);
            if (_settings.Preferences.Dictation.FinalEngine == engine)
            {
                _window?.SetModelDelivery(new(
                    busyMessage,
                    CanDownload: true,
                    Stage: SpeechModelDelivery.Failed));
            }

            return;
        }

        var existingDirectory = await ResolveModelDirectoryAsync(modelId).ConfigureAwait(true);
        if (existingDirectory is not null && ModelIsComplete(modelId, existingDirectory))
        {
            _speechEngineDeliveries.Remove(modelId);
            await PresentModelDeliveryAsync().ConfigureAwait(true);
            return;
        }

        if (BundledModelManifests.TryRead(modelId) is null)
        {
            _speechEngineDeliveries[modelId] = new(
                SpeechModelDelivery.CannotDownload,
                Detail: "This build cannot download this speech model. Reinstall EnviousWispr.");
            await RefreshSpeechEngineInventoryAsync().ConfigureAwait(true);
            return;
        }

        using var download = new CancellationTokenSource();
        _modelDownload = download;
        _activeDownloadModelId = modelId;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        _logger.Write(new AppLogEntry(DateTimeOffset.UtcNow, AppEventCode.ModelDeliveryStarted));
        _speechEngineDeliveries[modelId] = new(
            SpeechModelDelivery.Downloading,
            Percent: 0,
            Detail: $"Downloading {ModelDisplayName(modelId)}…");
        await RefreshSpeechEngineInventoryAsync().ConfigureAwait(true);
        if (_settings.Preferences.Dictation.FinalEngine == engine)
        {
            _window?.SetModelDelivery(new(
                $"Downloading {ModelDisplayName(modelId)}…",
                Percent: 0,
                CanCancel: true,
                Stage: SpeechModelDelivery.Downloading));
        }

        try
        {
            var verifier = new ModelManifestVerifier(new Dictionary<string, string>());
            var provisioner = new ModelProvisioner(
                CreateModelStore(),
                id => BundledModelManifests.Load(id, verifier));
            var result = await provisioner.ProvisionAsync(modelId, download.Token).ConfigureAwait(true);
            if (!result.Succeeded)
            {
                _logger.Write(new AppLogEntry(
                    DateTimeOffset.UtcNow,
                    AppEventCode.ModelDeliveryFailed,
                    AppFailureCategory.ModelDelivery,
                    clock.ElapsedMilliseconds));
                _modelDownload = null;
                _activeDownloadModelId = null;
                var failureStage = result.Failure == ModelDeliveryFailure.Cancelled
                    ? SpeechModelDelivery.Cancelled
                    : SpeechModelDelivery.Failed;
                var failureSentence = FailureSentence(result);
                _speechEngineDeliveries[modelId] = new(failureStage, Detail: failureSentence);
                await RefreshSpeechEngineInventoryAsync().ConfigureAwait(true);
                if (_settings.Preferences.Dictation.FinalEngine == engine)
                {
                    _window?.SetModelDelivery(new(
                        failureSentence,
                        CanDownload: true,
                        Stage: failureStage));
                }

                return;
            }

            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.ModelDeliveryCompleted,
                ElapsedMilliseconds: clock.ElapsedMilliseconds));
        }
        finally
        {
            _modelDownload = null;
            _activeDownloadModelId = null;
        }

        if (Leaving)
        {
            return;
        }

        _speechEngineDeliveries[modelId] = new(
            SpeechModelDelivery.Activating,
            Detail: "Download verified. Starting local transcription…");
        await RefreshSpeechEngineInventoryAsync().ConfigureAwait(true);
        if (_settings.Preferences.Dictation.FinalEngine == engine)
        {
            _window?.SetModelDelivery(new(
                "Download verified. Starting local transcription…",
                Stage: SpeechModelDelivery.Activating));
        }

        var attempt = _sessionCoordinator?.TryHold(SessionHolder.SpeechEngineSwitch)
            ?? new SessionHoldAttempt(NoScope.Instance);
        if (attempt.Hold is not { } hold)
        {
            if (attempt.Refusal != SessionHoldRefusal.Closed)
            {
                _transcriptionReconfigurePending = true;
                _speechEngineDeliveries.Remove(modelId);
                await RefreshSpeechEngineInventoryAsync().ConfigureAwait(true);
            }

            return;
        }

        using (hold)
        {
            _speechEngineDeliveries.Remove(modelId);
            await ReconfigureTranscriptionAsync().ConfigureAwait(true);
        }

        if (Leaving)
        {
            return;
        }

        PresentGraphicsRuntime();
        await PresentModelDeliveryAsync().ConfigureAwait(true);
    }

    private void OnSpeechEngineRemoveRequested(FinalAsrEngine engine)
    {
        if (IsSpeechEngineBusy)
        {
            _window?.ShowSpeechEngineNotice(EnginePolishPresentation.RemovalBlockedBusyNotice);
            return;
        }

        if (_modelDelivery is { IsCompleted: false } || Leaving)
        {
            return;
        }

        _modelDelivery = RemoveSpeechEngineModelAsync(engine);
    }

    private async Task RemoveSpeechEngineModelAsync(FinalAsrEngine engine)
    {
        if (IsSpeechEngineBusy)
        {
            _window?.ShowSpeechEngineNotice(EnginePolishPresentation.RemovalBlockedBusyNotice);
            return;
        }

        var attempt = _sessionCoordinator?.TryHold(SessionHolder.SpeechEngineSwitch)
            ?? new SessionHoldAttempt(NoScope.Instance);
        if (attempt.Hold is not { } hold)
        {
            _window?.ShowSpeechEngineNotice(EnginePolishPresentation.RemovalBlockedBusyNotice);
            return;
        }

        var modelId = SpeechEngineModelId(engine);
        _speechEngineDeliveries.Remove(modelId);

        using (hold)
        {
            ModelPackageRemovalOutcome removalOutcome;
            await _engineReconfiguration.WaitAsync().ConfigureAwait(true);
            try
            {
                if (Leaving)
                {
                    return;
                }

                removalOutcome = await ModelPackageCleaner.RemoveAsync(
                    CreateModelStore(),
                    modelId,
                    Path.Combine(ModelStoreRoot, modelId),
                    TeardownTranscriptionAsync).ConfigureAwait(true);
            }
            finally
            {
                _engineReconfiguration.Release();
            }

            if (!removalOutcome.Succeeded)
            {
                _window?.ShowSpeechEngineNotice(
                    "Could not remove all files for this speech model because another process is using them. Try again in a moment.");
            }

            if (Leaving)
            {
                return;
            }

            await ReconfigureTranscriptionAsync().ConfigureAwait(true);
        }

        if (Leaving)
        {
            return;
        }

        PresentGraphicsRuntime();
        await PresentModelDeliveryAsync().ConfigureAwait(true);
    }

    private void OnPreviewModelDownloadRequested()
    {
        if (_modelDelivery is { IsCompleted: false } || Leaving)
        {
            return;
        }

        _modelDelivery = DeliverPreviewModelAsync();
    }

    private async Task DeliverPreviewModelAsync()
    {
        if (_sessionController?.CurrentSession is not null || _sessionCoordinator?.IsProcessing == true)
        {
            _window?.SetLivePreviewWhisperDelivery(new(
                "Finish the current dictation, then download.",
                CanDownload: true,
                Stage: SpeechModelDelivery.Failed));
            return;
        }

        var previewModelId = RequiredPreviewModelId(LivePreviewEngineChoice.Whisper);
        if (BundledModelManifests.TryRead(previewModelId) is null)
        {
            _window?.SetLivePreviewWhisperDelivery(new(
                "This build cannot download the model it needs. Reinstall EnviousWispr.",
                Stage: SpeechModelDelivery.CannotDownload));
            return;
        }

        using var download = new CancellationTokenSource();
        _activeDownloadModelId = previewModelId;
        _modelDownload = download;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        _logger.Write(new AppLogEntry(DateTimeOffset.UtcNow, AppEventCode.ModelDeliveryStarted));
        _window?.SetLivePreviewWhisperDelivery(new(
            $"Downloading {ModelDisplayName(previewModelId)}…",
            Percent: 0,
            CanCancel: true,
            Stage: SpeechModelDelivery.Downloading));
        try
        {
            var verifier = new ModelManifestVerifier(new Dictionary<string, string>());
            var provisioner = new ModelProvisioner(
                CreateModelStore(),
                modelId => BundledModelManifests.Load(modelId, verifier));
            var result = await provisioner.ProvisionAsync(previewModelId, download.Token).ConfigureAwait(true);
            if (!result.Succeeded)
            {
                _logger.Write(new AppLogEntry(
                    DateTimeOffset.UtcNow,
                    AppEventCode.ModelDeliveryFailed,
                    AppFailureCategory.ModelDelivery,
                    clock.ElapsedMilliseconds));
                _modelDownload = null;
                _window?.SetLivePreviewWhisperDelivery(new(
                    FailureSentence(result),
                    CanDownload: true,
                    Stage: result.Failure == ModelDeliveryFailure.Cancelled
                        ? SpeechModelDelivery.Cancelled
                        : SpeechModelDelivery.Failed));
                return;
            }

            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.ModelDeliveryCompleted,
                ElapsedMilliseconds: clock.ElapsedMilliseconds));
        }
        finally
        {
            _modelDownload = null;
            _activeDownloadModelId = null;
        }

        if (Leaving)
        {
            return;
        }

        _window?.SetLivePreviewWhisperDelivery(new("Download verified. Starting Live Preview…", Stage: SpeechModelDelivery.Activating));
        await ReconfigureTranscriptionAsync().ConfigureAwait(true);
        if (Leaving)
        {
            return;
        }

        await PresentModelDeliveryAsync().ConfigureAwait(true);
    }

    private void OnPreviewModelRemoveRequested()
    {
        if (_modelDelivery is { IsCompleted: false } || Leaving)
        {
            return;
        }

        _modelDelivery = RemovePreviewModelAsync();
    }

    private async Task RemovePreviewModelAsync()
    {
        if (IsSpeechEngineBusy)
        {
            return;
        }

        var attempt = _sessionCoordinator?.TryHold(SessionHolder.SpeechEngineSwitch)
            ?? new SessionHoldAttempt(NoScope.Instance);
        if (attempt.Hold is not { } hold)
        {
            return;
        }

        using (hold)
        {
            await ModelPackageCleaner.RemoveAsync(
                CreateModelStore(),
                WhisperTranscriptionEngine.PreviewModelId,
                Path.Combine(ModelStoreRoot, WhisperTranscriptionEngine.PreviewModelId),
                async () =>
                {
                    if (_previewEngine is not null)
                    {
                        await _previewEngine.DisposeAsync().ConfigureAwait(true);
                        _previewEngine = null;
                    }
                }).ConfigureAwait(true);

            if (Leaving)
            {
                return;
            }

            if (_configuredTranscription is not null)
            {
                await ReconfigureTranscriptionAsync().ConfigureAwait(true);
                if (Leaving)
                {
                    return;
                }
            }
        }

        await PresentModelDeliveryAsync().ConfigureAwait(true);
    }

    private void OnDictationEnded() => TrySchedulePendingModelRefresh();

    private void TrySchedulePendingModelRefresh()
    {
        if ((!_livePreviewReconfigurePending && !_transcriptionReconfigurePending) ||
            _modelDelivery is { IsCompleted: false } ||
            IsSpeechEngineBusy ||
            Leaving)
        {
            return;
        }

        _modelDelivery = RefreshAfterSettingsChangeAsync();
    }

    private async Task RefreshAfterSettingsChangeAsync()
    {
        if (IsSpeechEngineBusy || Leaving)
        {
            return;
        }

        await PresentModelDeliveryAsync().ConfigureAwait(true);
    }

    /// <summary>The person asked for local transcription to be started again, from the first-run checklist or warm-up.</summary>
    /// <remarks>
    /// THE SAME TRACKED SLOT AS A DOWNLOAD, because it is the second half of one: a download ends by running the
    /// launch configuration again, and this runs only that half. One task at a time, joined under Quiesce, and
    /// refused while a dictation holds the session - tearing an engine down under a take would lose the take.
    /// </remarks>
    private void OnTranscriptionRetryRequested()
    {
        if (_modelDelivery is { IsCompleted: false } || Leaving)
        {
            return;
        }

        _modelDelivery = RetryTranscriptionAsync();
    }

    private async Task RetryTranscriptionAsync()
    {
        if (_sessionController?.CurrentSession is not null || _sessionCoordinator?.IsProcessing == true)
        {
            return;
        }

        _missingModelIds = await MissingModelIdsAsync(_settings.Preferences).ConfigureAwait(true);
        if (Leaving)
        {
            return;
        }

        if (_missingModelIds.Count > 0)
        {
            await DeliverModelsAsync().ConfigureAwait(true);
            return;
        }

        await ReconfigureTranscriptionAsync().ConfigureAwait(true);
        if (Leaving)
        {
            return;
        }

        PresentGraphicsRuntime();
        await PresentModelDeliveryAsync().ConfigureAwait(true);
    }

    private async Task TeardownTranscriptionAsync()
    {
        if (_previewEngine is not null)
        {
            await _previewEngine.DisposeAsync().ConfigureAwait(true);
            _previewEngine = null;
        }

        if (_transcriptionEngine is not null)
        {
            await _transcriptionEngine.DisposeAsync().ConfigureAwait(true);
            _transcriptionEngine = null;
        }
    }

    private void ReportModelDeliveryProgress(ModelDeliveryEvent deliveryEvent)
    {
        if (deliveryEvent.TotalBytes is not > 0 || deliveryEvent.CompletedBytes is null)
        {
            return;
        }

        var percent = Math.Clamp(100.0 * deliveryEvent.CompletedBytes.Value / deliveryEvent.TotalBytes.Value, 0, 100);
        var completedMb = Megabytes(deliveryEvent.CompletedBytes.Value);
        var totalMb = Megabytes(deliveryEvent.TotalBytes.Value);
        PostToLiveWindow(window =>
        {
            // A LATE REPORT MUST NOT REDRAW A FINISHED DOWNLOAD, as the graphics runtime's already knows: reports
            // are queued to the window, and one that runs after a cancel or a failure would put the progress bar
            // back over the outcome - and tell the first-run checklist a stopped download is still running.
            if (_modelDownload is not null)
            {
                var presentation = new ModelDeliveryPresentation(
                    $"Downloading… {completedMb} of {totalMb} MB, verified as it arrives.",
                    Percent: percent,
                    CanCancel: true,
                    Stage: SpeechModelDelivery.Downloading);
                if (string.Equals(_activeDownloadModelId, WhisperTranscriptionEngine.PreviewModelId, StringComparison.Ordinal))
                {
                    window.SetLivePreviewWhisperDelivery(presentation);
                }
                else
                {
                    var activeModelId = SpeechEngineModelId(_settings.Preferences.Dictation.FinalEngine);
                    if (_activeDeliveryMirrorsSelectedEngine ||
                        string.Equals(_activeDownloadModelId, activeModelId, StringComparison.Ordinal))
                    {
                        window.SetModelDelivery(presentation);
                    }

                    if (string.Equals(_activeDownloadModelId, ParakeetTranscriptionEngine.ModelId, StringComparison.Ordinal))
                    {
                        _speechEngineDeliveries[ParakeetTranscriptionEngine.ModelId] = new(
                            SpeechModelDelivery.Downloading,
                            percent,
                            presentation.Text);
                        window.SetSpeechEngineDelivery(FinalAsrEngine.Parakeet, presentation);
                    }
                    else if (string.Equals(_activeDownloadModelId, WhisperTranscriptionEngine.ModelId, StringComparison.Ordinal))
                    {
                        _speechEngineDeliveries[WhisperTranscriptionEngine.ModelId] = new(
                            SpeechModelDelivery.Downloading,
                            percent,
                            presentation.Text);
                        window.SetSpeechEngineDelivery(FinalAsrEngine.Whisper, presentation);
                    }
                }
            }
        });
    }

    private static string DescribeModels(IReadOnlyList<string> modelIds)
    {
        var names = modelIds.Select(ModelDisplayName).ToArray();
        return names.Length switch
        {
            1 => names[0],
            2 => $"{names[0]} and {names[1]}",
            _ => string.Join(", ", names[..^1]) + " and " + names[^1],
        };
    }

    // LOWER CASE, BECAUSE A NAME SITS INSIDE SENTENCES. "Downloading The Whisper speech model" and "the Live
    // Preview model is not installed" both came from mixed casing here; the sentence capitalises its start.
    private static string SentenceStart(string text) =>
        text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];

    private static string ModelDisplayName(string modelId) => modelId switch
    {
        ParakeetTranscriptionEngine.ModelId => "the Parakeet speech model",
        WhisperTranscriptionEngine.ModelId => "the Whisper speech model",
        WhisperTranscriptionEngine.PreviewModelId => "the Live Preview model",
        _ => modelId,
    };

    private static string Megabytes(long bytes) =>
        ((bytes + (1024 * 1024) - 1) / (1024 * 1024)).ToString(CultureInfo.CurrentCulture);

    /// <summary>
    /// One sentence per failure, each naming what the person can do about it.
    /// </summary>
    private static string FailureSentence(ModelDeliveryResult result) => result.Failure switch
    {
        ModelDeliveryFailure.NetworkUnavailable =>
            "Could not reach the model server. Check your connection and try again; the download resumes where it stopped.",
        ModelDeliveryFailure.SourceRejected =>
            "The model server refused the request. Try again later.",
        ModelDeliveryFailure.InsufficientDisk =>
            $"Not enough free disk space: about {Megabytes(result.RequiredBytes)} MB needed, {Megabytes(result.AvailableBytes)} MB free.",
        ModelDeliveryFailure.IntegrityMismatch =>
            "A downloaded file did not match its published checksum, so it was discarded. Try again.",
        ModelDeliveryFailure.StorageUnavailable =>
            "The model folder could not be written. Check that this PC's app data folder is not read-only.",
        ModelDeliveryFailure.AppVersionTooOld =>
            "This model needs a newer EnviousWispr. Check for updates first.",
        ModelDeliveryFailure.Cancelled =>
            "Download cancelled. It resumes from where it stopped when you try again.",
        _ => "This build's model manifest is not valid. Reinstall EnviousWispr.",
    };

    private sealed class ModelDeliveryProgressObserver(Action<ModelDeliveryEvent> report) : IModelDeliveryObserver
    {
        private DateTimeOffset _lastReport = DateTimeOffset.MinValue;

        public void Observe(ModelDeliveryEvent deliveryEvent)
        {
            // THE STORE REPORTS EVERY 128 KB CHUNK. A window repainted that often is a window doing
            // nothing else, so progress reaches it about four times a second and on every milestone.
            var milestone = deliveryEvent.Code is not ModelDeliveryEventCode.DownloadStarted;
            var now = DateTimeOffset.UtcNow;
            if (!milestone && now - _lastReport < TimeSpan.FromMilliseconds(250))
            {
                return;
            }

            _lastReport = now;
            report(deliveryEvent);
        }
    }
}
