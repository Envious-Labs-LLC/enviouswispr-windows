using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Sessions;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Pipeline;

public enum SessionTransitionKind
{
    Started,
    FinalizeReady,
    Delivering,
    Cancelled,
    Completed,
    Reset,
    Ignored,
    Failed,
}

public sealed record SessionTransitionResult(
    SessionTransitionKind Kind,
    DictationSessionSnapshot? Session = null,
    CapturedAudio? Audio = null,
    AppError? Error = null,
    bool IsHandsFree = false);

/// <summary>
/// What a press is about, taken at the instant of the press: the window under the caret and the
/// delivery choice in force. Carried with the press so that whatever runs it later cannot ask again.
/// </summary>
public sealed record RecordingStartContext(TargetWindowId? Target, TextDeliveryOptions DeliveryOptions);

/// <summary>
/// What a release is about, taken at the instant of the release when cursor-at-release targeting
/// is in force: the live non-self foreground window under the caret before transcription or polish runs.
/// </summary>
public sealed record ReleaseTargetContext(TargetWindowId? Target);

public sealed class PushToTalkSessionController : IAsyncDisposable
{
    private readonly IAudioCapture _audioCapture;
    private readonly IForegroundTargetProvider _targetProvider;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _minimumHoldDuration;
    private readonly Func<TextDeliveryOptions> _deliveryOptions;
    private readonly AudioDeviceId? _preferredAudioDevice;
    private readonly Func<CancellationToken, Task<AudioDeviceId?>>? _resolveAudioDevice;
    private readonly UatRuntimePhaseJournal? _phaseJournal;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public PushToTalkSessionController(
        IAudioCapture audioCapture,
        IForegroundTargetProvider targetProvider,
        TimeProvider? timeProvider = null,
        TimeSpan? minimumHoldDuration = null,
        Func<TextDeliveryOptions>? deliveryOptions = null,
        AudioDeviceId? preferredAudioDevice = null,
        UatRuntimePhaseJournal? phaseJournal = null,
        Func<CancellationToken, Task<AudioDeviceId?>>? resolveAudioDevice = null)
    {
        ArgumentNullException.ThrowIfNull(audioCapture);
        ArgumentNullException.ThrowIfNull(targetProvider);
        _audioCapture = audioCapture;
        _targetProvider = targetProvider;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _minimumHoldDuration = minimumHoldDuration ?? TimeSpan.FromMilliseconds(100);
        // ASKED FOR AT THE PRESS, NOT SNAPSHOT AT STARTUP. Reading the settings once when this was
        // built meant saving a delivery choice did nothing until the app was restarted - the toggle
        // moved, the file changed, and the next recording used the value from launch. Reading it
        // here keeps the other half of the promise too: whatever it answers is held for the whole of
        // that recording, so a change saved mid-take cannot alter where the words already going.
        _deliveryOptions = deliveryOptions ?? (() => TextDeliveryOptions.Default);
        _preferredAudioDevice = preferredAudioDevice;
        _resolveAudioDevice = resolveAudioDevice;
        _phaseJournal = phaseJournal ?? UatRuntimePhaseJournal.FromEnvironment();
        ArgumentOutOfRangeException.ThrowIfLessThan(_minimumHoldDuration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            _minimumHoldDuration,
            TimeSpan.FromSeconds(1));
    }

    public event EventHandler<DictationSessionSnapshot>? SessionChanged;

    public DictationSessionSnapshot? CurrentSession { get; private set; }

    /// <summary>
    /// Takes the window under the caret and the delivery choice in force, right now, with no await
    /// in between. The thing that starts the recording later is handed this rather than asking again.
    /// </summary>
    public RecordingStartContext CaptureStartContext() =>
        new(_targetProvider.CaptureForegroundTarget(), _deliveryOptions());

    public async Task<SessionTransitionResult> PressAsync(
        CancellationToken cancellationToken = default)
    {
        // BEFORE THE GATE, WHICH IS THE FIRST AWAIT AND THEREFORE THE FIRST PLACE TIME CAN PASS.
        // Reading it after the gate leaves a window under contention: the press happens, another
        // press or a release holds the gate, a save lands, and the recording that is starting takes
        // a choice made after the person pressed the key.
        var deliveryOptions = _deliveryOptions();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // THE PROVIDER IS NOT ASKED WHEN THERE IS NOTHING TO START. A second press during a
            // recording is answered Ignored before the foreground window is read, as it always was.
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (CurrentSession is not null)
            {
                return Ignored();
            }

            return await PressCoreAsync(
                    _targetProvider.CaptureForegroundTarget(),
                    deliveryOptions,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Starts a recording against a target and delivery choice captured earlier, at the press itself.
    /// </summary>
    /// <remarks>
    /// THE HOOK USED TO REACH THE TARGET CAPTURE SYNCHRONOUSLY, inside the key callback, before the
    /// first await. Anything that puts a scheduler between the press and the start - a queue, a
    /// thread hop - opens a window in which the person can switch windows or save a setting, and the
    /// recording would then go where the caret was a moment later. So the press captures, and this
    /// overload consumes what it captured without looking again.
    /// </remarks>
    public async Task<SessionTransitionResult> PressAsync(
        RecordingStartContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await PressCoreAsync(context.Target, context.DeliveryOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SessionTransitionResult> PressCoreAsync(
        TargetWindowId? target,
        TextDeliveryOptions deliveryOptions,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (CurrentSession is not null)
        {
            return Ignored();
        }

        if (target is null || !target.Value.IsValid)
        {
            return Failure(new AppError(
                AppErrorCode.TargetUnavailable,
                AppErrorStage.TargetCapture,
                CanRetry: true));
        }

        var sessionId = DictationSessionId.Create();
        var targetAudioDevice = _resolveAudioDevice is not null
            ? await _resolveAudioDevice(cancellationToken).ConfigureAwait(false)
            : _preferredAudioDevice;
        var started = await _audioCapture
            .StartAsync(new AudioCaptureRequest(sessionId, targetAudioDevice), cancellationToken)
            .ConfigureAwait(false);
        AppError? fallbackReason = null;
        if (!started.Succeeded &&
            targetAudioDevice is not null &&
            started.Error?.Code is AppErrorCode.AudioDeviceUnavailable or AppErrorCode.AudioDeviceLost)
        {
            fallbackReason = started.Error;
            started = await _audioCapture
                .StartAsync(new AudioCaptureRequest(sessionId), cancellationToken)
                .ConfigureAwait(false);
        }

        if (!started.Succeeded)
        {
            return Failure(started.Error ?? new AppError(
                AppErrorCode.AudioDeviceUnavailable,
                AppErrorStage.AudioCapture,
                CanRetry: true));
        }

        CurrentSession = DictationSessionSnapshot.Start(
            sessionId,
            _timeProvider.GetUtcNow(),
            target.Value,
            deliveryOptions);
        RaiseChanged();
        return new SessionTransitionResult(
            SessionTransitionKind.Started,
            CurrentSession,
            Error: fallbackReason);
    }

    /// <summary>
    /// Takes the release-time window under the caret when cursor-at-release targeting is active
    /// and Copy-only is off, excluding EnviousWispr's own windows. Returns null otherwise so start
    /// targeting avoids any unnecessary UI Automation probe on release.
    /// </summary>
    public ReleaseTargetContext? CaptureReleaseTarget()
    {
        var deliveryOptions = CurrentSession?.DeliveryOptions ?? _deliveryOptions();
        if (deliveryOptions.CopyInsteadOfPaste ||
            deliveryOptions.TargetMode != PasteTargetMode.WhereCursorIsAtRelease)
        {
            return null;
        }

        return new ReleaseTargetContext(CaptureNonSelfTarget());
    }

    private TargetWindowId? CaptureNonSelfTarget()
    {
        var captured = _targetProvider.CaptureForegroundTarget();
        return captured is { } target &&
            target.ProcessId != 0 &&
            target.ProcessId == (uint)Environment.ProcessId
                ? null
                : captured;
    }

    public Task<SessionTransitionResult> ReleaseAsync(
        CancellationToken cancellationToken = default) =>
        ReleaseAsync(releaseContext: null, cancellationToken);

    public async Task<SessionTransitionResult> ReleaseAsync(
        ReleaseTargetContext? releaseContext,
        CancellationToken cancellationToken = default)
    {
        var captureFallback = releaseContext is null &&
            CurrentSession is { DeliveryOptions: { CopyInsteadOfPaste: false, TargetMode: PasteTargetMode.WhereCursorIsAtRelease } };
        var fallbackReleaseTarget = captureFallback ? CaptureNonSelfTarget() : null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (CurrentSession?.State != DictationSessionState.Recording)
            {
                return Ignored();
            }

            var heldFor = _timeProvider.GetUtcNow() - CurrentSession.StartedAt;
            var remainingDebounce = _minimumHoldDuration - heldFor;
            if (remainingDebounce > TimeSpan.Zero)
            {
                await Task.Delay(
                    remainingDebounce,
                    _timeProvider,
                    cancellationToken).ConfigureAwait(false);
            }

            var sessionId = CurrentSession.Id.Value;
            var effectiveTarget = CurrentSession.DeliveryOptions is
                {
                    CopyInsteadOfPaste: false,
                    TargetMode: PasteTargetMode.WhereCursorIsAtRelease,
                }
                    ? (releaseContext is not null
                        ? releaseContext.Target
                        : (captureFallback ? fallbackReleaseTarget : CaptureNonSelfTarget())) ?? default
                    : CurrentSession.Target;
            CurrentSession = CurrentSession with
            {
                State = DictationSessionState.Finalizing,
                Target = effectiveTarget,
            };
            RaiseChanged();
            _phaseJournal?.Record(UatRuntimePhase.CaptureStopEntered, sessionId);
            var audio = await _audioCapture.StopAsync(cancellationToken).ConfigureAwait(false);
            _phaseJournal?.Record(
                UatRuntimePhase.CaptureStopReturned,
                sessionId,
                startSample: 0,
                endSample: audio.Samples.Length,
                remainingSamples: audio.Samples.Length,
                sampleRate: audio.SampleRate > 0 ? audio.SampleRate : null);
            if (audio.Error is not null && audio.Samples.IsEmpty)
            {
                CurrentSession = CurrentSession with
                {
                    State = DictationSessionState.Failed,
                    FinishedAt = _timeProvider.GetUtcNow(),
                    Error = audio.Error,
                };
                RaiseChanged();
                return new SessionTransitionResult(
                    SessionTransitionKind.Failed,
                    CurrentSession,
                    audio,
                    audio.Error);
            }

            return new SessionTransitionResult(
                SessionTransitionKind.FinalizeReady,
                CurrentSession,
                audio,
                audio.Error);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SessionTransitionResult> CancelAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (CurrentSession?.State is not (DictationSessionState.Recording or DictationSessionState.Finalizing))
            {
                return Ignored();
            }

            var shouldCancelCapture = CurrentSession.State == DictationSessionState.Recording || _audioCapture.IsCapturing;
            var cancelled = shouldCancelCapture
                ? await _audioCapture.CancelAsync(cancellationToken).ConfigureAwait(false)
                : new AudioOperationResult(Succeeded: true);
            var failed = !cancelled.Succeeded;
            CurrentSession = CurrentSession with
            {
                State = failed ? DictationSessionState.Failed : DictationSessionState.Cancelled,
                FinishedAt = _timeProvider.GetUtcNow(),
                Error = cancelled.Error,
            };
            RaiseChanged();
            return new SessionTransitionResult(
                failed ? SessionTransitionKind.Failed : SessionTransitionKind.Cancelled,
                CurrentSession,
                Error: cancelled.Error);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SessionTransitionResult> BeginDeliveryAsync(
        DictationSessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        // The transition raises a change that whoever listens may log; joined to the dictation named.
        using var dictation = DictationScope.Begin(sessionId.Value);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (CurrentSession?.Id != sessionId ||
                CurrentSession.State != DictationSessionState.Finalizing)
            {
                return Ignored();
            }

            CurrentSession = CurrentSession with { State = DictationSessionState.Delivering };
            RaiseChanged();
            return new SessionTransitionResult(SessionTransitionKind.Delivering, CurrentSession);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SessionTransitionResult> CompleteAsync(
        DictationSessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        using var dictation = DictationScope.Begin(sessionId.Value);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (CurrentSession?.Id != sessionId ||
                CurrentSession.State is not (DictationSessionState.Finalizing or
                    DictationSessionState.Delivering))
            {
                return Ignored();
            }

            CurrentSession = CurrentSession with
            {
                State = DictationSessionState.Completed,
                FinishedAt = _timeProvider.GetUtcNow(),
            };
            RaiseChanged();
            return new SessionTransitionResult(SessionTransitionKind.Completed, CurrentSession);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SessionTransitionResult> AbortAsync(
        AppError error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(error);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (CurrentSession is null)
            {
                return Ignored();
            }

            if (CurrentSession.State == DictationSessionState.Recording)
            {
                await _audioCapture.CancelAsync(cancellationToken).ConfigureAwait(false);
            }

            CurrentSession = CurrentSession with
            {
                State = DictationSessionState.Failed,
                FinishedAt = _timeProvider.GetUtcNow(),
                Error = error,
            };
            RaiseChanged();
            return new SessionTransitionResult(
                SessionTransitionKind.Failed,
                CurrentSession,
                Error: error);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SessionTransitionResult> ResetAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (CurrentSession?.State is not (DictationSessionState.Completed or
                DictationSessionState.Cancelled or DictationSessionState.Failed))
            {
                return Ignored();
            }

            CurrentSession = null;
            return new SessionTransitionResult(SessionTransitionKind.Reset);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (CurrentSession?.State == DictationSessionState.Recording)
        {
            await CancelAsync().ConfigureAwait(false);
        }

        _disposed = true;
        await _audioCapture.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private void RaiseChanged() => SessionChanged?.Invoke(this, CurrentSession!);

    private SessionTransitionResult Ignored() => new(
        SessionTransitionKind.Ignored,
        CurrentSession);

    private static SessionTransitionResult Failure(AppError error) => new(
        SessionTransitionKind.Failed,
        Error: error);
}
