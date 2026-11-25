using System.Reflection;
using EnviousWispr.App.Composition;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Distribution;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;
using EnviousWispr.Presentation;
using EnviousWispr.Services.Distribution;
using EnviousWispr.Services.Input;
using EnviousWispr.Services.Lifecycle;
using EnviousWispr.Services.Runtime;
using EnviousWispr.Services.Settings;
using Microsoft.UI.Windowing;

namespace EnviousWispr.App;

/// <summary>Tray icon, window visibility, system lifecycle, heartbeat, duplicate activation, updates, and exit lifetime.</summary>
public partial class App
{
    private readonly WindowsStartupRegistration _startupRegistration = new();
    // The session hold an accepted Store install keeps until the app leaves; see OnUpdateApplyRequested.
    private IDisposable? _updateInstallHold;
    private Task? _systemEndingNote;
    private SingleInstanceLock? _singleInstanceLock;
    private SingleInstanceActivationChannel? _activationChannel;
    private WindowsSystemLifecycleMonitor? _lifecycleMonitor;
    private WindowsTrayIcon? _trayIcon;
    private WindowsForegroundHistory? _foregroundHistory;
    private ApplicationLifetime? _lifetime;
    private bool _backgroundNoticeShown;
    private CancellationTokenSource? _heartbeatCancellation;
    private Task? _heartbeatLoop;
    private int _activationPending;

    private void OnDuplicateActivationRequested(object? sender, EventArgs args)
    {
        Interlocked.Exchange(ref _activationPending, 1);
        _logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            AppEventCode.DuplicateInstanceActivated));
        var window = _window;
        if (window is not null)
        {
            window.DispatcherQueue.TryEnqueue(() =>
            {
                Interlocked.Exchange(ref _activationPending, 0);
                ShowMainWindow(openSettings: false);
            });
        }
    }

    private void StartHeartbeat(Guid runId)
    {
        _heartbeatCancellation = new CancellationTokenSource();
        _heartbeatLoop = RunHeartbeatAsync(runId, _heartbeatCancellation.Token);
    }

    private async Task RunHeartbeatAsync(Guid runId, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await _runStateStore.HeartbeatAsync(
                        runId,
                        DateTimeOffset.UtcNow,
                        cancellationToken).ConfigureAwait(false))
                {
                    _logger.Write(new AppLogEntry(
                        DateTimeOffset.UtcNow,
                        AppEventCode.ApplicationHeartbeatFailed,
                        AppFailureCategory.StorageUnavailable));
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void ConfigureSystemLifecycleMonitor()
    {
        try
        {
            _lifecycleMonitor = new WindowsSystemLifecycleMonitor();
            _lifecycleMonitor.Transitioned += OnSystemLifecycleTransitioned;
        }
        catch (InvalidOperationException)
        {
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.UnhandledFailure,
                AppFailureCategory.SystemLifecycle));
        }
    }

    private void OnSystemLifecycleTransitioned(
        object? sender,
        SystemLifecycleTransition transition)
    {
        var eventCode = transition switch
        {
            SystemLifecycleTransition.Suspending => AppEventCode.SystemSuspending,
            SystemLifecycleTransition.Resumed => AppEventCode.SystemResumed,
            SystemLifecycleTransition.SessionLocked => AppEventCode.SessionLocked,
            SystemLifecycleTransition.SessionUnlocked => AppEventCode.SessionUnlocked,
            SystemLifecycleTransition.SessionEnding => AppEventCode.SystemSessionEnding,
            _ => AppEventCode.UnhandledFailure,
        };
        if (transition == SystemLifecycleTransition.SessionEnding && _runId is { } endingRunId)
        {
            // RECORDED HERE, NOT AT TEARDOWN, BECAUSE TEARDOWN MAY NEVER COME. Windows gives the
            // process a short and unguaranteed window after this notification and can kill it at any
            // point, so the one chance to write down that the ending was EXPECTED is now. Without it
            // a deliberate restart is stored as an interruption, which is the same trace a crash
            // leaves. Fire and forget for the same reason: waiting on a disk write inside a shutdown
            // notification is how an app becomes the thing that delays somebody's shutdown. Ref: #93.
            // KEPT, NOT DROPPED: an exit that does run joins this write under Quiesce before the
            // run-state store is closed, so the store is never disposed under it.
            _systemEndingNote = _runStateStore.NoteSystemEndingAsync(endingRunId, DateTimeOffset.UtcNow);
        }

        // WINDOWS LOCKING MID-DICTATION IS A FACT ABOUT THAT DICTATION. Written before the recovery
        // flow opens its own scope, so it opens one here too; otherwise the event that EXPLAINS the
        // recovery is the one line of it joined to nothing.
        using (_sessionController?.CurrentSession is { } interrupted
            ? DictationScope.Begin(interrupted.Id.Value)
            : NoScope.Instance)
        {
            _logger.Write(new AppLogEntry(DateTimeOffset.UtcNow, eventCode));
        }

        if (transition is SystemLifecycleTransition.Suspending or
            SystemLifecycleTransition.SessionLocked)
        {
            // THE INTERRUPTION OWNS ITS CANCEL. The coordinator cancels the finalisation in flight
            // for an interruption it admits, and for none it refuses; once leaving has begun the exit
            // has closed admission and cut the finalisation by its own policy (the lifetime's), and a
            // lock that lands then changes nothing. The exit no longer tears anything down beside a
            // finalisation, so a lock has nothing to make safe on the exit's behalf.
            if (!_exitRequested && !_disposed && _sessionCoordinator is { } coordinator)
            {
                _ = coordinator.InterruptAsync(transition);
            }
        }
        else
        {
            PostToLiveWindow(window =>
                window.SetSessionStatus(DictationStatus.Quiet("Windows resumed. EnviousWispr is ready")));
        }
    }

    private async void OnUpdateCheckRequested()
    {
        if (_exitRequested || _disposed)
        {
            return;
        }

        // NO SESSION HOLD FOR A CHECK. Asking the Store downloads and applies nothing, so a dictation
        // may start or finish while it runs; only the install below needs the session idle.
        _window?.SetUpdateCheckInProgress();
        var result = await _updateService.CheckAsync().ConfigureAwait(true);
        // THE APP MAY HAVE LEFT WHILE THE STORE ANSWERED; the window is not told anything then.
        if (!_exitRequested && !_disposed)
        {
            _window?.SetUpdateStatus(result);
        }
    }

    private async void OnStartupToggleRequested(bool enabled)
    {
        if (_exitRequested || _disposed)
        {
            return;
        }

        _window?.SetStartupRegistrationInProgress();
        var result = await _startupRegistration.SetEnabledAsync(enabled).ConfigureAwait(true);
        if (!_exitRequested && !_disposed)
        {
            _window?.SetStartupRegistration(result);
        }
    }

    /// <summary>What an update refused for the session says: a running file is named, not blamed on a dictation.</summary>
    private static UpdateOperationStatus UpdateBusyStatus(SessionHoldAttempt attempt) =>
        attempt.HeldBy == SessionHolder.FileTranscription
            ? UpdateOperationStatus.BusyTranscribingFile
            : UpdateOperationStatus.BusyDictating;

    private async void OnUpdateApplyRequested()
    {
        if (_exitRequested || _disposed)
        {
            return;
        }

        // THE INSTALL IS ASKED FOR ONLY WHEN IDLE, and the session is held from before the request until
        // the app has left: a press admitted in between would open a microphone under an update. A dictation or a file already running refuses the hold, and nothing is requested.
        var attempt = _sessionCoordinator?.TryHold(SessionHolder.UpdateApply) ?? new SessionHoldAttempt(NoScope.Instance);
        if (attempt.Hold is not { } hold)
        {
            _window?.SetUpdateStatus(new UpdateOperationResult(UpdateBusyStatus(attempt)));
            return;
        }

        // PUBLISHED BEFORE THE STORE IS ASKED. An exit that starts during the Store's prompt or download
        // closes admission and takes this hold on its way out; a hold still in a local here would be one
        // the session's shutdown waits its whole budget for, and the exit would read as unclean.
        Volatile.Write(ref _updateInstallHold, hold);
        var status = UpdateOperationStatus.Failed;
        try
        {
            _window?.SetUpdateInstallInProgress();
            status = await _updateService.RequestInstallAsync().ConfigureAwait(true);
        }
        finally
        {
            // GIVEN BACK UNLESS THE STORE IS INSTALLING: a declined or failed request lets dictation
            // resume at once. Taken atomically, because an exit may already have taken it.
            if (status != UpdateOperationStatus.Installing || _exitRequested || _disposed)
            {
                Interlocked.Exchange(ref _updateInstallHold, null)?.Dispose();
            }
        }

        if (_exitRequested || _disposed)
        {
            return;
        }

        _window?.SetUpdateStatus(new UpdateOperationResult(status));
        if (status != UpdateOperationStatus.Installing)
        {
            return;
        }

        // THE APP LEAVES ITSELF RATHER THAN WAITING TO BE CLOSED. Microsoft says a completed install may
        // need the app to restart and does not promise to close a full-trust process, so waiting would
        // leave dictation held until the person quit. The orderly exit runs with the hold still parked;
        // admission closes first and only then gives it back, so no key is admitted in between.
        _exitRequested = true;
        await PrepareForExitAsync().ConfigureAwait(true);
        Exit();
    }

    private string CurrentAppVersion() =>
        _updateService.InstalledVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
        ?? "0.19.0";

    private void ConfigureTrayIcon()
    {
        _trayIcon = new WindowsTrayIcon(Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Brand",
            "EnviousWispr.ico"));
        _trayIcon.SetVersion(CurrentAppVersion());
        _trayIcon.SetEngineAndPolishSummary(
            _settings.Preferences.Dictation.FinalEngine,
            _settings.Preferences.Polish.Provider);
        _trayIcon.SetTheme(_settings.Preferences.Theme);
        _trayIcon.ShowWindowRequested += () => ShowMainWindow(openSettings: false);
        _trayIcon.OpenSettingsRequested += () => ShowMainWindow(openSettings: true);
        _trayIcon.ExitRequested += ExitFromTray;
        // THE WINDOW BEFORE THE TRAY CLICK. Clicking the notification area makes the taskbar the
        // foreground, so "Paste last dictation" pastes into the last window that was really in use.
        _foregroundHistory = new WindowsForegroundHistory();
        _trayIcon.ToggleRecordingRequested += () => _ = ToggleRecordingFromTrayAsync();
        _trayIcon.PasteLastRequested += () => _ = ReuseLastDictationAsync(LastDictationAction.Paste);
        _trayIcon.CopyLastRequested += () => _ = ReuseLastDictationAsync(LastDictationAction.Copy);
        _trayIcon.TranscribeFileRequested += () => ShowWindowOnPage("settings-transcribe-file");
        _trayIcon.AddToDictionaryRequested += () => ShowWindowOnPage("dictionary", "your-words");
        _trayIcon.HelpCenterRequested += () => ShowWindowOnPage("help");
        _trayIcon.CheckForUpdatesRequested += () => ShowWindowOnPage("settings-app", "updates-licenses");
        _trayIcon.LastDictationPreviewRequested += RefreshLastDictationPreview;
        _trayIcon.MicrophoneMenuOpeningRequested += RefreshTrayMicrophones;
        _trayIcon.MicrophoneSelected += deviceId => _ = SelectMicrophoneFromTrayAsync(deviceId);
        _trayIcon.AppearanceSelected += theme => _ = SelectAppearanceFromTrayAsync(theme);
        _trayIcon.SetStatus("ready");
    }

    private void ShowWindowOnPage(string pageTag, string? tabKey = null)
    {
        _window?.DispatcherQueue.TryEnqueue(() =>
        {
            if (_window is null || Leaving)
            {
                return;
            }

            _window.AppWindow.Show();
            _window.Activate();
            _window.OpenPage(pageTag, tabKey);
            _window.AnnouncePendingHistoryState();
        });
    }

    private void RefreshTrayMicrophones()
    {
        if (Leaving || _disposed || _trayIcon is not { } tray ||
            _presentation is not { } presentation || presentation.Closing)
        {
            return;
        }

        try
        {
            var devices = presentation.DeviceCatalog()
                .GetCaptureDevicesAsync()
                .GetAwaiter()
                .GetResult();
            var items = devices
                .Select(device => new TrayMicrophoneItem(
                    device.Id.Value,
                    device.IsDefault ? $"{device.DisplayName} (Windows default)" : device.DisplayName))
                .ToArray();
            tray.SetMicrophones(items, _settings.PreferredMicrophoneId);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            tray.SetMicrophones([], _settings.PreferredMicrophoneId);
        }
    }

    private async Task ToggleRecordingFromTrayAsync()
    {
        if (Leaving)
        {
            return;
        }

        try
        {
            var isRecording = _sessionController?.CurrentSession?.State ==
                EnviousWispr.Core.Sessions.DictationSessionState.Recording;
            if (!isRecording && _foregroundHistory?.LastTarget is { IsValid: true } target)
            {
                // Clicking the tray moved foreground to the taskbar/menu; reacquire the user's
                // active window first so dictation targets the original editor rather than the shell.
                var reacquired = await Task.Run(() => WindowsForegroundTargetProvider.ReacquireAsync(
                    target,
                    TimeSpan.FromMilliseconds(500),
                    CancellationToken.None)).ConfigureAwait(false);
                if (reacquired is null)
                {
                    ShowLastDictationStatus(DictationStatus.Warning(
                        "Click into the app you want to dictate into, then try again"));
                    return;
                }
            }

            if (Leaving)
            {
                return;
            }

            var signal = isRecording ? PushToTalkSignal.Released : PushToTalkSignal.Pressed;
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

    private async Task SelectMicrophoneFromTrayAsync(string? deviceId)
    {
        if (Leaving || _presentation is not { } presentation ||
            !presentation.TryEnter(out var lease))
        {
            return;
        }

        var normalized = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;
        SettingsSaveResult result;
        using (lease)
        {
            result = await presentation.Settings
                .SaveAsync(current => current with { PreferredMicrophoneId = normalized })
                .ConfigureAwait(false);
        }

        if (!result.Saved)
        {
            if (result.Refusal != SettingsSaveRefusal.Closing)
            {
                PostToLiveWindow(window =>
                {
                    RefreshTrayMicrophones();
                    window.SetReliabilityNotice(
                        "That microphone choice was not kept",
                        "Your previous microphone remains saved until settings can be written again.",
                        isError: false);
                });
            }

            return;
        }

        var updated = presentation.Settings.Current;
        PostToLiveWindow(_ =>
        {
            _settings = updated;
            RefreshTrayMicrophones();
        });
    }

    private async Task SelectAppearanceFromTrayAsync(AppTheme theme)
    {
        if (Leaving || _presentation is not { } presentation ||
            !presentation.TryEnter(out var lease))
        {
            return;
        }

        SettingsSaveResult result;
        using (lease)
        {
            result = await presentation.Settings
                .SaveAsync(current => current with
                {
                    Preferences = current.Preferences with { Theme = theme },
                })
                .ConfigureAwait(false);
        }

        if (!result.Saved)
        {
            if (result.Refusal != SettingsSaveRefusal.Closing)
            {
                PostToLiveWindow(window =>
                {
                    try
                    {
                        _trayIcon?.SetTheme(_settings.Preferences.Theme);
                    }
                    catch (ObjectDisposedException)
                    {
                    }

                    window.SetReliabilityNotice(
                        "That choice was not kept",
                        "Your previous appearance remains active until settings can be written again.",
                        isError: false);
                });
            }

            return;
        }

        var updated = presentation.Settings.Current;
        PostToLiveWindow(window =>
        {
            _settings = updated;
            window.ApplyThemeFromExternal(theme);

            try
            {
                _trayIcon?.SetTheme(theme);
            }
            catch (ObjectDisposedException)
            {
            }
        });
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exitRequested)
        {
            return;
        }

        args.Cancel = true;
        sender.Hide();
        if (!_backgroundNoticeShown)
        {
            _backgroundNoticeShown = true;
            _trayIcon?.ShowBackgroundNotice();
        }
    }

    private void ShowMainWindow(bool openSettings)
    {
        _window?.DispatcherQueue.TryEnqueue(() =>
        {
            // NOT UNDER AN EXIT. The show is queued from several places - a tray click, a recovered
            // dictation, the Quick Add - and runs later; a window brought forward while the shell is
            // leaving is the one thing none of them means.
            if (_window is null || Leaving)
            {
                return;
            }

            _window.AppWindow.Show();
            _window.Activate();
            if (openSettings)
            {
                _window.OpenSettings();
            }

            // A RESULT THAT ARRIVED WHILE THE WINDOW WAS HIDDEN IS STILL NEWS WHEN IT COMES BACK.
            // History finishing while the app sits in the notification area is the ordinary case,
            // and without this the announcement is simply never made.
            //
            // AFTER THE NAVIGATION, NOT BEFORE. Opening Settings from the tray runs through here too,
            // and announcing first spoke the history result to somebody who had asked for Settings.
            // Once the page has been switched away from, the ancestor check refuses it and the
            // pending result is kept for whenever History is actually opened.
            _window.AnnouncePendingHistoryState();
        });
    }

    private void ExitFromTray()
    {
        _window?.DispatcherQueue.TryEnqueue(() => _ = ExitFromTrayAsync());
    }

    private async Task ExitFromTrayAsync()
    {
        if (_exitRequested)
        {
            return;
        }

        _exitRequested = true;
        await PrepareForExitAsync().ConfigureAwait(true);
        _window?.Close();
    }

    /// <summary>
    /// Leaving, shared by every path out - the tray, the window, an update, the shell's own disposal
    /// (a Windows session ending is a notification, not a path out: the run-state note is written
    /// and the process may be killed before anything here runs): admission
    /// closes before the first await and the exit budget starts; the settings write finishes; the shell
    /// closes its windows; then the exit under the one budget - the session shut down by its owner,
    /// what it used disposed only once nothing uses it, the run completed only when everything
    /// finished, and the host ended by force when something would not. A second caller shares the
    /// first's task; the window closing behind the first is one such caller.
    /// </summary>
    private Task<ExitReport> PrepareForExitAsync()
    {
        // ONE SCOPE FOR THE WHOLE TEARDOWN, rather than one per line found. Quitting mid-recording
        // is a fact about that recording, and everything written on the way out - the shell closing,
        // the preview stopping, whatever a teardown step logs - belongs to it. The lifetime's flow
        // captures the scope at the call and keeps it after this method lets it go.
        using var dictation = _sessionController?.CurrentSession is { } recording
            ? DictationScope.Begin(recording.Id.Value)
            : NoScope.Instance;
        _disposed = true;
        return Lifetime().ExitAsync();
    }

    /// <summary>The exit, reached as a disposal: the same shared task the exit paths await.</summary>
    public async ValueTask DisposeAsync()
    {
        await PrepareForExitAsync().ConfigureAwait(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>The lifetime, built once; its parts read the shell's fields at the step, not at the build.</summary>
    private ApplicationLifetime Lifetime() =>
        _lifetime ??= new ApplicationLifetime(LifetimeParts(), _logger, TimeProvider.System, new HostTerminator(EraseDataDirectory));

    /// <summary>
    /// The shell's steps of leaving, in the order the lifetime runs them. What each touches is the
    /// shell's; the order, the budget and the retention are the lifetime's.
    /// </summary>
    /// <remarks>
    /// WHAT THE SESSION USES IS LISTED UNDER DisposeSessionDependencies, AND NOWHERE ELSE BUT THE
    /// RUN-STATE STORE'S CLOSING. A command or a background loop the session's shutdown reported still
    /// running is inside the engines, the polish provider, the stores; the lifetime runs that list
    /// only behind a quiescent session with nothing outstanding - a gate reads this method for that.
    /// The run-state store is closed by the lifetime after the run's completion, which writes to it,
    /// and after the heartbeat, joined under Quiesce, which does too.
    /// </remarks>
    private LifetimeParts LifetimeParts() => new(
        CloseAdmission: () =>
        {
            _sessionCoordinator?.Close();
            // AFTER THE CLOSE, NOT BEFORE. An installing update keeps the session until the app leaves;
            // once admission is closed nothing can take it, and a hold still out would make the session's
            // shutdown wait its whole budget and report the exit unclean.
            Interlocked.Exchange(ref _updateInstallHold, null)?.Dispose();
            // The deletion's hold likewise: it was kept so no press could start a recording on the way out.
            Interlocked.Exchange(ref _dataDeletionHold, null)?.Dispose();
        },
        DrainPresentation: () => _presentation?.DrainAsync() ?? Task.CompletedTask,
        ShellClosing: () =>
        {
            _window?.ShutdownProductWindows();
            _logger.Write(new AppLogEntry(DateTimeOffset.UtcNow, AppEventCode.ShellClosed));
        },
        AbortPolishRuntime: AbortPolishRuntime,
        // THE EXIT POLICY: a transcription in flight is cut short rather than waited for, and so is the
        // polish warm-up; the session's shutdown then waits for the cut to land. Cancellation is not
        // quiescence - the shutdown's report says whether it landed.
        CancelProcessing: () =>
        {
            _sessionCoordinator?.CancelProcessing();
            _polishLifetime.Cancel();
            _startupCancellation.Cancel();
            _modelDownload?.Cancel();
            _graphicsRuntimeDownload?.Cancel();
        },
        ReleaseInputs: (LifetimeStep[])
        [
            LifetimeStep.Of("lifecycle monitor", () =>
            {
                if (_lifecycleMonitor is { } monitor)
                {
                    monitor.Transitioned -= OnSystemLifecycleTransitioned;
                    _lifecycleMonitor = null;
                    monitor.Dispose();
                }
            }),
            new LifetimeStep("push-to-talk hook", async () =>
            {
                if (_pushToTalkHook is { } hook)
                {
                    hook.Signalled -= OnPushToTalkSignalled;
                    _pushToTalkHook = null;
                    await hook.DisposeAsync().ConfigureAwait(true);
                }

                _globalHotkeyLock?.Dispose();
                _globalHotkeyLock = null;
            }),
        ],
        ShutDownSession: _sessionCoordinator is { } coordinator ? budget => coordinator.ShutdownAsync(budget) : null,
        Quiesce: (LifetimeStep[])
        [
            // THE LAUNCH AND A MODEL DELIVERY BORROW WHAT THE EXIT DISPOSES - the recovery store, the
            // engines they build or replace - so each is one tracked task, cancelled by the exit
            // policy above and joined here; one that outlives the budget keeps everything and
            // escalates like any step. Both read Leaving after every await, so nothing is built once
            // the exit has begun.
            new LifetimeStep("startup", () => Join(Interlocked.Exchange(ref _startup, null))),
            new LifetimeStep("model delivery", () => Join(Interlocked.Exchange(ref _modelDelivery, null))),
            new LifetimeStep("graphics runtime delivery", () => Join(Interlocked.Exchange(ref _graphicsRuntimeDelivery, null))),
            new LifetimeStep("polish warm-up", async () =>
            {
                if (_polishWarmup is { } warmup)
                {
                    _polishWarmup = null;
                    try
                    {
                        await warmup.ConfigureAwait(true);
                    }
                    catch (OperationCanceledException)
                    {
                        // The exit cancels an in-flight readiness probe; a cancelled probe is over.
                    }
                }
            }),
            // THE SYSTEM-ENDING NOTE WRITES TO THE RUN-STATE STORE, which the exit closes only once
            // nothing that writes to it is outstanding; a note still writing is joined here, and one
            // that outlives the budget keeps the store open and escalates like any other step.
            new LifetimeStep("system ending note", async () =>
            {
                if (Interlocked.Exchange(ref _systemEndingNote, null) is { } note)
                {
                    try
                    {
                        await note.ConfigureAwait(true);
                    }
                    catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
                    {
                        // A NOTE THAT FAILED IS A LOST NOTE, and the record then reads as an
                        // interruption - the honest fallback for an ending Windows made. The exit
                        // only needed the write to be over before the store closes.
                    }
                }
            }),
            new LifetimeStep("heartbeat", async () =>
            {
                var cancellation = Interlocked.Exchange(ref _heartbeatCancellation, null);
                var loop = Interlocked.Exchange(ref _heartbeatLoop, null);
                cancellation?.Cancel();
                if (loop is not null)
                {
                    try
                    {
                        await loop.ConfigureAwait(true);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }

                cancellation?.Dispose();
            }),
        ],
        DisposeSessionDependencies: (LifetimeStep[])
        [
            // THE SESSION ITSELF IS NOT HERE: the controller, its capture and the delivery route are
            // disposed by the executor's teardown under the session's shutdown, in its order, and a
            // shell that never composed a coordinator never built them either.
            new LifetimeStep("preview engine", async () =>
            {
                if (_previewEngine is { } engine)
                {
                    _previewEngine = null;
                    await engine.DisposeAsync().ConfigureAwait(true);
                }
            }),
            new LifetimeStep("transcription engine", async () =>
            {
                if (_transcriptionEngine is { } engine)
                {
                    _transcriptionEngine = null;
                    await engine.DisposeAsync().ConfigureAwait(true);
                }
            }),
            new LifetimeStep("polish provider", async () =>
            {
                if (_polishProvider is { } provider)
                {
                    _polishProvider = null;
                    await provider.DisposeAsync().ConfigureAwait(true);
                }
            }),
            LifetimeStep.Of("worker arbiter", _resourceArbiter.Dispose),
            LifetimeStep.Of("polish warmup cancellation", () =>
            {
                _polishWarmupCancellation?.Cancel();
                _polishWarmupCancellation?.Dispose();
                _polishWarmupCancellation = null;
            }),
            LifetimeStep.Of("polish lifetime", _polishLifetime.Dispose),
            new LifetimeStep("live preview", () => _livePreview.DisposeAsync().AsTask()),
            new LifetimeStep("watchdog", () => _watchdog.DisposeAsync().AsTask()),
            new LifetimeStep("auto-stop", () => _autoStop.DisposeAsync().AsTask()),
            LifetimeStep.Of("history store", _historyStore.Dispose),
            LifetimeStep.Of("recovery store", _recoveryTextStore.Dispose),
        ],
        DisposeShell: (LifetimeStep[])
        [
            // THE PRESENTATION SESSION: the drain first (the settings write in flight is kept), then
            // the writer's gate and the device catalogue - the one owner of what the window used to
            // build. Closed again by a later call it is a no-op.
            new LifetimeStep("presentation session", async () =>
            {
                if (_presentation is { } presentation)
                {
                    await presentation.DisposeAsync().ConfigureAwait(true);
                }

                var models = _polishModelSource;
                _polishModelSource = null;
                models?.Dispose();
            }),
            new LifetimeStep("activation channel", async () =>
            {
                if (_activationChannel is { } channel)
                {
                    channel.ActivationRequested -= OnDuplicateActivationRequested;
                    _activationChannel = null;
                    await channel.DisposeAsync().ConfigureAwait(true);
                }
            }),
            LifetimeStep.Of("tray icon", () =>
            {
                var tray = _trayIcon;
                _trayIcon = null;
                tray?.Dispose();
                var foreground = _foregroundHistory;
                _foregroundHistory = null;
                foreground?.Dispose();
            }),
            new LifetimeStep("session coordinator", async () =>
            {
                if (_sessionCoordinator is { } stopped)
                {
                    _sessionCoordinator = null;
                    await stopped.DisposeAsync().ConfigureAwait(true);
                }
            }),
        ],
        // THE SINGLE-INSTANCE LOCK IS NOT RELEASED HERE, OR ANYWHERE: it is held to the process's end,
        // which the operating system makes for free, so no other launch can begin a run - and write
        // the record - before this run's own completion has landed. An update's restart waits for
        // this process to exit before it launches the next.
        CompleteRun: (fence, cancellation) => _runId is { } runId
            ? _runStateStore.CompleteRunAsync(runId, DateTimeOffset.UtcNow, fence, cancellation)
            : Task.FromResult(true),
        CloseRunState: _runStateStore.Dispose,
        DisposeLogger: async () =>
        {
            RuntimeWorkerSupervisor.AbnormalWorkerExitObserved -= OnAbnormalWorkerExitObserved;
            if (_observabilityService is { } observabilityService)
            {
                _observabilityService = null;
                await observabilityService.DisposeAsync().ConfigureAwait(false);
            }

            _feedbackCoordinator?.Dispose();
            _feedbackCoordinator = null;
            _feedbackOutbox?.Dispose();
            _feedbackOutbox = null;
            _feedbackSender?.Dispose();
            _feedbackSender = null;

            await _logger.DisposeAsync().ConfigureAwait(false);
        });

    /// <summary>A tracked task's join for a lifetime step: taken once, its own ending swallowed - the exit only needs it over.</summary>
    private static async Task Join(Task? tracked)
    {
        if (tracked is null)
        {
            return;
        }

        try
        {
            await tracked.ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            // A launch or a delivery cut short by the exit policy, or one that failed on its own and
            // said so on its surface; the step needed it over, not successful.
        }
    }

    /// <summary>
    /// Ends the process when the exit could not: something would not finish inside the budget and is
    /// still running, holding whatever it holds; the log has said so and been closed; the next launch
    /// reads an interrupted run. The exit code is the escalation's own, so a harness can tell it apart.
    /// </summary>
    /// <remarks>
    /// THE DELETION IS NOT DROPPED BECAUSE THE EXIT WAS NOT CLEAN. The person asked for their data to be
    /// removed and was told the app would close to do it; an exit that escalates still reaches here, so the
    /// folder is emptied as far as it can be, and whatever was still held is counted for the next launch.
    /// </remarks>
    private sealed class HostTerminator(Action beforeExit) : IHostTerminator
    {
        public const int ExitCode = 70;

        public void Terminate(ExitReport report)
        {
            try
            {
                beforeExit();
            }
            catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
            {
                // The process is ending either way; a failure here must not stop it ending.
            }

            Environment.Exit(ExitCode);
        }
    }
}
