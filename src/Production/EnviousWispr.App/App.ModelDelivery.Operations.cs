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
                    OfflineModelStore,
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

    private void OnEgOneModelDownloadRequested(string modelId)
    {
        if (_modelDelivery is { IsCompleted: false } || Leaving)
        {
            return;
        }

        _modelDelivery = DeliverEgOneModelAsync(modelId);
    }

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
            var verifier = new ModelManifestVerifier(new Dictionary<string, string>());
            var provisioner = new ModelProvisioner(
                CreateModelStore(),
                id => BundledModelManifests.Load(id, verifier));
            var result = await provisioner.ProvisionAsync(normalizedId, download.Token).ConfigureAwait(true);
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

        if (_modelDelivery is { IsCompleted: false } || Leaving)
        {
            return;
        }

        _modelDelivery = RemoveEgOneModelAsync(modelId);
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
        await TryReconfigureTranscriptionUnderHoldAsync().ConfigureAwait(true);
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
        EgOneModelCatalog.FullId => "EG-1",
        EgOneModelCatalog.MiniId => "EG-1 Mini",
        EgOneModelCatalog.MicroId => "EG-1 Micro",
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
