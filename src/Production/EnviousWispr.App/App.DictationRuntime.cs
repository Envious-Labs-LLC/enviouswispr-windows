using EnviousWispr.App.Composition;
using EnviousWispr.Audio;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.History;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;
using EnviousWispr.Presentation;
using EnviousWispr.Services.Input;
using EnviousWispr.Services.Lifecycle;
using EnviousWispr.Services.Runtime;

namespace EnviousWispr.App;

/// <summary>Push-to-talk wiring, audio level routing, session reuse/paste, Quick Add, speed check, and debug archiving.</summary>
public partial class App
{
    private SingleInstanceLock? _globalHotkeyLock;
    private WindowsPushToTalkHook? _pushToTalkHook;
    private bool _keybindCaptureActive;
    private volatile bool _onboardingKeyRehearsalActive;
    private IAudioCapture? _audioCapture;
    private WindowsTextTargetAdapter? _textTargetAdapter;
    private WindowsElsLanguageDetector? _elsLanguageDetector;
    private ContextAwareTextDelivery? _textDelivery;
    private LastDictationReuse? _lastDictation;
    private SavedDictationPaste? _savedDictationPaste;
    private int _lastDictationPreviewVersion;
    private volatile bool _bluetoothAwarenessShownThisLaunch;
    private volatile bool _bluetoothMicDetectedPending;

    /// <summary>The dictation the tray last named; written on the UI thread, where the tray reads it.</summary>
    private Guid? _lastDictationPreviewEntry;

    /// <summary>How many times the speed check runs the pipeline.</summary>
    /// <remarks>
    /// Chosen so the 95th percentile means something. Below twenty samples it IS the maximum, and a
    /// tail figure that is secretly the worst single run reads as evidence while being an artefact
    /// of the sample size.
    /// </remarks>
    private const int SpeedCheckRuns = 50;

    /// <summary>
    /// A repeatable measurement of the text cleanup, with no microphone and no network.
    /// </summary>
    /// <remarks>
    /// WHAT IT MEASURES AND WHAT IT DOES NOT, said in the UI as well as here. It times the
    /// deterministic pipeline - the cleanup every dictation runs after transcription - which is the
    /// part of the wait that is entirely ours and entirely repeatable. It does NOT include
    /// recognition or AI polish: both need resources a speed check should not quietly consume, and
    /// a number silently including a cloud round trip would be measuring somebody's broadband.
    ///
    /// IT REFUSES WHILE A DICTATION IS RUNNING, and it uses the LIVE pipeline rather than a second
    /// one. Those two facts belong together: measuring a different object from the one every
    /// dictation uses is the one thing a speed check must not do, and sharing the live object means
    /// the two must not run at once.
    ///
    /// THE FIRST RUN IS DISCARDED and that is stated rather than silent. It pays for every lazy
    /// initialisation in the pipeline, which no dictation after the first one pays - but a
    /// benchmark that quietly drops its worst sample is precisely how a speed claim becomes untrue,
    /// so the reason sits beside the line that does it.
    /// </remarks>
    private async void OnSpeedCheckRequested()
    {
        var pipeline = _deterministicTextPipeline;
        if (_sessionController?.CurrentSession is not null)
        {
            _window?.SetSpeedCheckResult(null);
            return;
        }

        var summary = await Task.Run(() => MeasureDeterministicPipeline(pipeline)).ConfigureAwait(true);
        _window?.SetSpeedCheckResult(summary);
    }

    private LatencySummary MeasureDeterministicPipeline(DeterministicTextPipeline pipeline)
    {
        // A realistic dictation rather than a word: the cleanup's cost scales with what it is given,
        // so timing "hello" would produce a number no real dictation resembles.
        const string spoken =
            "so i think we should ship the windows build this week comma and see what people say "
            + "about it period i counted 14 things left on the list and 3 of them need review";

        var request = new DeterministicTextRequest(
            new Transcript(DictationSessionId.Create(), spoken, "speed-check", []),
            _customWords,
            _deterministicTextOptions,
            EnabledVocabularyPackIds: _enabledVocabularyPackIds,
            VocabularyPackOverrides: _vocabularyPackOverrides);

        var timings = new List<double>(SpeedCheckRuns);
        for (var run = 0; run <= SpeedCheckRuns; run++)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                _ = pipeline.ProcessAsync(request).GetAwaiter().GetResult();
            }
            catch (Exception exception) when (exception is not (StackOverflowException or OutOfMemoryException))
            {
                return LatencySummary.From([]);
            }

            timer.Stop();

            // Run zero pays for every lazy initialisation in the pipeline, and no dictation after
            // the first one pays it.
            if (run > 0)
            {
                timings.Add(timer.Elapsed.TotalMilliseconds);
            }
        }

        return LatencySummary.From(timings);
    }

    private void OnKeybindCaptureActiveChanged(bool active)
    {
        _keybindCaptureActive = active;
        var preserveActiveRecording = _sessionController?.CurrentSession is
        {
            State: EnviousWispr.Core.Sessions.DictationSessionState.Recording,
        };
        _pushToTalkHook?.ResetHeldKeys(preserveActiveRecording);
        _pushToTalkHook?.SetCapturingKeybind(active);
    }

    private void OnOnboardingKeyRehearsalActiveChanged(bool active) =>
        _onboardingKeyRehearsalActive = active;

    private async Task<(bool Applied, string? Error)> TryApplyOnboardingRecordingKeyAsync(
        string gestureText,
        DictationRecordingMode recordingMode)
    {
        var parsed = HotkeyGestureParser.Parse(gestureText);
        if (!parsed.Succeeded || parsed.Gesture is not { } parsedGesture || !Enum.IsDefined(recordingMode))
        {
            return (false, "Use a supported shortcut like Ctrl+Win, RightCtrl, or F8.");
        }

        var canonicalGesture = parsedGesture.ToString();
        var current = _presentation?.Settings.Current ?? _settings;
        var roleBindings = new (string Role, string Text)[]
        {
            ("Recording", canonicalGesture),
            ("Cancel", current.Preferences.Dictation.CancelGesture),
            ("Add-a-word", current.Preferences.Dictation.QuickAddGesture),
            ("Paste last", current.Preferences.Dictation.PasteLastGesture ?? string.Empty),
            ("Copy last", current.Preferences.Dictation.CopyLastGesture ?? string.Empty),
        };
        var conflicts = HotkeyConflictDetector.Find(roleBindings);
        if (conflicts.Count > 0)
        {
            return (false, HotkeyConflictDetector.Describe(conflicts));
        }

        var blockedSystem = HotkeyConflictDetector
            .FindSystemConflicts(roleBindings)
            .Where(conflict => conflict.Severity == SystemShortcutConflictSeverity.Blocked)
            .ToArray();
        if (blockedSystem.Length > 0)
        {
            return (false, HotkeyConflictDetector.DescribeSystemConflicts(blockedSystem));
        }

        var disableGlobalHotkey = string.Equals(
            Environment.GetEnvironmentVariable("ENVIOUSWISPR_UAT_DISABLE_GLOBAL_HOTKEY"),
            "1",
            StringComparison.Ordinal);
        var customDataDirectory = Environment.GetEnvironmentVariable("ENVIOUSWISPR_DATA_DIRECTORY");
        var isIsolatedDataDirectory = !string.IsNullOrWhiteSpace(customDataDirectory) &&
            !string.Equals(
                SingleInstanceLock.ResolveKey(_releaseIdentity.SingleInstanceKey, customDataDirectory),
                _releaseIdentity.SingleInstanceKey,
                StringComparison.Ordinal);
        var globalHotkeyKey = $"{_releaseIdentity.SingleInstanceKey}.GlobalHotkey";
        var suppressGlobalHook = disableGlobalHotkey ||
            (isIsolatedDataDirectory &&
             (SingleInstanceLock.IsHeld(_releaseIdentity.SingleInstanceKey) ||
              SingleInstanceLock.IsHeld(globalHotkeyKey)));

        async Task<bool> SaveShortcutAsync()
        {
            try
            {
                return await TrySaveOnboardingSettingsAsync(latest => latest with
                {
                    Preferences = latest.Preferences with
                    {
                        Dictation = latest.Preferences.Dictation with
                        {
                            PushToTalkGesture = canonicalGesture,
                            RecordingMode = recordingMode,
                        },
                    },
                }).ConfigureAwait(true);
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        if (suppressGlobalHook)
        {
            return await SaveShortcutAsync().ConfigureAwait(true)
                ? (true, null)
                : (false, "Could not save the new dictation shortcut right now.");
        }

        var attempt = _sessionCoordinator?.TryHold(SessionHolder.SpeechEngineSwitch)
            ?? new SessionHoldAttempt(NoScope.Instance);
        if (attempt.Hold is not { } hold)
        {
            return (false, attempt.Refusal == SessionHoldRefusal.Closed
                ? "Closing EnviousWispr."
                : "Finish the active dictation before changing the shortcut.");
        }

        using (hold)
        {
            var currentDictation = (_presentation?.Settings.Current ?? _settings).Preferences.Dictation;
            if (!WindowsPushToTalkHook.TryCreate(
                    canonicalGesture,
                    recordingMode,
                    currentDictation.CancelGesture,
                    currentDictation.QuickAddGesture,
                    currentDictation.PasteLastGesture,
                    currentDictation.CopyLastGesture,
                    out var nextHook,
                    out var error) ||
                nextHook is null)
            {
                return (false, HotkeyFailureStatus(error));
            }

            if (!await SaveShortcutAsync().ConfigureAwait(true))
            {
                await nextHook.DisposeAsync().ConfigureAwait(true);
                return (false, "Could not save the new dictation shortcut right now.");
            }

            if (_pushToTalkHook is { } existingHook)
            {
                existingHook.Signalled -= OnPushToTalkSignalled;
                _pushToTalkHook = null;
                await existingHook.DisposeAsync().ConfigureAwait(true);
            }

            _pushToTalkHook = nextHook;
            _pushToTalkHook.Signalled += OnPushToTalkSignalled;
            _pushToTalkHook.SetCapturingKeybind(_keybindCaptureActive);
            _window?.SetHotkeyReady(
                _pushToTalkHook.Gesture.ToString(),
                _pushToTalkHook.RecordingMode);
            _trayIcon?.SetRecordingShortcut(_pushToTalkHook.Gesture.ToString());
            return (true, null);
        }
    }

    private void OnRecoveryCleared() => _sessionPersistence.ForgetPendingRecovery();

    private void OnRecoveryUndoRequested() => _ = PasteSavedDictationAsync(SavedDictationPasteAction.Undo, entryId: null);

    private void OnRecoveryDiscardRequested() => _ = _recoveryReplayCoordinator?.DiscardActiveOrNextAsync();

    private void StartStartupRecoveryReplay()
    {
        if (Leaving || _recoveryReplayCoordinator is not { } coordinator || _recoverySpoolStore.ListOrphanSpools().Count == 0)
        {
            return;
        }

        _recoveryReplay = RunStartupRecoveryReplayAsync(coordinator, _recoveryReplayCancellation.Token);
    }

    private async Task RunStartupRecoveryReplayAsync(
        StartupRecoveryReplayCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        try
        {
            await coordinator.ReplayPendingSpoolsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || Leaving)
        {
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.UnhandledFailure,
                AppFailureCategory.Recovery));
        }
    }

    private void OnHistoryPasteRequested(Guid entryId) => _ = PasteSavedDictationAsync(SavedDictationPasteAction.HistoryPaste, entryId);

    private void OnSessionStatusChanged(DictationStatus status)
    {
        try
        {
            _trayIcon?.SetStatus(status.Text);
            // The tray and the pill are driven from the same status, so the two surfaces cannot
            // disagree about whether a recording is live.
            _trayIcon?.SetState(TrayIconStates.For(status.State));
            if (status.State == DictationOverlayState.Hidden &&
                _bluetoothMicDetectedPending &&
                !_bluetoothAwarenessShownThisLaunch &&
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ENVIOUSWISPR_UAT_OVERLAY_STATE")))
            {
                PostToLiveWindow(window =>
                {
                    if (_bluetoothAwarenessShownThisLaunch || !_bluetoothMicDetectedPending)
                    {
                        return;
                    }

                    if (window.TryShowBluetoothAwarenessCard())
                    {
                        _bluetoothMicDetectedPending = false;
                        _bluetoothAwarenessShownThisLaunch = true;
                    }
                });
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnRecordingSoundPlaybackFailed(
        RecordingSoundPairing pairing,
        EnviousWispr.Audio.RecordingSoundMoment moment) =>
        _logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            AppEventCode.RecordingSoundCueFailed,
            AppFailureCategory.AudioUnavailable));

    private void ConfigurePushToTalk(DictationPreferences preferences)
    {
        var disableGlobalHotkey = string.Equals(
            Environment.GetEnvironmentVariable("ENVIOUSWISPR_UAT_DISABLE_GLOBAL_HOTKEY"),
            "1",
            StringComparison.Ordinal);
        var customDataDirectory = Environment.GetEnvironmentVariable("ENVIOUSWISPR_DATA_DIRECTORY");
        var isIsolatedDataDirectory = !string.IsNullOrWhiteSpace(customDataDirectory) &&
            !string.Equals(
                SingleInstanceLock.ResolveKey(_releaseIdentity.SingleInstanceKey, customDataDirectory),
                _releaseIdentity.SingleInstanceKey,
                StringComparison.Ordinal);
        var globalHotkeyKey = $"{_releaseIdentity.SingleInstanceKey}.GlobalHotkey";

        var suppressGlobalHook = false;
        if (disableGlobalHotkey)
        {
            suppressGlobalHook = true;
        }
        else if (isIsolatedDataDirectory)
        {
            // Isolated data-directory instances (such as UI-capture windows) must never claim
            // .GlobalHotkey ahead of the primary app, and must stand down when a primary instance
            // already holds either the base lock or the global hotkey lock.
            suppressGlobalHook =
                SingleInstanceLock.IsHeld(_releaseIdentity.SingleInstanceKey) ||
                SingleInstanceLock.IsHeld(globalHotkeyKey);
        }
        else
        {
            SingleInstanceLock.TryAcquire(globalHotkeyKey, out _globalHotkeyLock);
        }

        if (suppressGlobalHook)
        {
            _pushToTalkHook = null;
            _window?.SetHotkeyUnavailable("Secondary window: global hotkeys remain owned by the running EnviousWispr instance");
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.HotkeyFailed,
                AppFailureCategory.HotkeyConflict));
        }
        else if (!WindowsPushToTalkHook.TryCreate(
                preferences.PushToTalkGesture,
                preferences.RecordingMode,
                preferences.CancelGesture,
                preferences.QuickAddGesture,
                preferences.PasteLastGesture,
                preferences.CopyLastGesture,
                out _pushToTalkHook,
                out var error) ||
            _pushToTalkHook is null)
        {
            _globalHotkeyLock?.Dispose();
            _globalHotkeyLock = null;
            _window?.SetHotkeyUnavailable(HotkeyFailureStatus(error));
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.HotkeyFailed,
                FailureFor(error)));
            return;
        }

        _audioCapture = TryCreateJourneyAudioFailure(out var failureCapture) &&
            failureCapture is not null
            ? failureCapture
            : TryCreatePublicFixtureAudioCapture(out var fixtureCapture) && fixtureCapture is not null
                ? fixtureCapture
                : new WasapiAudioCapture();
        var audioCapture = _audioCapture;
        if (audioCapture is IWarmAudioCapture warmCapture)
        {
            _ = ApplyWarmMicrophonePolicyAsync(warmCapture, preferences.WarmMicrophonePolicy);
        }
        audioCapture.LevelChanged += OnAudioLevelChanged;
        if (audioCapture is WasapiAudioCapture wasapiCapture)
        {
            wasapiCapture.MaxDurationBackstopReached += OnCaptureMaxDurationBackstopReached;
        }

        _elsLanguageDetector?.Dispose();
        _elsLanguageDetector = new WindowsElsLanguageDetector();
        _textTargetAdapter = new WindowsTextTargetAdapter(
            _logger,
            () => PostToLiveWindow(window => window.SetSessionStatus(DictationStatus.RetainedPaste())),
            TimeProvider.System,
            ClipboardBoardOwnership.Shared);
        _textDelivery = new ContextAwareTextDelivery(
            _textTargetAdapter,
            osLanguageDetector: _elsLanguageDetector);
        _lastDictation = new LastDictationReuse(new LastDictationEnvironment(
            async cancellation =>
            {
                await _sessionPersistence.WaitForPendingHistoryAsync(cancellation).ConfigureAwait(false);
                return (await _historyStore.LoadAsync(
                    _settings.Preferences.History.RetentionDays,
                    DateTimeOffset.UtcNow,
                    cancellation).ConfigureAwait(false)).Entries;
            },
            () => DateTimeOffset.UtcNow,
            () => _sessionController?.CurrentSession is not null || _sessionCoordinator?.IsProcessing == true,
            target => target.ProcessId == (uint)Environment.ProcessId,
            // ITS OWN DELIVERY over the shared adapter: the dictation's keeps one recovery slot, and an
            // old dictation pasted through it would replace a take's recovery text.
            new ContextAwareTextDelivery(
                _textTargetAdapter,
                osLanguageDetector: _elsLanguageDetector),
            () => TextDeliveryOptions.Default with
            {
                SmartInsertionEnabled = _settings.Preferences.SmartInsertionEnabled,
            },
            (window, cancellation) => WindowsForegroundTargetProvider.ReacquireAsync(
                window,
                TimeSpan.FromMilliseconds(1000),
                cancellation),
            ProtectedWords: () => DictationLanguageResolver.BuildProtectedWords(
                _customWords,
                _enabledVocabularyPackIds,
                _vocabularyPackOverrides)));
        // HOME'S UNDO AND HISTORY'S PASTE: the same borrowing as the reuse above - its own delivery over the
        // shared adapter, and the same way back to a window remembered without its field. The own-window rule
        // is the dictation's, which has none; the reuse's own-window refusal is not carried over.
        _savedDictationPaste = new SavedDictationPaste(new SavedDictationPasteEnvironment(
            async cancellation =>
            {
                await _sessionPersistence.WaitForPendingHistoryAsync(cancellation).ConfigureAwait(false);
                return (await _historyStore.LoadAsync(
                    _settings.Preferences.History.RetentionDays,
                    DateTimeOffset.UtcNow,
                    cancellation).ConfigureAwait(false)).Entries;
            },
            () => DateTimeOffset.UtcNow,
            () => _sessionController?.CurrentSession is not null || _sessionCoordinator?.IsProcessing == true,
            new ContextAwareTextDelivery(
                _textTargetAdapter,
                osLanguageDetector: _elsLanguageDetector),
            () => TextDeliveryOptions.Default with
            {
                SmartInsertionEnabled = _settings.Preferences.SmartInsertionEnabled,
            },
            (window, cancellation) => WindowsForegroundTargetProvider.ReacquireAsync(
                window,
                TimeSpan.FromMilliseconds(1000),
                cancellation),
            _sessionPersistence,
            ProtectedWords: () => DictationLanguageResolver.BuildProtectedWords(
                _customWords,
                _enabledVocabularyPackIds,
                _vocabularyPackOverrides)));
        RefreshLastDictationPreview();
        _sessionController = new PushToTalkSessionController(
            audioCapture,
            new WindowsForegroundTargetProvider(),
            minimumHoldDuration: audioCapture is PublicFixtureAudioCapture ? TimeSpan.Zero : null,
            deliveryOptions: () => TextDeliveryOptions.Default with
            {
                CopyInsteadOfPaste = _settings.Preferences.CopyInsteadOfPaste,
                TargetMode = _settings.Preferences.PasteTargetMode,
                SmartInsertionEnabled = _settings.Preferences.SmartInsertionEnabled,
            },
            preferredAudioDevice: string.IsNullOrWhiteSpace(_settings.PreferredMicrophoneId)
                ? null
                : new EnviousWispr.Core.Audio.AudioDeviceId(_settings.PreferredMicrophoneId),
            resolveAudioDevice: ResolveCaptureAudioDeviceAsync);
        // THE COORDINATOR OWNS THE SESSION. The watchdog's timeout and Windows locking or suspending
        // are commands on its queue; the update check holds the session through it; shutdown stops it
        // and needs no gate of its own. Built beside the controller so a press captures its target and
        // delivery choice from the same provider the controller would have asked, at the instant of
        // the key, before the queue's first hop.
        // THE JOINS ARE ONE FILE'S, SHARED WITH THE TESTS. The shell chooses the concrete things and
        // supplies leaf reads and sinks; SessionComposition builds the runner, the executor and the
        // coordinator the same way for the app and for a test that drives them without a window.
        _sessionCoordinator = SessionComposition.Compose(new SessionCompositionParts(
            _sessionController,
            audioCapture,
            _runtime,
            _resourceProbe,
            _runStateStore,
            _logger,
            new SessionShell(
                new WindowSessionView(this),
                AttachedSession: () => _sessionController?.CurrentSession,
                Dictation: () => _settings.Preferences.Dictation,
                Engine: () => _transcriptionEngine,
                Delivery: () => _textDelivery,
                Options: () => new FinalizationOptions(
                    _customWords,
                    _deterministicTextOptions,
                    _snippetVocabulary,
                    CurrentPolishSetup(),
                    _enabledVocabularyPackIds,
                    _vocabularyPackOverrides),
                CloudPolishProviderName: () => _cloudPolishConsent?.ProviderName,
                RunId: () => _runId,
                RecordingActive: active =>
                {
                    _pushToTalkHook?.SetRecordingActive(active);
                    OnSelfLearningRecordingChanged(active);
                },
                CancelArmed: armed => _pushToTalkHook?.SetCancelArmed(armed),
                ArchiveAudio: audio => ArchiveDictationAudio(audio),
                DetachCaptureObservers: DetachCaptureObservers,
                ReleaseSession: ReleaseSession,
                DisposeDeliveryRoute: DisposeDeliveryRoute,
                DictationActivityChanged: active =>
                {
                    SetPolishSessionActive(active);
                    PostToLiveWindow(window => window.SetDictationActive(active));
                },
                OnPasteCompleted: OnSelfLearningPasteCompleted,
                AutoStopUnavailable: isHandsFree =>
                    _settings.Preferences.Dictation.AutoStopEnabled &&
                    (isHandsFree || _settings.Preferences.Dictation.RecordingMode == DictationRecordingMode.Toggle) &&
                    _audioCapture is not IAudioSnapshotSource),
            TimeProvider.System));
        if (_pushToTalkHook is not null)
        {
            _pushToTalkHook.Signalled += OnPushToTalkSignalled;
            // A saved keybind builds a NEW hook, which starts armed and knows nothing about a capture
            // field that is still focused. Carrying the state across is what stops the hook re-arming
            // underneath a field the user is still standing in.
            _pushToTalkHook.SetCapturingKeybind(_keybindCaptureActive);
            _window?.SetHotkeyReady(
                _pushToTalkHook.Gesture.ToString(),
                _pushToTalkHook.RecordingMode);
            _logger.Write(new AppLogEntry(DateTimeOffset.UtcNow, AppEventCode.HotkeyReady));

            // A LAST-DICTATION SHORTCUT THAT COULD NOT LISTEN SAYS SO, by its own state, and costs nothing else: the
            // recording key is already listening above. The tray shows the shortcuts that are. Ref: #206.
            foreach (var state in new[] { _pushToTalkHook.PasteLastState, _pushToTalkHook.CopyLastState })
            {
                if (state is not (LastDictationShortcutState.Unset or LastDictationShortcutState.Bound))
                {
                    _logger.Write(new AppLogEntry(
                        DateTimeOffset.UtcNow,
                        AppEventCode.LastDictationShortcutUnavailable,
                        state == LastDictationShortcutState.Unavailable
                            ? AppFailureCategory.HotkeyConflict
                            : AppFailureCategory.HotkeyUnavailable,
                        ErrorCode: state == LastDictationShortcutState.Unavailable
                            ? AppErrorCode.HotkeyConflict
                            : AppErrorCode.HotkeyInvalid));
                }
            }

            var recordingShortcut = _pushToTalkHook.Gesture.ToString();
            var pasteShortcut = _pushToTalkHook.PasteLastGesture?.ToString();
            var copyShortcut = _pushToTalkHook.CopyLastGesture?.ToString();
            PostToLiveWindow(_ => _trayIcon?.SetRecordingShortcut(recordingShortcut));
            PostToLiveWindow(_ => _trayIcon?.SetLastDictationShortcuts(pasteShortcut, copyShortcut));
        }
    }

    private async Task<AudioDeviceId?> ResolveCaptureAudioDeviceAsync(CancellationToken cancellationToken)
    {
        var preferredId = _settings.PreferredMicrophoneId;
        var fallback = string.IsNullOrWhiteSpace(preferredId)
            ? (AudioDeviceId?)null
            : new AudioDeviceId(preferredId);

        if (_presentation is not { } presentation || presentation.Closing)
        {
            return fallback;
        }

        try
        {
            var devices = await presentation.DeviceCatalog()
                .GetCaptureDevicesAsync(cancellationToken)
                .ConfigureAwait(false);
            var resolution = InputDeviceResolver.Resolve(devices, preferredId);
            if (resolution.SelectedDevice?.Transport == AudioDeviceTransport.Bluetooth &&
                !_bluetoothAwarenessShownThisLaunch)
            {
                _bluetoothMicDetectedPending = true;
            }
            return resolution.SelectedDeviceId ?? fallback;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return fallback;
        }
    }

    private void OnAudioLevelChanged(object? sender, AudioLevel level)
    {
        // STRAIGHT THROUGH, ON THE CAPTURE'S OWN THREAD. This arrives once per audio buffer, roughly
        // two hundred times a second, and SetAudioLevel only records a number - so posting each one
        // to the UI thread did that scheduling work for a value the meter's own timer would ask for
        // when it was ready. Every UI touch stays inside that tick.
        _window?.SetAudioLevel(level);
    }

    private void OnCaptureMaxDurationBackstopReached(object? sender, DictationSessionId sessionId)
    {
        _runtime?.Queue.TimeOut(sessionId);
    }

    private void OnPushToTalkSignalled(object? sender, PushToTalkSignalEvent args)
    {
        if (_onboardingKeyRehearsalActive)
        {
            if (args.Signal is PushToTalkSignal.Pressed or PushToTalkSignal.Released or PushToTalkSignal.Cancelled)
            {
                PostToLiveWindow(window => window.ObserveOnboardingKeyRehearsalSignal(args));
            }
            return;
        }

        // THE WINDOW IN FRONT AT THE PRESS is where the words go - read now, on the key's own dispatch, before
        // anything can move it. Copy needs none. Neither is a dictation command. Ref: #206.
        if (args.Signal == PushToTalkSignal.PasteLast)
        {
            // The window only, here - microseconds. Naming its focused field takes UI Automation, which must not
            // hold up the signal loop a recording key is waiting in, so it runs off it and is accepted only if the
            // same window is still the one in front.
            var pressedWindow = args.TargetWindow ?? WindowsForegroundTargetProvider.ForegroundWindow();
            _ = ReuseLastDictationAsync(LastDictationAction.Paste, LastDictationSource.Shortcut, pressedWindow);
            return;
        }

        if (args.Signal == PushToTalkSignal.CopyLast)
        {
            _ = ReuseLastDictationAsync(LastDictationAction.Copy, LastDictationSource.Shortcut);
            return;
        }

        if (args.Signal == PushToTalkSignal.QuickAdd)
        {
            _logger.Write(new AppLogEntry(DateTimeOffset.UtcNow, AppEventCode.QuickAddRequested));
            _ = HandleQuickAddAsync();
            return;
        }

        if (args.Signal == PushToTalkSignal.Pressed)
        {
            _recoveryReplayCoordinator?.RequestYieldBetweenItems();
        }

        _ = _runtime.SubmitAsync(args.Signal, args.IsHandsFree);
    }

    private async void OnManualDictationToggleRequested()
    {
        try
        {
            var isRecording = _sessionController?.CurrentSession?.State ==
                EnviousWispr.Core.Sessions.DictationSessionState.Recording;
            var signal = isRecording ? PushToTalkSignal.Released : PushToTalkSignal.Pressed;
            if (signal == PushToTalkSignal.Pressed)
            {
                _recoveryReplayCoordinator?.RequestYieldBetweenItems();
            }

            if (_runtime is not null)
            {
                await _runtime.SubmitAsync(signal, isHandsFree: !isRecording).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            _logger?.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.UnhandledFailure,
                Failure: AppFailureCategory.Unknown));
        }
    }

    /// <summary>Paste or copy the last dictation, from the tray. Ref: #206.</summary>
    /// <remarks>
    /// THE TARGET IS TAKEN ON THE CLICK, before anything is awaited: the last window the person really
    /// used, which the foreground history kept while the tray took the foreground. Admitted like Quick
    /// Add, because it borrows the delivery adapter the exit disposes.
    /// </remarks>
    private async Task ReuseLastDictationAsync(
        LastDictationAction action,
        LastDictationSource source = LastDictationSource.Menu,
        TargetWindowId? pressed = null)
    {
        // FROM THE TRAY: the last window really in use, and the entry the menu named, so a deletion since it opened
        // refuses instead of reaching further back. FROM A SHORTCUT: the window in front at the press, and the
        // newest dictation - there was no menu to name one.
        var fromMenu = source == LastDictationSource.Menu;
        var target = action != LastDictationAction.Paste
            ? null
            : fromMenu ? _foregroundHistory?.LastTarget : pressed;
        var shownEntry = fromMenu ? _lastDictationPreviewEntry : null;
        if (Leaving || _lastDictation is not { } reuse ||
            _presentation is not { } presentation ||
            !presentation.TryEnter(out var lease))
        {
            return;
        }

        LastDictationReuseResult result;
        SessionHolder? heldBy = null;
        using (lease)
        {
            try
            {
                // THE SESSION IS HELD FOR THE REUSE, as the update check holds it: a key pressed while an old
                // dictation is being pasted is answered Busy rather than starting a take whose delivery would
                // race this one for the clipboard and the target. The checks inside the reuse stay - a
                // recording can exist between session commands, which the hold alone cannot see.
                var attempt = _sessionCoordinator?.TryHold(SessionHolder.LastDictationReuse)
                    ?? new SessionHoldAttempt(NoScope.Instance);
                heldBy = attempt.HeldBy;
                using var hold = attempt.Hold;

                // THE FIELD IS NAMED ONLY ONCE THE SESSION IS HELD: named before, a quick dictation could start and
                // finish while UI Automation answered, and this paste would then reuse THAT dictation. Held first,
                // a later key is Busy until the paste is done. Accepted only for the window in front at the press.
                // Ref: #206 review round 2.
                if (hold is not null &&
                    action == LastDictationAction.Paste &&
                    source == LastDictationSource.Shortcut)
                {
                    var requestedWindow = target;
                    var captured = await Task.Run(
                        () => new WindowsForegroundTargetProvider().CaptureForegroundTarget(),
                        lease.Closing).ConfigureAwait(false);
                    target = captured is { } capturedTarget &&
                        requestedWindow is { } window &&
                        capturedTarget.Value == window.Value &&
                        capturedTarget.ProcessId == window.ProcessId
                            ? captured
                            : null;
                }
                result = hold is null
                    ? new LastDictationReuseResult(
                        action,
                        source,
                        heldBy is null ? LastDictationOutcome.DictationInProgress : LastDictationOutcome.Busy)
                    : action == LastDictationAction.Paste
                        ? await PasteWhenKeysAreUpAsync(reuse, target, source, shownEntry, lease.Closing).ConfigureAwait(false)
                        : await reuse.CopyAsync(source, shownEntry, lease.Closing).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lease.Closing.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
            {
                // A history that cannot be read or pruned, or a delivery that threw: said on the pill and in the
                // log by name, never left as a faulted task nobody observes. Never the words or the message.
                _logger.Write(new AppLogEntry(
                    DateTimeOffset.UtcNow,
                    AppEventCode.LastDictationReuseFailed,
                    AppFailureCategory.Recovery));
                ShowLastDictationStatus(DictationStatus.Error("Your last dictation could not be reused"));
                return;
            }
        }

        // THE SAME NAMES A DICTATION'S DELIVERY LOGS for the same causes: the refusal's error code, and a
        // fault's stage and family. Never the words.
        _logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            LastDictationDiagnostics.EventFor(result.Outcome),
            result.Outcome is LastDictationOutcome.KeptOnClipboard or LastDictationOutcome.Failed
                ? AppFailureCategory.TextDelivery
                : AppFailureCategory.None,
            ErrorCode: result.Delivery is { } delivered ? DeliveryErrorCodes.For(delivered.RefusalReason) : null,
            DeliveryStage: result.Delivery?.Fault?.Stage,
            Fault: result.Delivery?.Fault?.Kind));
        if (DeliveryClipboardDiagnostics.EntryFor(result.Delivery, DateTimeOffset.UtcNow) is { } clipboardEntry)
        {
            _logger.Write(clipboardEntry);
        }

        // A REFUSAL FOR A HOLDER NAMES IT - a file being transcribed is not "busy with a dictation".
        if (heldBy is { } holder)
        {
            ShowSessionBusy(holder);
        }
        // A CLIPBOARD THAT COULD NOT BE GIVEN BACK IS SAID, in the dictation's own words (#242): a landed paste
        // is otherwise silent here, and the person would never learn their clipboard now holds these words.
        else if (result.Delivery is { ClipboardUncertain: true } clipboardNotRestored)
        {
            ShowLastDictationStatus(DeliveryStatusReport.For(clipboardNotRestored));
        }
        else if (LastDictationStatus(result.Outcome) is { } shown)
        {
            ShowLastDictationStatus(shown);
        }
    }

    /// <summary>Home's Undo or History's Paste, asked for from a button in this app's own window.</summary>
    /// <remarks>
    /// THE TARGET IS TAKEN ON THE CLICK, before anything is awaited. The click is in EnviousWispr's window, so
    /// what is in front is this app. History's Paste goes to the last window the person really worked in,
    /// which the foreground history kept - the tray's answer to the same problem. Undo needs none from here:
    /// it goes back to the window and field the take was aimed at, frozen when the key went down. Either way
    /// the delivery brings its target forward itself - Windows lets the app in front do that - and the words
    /// land there with this window left open behind it. macOS hides itself first only because its keystroke
    /// goes to whatever is frontmost; this delivery names its window.
    ///
    /// HELD LIKE THE REUSE: a key pressed while saved words are being pasted is Busy, and a paste asked for
    /// while a dictation or a file transcription has the session is refused and says which. Admitted under a
    /// presentation lease, because it borrows the delivery adapter the exit disposes.
    /// </remarks>
    private async Task PasteSavedDictationAsync(SavedDictationPasteAction action, Guid? entryId)
    {
        var target = action == SavedDictationPasteAction.HistoryPaste ? _foregroundHistory?.LastTarget : null;
        if (Leaving || _savedDictationPaste is not { } paste ||
            _presentation is not { } presentation ||
            !presentation.TryEnter(out var lease))
        {
            return;
        }

        SavedDictationPasteResult result;
        using (lease)
        {
            try
            {
                var attempt = _sessionCoordinator?.TryHold(SessionHolder.SavedDictationPaste)
                    ?? new SessionHoldAttempt(NoScope.Instance);
                using var hold = attempt.Hold;
                if (hold is null)
                {
                    _logger.Write(new AppLogEntry(
                        DateTimeOffset.UtcNow,
                        attempt.Refusal == SessionHoldRefusal.Dictation
                            ? AppEventCode.SavedDictationDeclinedDictationInProgress
                            : AppEventCode.SavedDictationDeclinedBusy));
                    ShowSavedDictationPasteResult(action, outcome: null, attempt, clipboardNotRestored: false);
                    return;
                }

                result = action == SavedDictationPasteAction.Undo
                    ? await paste.UndoAsync(lease.Closing).ConfigureAwait(false)
                    : entryId is { } id
                        ? await paste.PasteAsync(id, target, lease.Closing).ConfigureAwait(false)
                        : new SavedDictationPasteResult(action, SavedDictationPasteOutcome.NoLongerAvailable);
            }
            catch (OperationCanceledException) when (lease.Closing.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
            {
                // A history that cannot be read, or a delivery that threw: said in the window and in the log by
                // name, never left as a faulted task nobody observes. Never the words or the message.
                _logger.Write(new AppLogEntry(
                    DateTimeOffset.UtcNow,
                    AppEventCode.SavedDictationPasteFailed,
                    AppFailureCategory.Recovery));
                ShowSavedDictationPasteResult(action, SavedDictationPasteOutcome.Failed, refusal: null, clipboardNotRestored: false);
                return;
            }
        }

        // THE SAME NAMES A DICTATION'S DELIVERY LOGS for the same causes. Never the words.
        _logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            SavedDictationPasteDiagnostics.EventFor(result),
            result.Outcome is SavedDictationPasteOutcome.KeptOnClipboard or SavedDictationPasteOutcome.Failed
                or SavedDictationPasteOutcome.MayHavePasted
                ? AppFailureCategory.TextDelivery
                : AppFailureCategory.None,
            ErrorCode: result.Delivery is { } delivered ? DeliveryErrorCodes.For(delivered.RefusalReason) : null,
            DeliveryStage: result.Delivery?.Fault?.Stage,
            Fault: result.Delivery?.Fault?.Kind));
        if (DeliveryClipboardDiagnostics.EntryFor(result.Delivery, DateTimeOffset.UtcNow) is { } clipboardEntry)
        {
            _logger.Write(clipboardEntry);
        }

        ShowSavedDictationPasteResult(
            action,
            result.Outcome,
            refusal: null,
            clipboardNotRestored: result.Delivery is { ClipboardUncertain: true });
    }

    /// <summary>The paste's sentence in the window, and whether Home's Undo still stands, read now.</summary>
    private void ShowSavedDictationPasteResult(
        SavedDictationPasteAction action,
        SavedDictationPasteOutcome? outcome,
        SessionHoldAttempt? refusal,
        bool clipboardNotRestored)
    {
        var undoStanding = _sessionPersistence.UndoOffer is not null;
        PostToLiveWindow(window =>
            window.ShowSavedDictationPasteResult(action, outcome, refusal, undoStanding, clipboardNotRestored));
    }

    /// <summary>Says on the pill what has the session, after a press or a reuse it refused. Ref: #211.</summary>
    /// <remarks>
    /// ASKED AGAIN AT THE DRAW. The notice is queued to the window; by the time it runs the job may have ended and a
    /// new recording begun, and its pill must not be replaced by news that is no longer true. So it is drawn only
    /// while the same holder still has the session - which also means no recording can be on screen.
    /// </remarks>
    private void ShowSessionBusy(SessionHolder holder)
    {
        if (SessionBusyStatus(holder) is not { } found)
        {
            return;
        }

        DictationStatus busy = found;
        PostToLiveWindow(window =>
        {
            if (_sessionCoordinator?.HeldBy == holder)
            {
                window.SetSessionStatus(busy);
            }
        });
    }

    /// <summary>The pill for each holder, in macOS's words where macOS has them; null where nothing is worth saying.</summary>
    internal static DictationStatus? SessionBusyStatus(SessionHolder holder) => holder switch
    {
        SessionHolder.FileTranscription => DictationStatus.Busy(
            "Transcribing a file. Please wait.",
            new PillAction(
                "View progress",
                PillActionKind.OpenFileTranscription,
                "Open Transcribe a File to watch its progress or stop it")),
        SessionHolder.LastDictationReuse => DictationStatus.Busy("Pasting your last dictation. Please wait."),
        // SAID, BECAUSE THE HOLD NOW LASTS. The Store's prompt and download run inside it, which can be
        // minutes, and a press refused in silence for that long reads as a broken key.
        SessionHolder.UpdateApply => DictationStatus.Busy("Installing an update. Please wait."),
        // SAID FOR THE SAME REASON: the exit and the deletion run inside the hold, and a press refused in
        // silence then reads as a broken key rather than an app on its way out.
        SessionHolder.DataDeletion => DictationStatus.Busy("Deleting your EnviousWispr data. Please wait."),
        // SAID, BECAUSE THE ENGINE IS DOWN FOR THE SECONDS IT TAKES TO START ON THE CARD, and a press refused in
        // silence for that long reads as a broken key.
        SessionHolder.GraphicsRuntimeSwitch => DictationStatus.Busy("Moving dictation to your graphics card. Please wait."),
        SessionHolder.SpeechEngineSwitch => DictationStatus.Busy("Updating the local speech engine. Please wait."),
        // SAID LIKE THE LAST-DICTATION PASTE: a press refused while saved words are being pasted back.
        SessionHolder.SavedDictationPaste => DictationStatus.Busy("Pasting your dictation. Please wait."),
        _ => null,
    };

    /// <summary>A shortcut's paste waits for the person's fingers to leave the chord.</summary>
    /// <remarks>
    /// THE CHORD'S MODIFIERS ARE STILL DOWN WHEN ITS KEY IS, and a synthesised Ctrl+V under a held Alt or Shift is a
    /// different chord in the target. The delivery already refuses a held modifier, so without this wait a paste from
    /// the shortcut would fall back to the clipboard almost every time. Waited for up to a second, observed, not
    /// guessed; a key still held after that is left to the delivery's own refusal. The tray needs no wait.
    /// </remarks>
    private static async Task<LastDictationReuseResult> PasteWhenKeysAreUpAsync(
        LastDictationReuse reuse,
        TargetWindowId? target,
        LastDictationSource source,
        Guid? shownEntry,
        CancellationToken cancellationToken)
    {
        if (source == LastDictationSource.Shortcut)
        {
            // PasteLast fires on chord key-up, returning immediately once modifiers are already up, while
            // still giving slowly lifted modifiers up to 1 second before falling back to clipboard-only copy.
            var released = await ModifierKeys
                .WaitForReleaseAsync(TimeSpan.FromSeconds(1), cancellationToken)
                .ConfigureAwait(false);
            if (!released)
            {
                return await reuse.CopyAsync(source, shownEntry, cancellationToken).ConfigureAwait(false);
            }
        }

        return await reuse.PasteAsync(target, source, shownEntry, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The reuse's sentence on the pill, queued to the window and checked when it runs.</summary>
    /// <remarks>
    /// NEVER OVER A LIVE DICTATION. The pill is also the recording indicator, and a reuse's sentence set while a
    /// take records would tell the person it had stopped when it has not. So the sentence is shown only when
    /// nothing is recording or queued at the moment it would be drawn; otherwise the log keeps the outcome.
    /// </remarks>
    private void ShowLastDictationStatus(DictationStatus reuseStatus) =>
        PostToLiveWindow(window =>
        {
            if (_sessionController?.CurrentSession is null &&
                _sessionCoordinator is { IsProcessing: false, PendingCount: 0 })
            {
                window.SetSessionStatus(reuseStatus);
            }
        });

    /// <summary>
    /// What the pill says after a reuse, or nothing when the words landing in front of the person already
    /// say it. A refusal that is silent reads exactly like a menu item that did nothing.
    /// </summary>
    private static DictationStatus? LastDictationStatus(LastDictationOutcome outcome) => outcome switch
    {
        LastDictationOutcome.Pasted => null,
        LastDictationOutcome.Copied => DictationStatus.Success("Your last dictation is on the clipboard"),
        LastDictationOutcome.KeptOnClipboard =>
            DictationStatus.Warning("That app refused the paste. Your last dictation is on the clipboard"),
        LastDictationOutcome.NothingToReuse => DictationStatus.Warning("There is no dictation to reuse yet"),
        LastDictationOutcome.DictationInProgress or LastDictationOutcome.Busy =>
            DictationStatus.Warning("EnviousWispr is busy with a dictation. Try again in a moment"),
        LastDictationOutcome.NoTarget or LastDictationOutcome.OwnWindow =>
            DictationStatus.Warning("Click into the app you want it in, then try again"),
        LastDictationOutcome.Failed => DictationStatus.Error("Your last dictation could not be pasted"),
        _ => null,
    };

    /// <summary>Tells the tray which dictation its two items mean, read the same way the action reads it.</summary>
    private void RefreshLastDictationPreview() => _ = RefreshLastDictationPreviewAsync();

    private async Task RefreshLastDictationPreviewAsync()
    {
        if (Leaving || _lastDictation is not { } reuse ||
            _presentation is not { } presentation ||
            !presentation.TryEnter(out var lease))
        {
            return;
        }

        // THE NEWEST READ WINS. A menu opening and a dictation finishing can start two reads at once, and the one
        // that finishes last is not necessarily the one that started last.
        var version = Interlocked.Increment(ref _lastDictationPreviewVersion);
        string? preview;
        Guid? entryId;
        using (lease)
        {
            try
            {
                var entry = await reuse.PeekAsync(lease.Closing).ConfigureAwait(false);
                preview = entry is null ? null : LastDictation.Preview(entry.Text);
                entryId = entry?.Id;
            }
            catch (OperationCanceledException) when (lease.Closing.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
            {
                _logger.Write(new AppLogEntry(
                    DateTimeOffset.UtcNow,
                    AppEventCode.LastDictationReuseFailed,
                    AppFailureCategory.Recovery));
                preview = null;
                entryId = null;
            }
        }

        PostToLiveWindow(_ =>
        {
            if (version == Volatile.Read(ref _lastDictationPreviewVersion))
            {
                _lastDictationPreviewEntry = entryId;
                _trayIcon?.SetLastDictationPreview(preview);
            }
        });
    }

    private async Task HandleQuickAddAsync()
    {
        // ADMITTED LIKE A PRESENTER'S OPERATION. This borrows the delivery adapter - the thing the
        // session's teardown disposes - and ends on the window; the lease is what the exit's first
        // step, the presentation drain, joins before the session is asked to shut down, so the
        // adapter is never disposed under a read still inside it, and an operation admitted after
        // the drain began is refused here rather than half-run.
        if (_exitRequested || _disposed || _textTargetAdapter is null ||
            _sessionController?.CurrentSession is not null ||
            _presentation is not { } presentation ||
            !presentation.TryEnter(out var lease))
        {
            return;
        }

        using (lease)
        {
            await QuickAddUnderLeaseAsync(lease).ConfigureAwait(false);
        }
    }

    private async Task QuickAddUnderLeaseAsync(PresentationAdmission.Lease lease)
    {
        var target = new WindowsForegroundTargetProvider().CaptureForegroundTarget();
        if (target is null || !target.Value.IsValid)
        {
            OpenQuickAddUnlessLeaving(null, "Select a word in another app, then press the Add-a-word shortcut again.");
            return;
        }

        var adapter = _textTargetAdapter;
        if (adapter is null)
        {
            return;
        }

        var context = await adapter.CaptureContextAsync(
            target.Value,
            TextDeliveryOptions.Default,
            lease.Closing).ConfigureAwait(false);
        // NOTHING LATE. The drain began while the read was out: no clipboard borrow, no window.
        if (lease.Closing.IsCancellationRequested)
        {
            return;
        }

        var published = context.Status == TargetContextStatus.Available
            ? context.Context?.Selection.Trim()
            : null;

        // EVERY OUTCOME BELOW SAYS SOMETHING, INCLUDING THE REFUSALS. A refusal that is silent is
        // indistinguishable from nothing having happened at all - to the user, who is left looking
        // at an empty box, and to anyone testing it, who would be reporting the absence of an
        // effect as evidence of a decision.
        var acquisition = SelectionAcquisitionPolicy.Decide(
            hasValidTarget: true,
            published,
            isDictationRunning: _sessionController?.CurrentSession is not null,
            isDeliveryInFlight: _sessionCoordinator?.IsProcessing == true);

        string? selection;
        string? message;
        switch (acquisition)
        {
            case SelectionAcquisition.UsePublished:
                selection = published;
                message = null;
                break;

            case SelectionAcquisition.SyntheticCopy:
                // Static because it holds no state - it borrows the clipboard and gives it back.
                selection = await WindowsTextTargetAdapter
                    .TryReadSelectionWithCopyAsync(lease.Closing)
                    .ConfigureAwait(false);
                if (lease.Closing.IsCancellationRequested)
                {
                    return;
                }

                selection = selection?.Trim();
                message = string.IsNullOrWhiteSpace(selection)
                    ? "Nothing was selected in that app. Select a misheard word, then try the shortcut again."
                    : null;
                break;

            default:
                selection = null;
                message = "EnviousWispr was busy with a dictation, so it left your clipboard alone. Try the shortcut again in a moment.";
                break;
        }

        // THREE OUTCOMES, THREE EVENTS. The first version had two, so "the copy found nothing" and
        // "the user got their word" logged identically - and the log is where a support case looks
        // when nobody can reproduce the screen. The messages had been split from the start; the log
        // had not, which is the half that goes unnoticed until it is needed.
        _logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            acquisition == SelectionAcquisition.Refuse
                ? AppEventCode.QuickAddRefused
                : string.IsNullOrWhiteSpace(selection)
                    ? AppEventCode.QuickAddSelectionEmpty
                    : AppEventCode.QuickAddPrepared));
        OpenQuickAddUnlessLeaving(selection, message);
    }

    /// <summary>
    /// The Quick Add dialog, queued to the window. CHECKED WHEN IT RUNS, NOT WHEN IT IS QUEUED: the
    /// lease that admitted the capture ends when this method returns, and the queued callback runs
    /// later; a drain that began in between is read off the presentation's own durable closure, so a
    /// dialog does not open and the window is not brought forward under an exit.
    /// </summary>
    private void OpenQuickAddUnlessLeaving(string? selection, string? message) =>
        _window?.DispatcherQueue.TryEnqueue(() =>
        {
            if (Leaving)
            {
                return;
            }

            ShowMainWindow(openSettings: false);
            _window?.OpenQuickAdd(selection, message);
        });

    /// <summary>
    /// Keeps the audio of a dictation so a bad transcript can be replayed. DEBUG builds only.
    /// </summary>
    /// <remarks>
    /// WITHOUT THIS, "the app heard that wrong" IS UNREPRODUCIBLE. The audio is gone the moment
    /// the dictation finishes, so the only evidence is the wrong text and somebody's memory of
    /// what they said - which is the least reliable input available and the one every report is
    /// currently built on.
    ///
    /// DEBUG ONLY, compiled out entirely rather than gated at runtime. Audio is the most sensitive
    /// thing this app touches, and a runtime flag is a thing that can be turned on; a conditional
    /// compile is not present in the binary a user runs at all. It never leaves the machine either
    /// way - the network boundary is untouched - but "cannot be enabled" is a stronger claim than
    /// "is not enabled" and it costs nothing here.
    ///
    /// FAILURES ARE SWALLOWED, and that is right for this one specifically. A debugging aid that
    /// can break a dictation is worse than no debugging aid: the archive exists to help diagnose
    /// the pipeline, so it must never be the reason the pipeline failed.
    /// </remarks>
    /// <summary>
    /// Ensures the local-only release dictation recording folder is ready when a capture finishes.
    /// Individual WAV recordings are saved under <c>recordings\dictations\&lt;entryId&gt;.wav</c> when
    /// History persists the entry.
    /// </summary>
    private async Task ApplyWarmMicrophonePolicyAsync(MicrophoneWarmPolicy policy)
    {
        if (_audioCapture is IWarmAudioCapture warm)
        {
            await ApplyWarmMicrophonePolicyAsync(warm, policy).ConfigureAwait(false);
        }
    }

    private static async Task ApplyWarmMicrophonePolicyAsync(IWarmAudioCapture warm, MicrophoneWarmPolicy policy)
    {
        try
        {
            await warm.SetWarmPolicyAsync(policy).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
        }
    }

    private async Task TearDownWarmAudioCaptureAsync()
    {
        if (_audioCapture is IWarmAudioCapture warm)
        {
            try
            {
                await warm.TearDownWarmAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
            {
            }
        }
    }

    private void ArchiveDictationAudio(CapturedAudio audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        if (audio.SampleRate <= 0 || audio.Samples.IsEmpty)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_dictationAudioArchive.RecordingsDirectory);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
    }

    private static string HotkeyFailureStatus(AppError? error) => error?.Code switch
    {
        AppErrorCode.HotkeyConflict => "Configured shortcut is already in use",
        AppErrorCode.HotkeyInvalid => "Configured shortcut is invalid",
        _ => "Global shortcut is unavailable",
    };
}
