using System.Buffers.Binary;
using System.Runtime.InteropServices;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Audio;

public sealed class WasapiAudioCapture :
    IMicrophoneTestCapture,
    IAudioIncrementalSnapshotSource,
    ICaptureStartTimings,
    IWarmAudioCapture,
    ISilentMicSampleSource
{
    public static readonly TimeSpan DefaultHardCaptureBackstop = TimeSpan.FromMinutes(61);
    private const int PreRollCapacitySamples = 8_000;

    private static readonly AudioBufferFormat CaptureFormat = new(
        AudioSampleConverter.TargetSampleRate,
        Channels: 1,
        AudioSampleEncoding.Ieee754Single);

    private enum CaptureState
    {
        Cold = 0,
        WarmIdle = 1,
        Capturing = 2,
        DrainingToWarm = 3,
    }

    private readonly object _bufferGate = new();
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private readonly MemoryStream _capturedBytes = new();
    private readonly float[] _preRollRing = new float[PreRollCapacitySamples];
    private readonly IAudioRecorderFactory _recorderFactory;
    private readonly TimeSpan _stopTimeout;
    private readonly long _maxCapturedBytes;
    private readonly TimeProvider _timeProvider;
    private IAudioRecorderSession? _recorder;
    private TaskCompletionSource<Exception?>? _recordingStopped;
    private TaskCompletionSource? _warmDrainTcs;
    private AudioCaptureRequest? _request;
    private AudioDeviceId? _warmDeviceId;
    private ITimer? _warmTeardownTimer;
    private CaptureState _state = CaptureState.Cold;
    private int _warmPolicy = (int)MicrophoneWarmPolicy.Off;
    private long _warmIdleSinceTimestamp;
    private long _warmGeneration;
    private int _inFlightCaptureCallbacks;
    private int _preRollWriteIndex;
    private int _preRollCount;
    private int _drainedPreRollSampleCount;
    private int _exactZeroRunSamples;
    private bool _exactZeroSinceTakeStart = true;
    private bool _sawNonZeroPostPreRoll;
    private bool _lastCaptureWasWholeZero;
    private volatile bool _isCapturing;
    private volatile bool _clientRequestedStop;
    private volatile bool _backendStopped;
    private volatile bool _unexpectedStop;
    private int _backstopTriggered;
    private int _backstopSignalled;
    private bool _disposed;
    private long? _lastDeviceOpenMilliseconds;
    private long? _lastStreamStartMilliseconds;
    private int _packets;
    private int _silentPackets;
    private float _peak;
    private float _rms;

    public WasapiAudioCapture()
        : this(
            new WasapiRecorderFactory(),
            TimeSpan.FromSeconds(2),
            DefaultHardCaptureBackstop,
            TimeProvider.System)
    {
    }

    internal WasapiAudioCapture(
        IAudioRecorderFactory recorderFactory,
        TimeSpan stopTimeout,
        TimeSpan? hardCaptureBackstop = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(recorderFactory);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(stopTimeout, TimeSpan.Zero);
        var effectiveBackstop = hardCaptureBackstop ?? DefaultHardCaptureBackstop;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(effectiveBackstop, TimeSpan.Zero);

        _recorderFactory = recorderFactory;
        _stopTimeout = stopTimeout;
        _timeProvider = timeProvider ?? TimeProvider.System;
        var requestedSamples = effectiveBackstop.TotalSeconds * AudioSampleConverter.TargetSampleRate;
        var maxSamples = requestedSamples >= (int.MaxValue / sizeof(float))
            ? int.MaxValue / sizeof(float)
            : Math.Max(1L, (long)Math.Ceiling(requestedSamples));
        _maxCapturedBytes = maxSamples * sizeof(float);
    }

    public event EventHandler<AudioLevel>? LevelChanged;

    public event EventHandler<DictationSessionId>? MaxDurationBackstopReached;

    public bool IsCapturing => _isCapturing;

    public MicrophoneWarmPolicy WarmPolicy => (MicrophoneWarmPolicy)Volatile.Read(ref _warmPolicy);

    public long? LastDeviceOpenMilliseconds => _lastDeviceOpenMilliseconds;

    public long? LastStreamStartMilliseconds => _lastStreamStartMilliseconds;

    internal Action? BeforeDataConversionForTesting { get; set; }

    internal Action? BeforeAnyDataConversionForTesting { get; set; }

    public int LastPacketCount => Volatile.Read(ref _packets);

    public int LastSilentPacketCount => Volatile.Read(ref _silentPackets);

    public float LastPeak => _peak;

    public float LastRootMeanSquare => _rms;

    public ExactZeroRun? CurrentExactZeroRun
    {
        get
        {
            lock (_bufferGate)
            {
                if (_state != CaptureState.Capturing || !_isCapturing)
                {
                    return null;
                }

                return new ExactZeroRun(_exactZeroRunSamples, _exactZeroSinceTakeStart);
            }
        }
    }

    public bool LastCaptureWasWholeZero => Volatile.Read(ref _lastCaptureWasWholeZero);

    public async Task SetWarmPolicyAsync(
        MicrophoneWarmPolicy policy,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(policy))
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }

        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Volatile.Write(ref _warmPolicy, (int)policy);
            if (_state != CaptureState.WarmIdle)
            {
                return;
            }

            CancelWarmTeardownTimer();
            switch (policy)
            {
                case MicrophoneWarmPolicy.Off:
                    await TearDownWarmCoreLockedAsync().ConfigureAwait(false);
                    break;
                case MicrophoneWarmPolicy.Always:
                    break;
                case MicrophoneWarmPolicy.Seconds10:
                case MicrophoneWarmPolicy.Seconds30:
                case MicrophoneWarmPolicy.Seconds60:
                    var timeout = WarmTimeoutFor(policy);
                    var elapsed = _timeProvider.GetElapsedTime(_warmIdleSinceTimestamp);
                    if (elapsed >= timeout)
                    {
                        await TearDownWarmCoreLockedAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        ScheduleWarmTeardownTimerLocked(timeout - elapsed);
                    }

                    break;
            }
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    public async Task TearDownWarmAsync(CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed || _state != CaptureState.WarmIdle)
            {
                return;
            }

            await TearDownWarmCoreLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    public AudioSnapshot? GetSnapshot(TimeSpan maximumDuration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumDuration, TimeSpan.Zero);
        // SATURATES RATHER THAN OVERFLOWS, AND A CALLER ASKING FOR EVERYTHING IS THE REASON. The
        // streaming head start wants the whole recording rather than a window - a commit is a range
        // measured from the start, so a rolling window would make those indices mean something
        // different on every poll - and it says so with `TimeSpan.MaxValue`. That is about 9.2e11
        // seconds; times the sample rate it is about 1.5e16, and the `checked` cast threw
        // `OverflowException` on the FIRST poll of every recording ever made. Measured 2026-09-03:
        // the head start was abandoned 511 ms in, every single time, since the day it shipped.
        //
        // NOT ONLY THE SENTINEL. A year-long duration overflowed too, so this was never about one
        // magic value being mishandled.
        //
        // A CEILING THAT CANNOT BE REACHED IS NOT A LIMIT. This value only ever TRIMS the buffer, so
        // clamping it changes nothing for any duration a caller could mean, and it removes the one
        // way "give me everything" could become a crash. Ref: #96.
        var requestedSamples = Math.Ceiling(
            maximumDuration.TotalSeconds * AudioSampleConverter.TargetSampleRate);
        var maximumSamples = requestedSamples >= int.MaxValue
            ? int.MaxValue
            : Math.Max(1, (int)requestedSamples);
        var maximumBytes = (long)maximumSamples * sizeof(float);
        byte[] bytes;
        DictationSessionId sessionId;
        lock (_bufferGate)
        {
            if (!_isCapturing || _request is null)
            {
                return null;
            }

            sessionId = _request.SessionId;
            var byteCount = (int)Math.Min(_capturedBytes.Length, maximumBytes);
            if (!_capturedBytes.TryGetBuffer(out var buffer) || buffer.Array is null)
            {
                return null;
            }

            bytes = buffer.Array.AsSpan(
                buffer.Offset + checked((int)_capturedBytes.Length) - byteCount,
                byteCount).ToArray();
        }

        var samples = AudioSampleConverter.ConvertToMono16Khz(bytes, CaptureFormat).Samples;
        return new AudioSnapshot(
            sessionId,
            samples,
            AudioSampleConverter.TargetSampleRate,
            Channels: 1);
    }

    public IncrementalAudioSnapshot? GetSnapshotFrom(int startSample)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startSample);
        byte[]? tailBytes = null;
        DictationSessionId sessionId;
        int clampedStartSample;
        int totalSamples;
        lock (_bufferGate)
        {
            if (!_isCapturing || _request is null)
            {
                return null;
            }

            sessionId = _request.SessionId;
            var totalBytes = checked((int)_capturedBytes.Length);
            totalSamples = totalBytes / sizeof(float);
            clampedStartSample = Math.Min(startSample, totalSamples);
            var byteOffset = clampedStartSample * sizeof(float);
            var tailByteCount = totalBytes - byteOffset;
            if (tailByteCount > 0 && _capturedBytes.TryGetBuffer(out var buffer) && buffer.Array is null == false)
            {
                tailBytes = buffer.Array.AsSpan(buffer.Offset + byteOffset, tailByteCount).ToArray();
            }
        }

        var tailSamples = tailBytes is null
            ? ReadOnlyMemory<float>.Empty
            : AudioSampleConverter.ConvertToMono16Khz(tailBytes, CaptureFormat).Samples;
        return new IncrementalAudioSnapshot(
            sessionId,
            AudioSampleConverter.TargetSampleRate,
            clampedStartSample,
            totalSamples,
            tailSamples);
    }

    public async Task<AudioOperationResult> StartAsync(
        AudioCaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state == CaptureState.Capturing)
            {
                return Failure(AppErrorCode.CaptureAlreadyActive, canRetry: false);
            }

            CancelWarmTeardownTimer();
            if (_state == CaptureState.WarmIdle)
            {
                var reusedWarm = false;
                lock (_bufferGate)
                {
                    if (_state == CaptureState.WarmIdle &&
                        _recorder is not null &&
                        !_backendStopped &&
                        !_unexpectedStop &&
                        _warmDeviceId == request.DeviceId)
                    {
                        _capturedBytes.SetLength(0);
                        _capturedBytes.Capacity = 0;
                        DrainPreRollToCapturedBytesLocked();
                        _packets = 0;
                        _silentPackets = 0;
                        _peak = 0f;
                        _rms = 0f;
                        _exactZeroRunSamples = 0;
                        _exactZeroSinceTakeStart = true;
                        _sawNonZeroPostPreRoll = false;
                        Volatile.Write(ref _lastCaptureWasWholeZero, false);
                        _request = request;
                        _clientRequestedStop = false;
                        _unexpectedStop = false;
                        Volatile.Write(ref _backstopTriggered, 0);
                        Volatile.Write(ref _backstopSignalled, 0);
                        _warmGeneration++;
                        _state = CaptureState.Capturing;
                        _isCapturing = true;
                        reusedWarm = true;
                    }
                }

                if (reusedWarm)
                {
                    _lastDeviceOpenMilliseconds = 0;
                    _lastStreamStartMilliseconds = 0;
                    return new AudioOperationResult(Succeeded: true);
                }

                await TearDownWarmCoreLockedAsync().ConfigureAwait(false);
            }
            else if (_recorder is not null)
            {
                return Failure(AppErrorCode.CaptureAlreadyActive, canRetry: false);
            }

            Volatile.Write(ref _lastCaptureWasWholeZero, false);

            // TIMED SEPARATELY BECAUSE THE TWO HALVES ANSWER DIFFERENT QUESTIONS. Opening is the
            // half a warm device would remove; starting the stream is the half nothing can remove.
            // The stopwatch starts before the try so a FAILED open is still timed - a device that
            // takes two seconds to refuse is worth knowing about, and it is exactly the case a
            // success-only measurement never sees.
            var openTimer = System.Diagnostics.Stopwatch.StartNew();
            IAudioRecorderSession recorder;
            try
            {
                recorder = await _recorderFactory
                    .CreateAsync(request.DeviceId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ArgumentException)
            {
                return Failure(AppErrorCode.AudioFormatUnsupported, canRetry: true);
            }
            catch (InvalidOperationException)
            {
                return Failure(AppErrorCode.AudioDeviceUnavailable, canRetry: true);
            }
            catch (UnauthorizedAccessException)
            {
                return Failure(AppErrorCode.AccessDenied, canRetry: true);
            }
            catch (COMException)
            {
                return Failure(AppErrorCode.AudioDeviceUnavailable, canRetry: true);
            }
            finally
            {
                // ONE PLACE FOR ALL FIVE EXITS. Recording it inside each branch means the next
                // branch someone adds is the one that quietly stops being measured, and a missing
                // number here does not look like a defect - it looks like the case never happened.
                openTimer.Stop();
                _lastDeviceOpenMilliseconds = openTimer.ElapsedMilliseconds;
            }

            lock (_bufferGate)
            {
                _capturedBytes.SetLength(0);
                _capturedBytes.Capacity = 0;
                ClearPreRollRingLocked();
                _drainedPreRollSampleCount = 0;
                _packets = 0;
                _silentPackets = 0;
                _peak = 0f;
                _rms = 0f;
                _exactZeroRunSamples = 0;
                _exactZeroSinceTakeStart = true;
                _sawNonZeroPostPreRoll = false;
                _request = request;
                _warmDeviceId = request.DeviceId;
                _recordingStopped = new TaskCompletionSource<Exception?>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _recorder = recorder;
                _clientRequestedStop = false;
                _backendStopped = false;
                _unexpectedStop = false;
                Volatile.Write(ref _backstopTriggered, 0);
                Volatile.Write(ref _backstopSignalled, 0);
                _state = CaptureState.Capturing;
                _isCapturing = true;
            }

            recorder.DataAvailable += OnDataAvailable;
            recorder.Stopped += OnRecordingStopped;

            try
            {
                var startTimer = System.Diagnostics.Stopwatch.StartNew();
                recorder.Start();
                startTimer.Stop();
                _lastStreamStartMilliseconds = startTimer.ElapsedMilliseconds;
                return new AudioOperationResult(Succeeded: true);
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or ArgumentException or COMException)
            {
                await ReleaseRecorderAsync(recorder).ConfigureAwait(false);
                ClearSession();
                return Failure(AppErrorCode.AudioDeviceUnavailable, canRetry: true);
            }
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    public async Task<CapturedAudio> StopAsync(CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state != CaptureState.Capturing || _recorder is null || _request is null || _recordingStopped is null)
            {
                return EmptyFailure(AppErrorCode.InvalidTransition);
            }

            var recorder = _recorder;
            var request = _request;
            var stopped = _recordingStopped;
            var warmPolicy = WarmPolicy;

            float[]? warmSamples = null;
            var warmDrainedPreRoll = 0;
            var transitionedToWarm = false;
            TaskCompletionSource? drainTcs = null;
            var attemptWarmTransition = false;
            lock (_bufferGate)
            {
                if (warmPolicy != MicrophoneWarmPolicy.Off && !_backendStopped && !_unexpectedStop)
                {
                    attemptWarmTransition = true;
                    _isCapturing = false;
                    _state = CaptureState.DrainingToWarm;
                    ClearPreRollRingLocked();
                    if (_inFlightCaptureCallbacks > 0)
                    {
                        drainTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        _warmDrainTcs = drainTcs;
                    }
                }
            }

            if (attemptWarmTransition)
            {
                var drainTimedOut = false;
                if (drainTcs is not null)
                {
                    try
                    {
                        await drainTcs.Task
                            .WaitAsync(_stopTimeout, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        drainTimedOut = true;
                    }
                }

                lock (_bufferGate)
                {
                    _warmDrainTcs = null;
                    if (!drainTimedOut && !_backendStopped && !_unexpectedStop && _state == CaptureState.DrainingToWarm)
                    {
                        warmSamples = SnapshotSamplesLocked();
                        warmDrainedPreRoll = _drainedPreRollSampleCount;
                        Volatile.Write(
                            ref _lastCaptureWasWholeZero,
                            !_sawNonZeroPostPreRoll && (_packets > 0 || warmSamples.Length > warmDrainedPreRoll));
                        _drainedPreRollSampleCount = 0;
                        _exactZeroRunSamples = 0;
                        _exactZeroSinceTakeStart = true;
                        _sawNonZeroPostPreRoll = false;
                        _request = null;
                        _clientRequestedStop = false;
                        _backendStopped = false;
                        _unexpectedStop = false;
                        Volatile.Write(ref _backstopTriggered, 0);
                        Volatile.Write(ref _backstopSignalled, 0);
                        _state = CaptureState.WarmIdle;
                        _warmGeneration++;
                        _warmIdleSinceTimestamp = _timeProvider.GetTimestamp();
                        transitionedToWarm = true;
                    }
                }
            }

            if (transitionedToWarm)
            {
                ScheduleWarmTeardownForPolicyLocked(warmPolicy);
                return new CapturedAudio(
                    request.SessionId,
                    warmSamples!,
                    AudioSampleConverter.TargetSampleRate,
                    Channels: 1,
                    DrainedPreRollSampleCount: warmDrainedPreRoll);
            }

            Exception? stopException = null;
            if (!_backendStopped)
            {
                _clientRequestedStop = true;
                _isCapturing = false;
                try
                {
                    recorder.Stop();
                }
                catch (Exception exception) when (exception is InvalidOperationException or COMException)
                {
                    stopException = exception;
                }
            }

            if (stopException is null)
            {
                try
                {
                    stopException = await stopped.Task
                        .WaitAsync(_stopTimeout, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException timeoutException)
                {
                    stopException = timeoutException;
                }
            }

            var interrupted = _unexpectedStop || stopException is not null;
            float[] samples;
            int coldDrainedPreRoll;
            lock (_bufferGate)
            {
                samples = SnapshotSamplesLocked();
                coldDrainedPreRoll = _drainedPreRollSampleCount;
                Volatile.Write(
                    ref _lastCaptureWasWholeZero,
                    !_sawNonZeroPostPreRoll && (_packets > 0 || samples.Length > coldDrainedPreRoll));
            }

            try
            {
                await ReleaseRecorderAsync(recorder).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidOperationException or COMException)
            {
                stopException ??= exception;
                interrupted = true;
            }

            ClearSession();
            return interrupted
                ? Interrupted(request.SessionId, samples, coldDrainedPreRoll)
                : new CapturedAudio(
                    request.SessionId,
                    samples,
                    AudioSampleConverter.TargetSampleRate,
                    Channels: 1,
                    DrainedPreRollSampleCount: coldDrainedPreRoll);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    public async Task<AudioOperationResult> CancelAsync(CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CancelCoreLockedAsync(emitSilentLevel: true).ConfigureAwait(false);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _transitionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            await CancelCoreLockedAsync(emitSilentLevel: true).ConfigureAwait(false);
            _disposed = true;
        }
        finally
        {
            _transitionGate.Release();
        }

        _capturedBytes.Dispose();
        _transitionGate.Dispose();
    }

    private async Task<AudioOperationResult> CancelCoreLockedAsync(bool emitSilentLevel)
    {
        CancelWarmTeardownTimer();
        Exception? cleanupException = null;
        if (_recorder is not null)
        {
            var recorder = _recorder;
            var stopped = _recordingStopped;
            if (stopped is not null && !_backendStopped)
            {
                _clientRequestedStop = true;
                _isCapturing = false;
                try
                {
                    recorder.Stop();
                    await stopped.Task
                        .WaitAsync(_stopTimeout, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or COMException or TimeoutException)
                {
                    cleanupException = exception;
                }
            }

            try
            {
                await ReleaseRecorderAsync(recorder).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidOperationException or COMException)
            {
                cleanupException ??= exception;
            }
        }

        lock (_bufferGate)
        {
            _capturedBytes.SetLength(0);
            _capturedBytes.Capacity = 0;
            ClearPreRollRingLocked();
            _drainedPreRollSampleCount = 0;
            Volatile.Write(ref _lastCaptureWasWholeZero, false);
        }

        ClearSession();
        if (emitSilentLevel)
        {
            LevelChanged?.Invoke(this, AudioLevel.Silent);
        }

        return cleanupException is null
            ? new AudioOperationResult(Succeeded: true)
            : Failure(AppErrorCode.AudioDeviceLost, canRetry: true);
    }

    private async Task TearDownWarmCoreLockedAsync()
    {
        CancelWarmTeardownTimer();
        var recorder = _recorder;
        var stopped = _recordingStopped;
        var alreadyStopped = _backendStopped;
        lock (_bufferGate)
        {
            _warmGeneration++;
            _state = CaptureState.Cold;
            _isCapturing = false;
            _warmDeviceId = null;
            _drainedPreRollSampleCount = 0;
            ClearPreRollRingLocked();
            _capturedBytes.SetLength(0);
            _capturedBytes.Capacity = 0;
            if (recorder is not null && !alreadyStopped)
            {
                _clientRequestedStop = true;
            }
        }

        if (recorder is not null)
        {
            if (stopped is not null && !alreadyStopped)
            {
                try
                {
                    recorder.Stop();
                    await stopped.Task
                        .WaitAsync(_stopTimeout, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or COMException or TimeoutException)
                {
                }
            }

            try
            {
                await ReleaseRecorderAsync(recorder).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidOperationException or COMException)
            {
            }
        }

        ClearSession();
    }

    private void ScheduleWarmTeardownForPolicyLocked(MicrophoneWarmPolicy policy)
    {
        CancelWarmTeardownTimer();
        if (policy is MicrophoneWarmPolicy.Off or MicrophoneWarmPolicy.Always)
        {
            return;
        }

        ScheduleWarmTeardownTimerLocked(WarmTimeoutFor(policy));
    }

    private void ScheduleWarmTeardownTimerLocked(TimeSpan delay)
    {
        CancelWarmTeardownTimer();
        var generation = _warmGeneration;
        _warmTeardownTimer = _timeProvider.CreateTimer(
            static state =>
            {
                var (self, gen) = ((WasapiAudioCapture, long))state!;
                _ = self.TearDownWarmIfGenerationMatchesAsync(gen);
            },
            (this, generation),
            delay,
            Timeout.InfiniteTimeSpan);
    }

    private void CancelWarmTeardownTimer()
    {
        var timer = Interlocked.Exchange(ref _warmTeardownTimer, null);
        timer?.Dispose();
    }

    private async Task TearDownWarmIfGenerationMatchesAsync(long generation)
    {
        try
        {
            await _transitionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (_disposed || _state != CaptureState.WarmIdle || _warmGeneration != generation)
            {
                return;
            }

            await TearDownWarmCoreLockedAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
        }
        finally
        {
            try
            {
                _transitionGate.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private static TimeSpan WarmTimeoutFor(MicrophoneWarmPolicy policy) => policy switch
    {
        MicrophoneWarmPolicy.Seconds10 => TimeSpan.FromSeconds(10),
        MicrophoneWarmPolicy.Seconds30 => TimeSpan.FromSeconds(30),
        MicrophoneWarmPolicy.Seconds60 => TimeSpan.FromSeconds(60),
        _ => TimeSpan.FromSeconds(30),
    };

    private void OnDataAvailable(object? sender, AudioRecorderData args)
    {
        // AN EMPTY BUFFER IS NOT A MEASUREMENT OF SILENCE, AND TREATING IT AS ONE BLINDED EVERY
        // METER IN THE APP. This recorder delivers a zero-length packet on roughly half its
        // callbacks, and each one arrived within a millisecond of a real one - so the sequence was
        // always a true level published and then a zero published over the top of it. Anything
        // reading the latest level, which is what both meters do, therefore read a zero on
        // virtually every look, and the recording pill sat at its floor through dictations that
        // transcribed perfectly.
        //
        // MEASURED, NOT INFERRED. Ten consecutive packets of a real dictation: 516, 0, 640, 0, 640,
        // 0, 640, 0, 640, 0 bytes, with the empty ones landing in the same millisecond as the full
        // one before them, and every one of them flagged NOT silent. The full packets carried a
        // coherent rising attack from 0.000012 to 0.000509, so nothing about the audio or the
        // conversion was ever wrong.
        //
        // BEFORE THE COUNTERS, DELIBERATELY. An empty packet is not a packet that arrived quietly,
        // it is a callback with nothing in it. These callbacks are flagged NOT silent, so counting
        // them would halve the silent RATIO rather than raise it - a capture that was genuinely
        // half silent would read as a quarter silent, and the microphone test's whole job is to
        // tell three kinds of nothing apart. LastPacketCount means packets of audio that arrived,
        // not times the recorder woke up.
        if (args.Bytes.Length == 0)
        {
            return;
        }

        bool enteredDuringCapture;
        lock (_bufferGate)
        {
            if (_state == CaptureState.Capturing)
            {
                _inFlightCaptureCallbacks++;
                enteredDuringCapture = true;
            }
            else if (_state is CaptureState.WarmIdle or CaptureState.DrainingToWarm)
            {
                enteredDuringCapture = false;
            }
            else
            {
                return;
            }
        }

        try
        {
            BeforeAnyDataConversionForTesting?.Invoke();
            if (enteredDuringCapture)
            {
                BeforeDataConversionForTesting?.Invoke();
            }

            var bytes = args.IsSilent ? new byte[args.Bytes.Length] : args.Bytes.ToArray();
            var converted = AudioSampleConverter.ConvertToMono16Khz(bytes, CaptureFormat);
            var hitBackstop = false;
            var wroteAudio = false;
            DictationSessionId backstopSession = default;
            lock (_bufferGate)
            {
                if (!enteredDuringCapture)
                {
                    if (_state is CaptureState.WarmIdle or CaptureState.DrainingToWarm)
                    {
                        if (!_backendStopped && !_unexpectedStop)
                        {
                            AppendPreRollLocked(converted.Samples);
                        }

                        return;
                    }

                    if (_state != CaptureState.Capturing ||
                        _request is null ||
                        Volatile.Read(ref _backstopTriggered) != 0)
                    {
                        return;
                    }
                }
                else if (_state is not (CaptureState.Capturing or CaptureState.DrainingToWarm) ||
                    _request is null ||
                    Volatile.Read(ref _backstopTriggered) != 0)
                {
                    return;
                }

                Interlocked.Increment(ref _packets);
                if (args.IsSilent)
                {
                    Interlocked.Increment(ref _silentPackets);
                }

                var remaining = _maxCapturedBytes - _capturedBytes.Length;
                var writeCount = bytes.Length;
                if (remaining <= 0)
                {
                    writeCount = 0;
                    hitBackstop = true;
                }
                else if (writeCount >= remaining)
                {
                    writeCount = (int)(remaining - (remaining % sizeof(float)));
                    hitBackstop = true;
                }

                if (writeCount > 0)
                {
                    _capturedBytes.Write(bytes.AsSpan(0, writeCount));
                    wroteAudio = true;

                    if (converted.Peak > _peak)
                    {
                        _peak = converted.Peak;
                    }

                    if (converted.RootMeanSquare > _rms)
                    {
                        _rms = converted.RootMeanSquare;
                    }

                    var sampleCount = Math.Min(converted.Samples.Length, writeCount / sizeof(float));
                    if (args.IsSilent)
                    {
                        _exactZeroRunSamples += sampleCount;
                    }
                    else
                    {
                        var samplesSpan = converted.Samples.AsSpan(0, sampleCount);
                        foreach (var sample in samplesSpan)
                        {
                            if (sample == 0f)
                            {
                                _exactZeroRunSamples++;
                            }
                            else
                            {
                                _exactZeroRunSamples = 0;
                                _exactZeroSinceTakeStart = false;
                                _sawNonZeroPostPreRoll = true;
                            }
                        }
                    }
                }

                if (hitBackstop)
                {
                    backstopSession = _request.SessionId;
                    Volatile.Write(ref _backstopTriggered, 1);
                }
            }

            if (wroteAudio && _state == CaptureState.Capturing)
            {
                LevelChanged?.Invoke(this, new AudioLevel(converted.Peak, converted.RootMeanSquare));
            }

            if (hitBackstop && Interlocked.CompareExchange(ref _backstopSignalled, 1, 0) == 0)
            {
                ThreadPool.QueueUserWorkItem(
                    static state =>
                    {
                        var (self, id) = ((WasapiAudioCapture, DictationSessionId))state!;
                        self.MaxDurationBackstopReached?.Invoke(self, id);
                    },
                    (this, backstopSession));
            }
        }
        catch (ArgumentException exception)
        {
            TaskCompletionSource<Exception?>? stoppedTcs;
            lock (_bufferGate)
            {
                _unexpectedStop = true;
                _isCapturing = false;
                stoppedTcs = _recordingStopped;
            }

            stoppedTcs?.TrySetResult(exception);
        }
        finally
        {
            if (enteredDuringCapture)
            {
                TaskCompletionSource? drainToSignal = null;
                lock (_bufferGate)
                {
                    if (_inFlightCaptureCallbacks > 0)
                    {
                        _inFlightCaptureCallbacks--;
                    }

                    if (_inFlightCaptureCallbacks == 0)
                    {
                        drainToSignal = _warmDrainTcs;
                    }
                }

                drainToSignal?.TrySetResult();
            }
        }
    }

    private void OnRecordingStopped(object? sender, AudioRecorderStopped args)
    {
        var scheduleWarmCleanup = false;
        long warmGeneration = 0;
        TaskCompletionSource<Exception?>? stoppedTcs;
        TaskCompletionSource? drainToSignal = null;
        lock (_bufferGate)
        {
            _backendStopped = true;
            stoppedTcs = _recordingStopped;
            if (_state is CaptureState.Capturing or CaptureState.DrainingToWarm && !_clientRequestedStop)
            {
                _unexpectedStop = true;
                _isCapturing = false;
                drainToSignal = _warmDrainTcs;
            }
            else if (_state == CaptureState.WarmIdle && !_clientRequestedStop)
            {
                scheduleWarmCleanup = true;
                warmGeneration = _warmGeneration;
            }
        }

        drainToSignal?.TrySetResult();
        stoppedTcs?.TrySetResult(args.Exception);
        if (scheduleWarmCleanup)
        {
            _ = Task.Run(() => TearDownWarmIfGenerationMatchesAsync(warmGeneration));
        }
    }

    private void AppendPreRollLocked(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return;
        }

        if (samples.Length >= PreRollCapacitySamples)
        {
            samples[^PreRollCapacitySamples..].CopyTo(_preRollRing);
            _preRollWriteIndex = 0;
            _preRollCount = PreRollCapacitySamples;
            return;
        }

        var firstPart = Math.Min(samples.Length, PreRollCapacitySamples - _preRollWriteIndex);
        samples[..firstPart].CopyTo(_preRollRing.AsSpan(_preRollWriteIndex, firstPart));
        var remaining = samples.Length - firstPart;
        if (remaining > 0)
        {
            samples[firstPart..].CopyTo(_preRollRing.AsSpan(0, remaining));
        }

        _preRollWriteIndex = (_preRollWriteIndex + samples.Length) % PreRollCapacitySamples;
        _preRollCount = Math.Min(PreRollCapacitySamples, _preRollCount + samples.Length);
    }

    private void DrainPreRollToCapturedBytesLocked()
    {
        if (_preRollCount == 0)
        {
            _drainedPreRollSampleCount = 0;
            return;
        }

        var maxSamples = (int)Math.Min(_preRollCount, _maxCapturedBytes / sizeof(float));
        if (maxSamples <= 0)
        {
            ClearPreRollRingLocked();
            _drainedPreRollSampleCount = 0;
            return;
        }

        var start = (_preRollWriteIndex - maxSamples + PreRollCapacitySamples) % PreRollCapacitySamples;
        var firstPart = Math.Min(maxSamples, PreRollCapacitySamples - start);
        WriteSamplesAsLittleEndian(_preRollRing.AsSpan(start, firstPart));
        var remaining = maxSamples - firstPart;
        if (remaining > 0)
        {
            WriteSamplesAsLittleEndian(_preRollRing.AsSpan(0, remaining));
        }

        _drainedPreRollSampleCount = maxSamples;
        ClearPreRollRingLocked();
    }

    private void WriteSamplesAsLittleEndian(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return;
        }

        if (BitConverter.IsLittleEndian)
        {
            _capturedBytes.Write(MemoryMarshal.AsBytes(samples));
            return;
        }

        Span<byte> buffer = stackalloc byte[sizeof(float)];
        foreach (var sample in samples)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer, BitConverter.SingleToInt32Bits(sample));
            _capturedBytes.Write(buffer);
        }
    }

    private void ClearPreRollRingLocked()
    {
        Array.Clear(_preRollRing);
        _preRollWriteIndex = 0;
        _preRollCount = 0;
    }

    private float[] SnapshotSamplesLocked()
    {
        var byteCount = checked((int)_capturedBytes.Length);
        if (byteCount == 0 || !_capturedBytes.TryGetBuffer(out var buffer) || buffer.Array is null)
        {
            _capturedBytes.SetLength(0);
            _capturedBytes.Capacity = 0;
            return [];
        }

        try
        {
            return AudioSampleConverter.ConvertToMono16Khz(
                buffer.Array.AsSpan(buffer.Offset, byteCount),
                CaptureFormat).Samples;
        }
        finally
        {
            _capturedBytes.SetLength(0);
            _capturedBytes.Capacity = 0;
        }
    }

    private async ValueTask ReleaseRecorderAsync(IAudioRecorderSession recorder)
    {
        recorder.DataAvailable -= OnDataAvailable;
        recorder.Stopped -= OnRecordingStopped;
        await recorder.DisposeAsync().ConfigureAwait(false);
    }

    private void ClearSession()
    {
        TaskCompletionSource? drainToSignal;
        lock (_bufferGate)
        {
            _state = CaptureState.Cold;
            _isCapturing = false;
            _inFlightCaptureCallbacks = 0;
            drainToSignal = _warmDrainTcs;
            _warmDrainTcs = null;
            _recorder = null;
            _recordingStopped = null;
            _request = null;
            _warmDeviceId = null;
            _drainedPreRollSampleCount = 0;
            ClearPreRollRingLocked();
            _exactZeroRunSamples = 0;
            _exactZeroSinceTakeStart = true;
            _sawNonZeroPostPreRoll = false;
            _clientRequestedStop = false;
            _backendStopped = false;
            _unexpectedStop = false;
            _warmGeneration++;
            Volatile.Write(ref _backstopTriggered, 0);
            Volatile.Write(ref _backstopSignalled, 0);
        }

        drainToSignal?.TrySetResult();
    }

    private static AudioOperationResult Failure(AppErrorCode code, bool canRetry) => new(
        Succeeded: false,
        new AppError(code, AppErrorStage.AudioCapture, canRetry));

    private static CapturedAudio EmptyFailure(AppErrorCode code) => new(
        new DictationSessionId(Guid.Empty),
        ReadOnlyMemory<float>.Empty,
        AudioSampleConverter.TargetSampleRate,
        Channels: 1,
        AudioCaptureOutcome.Interrupted,
        new AppError(code, AppErrorStage.AudioCapture, CanRetry: false));

    private static CapturedAudio Interrupted(
        DictationSessionId sessionId,
        float[] samples,
        int drainedPreRollSampleCount = 0) => new(
        sessionId,
        samples,
        AudioSampleConverter.TargetSampleRate,
        Channels: 1,
        AudioCaptureOutcome.Interrupted,
        new AppError(
            AppErrorCode.AudioDeviceLost,
            AppErrorStage.AudioCapture,
            CanRetry: samples.Length > 0),
        DrainedPreRollSampleCount: drainedPreRollSampleCount);
}
