using System.Globalization;
using EnviousWispr.ASR;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Polish;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.ModelDelivery;
using EnviousWispr.Pipeline;
using EnviousWispr.Presentation;
using EnviousWispr.Services.Diagnostics;

namespace EnviousWispr.App;

public partial class App
{
    private void OnModelDownloadRequested()
    {
        // ONE AT A TIME, READ OFF THE TASK ITSELF: a delivery still running refuses a second request;
        // one that has ended - including one that refused synchronously because a dictation was in
        // flight - does not, so "finish the current dictation, then download" can be followed.
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
            _modelDelivery = RunAfterExistingDeliveryAsync(existing, DeliverModelsAsync);
            return;
        }

        // ONE TRACKED TASK, from the request to the replaced engines: the exit policy cancels its
        // download and the lifetime joins the whole of it under Quiesce, so a delivery is never
        // tearing engines down or building them beside the exit's own disposals.
        _modelDelivery = DeliverModelsAsync();
    }

    private async Task DeliverModelsAsync()
    {
        if (IsSpeechEngineBusy)
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
            var provisioner = CreateBundledProvisioner();
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
        await TryReconfigureTranscriptionUnderHoldAsync().ConfigureAwait(true);
        if (Leaving)
        {
            return;
        }

        PresentGraphicsRuntime();
        await PresentModelDeliveryAsync().ConfigureAwait(true);
    }

    private async Task<bool> TryReconfigureTranscriptionUnderHoldAsync()
    {
        var attempt = _sessionCoordinator?.TryHold(SessionHolder.SpeechEngineSwitch)
            ?? new SessionHoldAttempt(NoScope.Instance);
        if (attempt.Hold is not { } hold)
        {
            if (attempt.Refusal != SessionHoldRefusal.Closed)
            {
                _transcriptionReconfigurePending = true;
            }

            return false;
        }

        using (hold)
        {
            await ReconfigureTranscriptionAsync().ConfigureAwait(true);
            return true;
        }
    }

    private void OnModelDownloadCancelRequested() => _modelDownload?.Cancel();

    private ModelProvisioner CreateBundledProvisioner()
    {
        var verifier = new ModelManifestVerifier(new Dictionary<string, string>());
        return new ModelProvisioner(CreateModelStore(), id => BundledModelManifests.Load(id, verifier));
    }

    private bool TryScheduleModelDeliveryTask(Func<Task> start)
    {
        if (Leaving)
        {
            return false;
        }

        var existing = _modelDelivery;
        if (existing is { IsCompleted: false })
        {
            if (!_isSilentParakeetUpgrade)
            {
                return false;
            }

            _modelDownload?.Cancel();
            _modelDelivery = RunAfterExistingDeliveryAsync(existing, start);
            return true;
        }

        _modelDelivery = start();
        return true;
    }

    private async Task RunAfterExistingDeliveryAsync(Task existing, Func<Task> next)
    {
        try
        {
            await existing.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }

        if (!Leaving)
        {
            await next().ConfigureAwait(true);
        }
    }

    private async Task RunSilentParakeetUpgradeAsync()
    {
        try
        {
            if (IsSpeechEngineBusy || Leaving)
            {
                _silentParakeetUpgradePending = true;
                _silentParakeetUpgradeAttemptedThisSession = false;
                return;
            }

            var resolution = await ResolveParakeetModelAsync().ConfigureAwait(true);
            if (Leaving || !resolution.ShouldTriggerSilentUpgrade || BundledModelManifests.TryRead(ParakeetModelIds.Final) is null)
            {
                return;
            }

            using var download = new CancellationTokenSource();
            _modelDownload = download;
            _activeDownloadModelId = ParakeetModelIds.Final;
            _isSilentParakeetUpgrade = true;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            _logger.Write(new AppLogEntry(DateTimeOffset.UtcNow, AppEventCode.ModelDeliveryStarted));
            try
            {
                var result = await CreateBundledProvisioner()
                    .ProvisionAsync(ParakeetModelIds.Final, download.Token).ConfigureAwait(true);
                if (!result.Succeeded)
                {
                    _logger.Write(new AppLogEntry(
                        DateTimeOffset.UtcNow, AppEventCode.ModelDeliveryFailed, AppFailureCategory.ModelDelivery, clock.ElapsedMilliseconds));
                    return;
                }

                _logger.Write(new AppLogEntry(
                    DateTimeOffset.UtcNow, AppEventCode.ModelDeliveryCompleted, ElapsedMilliseconds: clock.ElapsedMilliseconds));
            }
            finally
            {
                _modelDownload = null;
                _activeDownloadModelId = null;
                _isSilentParakeetUpgrade = false;
            }

            if (Leaving)
            {
                return;
            }

            var needsFinal = IsConfiguredTranscriptionUsingLegacyV3() ||
                _settings.Preferences.Dictation.FinalEngine == FinalAsrEngine.Parakeet;
            var needsPreview = !needsFinal && _settings.Preferences.LivePreviewEnabled && _previewEngine is not null;
            _retireLegacyParakeetPending = true;
            if (needsFinal)
            {
                if (IsSpeechEngineBusy || !await TryReconfigureTranscriptionUnderHoldAsync().ConfigureAwait(true))
                {
                    _transcriptionReconfigurePending = true;
                    return;
                }
            }
            else if (needsPreview)
            {
                var outcome = IsSpeechEngineBusy
                    ? PreviewReconfigureOutcome.Busy
                    : await ReconfigureLivePreviewOnlyAsync().ConfigureAwait(true);
                if (Leaving || outcome == PreviewReconfigureOutcome.Closed)
                {
                    return;
                }

                if (outcome == PreviewReconfigureOutcome.Busy)
                {
                    _livePreviewReconfigurePending = true;
                    return;
                }
            }

            if (Leaving)
            {
                return;
            }

            if (!IsSpeechEngineBusy && !IsConfiguredTranscriptionUsingLegacyV3())
            {
                _retireLegacyParakeetPending = false;
                await ParakeetModelLocator.RetireLegacyV3AfterUltraInstalledAsync(
                    OfflineModelStore, ModelStoreRoot, () => Task.CompletedTask, DevelopmentModelDirectory(ParakeetModelIds.LegacyV3)).ConfigureAwait(true);
            }

            if (!Leaving)
            {
                PresentGraphicsRuntime();
                await PresentModelDeliveryAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            if (!Leaving && !IsSpeechEngineBusy &&
                (_livePreviewReconfigurePending || _transcriptionReconfigurePending || _polishReconfigurePending || _retireLegacyParakeetPending))
            {
                PostToLiveWindow(_ => TrySchedulePendingModelRefresh());
            }
        }
    }

    private void OnSpeechEngineDownloadRequested(FinalAsrEngine engine) =>
        _ = TryScheduleModelDeliveryTask(() => DeliverSpeechEngineModelAsync(engine));

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

        if (engine == FinalAsrEngine.Parakeet)
        {
            var resolution = await ResolveParakeetModelAsync().ConfigureAwait(true);
            if (resolution.IsUltra)
            {
                _speechEngineDeliveries.Remove(modelId);
                await PresentModelDeliveryAsync().ConfigureAwait(true);
                return;
            }
        }
        else
        {
            var existingDirectory = await ResolveModelDirectoryAsync(modelId).ConfigureAwait(true);
            if (existingDirectory is not null && ModelIsComplete(modelId, existingDirectory))
            {
                _speechEngineDeliveries.Remove(modelId);
                await PresentModelDeliveryAsync().ConfigureAwait(true);
                return;
            }
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
            var result = await CreateBundledProvisioner().ProvisionAsync(modelId, download.Token).ConfigureAwait(true);
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

        _ = TryScheduleModelDeliveryTask(() => RemoveSpeechEngineModelAsync(engine));
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
                    OfflineModelStore,
                    modelId,
                    Path.Combine(ModelStoreRoot, modelId),
                    TeardownTranscriptionAsync).ConfigureAwait(true);
                if (!Leaving && engine == FinalAsrEngine.Parakeet)
                {
                    var legacyOutcome = await ParakeetModelLocator.RemoveLegacyV3FromStoreOrDataAsync(
                        OfflineModelStore,
                        ModelStoreRoot,
                        () => Task.CompletedTask,
                        DevelopmentModelDirectory(ParakeetModelIds.LegacyV3)).ConfigureAwait(true);
                    if (!legacyOutcome.Succeeded && removalOutcome.Succeeded)
                    {
                        removalOutcome = legacyOutcome;
                    }
                }
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

    private void OnEgOneModelDownloadRequested(string modelId) =>
        _ = TryScheduleModelDeliveryTask(() => DeliverEgOneModelAsync(modelId));

    private async Task DeliverEgOneModelAsync(string modelId)
    {
        var normalizedId = EgOneModelCatalog.NormalizeOrDefault(modelId);
        if (IsSpeechEngineBusy)
        {
            _egOneDeliveries[normalizedId] = new(
                SpeechModelDelivery.Failed,
                Detail: EnginePolishPresentation.EgOneDownloadBlockedBusyNotice);
            _window?.ShowEgOneNotice(EnginePolishPresentation.EgOneDownloadBlockedBusyNotice);
            await RefreshEgOneInventoryAsync().ConfigureAwait(true);
            return;
        }

        var existingState = await ProbeEgOneModelAsync(normalizedId).ConfigureAwait(true);
        if (existingState.IsInstalled)
        {
            _egOneDeliveries.Remove(normalizedId);
            await RefreshEgOneInventoryAsync().ConfigureAwait(true);
            return;
        }

        if (BundledModelManifests.TryRead(normalizedId) is null)
        {
            var unavailable = EnginePolishPresentation.DescribeEgOneUnavailableNotice(normalizedId);
            _egOneDeliveries[normalizedId] = new(
                SpeechModelDelivery.CannotDownload,
                Detail: unavailable);
            _window?.ShowEgOneNotice(unavailable);
            await RefreshEgOneInventoryAsync().ConfigureAwait(true);
            return;
        }

        using var download = new CancellationTokenSource();
        _modelDownload = download;
        _activeDownloadModelId = normalizedId;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var tierTitle = EnginePolishPresentation.DescribeEgOneTier(normalizedId).Title;
        _logger.Write(new AppLogEntry(DateTimeOffset.UtcNow, AppEventCode.ModelDeliveryStarted));
        _egOneDeliveries[normalizedId] = new(
            SpeechModelDelivery.Downloading,
            Percent: 0,
            Detail: $"Downloading {tierTitle}…");
        await RefreshEgOneInventoryAsync().ConfigureAwait(true);

        try
        {
            var result = await CreateBundledProvisioner().ProvisionAsync(normalizedId, download.Token).ConfigureAwait(true);
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
                _egOneDeliveries[normalizedId] = new(failureStage, Detail: failureSentence);
                _window?.ShowEgOneNotice(failureSentence);
                await RefreshEgOneInventoryAsync().ConfigureAwait(true);
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

        _egOneDeliveries[normalizedId] = new(
            SpeechModelDelivery.Activating,
            Detail: "Download verified. Activating EG-1…");
        await RefreshEgOneInventoryAsync().ConfigureAwait(true);

        var attempt = _sessionCoordinator?.TryHold(SessionHolder.SpeechEngineSwitch)
            ?? new SessionHoldAttempt(NoScope.Instance);
        if (attempt.Hold is not { } hold)
        {
            if (attempt.Refusal != SessionHoldRefusal.Closed)
            {
                _polishReconfigurePending = true;
                _egOneDeliveries.Remove(normalizedId);
                await RefreshEgOneInventoryAsync().ConfigureAwait(true);
            }

            return;
        }

        using (hold)
        {
            _egOneDeliveries.Remove(normalizedId);
            await ReconfigurePolishRuntimeAsync().ConfigureAwait(true);
        }

        if (Leaving)
        {
            return;
        }

        await RefreshEgOneInventoryAsync().ConfigureAwait(true);
    }

    private void OnEgOneModelRemoveRequested(string modelId)
    {
        if (IsSpeechEngineBusy)
        {
            _window?.ShowEgOneNotice(EnginePolishPresentation.EgOneRemovalBlockedBusyNotice);
            return;
        }

        _ = TryScheduleModelDeliveryTask(() => RemoveEgOneModelAsync(modelId));
    }

    private async Task RemoveEgOneModelAsync(string modelId)
    {
        if (IsSpeechEngineBusy)
        {
            _window?.ShowEgOneNotice(EnginePolishPresentation.EgOneRemovalBlockedBusyNotice);
            return;
        }

        var attempt = _sessionCoordinator?.TryHold(SessionHolder.SpeechEngineSwitch)
            ?? new SessionHoldAttempt(NoScope.Instance);
        if (attempt.Hold is not { } hold)
        {
            _window?.ShowEgOneNotice(EnginePolishPresentation.EgOneRemovalBlockedBusyNotice);
            return;
        }

        var normalizedId = EgOneModelCatalog.NormalizeOrDefault(modelId);
        _egOneDeliveries.Remove(normalizedId);

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

                // Stop llama-server.exe via AbortPolishRuntime before removing ModelStore versions + legacy directory.
                removalOutcome = await ModelPackageCleaner.RemoveAsync(
                    OfflineModelStore,
                    normalizedId,
                    Path.Combine(ModelStoreRoot, normalizedId),
                    TeardownPolishAsync).ConfigureAwait(true);
            }
            finally
            {
                _engineReconfiguration.Release();
            }

            if (!removalOutcome.Succeeded)
            {
                _window?.ShowEgOneNotice(
                    "Could not remove all files for this EG-1 model because another process is using them. Try again in a moment.");
            }

            if (Leaving)
            {
                return;
            }

            await ReconfigurePolishRuntimeAsync().ConfigureAwait(true);
        }

        if (Leaving)
        {
            return;
        }

        await RefreshEgOneInventoryAsync().ConfigureAwait(true);
    }

    private void OnPreviewModelDownloadRequested() =>
        _ = TryScheduleModelDeliveryTask(DeliverPreviewModelAsync);

    private async Task DeliverPreviewModelAsync()
    {
        if (IsSpeechEngineBusy)
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
            var result = await CreateBundledProvisioner().ProvisionAsync(previewModelId, download.Token).ConfigureAwait(true);
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
        await TryReconfigureTranscriptionUnderHoldAsync().ConfigureAwait(true);
        if (Leaving)
        {
            return;
        }

        await PresentModelDeliveryAsync().ConfigureAwait(true);
    }

    private void OnPreviewModelRemoveRequested() =>
        _ = TryScheduleModelDeliveryTask(RemovePreviewModelAsync);

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
                OfflineModelStore,
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
            if (_modelDownload is not null && !_isSilentParakeetUpgrade)
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
                else if (EgOneModelCatalog.IsKnown(_activeDownloadModelId))
                {
                    var egOneId = EgOneModelCatalog.NormalizeOrDefault(_activeDownloadModelId);
                    _egOneDeliveries[egOneId] = new(
                        SpeechModelDelivery.Downloading,
                        percent,
                        presentation.Text);
                    window.SetEgOneDelivery(egOneId, presentation);
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
}
