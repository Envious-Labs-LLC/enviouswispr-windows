using System.Globalization;
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

namespace EnviousWispr.App;

public partial class App
{
    private void OnOnboardingDualDownloadRequested(string speechModelId, string? polishModelId, bool parallelDownloads)
    {
        if (Leaving)
        {
            return;
        }

        var request = (speechModelId, polishModelId, parallelDownloads);
        if (_onboardingDelivery is { IsCompleted: false })
        {
            if (Nullable.Equals(_lastOnboardingDownloadRequest, request))
            {
                return;
            }

            _pendingOnboardingDownloadRequest = request;
            _onboardingDownloadCancellation?.Cancel();
            return;
        }

        _pendingOnboardingDownloadRequest = null;
        _lastOnboardingDownloadRequest = request;
        _onboardingDelivery = DeliverOnboardingDualModelsAsync(speechModelId, polishModelId, parallelDownloads);
    }

    private void OnOnboardingDualDownloadCancelRequested()
    {
        _pendingOnboardingDownloadRequest = null;
        _onboardingDownloadCancellation?.Cancel();
    }

    private void OnOnboardingDualDownloadSkipPolishRequested()
    {
        if (_pendingOnboardingDownloadRequest is { } pending)
        {
            _pendingOnboardingDownloadRequest = pending with { PolishModelId = null };
        }

        if (_lastOnboardingDownloadRequest is { } req)
        {
            _lastOnboardingDownloadRequest = req with { PolishModelId = null };
        }

        _activeOnboardingCoordinator?.SkipPolish();
    }

    private void OnOnboardingDualDownloadRetryRequested()
    {
        if (_onboardingDelivery is { IsCompleted: false } || Leaving || _lastOnboardingDownloadRequest is not { } req)
        {
            return;
        }

        _onboardingDelivery = DeliverOnboardingDualModelsAsync(req.SpeechModelId, req.PolishModelId, req.ParallelDownloads);
    }

    private async Task DeliverOnboardingDualModelsAsync(
        string speechModelId,
        string? polishModelId,
        bool parallelDownloads)
    {
        if (IsSpeechEngineBusy || Leaving)
        {
            return;
        }

        var speechEngine = string.Equals(speechModelId, WhisperTranscriptionEngine.ModelId, StringComparison.Ordinal)
            ? FinalAsrEngine.Whisper
            : FinalAsrEngine.Parakeet;
        var speechState = await ProbeSpeechEngineModelAsync(speechEngine).ConfigureAwait(true);
        var normalizedPolishId = string.IsNullOrWhiteSpace(polishModelId)
            ? null
            : EgOneModelCatalog.NormalizeOrDefault(polishModelId);
        var polishState = normalizedPolishId is null
            ? null
            : await ProbeEgOneModelAsync(normalizedPolishId).ConfigureAwait(true);
        var polishInstalled = normalizedPolishId is null || (polishState?.IsInstalled == true &&
            IsExpectedEgOneConfigured(normalizedPolishId));
        var speechInstalled = speechState.IsInstalled &&
            IsExpectedSpeechConfigured(speechEngine);

        if (Leaving)
        {
            return;
        }

        var verifier = new ModelManifestVerifier(new Dictionary<string, string>());
        var rules = WindowsSetupRules.Current;
        var speechFallbackBytes = rules.SpeechCatalog.TryGetValue(speechModelId, out var speechRule)
            ? speechRule.SizeBytes
            : 0L;
        var (speechTotalBytes, speechDiskBytes) = MeasureBundledDownloadAndDiskBytes(
            speechModelId,
            verifier,
            speechFallbackBytes);

        var polishFallbackBytes = normalizedPolishId is not null &&
            rules.PolishCatalog.TryGetValue(normalizedPolishId, out var polishRule)
                ? polishRule.SizeBytes
                : 0L;
        var (polishTotalBytes, polishDiskBytes) = normalizedPolishId is not null
            ? MeasureBundledDownloadAndDiskBytes(normalizedPolishId, verifier, polishFallbackBytes)
            : (0L, 0L);

        var plan = new OnboardingDownloadPlan(
            SpeechModelId: speechModelId,
            SpeechTotalBytes: speechTotalBytes,
            PolishModelId: normalizedPolishId,
            PolishTotalBytes: polishTotalBytes,
            ParallelDownloads: parallelDownloads,
            SpeechAlreadyInstalled: speechInstalled,
            PolishAlreadyInstalled: polishInstalled,
            SpeechDiskBytes: speechState.IsInstalled ? 0L : speechDiskBytes,
            PolishDiskBytes: normalizedPolishId is null || polishState?.IsInstalled == true ? 0L : polishDiskBytes);

        using var cancellation = new CancellationTokenSource();
        _onboardingDownloadCancellation = cancellation;

        using var coordinator = new OnboardingDownloadCoordinator(
            plan,
            provisionAsync: async (modelId, observer, ct) =>
            {
                if (string.Equals(modelId, ParakeetTranscriptionEngine.ModelId, StringComparison.Ordinal) &&
                    _isSilentParakeetUpgrade &&
                    _modelDelivery is { IsCompleted: false } silentUpgrade)
                {
                    _modelDownload?.Cancel();
                    try
                    {
                        await silentUpgrade.ConfigureAwait(true);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }

                var provisioner = new ModelProvisioner(
                    CreateModelStore(observer),
                    id => BundledModelManifests.Load(id, verifier));
                return await provisioner.ProvisionAsync(modelId, ct).ConfigureAwait(true);
            },
            onSpeechInstalledAsync: ct => DispatchOnUiThreadAsync(async () =>
            {
                if (!await ApplyOnboardingSpeechChoiceAsync(speechEngine).ConfigureAwait(true) || Leaving)
                {
                    return;
                }

                if (!await EnsureOnboardingActivationAsync(
                    TryReconfigureTranscriptionUnderHoldAsync,
                    () => _transcriptionReconfigurePending,
                    completion => _pendingSpeechReconfigureCompletion = completion,
                    () => IsExpectedSpeechConfigured(speechEngine),
                    $"Could not start the {speechEngine} speech engine after download.",
                    ct).ConfigureAwait(true))
                {
                    return;
                }

                PresentGraphicsRuntime();
                await PresentModelDeliveryAsync().ConfigureAwait(true);
            }, ct),
            onPolishInstalledAsync: ct => DispatchOnUiThreadAsync(async () =>
            {
                if (Leaving || normalizedPolishId is null)
                {
                    return;
                }

                if (!await ApplyOnboardingPolishChoiceAsync(normalizedPolishId).ConfigureAwait(true) || Leaving)
                {
                    return;
                }

                if (!await EnsureOnboardingActivationAsync(
                    TryReconfigurePolishUnderHoldAsync,
                    () => _polishReconfigurePending,
                    completion => _pendingPolishReconfigureCompletion = completion,
                    () => IsExpectedEgOneConfigured(normalizedPolishId),
                    $"Could not configure local AI polish ({normalizedPolishId}) after download.",
                    ct).ConfigureAwait(true))
                {
                    return;
                }

                await RefreshEgOneInventoryAsync().ConfigureAwait(true);
            }, ct),
            onSnapshotChanged: snapshot => PostToLiveWindow(window => ApplyOnboardingDownloadSnapshot(window, snapshot)),
            diskProbePath: ModelStoreRoot,
            failureFormatter: FailureSentence);

        try
        {
            _activeOnboardingCoordinator = coordinator;
            await coordinator.RunAsync(cancellation.Token).ConfigureAwait(true);
        }
        finally
        {
            _activeOnboardingCoordinator = null;
            _onboardingDownloadCancellation = null;
            if (_pendingOnboardingDownloadRequest is { } pending && !Leaving)
            {
                _pendingOnboardingDownloadRequest = null;
                _lastOnboardingDownloadRequest = pending;
                _onboardingDelivery = DeliverOnboardingDualModelsAsync(
                    pending.SpeechModelId,
                    pending.PolishModelId,
                    pending.ParallelDownloads);
            }
        }
    }

    private Task DispatchOnUiThreadAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        if (Leaving || cancellationToken.IsCancellationRequested || _window is not { } window)
        {
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!window.DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (!Leaving && !cancellationToken.IsCancellationRequested)
                {
                    await action().ConfigureAwait(true);
                }

                tcs.TrySetResult();
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetCanceled(cancellationToken);
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
            {
                tcs.TrySetException(ex);
            }
        }))
        {
            tcs.TrySetResult();
        }

        return tcs.Task;
    }

    private void ApplyOnboardingDownloadSnapshot(MainWindow window, OnboardingDownloadSnapshot snapshot)
    {
        window.ObserveOnboardingDownloadSnapshot(snapshot);

        var speechStage = MainWindow.ToSpeechModelDelivery(snapshot.Speech.Stage);
        var speechPresentation = new ModelDeliveryPresentation(
            snapshot.Speech.StatusDetail ?? string.Empty,
            Percent: snapshot.Speech.Stage == OnboardingDownloadStage.Downloading ? snapshot.Speech.Percent : null,
            CanDownload: snapshot.Speech.CanRetry,
            CanCancel: snapshot.Speech.CanCancel,
            Stage: speechStage);
        var speechEngine = string.Equals(snapshot.Speech.ModelId, WhisperTranscriptionEngine.ModelId, StringComparison.Ordinal)
            ? FinalAsrEngine.Whisper
            : FinalAsrEngine.Parakeet;
        window.SetSpeechEngineDelivery(speechEngine, speechPresentation);
        window.SetModelDelivery(speechPresentation);

        if (snapshot.Polish is { } polish)
        {
            var polishStage = MainWindow.ToSpeechModelDelivery(polish.Stage);
            var polishPresentation = new ModelDeliveryPresentation(
                polish.StatusDetail ?? string.Empty,
                Percent: polish.Stage == OnboardingDownloadStage.Downloading ? polish.Percent : null,
                CanDownload: polish.CanRetry,
                CanCancel: polish.CanCancel,
                Stage: polishStage);
            window.SetEgOneDelivery(EgOneModelCatalog.NormalizeOrDefault(polish.ModelId), polishPresentation);
        }

        if (!IsSpeechEngineBusy && snapshot.BackgroundPillSummary is { } pillSummary)
        {
            window.SetSessionStatus(DictationStatus.Quiet(pillSummary));
        }
    }

    private static (long TotalBytes, long DiskBytes) MeasureBundledDownloadAndDiskBytes(
        string modelId,
        ModelManifestVerifier verifier,
        long fallbackBytes)
    {
        var loaded = BundledModelManifests.Load(modelId, verifier);
        if (!loaded.Succeeded || loaded.Manifest is null)
        {
            return (fallbackBytes, fallbackBytes);
        }

        var totalBytes = loaded.Manifest.Payload.Files.Sum(file => file.SizeBytes);
        var diskBytes = OnboardingDownloadCoordinator.ComputeManifestRequiredDiskBytes(loaded.Manifest);
        return (totalBytes, diskBytes);
    }

    private async Task<(bool SpeechInstalled, bool PolishInstalled)> ProbeOnboardingModelsInstalledAsync(
        FinalAsrEngine speechEngine,
        string? egOneModelId)
    {
        await PresentModelDeliveryAsync().ConfigureAwait(true);
        var speechState = await ProbeSpeechEngineModelAsync(speechEngine).ConfigureAwait(true);
        var polishInstalled = true;
        if (!string.IsNullOrWhiteSpace(egOneModelId))
        {
            await RefreshEgOneInventoryAsync().ConfigureAwait(true);
            var normalized = EgOneModelCatalog.NormalizeOrDefault(egOneModelId);
            var egState = await ProbeEgOneModelAsync(normalized).ConfigureAwait(true);
            polishInstalled = egState.IsInstalled;
        }

        return (speechState.IsInstalled, polishInstalled);
    }

    private async Task<bool> ApplyOnboardingSpeechChoiceAsync(FinalAsrEngine engine)
    {
        var current = _presentation?.Settings.Current ?? _settings;
        _settings = current;
        if (current.Preferences.Dictation.FinalEngine == engine)
        {
            return !Leaving;
        }

        return await TrySaveOnboardingSettingsAsync(latest => latest with
        {
            Preferences = latest.Preferences with
            {
                Dictation = latest.Preferences.Dictation with { FinalEngine = engine },
            },
        }).ConfigureAwait(true);
    }

    private async Task ApplyOnboardingSelectedEnginesAsync(
        FinalAsrEngine speechEngine,
        OnboardingPolishChoiceKind polishChoice,
        string? egOneModelId,
        string? cloudModelId)
    {
        if (Leaving)
        {
            return;
        }

        var polishProvider = polishChoice switch
        {
            OnboardingPolishChoiceKind.EgOne => PolishProvider.EgOne,
            OnboardingPolishChoiceKind.Ollama => PolishProvider.Ollama,
            OnboardingPolishChoiceKind.OpenAI => PolishProvider.OpenAI,
            OnboardingPolishChoiceKind.Claude => PolishProvider.Anthropic,
            OnboardingPolishChoiceKind.Gemini => PolishProvider.Gemini,
            _ => PolishProvider.None,
        };

        var normalizedEgOne = polishProvider == PolishProvider.EgOne && !string.IsNullOrWhiteSpace(egOneModelId)
            ? EgOneModelCatalog.NormalizeOrDefault(egOneModelId)
            : null;
        var resolvedModelId = polishProvider switch
        {
            PolishProvider.EgOne => normalizedEgOne ?? EgOneModelCatalog.MiniId,
            PolishProvider.OpenAI or PolishProvider.Anthropic or PolishProvider.Gemini => string.IsNullOrWhiteSpace(cloudModelId)
                ? _polishModelSource?.RecommendedModel(polishProvider)
                : cloudModelId.Trim(),
            _ => null,
        };

        var saved = await TrySaveOnboardingSettingsAsync(latest => latest with
        {
            Preferences = latest.Preferences with
            {
                Dictation = latest.Preferences.Dictation with { FinalEngine = speechEngine },
                Polish = latest.Preferences.Polish with
                {
                    Provider = polishProvider,
                    EgOneModelId = normalizedEgOne ?? latest.Preferences.Polish.EgOneModelId,
                    ModelId = resolvedModelId,
                },
            },
        }).ConfigureAwait(true);
        if (!saved || Leaving)
        {
            return;
        }

        await TryReconfigureTranscriptionUnderHoldAsync().ConfigureAwait(true);
        if (!Leaving)
        {
            await TryReconfigurePolishUnderHoldAsync().ConfigureAwait(true);
        }
    }

    private async Task<bool> ApplyOnboardingPolishChoiceAsync(string egOneModelId)
    {
        var current = _presentation?.Settings.Current ?? _settings;
        _settings = current;
        var currentPolish = current.Preferences.Polish;
        if (currentPolish.Provider == PolishProvider.EgOne &&
            string.Equals(ConfiguredEgOneModelId(currentPolish), egOneModelId, StringComparison.Ordinal))
        {
            return !Leaving;
        }

        return await TrySaveOnboardingSettingsAsync(latest => latest with
        {
            Preferences = latest.Preferences with
            {
                Polish = latest.Preferences.Polish with
                {
                    Provider = PolishProvider.EgOne,
                    EgOneModelId = egOneModelId,
                },
            },
        }).ConfigureAwait(true);
    }

    private async Task<bool> TrySaveOnboardingSettingsAsync(Func<AppSettings, AppSettings> transform)
    {
        if (Leaving)
        {
            return false;
        }

        if (_presentation is { } presentation)
        {
            if (!presentation.TryEnter(out var lease))
            {
                return false;
            }

            SettingsSaveResult result;
            using (lease)
            {
                result = await presentation.Settings
                    .SaveAsync(transform)
                    .ConfigureAwait(true);
            }

            if (!result.Saved)
            {
                if (result.Refusal != SettingsSaveRefusal.Closing)
                {
                    _logger.Write(new AppLogEntry(
                        DateTimeOffset.UtcNow,
                        AppEventCode.SettingsRecovered,
                        AppFailureCategory.StorageUnavailable));
                    throw new InvalidOperationException(
                        $"Could not persist onboarding settings ({result.Refusal}).");
                }

                return false;
            }

            var saved = presentation.Settings.Current;
            _settings = saved;
            _trayIcon?.SetEngineAndPolishSummary(
                saved.Preferences.Dictation.FinalEngine,
                saved.Preferences.Polish.Provider);
            _window?.SyncExternalSettingsUpdate();
            return !Leaving;
        }

        var updated = transform(_settings);
        _settings = updated;
        _trayIcon?.SetEngineAndPolishSummary(
            updated.Preferences.Dictation.FinalEngine,
            updated.Preferences.Polish.Provider);
        try
        {
            await _settingsStore.SaveAsync(updated).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.SettingsRecovered,
                AppFailureCategory.StorageUnavailable));
            throw new InvalidOperationException("Could not save onboarding settings.", ex);
        }

        return !Leaving;
    }

    private async Task<bool> TryReconfigurePolishUnderHoldAsync()
    {
        var attempt = _sessionCoordinator?.TryHold(SessionHolder.SpeechEngineSwitch)
            ?? new SessionHoldAttempt(NoScope.Instance);
        if (attempt.Hold is not { } hold)
        {
            if (attempt.Refusal != SessionHoldRefusal.Closed)
            {
                _polishReconfigurePending = true;
            }

            return false;
        }

        using (hold)
        {
            await ReconfigurePolishRuntimeAsync().ConfigureAwait(true);
            return true;
        }
    }

    private bool IsExpectedSpeechConfigured(FinalAsrEngine engine) =>
        _transcriptionEngine is not null &&
        _configuredTranscription?.Engine == engine &&
        _settings.Preferences.Dictation.FinalEngine == engine;

    private bool IsExpectedEgOneConfigured(string modelId) =>
        _polishProvider is EgOnePolishProvider egOne &&
        string.Equals(egOne.ModelId, modelId, StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(_configuredEgOneModelPath) &&
        _settings.Preferences.Polish.Provider == PolishProvider.EgOne &&
        string.Equals(ConfiguredEgOneModelId(_settings.Preferences.Polish), modelId, StringComparison.Ordinal);

    private async Task<bool> EnsureOnboardingActivationAsync(
        Func<Task<bool>> tryReconfigureAsync,
        Func<bool> isPending,
        Action<TaskCompletionSource?> setCompletion,
        Func<bool> isConfigured,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        var reconfigured = await tryReconfigureAsync().ConfigureAwait(true);
        if (Leaving)
        {
            return false;
        }

        if (!reconfigured)
        {
            if (!isPending())
            {
                throw new InvalidOperationException(failureMessage);
            }

            while (isPending() && !Leaving)
            {
                var wait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                setCompletion(wait);
                try
                {
                    TrySchedulePendingModelRefresh();
                    await wait.Task.WaitAsync(cancellationToken).ConfigureAwait(true);
                }
                finally
                {
                    setCompletion(null);
                }
            }

            if (Leaving)
            {
                return false;
            }
        }

        if (!isConfigured())
        {
            throw new InvalidOperationException(failureMessage);
        }

        return true;
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
