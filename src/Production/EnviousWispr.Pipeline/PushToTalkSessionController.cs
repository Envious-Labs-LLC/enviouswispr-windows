using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Sessions;

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
    AppError? Error = null);

public sealed class PushToTalkSessionController : IAsyncDisposable
{
    private readonly IAudioCapture _audioCapture;
    private readonly IForegroundTargetProvider _targetProvider;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _minimumHoldDuration;
    private readonly Func<TextDeliveryOptions> _deliveryOptions;
    private readonly AudioDeviceId? _preferredAudioDevice;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public PushToTalkSessionController(
        IAudioCapture audioCapture,
        IForegroundTargetProvider targetProvider,
        TimeProvider? timeProvider = null,
        TimeSpan? minimumHoldDuration = null,
        Func<TextDeliveryOptions>? deliveryOptions = null,
        AudioDeviceId? preferredAudioDevice = null)
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
        ArgumentOutOfRangeException.ThrowIfLessThan(_minimumHoldDuration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            _minimumHoldDuration,
            TimeSpan.FromSeconds(1));
    }

    public event EventHandler<DictationSessionSnapshot>? SessionChanged;

    public DictationSessionSnapshot? CurrentSession { get; private set; }

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
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (CurrentSession is not null)
            {
                return Ignored();
            }

            var target = _targetProvider.CaptureForegroundTarget();
            if (target is null || !target.Value.IsValid)
            {
                return Failure(new AppError(
                    AppErrorCode.TargetUnavailable,
                    AppErrorStage.TargetCapture,
                    CanRetry: true));
            }

            var sessionId = DictationSessionId.Create();
            var started = await _audioCapture
                .StartAsync(new AudioCaptureRequest(sessionId, _preferredAudioDevice), cancellationToken)
                .ConfigureAwait(false);
            AppError? fallbackReason = null;
            if (!started.Succeeded &&
                _preferredAudioDevice is not null &&
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
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SessionTransitionResult> ReleaseAsync(
        CancellationToken cancellationToken = default)
    {
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

            CurrentSession = CurrentSession with { State = DictationSessionState.Finalizing };
            RaiseChanged();
            var audio = await _audioCapture.StopAsync(cancellationToken).ConfigureAwait(false);
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
            if (CurrentSession?.State != DictationSessionState.Recording)
            {
                return Ignored();
            }

            var cancelled = await _audioCapture.CancelAsync(cancellationToken).ConfigureAwait(false);
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
