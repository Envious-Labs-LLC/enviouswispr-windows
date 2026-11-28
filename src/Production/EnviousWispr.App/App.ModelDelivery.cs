using System.Globalization;
using System.Reflection;
using EnviousWispr.ASR;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Onboarding;
using EnviousWispr.Core.Polish;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.LLM;
using EnviousWispr.ModelDelivery;
using EnviousWispr.Pipeline;
using EnviousWispr.Presentation;
using EnviousWispr.Services.Diagnostics;
using EnviousWispr.Services.Distribution;

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
    private readonly Dictionary<string, SpeechEngineTransientDelivery> _egOneDeliveries = new(StringComparer.Ordinal);
    private CancellationTokenSource? _modelDownload;
    private Task? _modelDelivery;
    private CancellationTokenSource? _onboardingDownloadCancellation;
    private OnboardingDownloadCoordinator? _activeOnboardingCoordinator;
    private Task? _onboardingDelivery;
    private (string SpeechModelId, string? PolishModelId, bool ParallelDownloads)? _lastOnboardingDownloadRequest;
    private (string SpeechModelId, string? PolishModelId, bool ParallelDownloads)? _pendingOnboardingDownloadRequest;
    private IReadOnlyList<string> _missingModelIds = [];
    private string? _activeDownloadModelId;
    private bool _activeDeliveryMirrorsSelectedEngine;
    private bool _livePreviewReconfigurePending;
    private bool _transcriptionReconfigurePending;
    private bool _polishReconfigurePending;
    private bool _retireLegacyParakeetPending;
    private bool _silentParakeetUpgradePending;
    private bool _silentParakeetUpgradeAttemptedThisSession;
    private bool _isSilentParakeetUpgrade;
    private TaskCompletionSource? _pendingSpeechReconfigureCompletion;
    private TaskCompletionSource? _pendingPolishReconfigureCompletion;
    private ModelStore? _offlineModelStore;

    private sealed record SpeechEngineTransientDelivery(
        SpeechModelDelivery Stage,
        double? Percent = null,
        string? Detail = null);

    private bool IsSpeechEngineBusy =>
        _sessionController?.CurrentSession is not null ||
        _sessionCoordinator?.IsProcessing == true ||
        _sessionCoordinator?.HeldBy is not null;

    private string ModelStoreRoot => Path.Combine(_dataDirectory, "models");

    private ModelStore OfflineModelStore =>
        _offlineModelStore ??= CreateModelStore(NullModelDeliveryObserver.Instance);

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
    private Task<ParakeetModelResolution> ResolveParakeetModelAsync(
        string environmentVariable = "ENVIOUSWISPR_MODEL_DIRECTORY") =>
        ParakeetModelLocator.ResolveAsync(
            OfflineModelStore,
            ModelStoreRoot,
            Environment.GetEnvironmentVariable(environmentVariable),
            DevelopmentModelDirectory(ParakeetModelIds.Final));

    private async Task<string?> ResolveModelDirectoryAsync(
        string modelId,
        string environmentVariable = "ENVIOUSWISPR_MODEL_DIRECTORY")
    {
        if (string.Equals(modelId, ParakeetTranscriptionEngine.ModelId, StringComparison.Ordinal))
        {
            var resolution = await ResolveParakeetModelAsync(environmentVariable).ConfigureAwait(true);
            return resolution.DirectoryPath;
        }

        var active = await OfflineModelStore.OpenActiveOfflineAsync(modelId).ConfigureAwait(true);
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
        if (string.Equals(modelId, ParakeetTranscriptionEngine.ModelId, StringComparison.Ordinal) ||
            string.Equals(modelId, ParakeetModelIds.LegacyV3, StringComparison.Ordinal))
        {
            return ParakeetModelLocator.IsModelComplete(directory);
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
            (await OfflineModelStore.ListInstalledAsync(WhisperTranscriptionEngine.PreviewModelId).ConfigureAwait(true)).Count > 0;
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
        bool installed;
        bool canRemove;
        if (engine == FinalAsrEngine.Parakeet)
        {
            var resolution = await ResolveParakeetModelAsync().ConfigureAwait(true);
            installed = resolution.IsInstalled;
            canRemove = installed &&
                await ParakeetModelLocator.HasAnyInstalledPackageAsync(OfflineModelStore, ModelStoreRoot).ConfigureAwait(true);
        }
        else
        {
            var directory = await ResolveModelDirectoryAsync(modelId).ConfigureAwait(true);
            installed = directory is not null && ModelIsComplete(modelId, directory);
            var storeInstalled = installed &&
                (await OfflineModelStore.ListInstalledAsync(modelId).ConfigureAwait(true)).Count > 0;
            var legacyRoot = Path.Combine(ModelStoreRoot, modelId);
            canRemove = storeInstalled || (installed && Directory.Exists(legacyRoot));
        }

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

    private static string? EgOneBundleSizeText(string modelId)
    {
        if (BundledModelManifests.TryRead(modelId) is null)
        {
            return null;
        }

        var manifest = BundledModelManifests.Load(modelId, new ModelManifestVerifier(new Dictionary<string, string>()));
        if (!manifest.Succeeded || manifest.Manifest is null)
        {
            return null;
        }

        var totalBytes = manifest.Manifest.Payload.Files.Sum(file => file.SizeBytes);
        return $"{Megabytes(totalBytes)} MB";
    }

    private async Task<EgOneModelState> ProbeEgOneModelAsync(string modelId)
    {
        var normalizedId = EgOneModelCatalog.NormalizeOrDefault(modelId);
        var active = await Task.Run(() => OfflineModelStore.OpenActiveOfflineAsync(normalizedId)).ConfigureAwait(true);
        var storeInstalled = active.Succeeded && active.Installed is not null;
        var storeFile = storeInstalled
            ? EgOneRuntimeLocator.TryResolveInstalledGgufInDirectory(active.Installed!.DirectoryPath, normalizedId)
            : null;
        var configuredModelId = ConfiguredEgOneModelId(_settings.Preferences.Polish);
        var envOverride = string.Equals(normalizedId, configuredModelId, StringComparison.Ordinal)
            ? Environment.GetEnvironmentVariable("ENVIOUSWISPR_EG1_MODEL_PATH")
            : null;
        var legacyFile = EgOneRuntimeLocator.ResolveModelFile(_dataDirectory, normalizedId, envOverride);
        var installed = storeFile is not null || EgOneRuntimeLocator.IsModelFileReady(legacyFile);
        var legacyRoot = Path.Combine(ModelStoreRoot, normalizedId);
        var canRemove = storeInstalled ||
            (EgOneRuntimeLocator.IsModelFileReady(EgOneRuntimeLocator.ResolveModelFile(_dataDirectory, normalizedId)) &&
             Directory.Exists(legacyRoot));
        var hasBundledManifest = BundledModelManifests.TryRead(normalizedId) is not null;
        var bundleSizeText = EgOneBundleSizeText(normalizedId);

        if (installed)
        {
            if (_egOneDeliveries.TryGetValue(normalizedId, out var activating) &&
                activating.Stage == SpeechModelDelivery.Activating)
            {
                return new EgOneModelState(
                    ModelId: normalizedId,
                    IsInstalled: true,
                    CanRemove: canRemove,
                    HasBundledManifest: hasBundledManifest,
                    DeliveryStage: SpeechModelDelivery.Activating,
                    StatusDetail: activating.Detail,
                    BundleSizeText: bundleSizeText);
            }

            _egOneDeliveries.Remove(normalizedId);
            return new EgOneModelState(
                ModelId: normalizedId,
                IsInstalled: true,
                CanRemove: canRemove,
                HasBundledManifest: hasBundledManifest,
                DeliveryStage: SpeechModelDelivery.Installed,
                BundleSizeText: bundleSizeText);
        }

        if (_egOneDeliveries.TryGetValue(normalizedId, out var delivery))
        {
            return new EgOneModelState(
                ModelId: normalizedId,
                IsInstalled: false,
                CanRemove: false,
                HasBundledManifest: hasBundledManifest,
                DeliveryStage: delivery.Stage,
                DownloadPercent: delivery.Percent,
                StatusDetail: delivery.Detail,
                BundleSizeText: bundleSizeText);
        }

        return new EgOneModelState(
            ModelId: normalizedId,
            IsInstalled: false,
            CanRemove: false,
            HasBundledManifest: hasBundledManifest,
            DeliveryStage: SpeechModelDelivery.Missing,
            BundleSizeText: bundleSizeText);
    }

    private bool CheckEgOneServerExecutableExists()
    {
        try
        {
            var serverExecutable = EgOneRuntimeLocator.ResolveServerExecutable(
                AppContext.BaseDirectory,
                _dataDirectory,
                _updateService.IsStoreInstalled,
                Environment.GetEnvironmentVariable("ENVIOUSWISPR_EG1_SERVER_EXE"));
            return File.Exists(serverExecutable);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private async Task RefreshEgOneInventoryAsync()
    {
        var states = new Dictionary<string, EgOneModelState>(StringComparer.Ordinal);
        foreach (var modelId in EgOneModelCatalog.AllIds)
        {
            states[modelId] = await ProbeEgOneModelAsync(modelId).ConfigureAwait(true);
        }

        var activeDownloadId = _modelDownload is not null && EgOneModelCatalog.IsKnown(_activeDownloadModelId)
            ? EgOneModelCatalog.NormalizeOrDefault(_activeDownloadModelId)
            : null;
        _window?.SetEgOneModels(states, CheckEgOneServerExecutableExists(), activeDownloadId);
    }

    private enum PreviewReconfigureOutcome
    {
        Applied,
        Busy,
        Closed,
    }

    private Task<bool> PresentModelDeliveryAsync() => PresentModelDeliveryCoreAsync();

    private async Task<bool> PresentModelDeliveryCoreAsync()
    {
        if (_modelDownload is not null)
        {
            return true;
        }

        var coordinatorClosed = false;
        do
        {
            while (_livePreviewReconfigurePending || _transcriptionReconfigurePending || _polishReconfigurePending)
            {
                if (IsSpeechEngineBusy || Leaving)
                {
                    break;
                }

                if (_transcriptionReconfigurePending)
                {
                    var attempt = _sessionCoordinator?.TryHold(SessionHolder.SpeechEngineSwitch)
                        ?? new SessionHoldAttempt(NoScope.Instance);
                    if (attempt.Hold is not { } hold)
                    {
                        coordinatorClosed = attempt.Refusal == SessionHoldRefusal.Closed;
                        break;
                    }

                    using (hold)
                    {
                        try
                        {
                            _transcriptionReconfigurePending = false;
                            _livePreviewReconfigurePending = false;
                            await ReconfigureTranscriptionAsync().ConfigureAwait(true);
                            if (Leaving)
                            {
                                return false;
                            }

                            PresentGraphicsRuntime();
                        }
                        finally
                        {
                            _pendingSpeechReconfigureCompletion?.TrySetResult();
                        }
                    }

                    continue;
                }

                if (_livePreviewReconfigurePending)
                {
                    _livePreviewReconfigurePending = false;
                    if (_configuredTranscription is not null)
                    {
                        var outcome = await ReconfigureLivePreviewOnlyAsync().ConfigureAwait(true);
                        if (Leaving || outcome == PreviewReconfigureOutcome.Closed)
                        {
                            return false;
                        }

                        if (outcome == PreviewReconfigureOutcome.Busy)
                        {
                            _livePreviewReconfigurePending = true;
                            break;
                        }
                    }

                    continue;
                }

                if (_polishReconfigurePending)
                {
                    var attempt = _sessionCoordinator?.TryHold(SessionHolder.SpeechEngineSwitch)
                        ?? new SessionHoldAttempt(NoScope.Instance);
                    if (attempt.Hold is not { } hold)
                    {
                        coordinatorClosed = attempt.Refusal == SessionHoldRefusal.Closed;
                        break;
                    }

                    using (hold)
                    {
                        try
                        {
                            _polishReconfigurePending = false;
                            await ReconfigurePolishRuntimeAsync().ConfigureAwait(true);
                            if (Leaving)
                            {
                                return false;
                            }
                        }
                        finally
                        {
                            _pendingPolishReconfigureCompletion?.TrySetResult();
                        }
                    }
                }
            }

            if (coordinatorClosed || Leaving)
            {
                if (coordinatorClosed)
                {
                    _transcriptionReconfigurePending = false;
                    _polishReconfigurePending = false;
                }

                _pendingSpeechReconfigureCompletion?.TrySetResult();
                _pendingPolishReconfigureCompletion?.TrySetResult();
                return false;
            }

            await RefreshLivePreviewModelInventoryAsync().ConfigureAwait(true);
            await RefreshSpeechEngineInventoryAsync().ConfigureAwait(true);
            await RefreshEgOneInventoryAsync().ConfigureAwait(true);
            await RefreshEgOneConfiguredProviderFromStoreAsync(_settings.Preferences.Polish).ConfigureAwait(true);
            if (Leaving)
            {
                return false;
            }
            _missingModelIds = await MissingModelIdsAsync(_settings.Preferences).ConfigureAwait(true);
            if (_missingModelIds.Count == 0)
            {
                _window?.SetModelDelivery(new(
                    "The speech model this build pins is installed and verified.",
                    Stage: SpeechModelDelivery.Installed));
            }
            else
            {
                var installable = _missingModelIds.Where(id => BundledModelManifests.TryRead(id) is not null).ToArray();
                if (installable.Length == 0)
                {
                    _window?.SetModelDelivery(new(
                        "This build cannot download the model it needs. Reinstall EnviousWispr.",
                        Stage: SpeechModelDelivery.CannotDownload));
                }
                else
                {
                    var totalBytes = installable.Sum(id =>
                        BundledModelManifests.Load(id, new ModelManifestVerifier(new Dictionary<string, string>()))
                            .Manifest?.Payload.Files.Sum(file => file.SizeBytes) ?? 0);
                    _window?.SetModelDelivery(new(
                        $"{SentenceStart(DescribeModels(installable))} {(installable.Length == 1 ? "is" : "are")} not installed on this PC. About {Megabytes(totalBytes)} MB to download, verified file by file.",
                        CanDownload: true,
                        Stage: SpeechModelDelivery.Missing));
                }
            }
        }
        while (!Leaving &&
               !IsSpeechEngineBusy &&
               (_livePreviewReconfigurePending || _transcriptionReconfigurePending || _polishReconfigurePending));

        await EvaluateParakeetUpgradeStateAsync().ConfigureAwait(true);
        if (Leaving)
        {
            return false;
        }

        return true;
    }

    private void OnDictationEnded()
    {
        TrySchedulePendingModelRefresh();
        TryStartSilentParakeetUpgrade();
    }

    private void TrySchedulePendingModelRefresh()
    {
        if ((!_livePreviewReconfigurePending && !_transcriptionReconfigurePending && !_polishReconfigurePending && !_retireLegacyParakeetPending) ||
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
        var completedCleanly = false;
        try
        {
            if (IsSpeechEngineBusy || Leaving)
            {
                return;
            }

            completedCleanly = await PresentModelDeliveryCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            if (completedCleanly &&
                !Leaving &&
                !IsSpeechEngineBusy &&
                (_livePreviewReconfigurePending || _transcriptionReconfigurePending || _polishReconfigurePending || _retireLegacyParakeetPending))
            {
                // If dictation ended after PresentModelDeliveryCoreAsync checked IsSpeechEngineBusy and before
                // _modelDelivery completed, OnDictationEnded skipped scheduling; drain on the next UI tick.
                PostToLiveWindow(_ => TrySchedulePendingModelRefresh());
            }
        }
    }

    private bool IsConfiguredTranscriptionUsingLegacyV3() =>
        ParakeetModelLocator.IsDirectoryUnderLegacyV3(_configuredTranscription?.ModelDirectory, ModelStoreRoot);

    private async Task EvaluateParakeetUpgradeStateAsync()
    {
        if (Leaving)
        {
            return;
        }

        var developmentLegacyDir = DevelopmentModelDirectory(ParakeetModelIds.LegacyV3);
        var action = await ParakeetModelLocator.EvaluateUpgradeActionAsync(
            OfflineModelStore,
            ModelStoreRoot,
            Environment.GetEnvironmentVariable("ENVIOUSWISPR_MODEL_DIRECTORY"),
            DevelopmentModelDirectory(ParakeetModelIds.Final),
            developmentLegacyDir).ConfigureAwait(true);
        if (Leaving)
        {
            return;
        }

        switch (action)
        {
            case ParakeetUpgradeAction.None:
                _retireLegacyParakeetPending = false;
                return;
            case ParakeetUpgradeAction.RetireLegacyV3:
                if (IsSpeechEngineBusy)
                {
                    _retireLegacyParakeetPending = true;
                    return;
                }

                if (IsConfiguredTranscriptionUsingLegacyV3())
                {
                    if (!await TryReconfigureTranscriptionUnderHoldAsync().ConfigureAwait(true) || Leaving)
                    {
                        _retireLegacyParakeetPending = true;
                        return;
                    }

                    PresentGraphicsRuntime();
                }

                if (!IsConfiguredTranscriptionUsingLegacyV3() && !IsSpeechEngineBusy)
                {
                    _retireLegacyParakeetPending = false;
                    await ParakeetModelLocator.RetireLegacyV3AfterUltraInstalledAsync(
                        OfflineModelStore, ModelStoreRoot, () => Task.CompletedTask, developmentLegacyDir).ConfigureAwait(true);
                }

                return;
            case ParakeetUpgradeAction.StartSilentUpgrade when !_silentParakeetUpgradeAttemptedThisSession:
                _silentParakeetUpgradePending = true;
                if (_modelDelivery is not { IsCompleted: false })
                {
                    TryStartSilentParakeetUpgrade();
                }
                else
                {
                    PostToLiveWindow(_ => TryStartSilentParakeetUpgrade());
                }

                return;
        }
    }

    private void TryStartSilentParakeetUpgrade()
    {
        if (!_silentParakeetUpgradePending ||
            _silentParakeetUpgradeAttemptedThisSession ||
            _modelDelivery is { IsCompleted: false } ||
            _onboardingDelivery is { IsCompleted: false } ||
            IsSpeechEngineBusy ||
            Leaving)
        {
            return;
        }

        _silentParakeetUpgradePending = false;
        _silentParakeetUpgradeAttemptedThisSession = true;
        _modelDelivery = RunSilentParakeetUpgradeAsync();
    }

    /// <summary>The person asked for local transcription to be started again, from the first-run checklist or warm-up.</summary>
    /// <remarks>
    /// THE SAME TRACKED SLOT AS A DOWNLOAD, because it is the second half of one: a download ends by running the
    /// launch configuration again, and this runs only that half. One task at a time, joined under Quiesce, and
    /// refused while a dictation holds the session - tearing an engine down under a take would lose the take.
    /// </remarks>
    private void OnTranscriptionRetryRequested()
    {
        if (Leaving)
        {
            return;
        }

        if (_modelDelivery is { IsCompleted: false } existing)
        {
            if (!_isSilentParakeetUpgrade)
            {
                return;
            }

            _modelDownload?.Cancel();
            _modelDelivery = RunAfterExistingDeliveryAsync(existing, RetryTranscriptionAsync);
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

    private async Task<PreviewReconfigureOutcome> ReconfigureLivePreviewOnlyAsync()
    {
        var previewEnabledAtStart = _settings.Preferences.LivePreviewEnabled;
        string? parakeetPreviewDirectory = null;
        string? previewModelDirectory = null;
        if (previewEnabledAtStart)
        {
            parakeetPreviewDirectory = await ResolveModelDirectoryAsync(ParakeetTranscriptionEngine.ModelId).ConfigureAwait(true);
            if (Leaving)
            {
                return PreviewReconfigureOutcome.Closed;
            }

            previewModelDirectory = await ResolveModelDirectoryAsync(
                WhisperTranscriptionEngine.PreviewModelId,
                "ENVIOUSWISPR_PREVIEW_MODEL_DIRECTORY").ConfigureAwait(true);
            if (Leaving)
            {
                return PreviewReconfigureOutcome.Closed;
            }
        }

        if (IsSpeechEngineBusy)
        {
            return PreviewReconfigureOutcome.Busy;
        }

        var attempt = _sessionCoordinator?.TryHold(SessionHolder.SpeechEngineSwitch)
            ?? new SessionHoldAttempt(NoScope.Instance);
        if (attempt.Hold is not { } hold)
        {
            return attempt.Refusal == SessionHoldRefusal.Closed
                ? PreviewReconfigureOutcome.Closed
                : PreviewReconfigureOutcome.Busy;
        }

        using (hold)
        {
            await _engineReconfiguration.WaitAsync().ConfigureAwait(true);
            try
            {
                if (Leaving)
                {
                    return PreviewReconfigureOutcome.Closed;
                }

                if (_previewEngine is not null)
                {
                    await _previewEngine.DisposeAsync().ConfigureAwait(true);
                    _previewEngine = null;
                }

                if (Leaving)
                {
                    return PreviewReconfigureOutcome.Closed;
                }

                if (!_settings.Preferences.LivePreviewEnabled)
                {
                    _previewUnavailableReason = null;
                    return PreviewReconfigureOutcome.Applied;
                }

                if (!previewEnabledAtStart)
                {
                    _livePreviewReconfigurePending = true;
                    return PreviewReconfigureOutcome.Applied;
                }

                if (_configuredTranscription is not { } configured)
                {
                    return PreviewReconfigureOutcome.Applied;
                }

                var hardware = _lastHardwareSnapshot ?? configured.Hardware;
                var workerExecutable = Path.Combine(AppContext.BaseDirectory, "EnviousWispr.RuntimeWorker.exe");
                ConfigureLivePreview(
                    workerExecutable,
                    hardware,
                    CurrentWhisperLanguage(),
                    parakeetPreviewDirectory,
                    previewModelDirectory,
                    forceCpu: configured.ForcePreviewCpu);
                return PreviewReconfigureOutcome.Applied;
            }
            finally
            {
                _engineReconfiguration.Release();
            }
        }
    }

    private async Task TeardownPolishAsync()
    {
        AbortPolishRuntime();
        if (_polishWarmup is { } warmup)
        {
            _polishWarmup = null;
            try
            {
                await warmup.ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
            }
        }

        var provider = _polishProvider;
        _polishProvider = null;
        if (provider is EgOnePolishProvider egOne)
        {
            egOne.ServerActivityChanged -= OnEgOneServerActivityChanged;
        }
        NotifyWindowEgOneActivityChanged();
        await CancelAndDrainRepolishAsync().ConfigureAwait(true);
        if (provider is not null)
        {
            await provider.DisposeAsync().ConfigureAwait(true);
        }
    }

    private async Task ReconfigurePolishRuntimeAsync()
    {
        await _engineReconfiguration.WaitAsync().ConfigureAwait(true);
        try
        {
            if (Leaving)
            {
                return;
            }

            await TeardownPolishAsync().ConfigureAwait(true);
            if (Leaving)
            {
                return;
            }

            await ConfigurePolishAsync(_settings.Preferences.Polish).ConfigureAwait(true);
            _window?.SetCloudPolishNotice(_cloudPolishConsent?.Notice);
            _window?.SetOllamaPolishNotice(_localPolishNotice);
            StartPolishWarmup();
        }
        finally
        {
            _engineReconfiguration.Release();
        }
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
}
