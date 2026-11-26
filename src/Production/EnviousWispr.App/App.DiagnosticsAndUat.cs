using EnviousWispr.Audio;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;

namespace EnviousWispr.App;

/// <summary>Observability/telemetry/device/import event handlers and UAT harness helpers.</summary>
public partial class App
{
    private bool _appLaunchedTelemetryRecorded;

    private void SignalPerformanceUatReady()
    {
        SignalPerformanceUatEvent("ENVIOUSWISPR_UAT_READY_EVENT");
        if (_transcriptionEngine is not null)
        {
            SignalPerformanceUatEvent("ENVIOUSWISPR_UAT_RUNTIME_READY_EVENT");
        }
    }

    private static void SignalPerformanceUatEvent(string environmentVariable)
    {
        const string allowedPrefix = @"Local\EnviousLabs.EnviousWispr.PerformanceUat.";
        var eventName = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(eventName) ||
            eventName.Length > 200 ||
            !eventName.StartsWith(allowedPrefix, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            using var readyEvent = EventWaitHandle.OpenExisting(eventName);
            readyEvent.Set();
        }
        catch (Exception exception) when (exception is
                                          ArgumentException or
                                          WaitHandleCannotBeOpenedException or
                                          UnauthorizedAccessException or
                                          IOException)
        {
            // UAT instrumentation must never make normal startup fail.
        }
    }

    private void ApplyReliabilityUatExit()
    {
        var requested = Environment.GetEnvironmentVariable(
            "ENVIOUSWISPR_UAT_EXIT_AFTER_MILLISECONDS");
        if (!int.TryParse(requested, out var milliseconds) ||
            milliseconds is < 500 or > 30_000)
        {
            return;
        }

        _ = ExitAfterUatDelayAsync(TimeSpan.FromMilliseconds(milliseconds));
    }

    private async Task ExitAfterUatDelayAsync(TimeSpan delay)
    {
        await Task.Delay(delay).ConfigureAwait(false);
        _window?.DispatcherQueue.TryEnqueue(ExitFromTray);
    }

    private void OnAudioDevicesChanged(AudioDeviceChange change)
    {
        // A MICROPHONE VANISHING MID-DICTATION IS A FACT ABOUT THAT DICTATION, and this arrives on
        // its own Windows device callback, so it inherits nothing. It is also the single most
        // useful line in the log when somebody asks why a recording went wrong, which is exactly
        // the line worth not leaving joined to nothing.
        using var dictation = _sessionController?.CurrentSession is { } recording
            ? DictationScope.Begin(recording.Id.Value)
            : NoScope.Instance;
        _logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            AppEventCode.AudioDevicesChanged,
            change.AffectsCapture
                ? AppFailureCategory.AudioUnavailable
                : AppFailureCategory.None));
    }

    /// <summary>One snippet import attempt, logged as counts and categories (macOS <c>SnippetImportReporter</c>).</summary>
    private void OnSnippetImportReported(DiagnosticSnippetImport report) =>
        _logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            AppEventCode.SnippetsImported,
            report.Outcome != DiagnosticSnippetImportOutcome.Failed ? AppFailureCategory.None
                : report.Failure == SnippetImportFailure.WriteFailed ? AppFailureCategory.StorageUnavailable
                : AppFailureCategory.InvalidData,
            SnippetImport: report));

    private void SweepAppImportCopies()
    {
        if (!EnviousWispr.Services.AppImport.AppImportScratch.SweepLeftovers())
        {
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow, AppEventCode.AppImportCopyLeftBehind, AppFailureCategory.StorageUnavailable));
        }
    }

    /// <summary>One word import from another app, logged as counts and categories, never a word.</summary>
    private void OnWordImportReported(DiagnosticWordImport report) =>
        _logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            AppEventCode.WordsImportedFromApp,
            report.Outcome != DiagnosticWordImportOutcome.Failed ? AppFailureCategory.None
                : report.Failure == WordImportFailure.WriteFailed ? AppFailureCategory.StorageUnavailable
                : AppFailureCategory.InvalidData,
            WordImport: report));

    private void OnDiagnosticsExportCompleted(bool succeeded, int recordCount)
    {
        _logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            succeeded ? AppEventCode.DiagnosticsExported : AppEventCode.DiagnosticsExportFailed,
            succeeded ? AppFailureCategory.None : AppFailureCategory.Observability,
            ElapsedMilliseconds: null));
    }

    private void ApplyOverlayUatState()
    {
        var requested = Environment.GetEnvironmentVariable("ENVIOUSWISPR_UAT_OVERLAY_STATE");
        DictationStatus? status = requested?.Trim().ToLowerInvariant() switch
        {
            "recording" =>
                DictationStatus.Recording("Recording. Release to finish, Escape to cancel"),
            "recording-hands-free" =>
                DictationStatus.Recording("Hands-free recording. Tap twice to stop, Escape to cancel", isHandsFree: true),
            "processing" => DictationStatus.Processing("Transcribing locally..."),
            "cold-start" => DictationStatus.ColdStart("Getting dictation ready...", "Parakeet is warming up after a restart"),
            "engine-ready" => DictationStatus.EngineReady("Ready, press to dictate"),
            "import-status" => DictationStatus.ImportStatus("Importing your words now. Check progress on the Dictionary page."),
            "success" => DictationStatus.Success("Copied to your clipboard"),
            "warning" =>
                DictationStatus.Warning("Protected field: copied only. Paste manually if intended"),
            "advisory" => DictationStatus.Advisory(
                "Ollama is offline. Cleaned text will still be preserved", OpenPolish),
            "suggestion" => DictationStatus.Suggestion(
                "Detected Spanish. Lock it?",
                new PillAction("Lock", PillActionKind.LockDetectedLanguage, "Lock Spanish for dictation")),
            "distress" => DictationStatus.Distress(
                "Microphone disconnected."),
            "escape-recovery" => DictationStatus.EscapeRecovery(
                "Dictation cancelled", EnviousWispr.App.Composition.StatusActions.UndoEscapeRecovery),
            "error" => DictationStatus.Error("Audio capture error. Try again."),
            "rich-error-permission" => DictationStatus.RichError(
                "Microphone access is blocked",
                "Allow EnviousWispr to use your microphone in Windows Settings.",
                new PillAction("Open Settings", PillActionKind.OpenMicrophonePrivacySettings, "Open Windows microphone privacy settings"),
                RichErrorVariant.PermissionDenied),
            "rich-error-mic" => DictationStatus.RichError(
                "Microphone not responding",
                "No audio was received from your selected microphone. Check mute switches or pick another device.",
                new PillAction("How to fix", PillActionKind.OpenMicrophoneSettings, "Open microphone settings"),
                RichErrorVariant.MicNotResponding),
            "bluetooth-card" => DictationStatus.BluetoothCard(),
            "polish-setup-card" => DictationStatus.PolishSetupCard("Choose a model for Claude.", "Select a new cloud model"),
            _ => null,
        };
        if (status.HasValue)
        {
            // Read through the declared local rather than a pattern-bound name. The gate that
            // checks every status names its pill can see what `status` was declared as; it cannot
            // see what a name introduced by a pattern is, and a gate that cannot tell has to
            // refuse. Saying it plainly here is cheaper than widening what the gate accepts.
            _window?.SetSessionStatus(status.Value);
        }
    }

    /// <summary>Drives the meters from a synthetic ramp, with no microphone in the loop.</summary>
    /// <remarks>
    /// EVERY MEASUREMENT OF A FLAT METER SO FAR IS ENTANGLED WITH AN ACOUSTIC PATH NOBODY CONTROLS.
    /// A person speaks, a room absorbs, a device gains, a driver converts, and only then does a
    /// number reach the code - so a flat meter could be any link in that chain. This replaces the
    /// whole chain with a number that is known, which separates "the meters cannot draw" from "the
    /// capture cannot hear" in one run rather than by argument.
    ///
    /// A RAMP RATHER THAN A CONSTANT, because a constant proves only that one value renders. A ramp
    /// climbing from silence to full and back proves the scale, the direction, and on the recording
    /// rail the scroll as well: the shape has to appear as a shape.
    ///
    /// IT GOES THROUGH THE SAME DOOR AS A REAL LEVEL, which is the entire point. Anything that drew
    /// bars directly would prove the bars and nothing between here and them.
    ///
    /// UAT ONLY, and it says so by living behind the same environment variable convention as every
    /// other harness switch in this app. It writes nothing, records nothing, and touches no setting.
    /// </remarks>
    private void StartSyntheticLevelRampIfRequested()
    {
        if (Environment.GetEnvironmentVariable("ENVIOUSWISPR_UAT_LEVEL_RAMP") is not "1")
        {
            return;
        }

        var window = _window;
        if (window is null)
        {
            return;
        }

        var timer = window.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(50);
        var step = 0;
        timer.Tick += (_, _) =>
        {
            // Two seconds up, two seconds down, forever. Eighty ticks a cycle at fifty milliseconds.
            var phase = step++ % 80;
            var climb = phase < 40 ? phase / 40f : (80 - phase) / 40f;

            // FROM THE FLOOR TO FULL SCALE IN THE UNIT THE METER SPEAKS, so the ramp exercises the
            // real curve rather than sidestepping it.
            var level = climb <= 0f ? 0f : MathF.Pow(10f, (RecordingLevelHistory.FloorDecibels * (1f - climb)) / 20f);
            window.SetAudioLevel(new AudioLevel(level, level));
        };
        timer.Start();
    }

    private static bool TryCreatePublicFixtureAudioCapture(
        out PublicFixtureAudioCapture? capture)
    {
        capture = null;
        return HasValidPublicFixtureJourneyUatConfiguration() &&
            PublicFixtureAudioCapture.TryCreate(
                Environment.GetEnvironmentVariable("ENVIOUSWISPR_UAT_AUDIO_FIXTURE"),
                out capture);
    }

    private static bool TryCreateJourneyAudioFailure(out IAudioCapture? capture)
    {
        capture = null;
        if (!HasValidFailureJourneyUatConfiguration() ||
            !string.Equals(
                Environment.GetEnvironmentVariable("ENVIOUSWISPR_UAT_AUDIO_FAILURE"),
                "access-denied",
                StringComparison.Ordinal))
        {
            return false;
        }

        capture = new AccessDeniedAudioCapture();
        return true;
    }

    private void StartPublicFixtureJourneyUat()
    {
        if (!HasValidPublicFixtureJourneyUatConfiguration())
        {
            return;
        }

        _ = RunPublicFixtureJourneyUatAsync();
    }

    private async Task RunPublicFixtureJourneyUatAsync()
    {
        const string startVariable = "ENVIOUSWISPR_UAT_JOURNEY_START_EVENT";
        const string completeVariable = "ENVIOUSWISPR_UAT_JOURNEY_COMPLETE_EVENT";
        try
        {
            if (_audioCapture is not PublicFixtureAudioCapture ||
                _sessionController is null ||
                !TryOpenJourneyUatEvent(startVariable, out var startEvent) ||
                startEvent is null)
            {
                return;
            }

            using (startEvent)
            {
                // A HARNESS THAT DRIVES THE REAL HOTKEY NEVER SIGNALS START. It presses the key itself
                // and asks this method for one thing only: a clean exit it can trigger once the log and
                // the target have said the take landed. Without that, the only exit left is this wait
                // timing out, and a slow transcription would then end the app mid-journey and read as
                // omitted stages - a harness limit reported as a product defect. The exit event is
                // optional and lives in the same allowlisted scheme as START; when it is configured the
                // window is long enough that the harness, not this constant, decides when the run ends.
                TryOpenJourneyUatEvent("ENVIOUSWISPR_UAT_JOURNEY_EXIT_EVENT", out var exitEvent);
                using (exitEvent)
                {
                    // When the app launches without an installed speech model (--clean-user first-run model
                    // delivery), the harness waits up to 10 minutes for onboarding model download and worker
                    // activation before verifying identity and signalling START; keep the app's START wait 2
                    // minutes longer (12 minutes) so the harness deadline always governs.
                    var waitingForFirstRunModelDelivery = _transcriptionEngine is null;
                    var started = await Task.Run(() => exitEvent is null
                            ? startEvent.WaitOne(
                                waitingForFirstRunModelDelivery
                                    ? TimeSpan.FromMinutes(12)
                                    : TimeSpan.FromSeconds(30))
                            : WaitHandle.WaitAny(
                                [startEvent, exitEvent],
                                waitingForFirstRunModelDelivery
                                    ? TimeSpan.FromMinutes(12)
                                    : TimeSpan.FromSeconds(120)) == 0)
                        .ConfigureAwait(false);
                    if (!started || _exitRequested || _disposed)
                    {
                        return;
                    }
                }
            }

            await _runtime.SubmitAsync(PushToTalkSignal.Pressed).ConfigureAwait(false);
            if (_sessionController.CurrentSession?.State !=
                EnviousWispr.Core.Sessions.DictationSessionState.Recording)
            {
                return;
            }

            var fixtureHold = ResolveJourneyUatHoldDuration();
            await Task.Delay(fixtureHold).ConfigureAwait(false);
            var stopSignal = string.Equals(
                    Environment.GetEnvironmentVariable("ENVIOUSWISPR_UAT_JOURNEY_CANCEL"),
                    "1",
                    StringComparison.Ordinal)
                ? PushToTalkSignal.Cancelled
                : PushToTalkSignal.Released;
            await _runtime.SubmitAsync(stopSignal).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.UnhandledFailure,
                AppFailureCategory.Unknown));
        }
        finally
        {
            SignalJourneyUatEvent(completeVariable);
            if (TryOpenJourneyUatEvent("ENVIOUSWISPR_UAT_JOURNEY_EXIT_EVENT", out var leaveEvent) &&
                leaveEvent is not null)
            {
                // A HARNESS THAT CONFIGURED AN EXIT EVENT DECIDES WHEN THE APP LEAVES, after the take as
                // well as instead of one. The Escape Recovery journeys press Undo or Paste in this window
                // once the take has finished, and an app that left 250 ms after completion would take the
                // offer with it. Bounded as the wait for START is; the synthetic-hotkey journey has
                // already set the event by the time this runs, so it passes straight through.
                using (leaveEvent)
                {
                    await Task.Run(() => leaveEvent.WaitOne(TimeSpan.FromSeconds(120))).ConfigureAwait(false);
                }
            }

            if (string.Equals(
                    Environment.GetEnvironmentVariable(
                        "ENVIOUSWISPR_UAT_JOURNEY_EXIT_AFTER_COMPLETION"),
                    "1",
                    StringComparison.Ordinal))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
                _window?.DispatcherQueue.TryEnqueue(ExitFromTray);
            }
        }
    }

    private static bool TryOpenJourneyUatEvent(
        string environmentVariable,
        out EventWaitHandle? journeyEvent)
    {
        journeyEvent = null;
        var eventName = Environment.GetEnvironmentVariable(environmentVariable);
        if (!IsJourneyUatEventName(eventName))
        {
            return false;
        }

        try
        {
            journeyEvent = EventWaitHandle.OpenExisting(eventName!);
            return true;
        }
        catch (Exception exception) when (exception is
                                          ArgumentException or
                                          WaitHandleCannotBeOpenedException or
                                          UnauthorizedAccessException or
                                          IOException)
        {
            return false;
        }
    }

    private static bool HasValidPublicFixtureJourneyUatConfiguration()
    {
        return string.Equals(
                Environment.GetEnvironmentVariable("ENVIOUSWISPR_UAT_JOURNEY"),
                "public-fixture-v1",
                StringComparison.Ordinal) &&
            HasValidJourneyUatCredentialSuffix() &&
            IsJourneyUatEventName(Environment.GetEnvironmentVariable(
                "ENVIOUSWISPR_UAT_JOURNEY_START_EVENT")) &&
            IsJourneyUatEventName(Environment.GetEnvironmentVariable(
                "ENVIOUSWISPR_UAT_JOURNEY_COMPLETE_EVENT"));
    }

    private static bool HasValidFailureJourneyUatConfiguration() =>
        string.Equals(
            Environment.GetEnvironmentVariable("ENVIOUSWISPR_UAT_JOURNEY"),
            "failure-v1",
            StringComparison.Ordinal) &&
        HasValidJourneyUatCredentialSuffix() &&
        IsPerformanceUatEventName(Environment.GetEnvironmentVariable(
            "ENVIOUSWISPR_UAT_READY_EVENT")) &&
        IsPerformanceUatEventName(Environment.GetEnvironmentVariable(
            "ENVIOUSWISPR_UAT_RUNTIME_READY_EVENT"));

    private static bool HasValidJourneyUatCredentialSuffix()
    {
        var credentialSuffix = Environment.GetEnvironmentVariable(
            "ENVIOUSWISPR_UAT_CREDENTIAL_SUFFIX");
        return credentialSuffix is { Length: 40 } &&
            credentialSuffix.StartsWith("journey-", StringComparison.Ordinal) &&
            Guid.TryParseExact(credentialSuffix[8..], "N", out _);
    }

    private static bool IsPerformanceUatEventName(string? eventName)
    {
        const string allowedPrefix = @"Local\EnviousLabs.EnviousWispr.PerformanceUat.";
        return !string.IsNullOrWhiteSpace(eventName) &&
            eventName.Length <= 200 &&
            eventName.StartsWith(allowedPrefix, StringComparison.Ordinal);
    }

    private static TimeSpan ResolveJourneyUatHoldDuration()
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable("ENVIOUSWISPR_UAT_LIVE_PREVIEW"),
                "1",
                StringComparison.Ordinal))
        {
            return TimeSpan.FromSeconds(5);
        }

        var requested = Environment.GetEnvironmentVariable(
            "ENVIOUSWISPR_UAT_JOURNEY_HOLD_MILLISECONDS");
        return int.TryParse(requested, out var milliseconds) &&
            milliseconds is >= 100 and <= 5_000
                ? TimeSpan.FromMilliseconds(milliseconds)
                : TimeSpan.FromMilliseconds(150);
    }

    private static bool IsJourneyUatEventName(string? eventName)
    {
        const string allowedPrefix = @"Local\EnviousLabs.EnviousWispr.JourneyUat.";
        return !string.IsNullOrWhiteSpace(eventName) &&
            eventName.Length <= 200 &&
            eventName.StartsWith(allowedPrefix, StringComparison.Ordinal);
    }

    private static void SignalJourneyUatEvent(string environmentVariable)
    {
        if (!TryOpenJourneyUatEvent(environmentVariable, out var journeyEvent) ||
            journeyEvent is null)
        {
            return;
        }

        using (journeyEvent)
        {
            journeyEvent.Set();
        }
    }

    private async Task FlushFeedbackOutboxSafelyAsync(CancellationToken cancellationToken)
    {
        if (_feedbackCoordinator is not { } coordinator)
        {
            return;
        }

        try
        {
            await coordinator.FlushOutboxAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            // Startup outbox flush is best-effort and must never fail launch.
        }
    }

    private void RecordAppLaunchedOnce(HardwareSnapshot? hardware, bool hasParakeet, bool hasWhisper)
    {
        if (_appLaunchedTelemetryRecorded || _observabilityService is null)
        {
            return;
        }

        _appLaunchedTelemetryRecorded = true;
        var modelsInstalled = (hasParakeet, hasWhisper) switch
        {
            (true, true) => "both",
            (true, false) => "parakeet",
            (false, true) => "whisper",
            _ => "none",
        };
        var aiEnabled = _settings.Preferences.Polish.Provider != PolishProvider.None;
        _observabilityService.RecordAppLaunched(
            hardware: hardware,
            isFreshInstall: _settings.LaunchCount <= 1,
            aiCapable: true,
            aiEnabled: aiEnabled,
            speechModelsInstalled: modelsInstalled);
    }

    private void OnAbnormalWorkerExitObserved(int exitCode)
    {
        var asrBackend = _settings.Preferences.Dictation.FinalEngine.ToString().ToLowerInvariant();
        _ = _observabilityService?.RecordRuntimeWorkerFailedAsync(
            asrBackend: asrBackend,
            exitCode: exitCode,
            errorCode: AppErrorCode.RuntimeWorkerFailed);
    }

    private static DiagnosticHardwareClass DiagnosticHardwareClassFor(HardwareSnapshot hardware)
    {
        if (hardware.Cuda.IsDriverAvailable && hardware.Cuda.DeviceCount > 0)
        {
            return DiagnosticHardwareClass.NvidiaCuda;
        }

        if (hardware.GraphicsAdapters.Any(adapter => adapter.IsActive))
        {
            return DiagnosticHardwareClass.GpuPresent;
        }

        return hardware.Status == HardwareProbeStatus.Complete
            ? DiagnosticHardwareClass.CpuOnly
            : DiagnosticHardwareClass.Unknown;
    }
}

internal sealed class AccessDeniedAudioCapture : IAudioCapture
{
    public event EventHandler<AudioLevel>? LevelChanged
    {
        add { }
        remove { }
    }

    public bool IsCapturing => false;

    public Task<AudioOperationResult> StartAsync(
        AudioCaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new AudioOperationResult(
            Succeeded: false,
            new AppError(
                AppErrorCode.AccessDenied,
                AppErrorStage.AudioCapture,
                CanRetry: true)));
    }

    public Task<CapturedAudio> StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new CapturedAudio(
            new DictationSessionId(Guid.Empty),
            ReadOnlyMemory<float>.Empty,
            SampleRate: 16_000,
            Channels: 1,
            Outcome: AudioCaptureOutcome.Interrupted,
            Error: new AppError(
                AppErrorCode.InvalidTransition,
                AppErrorStage.AudioCapture,
                CanRetry: false)));
    }

    public Task<AudioOperationResult> CancelAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new AudioOperationResult(Succeeded: true));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
