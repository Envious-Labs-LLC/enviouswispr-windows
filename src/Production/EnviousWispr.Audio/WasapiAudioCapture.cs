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
    public static readonly TimeSpan WiredFirstPacketTimeout = TimeSpan.FromMilliseconds(800);
    public static readonly TimeSpan BluetoothFirstPacketTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan BluetoothSettlingProbeDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan BluetoothColdStartSettlingDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan WarmRecentPacketWindow = TimeSpan.FromSeconds(2);
    private const int PreRollCapacitySamples = 8_000;
    private const int MuteProbeIntervalSamples = 8_000;

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
    private readonly Func<AudioDeviceId?, bool> _isBluetoothDevice;
    private readonly TimeSpan _stopTimeout;
    private readonly long _maxCapturedBytes;
    private readonly TimeProvider _timeProvider;
    private IAudioRecorderSession? _recorder;
    private TaskCompletionSource<Exception?>? _recordingStopped;
    private TaskCompletionSource? _warmDrainTcs;
    private AudioCaptureRequest? _request;
    private AudioDeviceId? _warmDeviceId;
    private ITimer? _warmTeardownTimer;
    private ITimer? _firstPacketWatchdogTimer;
    private CaptureState _state = CaptureState.Cold;
    private int _warmPolicy = (int)MicrophoneWarmPolicy.Off;
    private long _warmIdleSinceTimestamp;
    private long? _lastWarmPacketTimestamp;
    private long _warmGeneration;
    private long _captureAttemptGeneration;
    private int _inFlightCaptureCallbacks;
    private int _preRollWriteIndex;
    private int _preRollCount;
    private int _drainedPreRollSampleCount;
    private int _exactZeroRunSamples;
    private bool _exactZeroSinceTakeStart = true;
    private bool _sawNonZeroPostPreRoll;
    private bool _lastCaptureWasWholeZero;
    private bool _isEndpointMuted;
    private bool _lastCaptureWasEndpointMuted;
    private AppErrorCode? _stallErrorCode;
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
            TimeProvider.System,
            WasapiDeviceCatalog.IsBluetoothDevice)
    {
    }

    internal WasapiAudioCapture(
        IAudioRecorderFactory recorderFactory,
        TimeSpan stopTimeout,
        TimeSpan? hardCaptureBackstop = null,
        TimeProvider? timeProvider = null,
        Func<AudioDeviceId?, bool>? isBluetoothDevice = null)
    {
        ArgumentNullException.ThrowIfNull(recorderFactory);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(stopTimeout, TimeSpan.Zero);
        var effectiveBackstop = hardCaptureBackstop ?? DefaultHardCaptureBackstop;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(effectiveBackstop, TimeSpan.Zero);

        _recorderFactory = recorderFactory;
        _isBluetoothDevice = isBluetoothDevice ?? (static _ => false);
        _stopTimeout = stopTimeout;
        _timeProvider = timeProvider ?? TimeProvider.System;
        var requestedSamples = effectiveBackstop.TotalSeconds * AudioSampleConverter.TargetSampleRate;
        var maxSamples = requestedSamples >= (int.MaxValue / sizeof(float))
            ? int.MaxValue / sizeof(float)
            : Math.Max(1L, (long)Math.Ceiling(requestedSamples));
        _maxCapturedBytes = maxSamples * sizeof(float);
    }

    public event EventHandler<AudioLevel>? LevelChanged;

    public event EventHandler<AudioCaptureStopped>? UnexpectedStop;

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

    public bool IsEndpointMuted => Volatile.Read(ref _isEndpointMuted);

    public bool LastCaptureWasEndpointMuted => Volatile.Read(ref _lastCaptureWasEndpointMuted);

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

                return new ExactZeroRun(_exactZeroRunSamples, _exactZeroSinceTakeStart, Volatile.Read(ref _isEndpointMuted));
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

    public async Task RefreshWarmAsync(
        AudioDeviceId? deviceId,
        bool isBluetooth = false,
        CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed || _state != CaptureState.WarmIdle)
            {
                return;
            }

            if (_warmDeviceId == deviceId && !_backendStopped && !_unexpectedStop)
            {
                return;
            }

            await TearDownWarmCoreLockedAsync().ConfigureAwait(false);
            var warmPolicy = WarmPolicy;
            if (_disposed || warmPolicy == MicrophoneWarmPolicy.Off)
            {
                return;
            }

            IAudioRecorderSession? recorder = null;
            try
            {
                recorder = await _recorderFactory.CreateAsync(deviceId, cancellationToken).ConfigureAwait(false);
                lock (_bufferGate)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    ClearPreRollRingLocked();
                    _capturedBytes.SetLength(0);
                    _capturedBytes.Capacity = 0;
                    _recordingStopped = new TaskCompletionSource<Exception?>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _recorder = recorder;
                    _warmDeviceId = deviceId;
                    _clientRequestedStop = false;
                    _backendStopped = false;
                    _unexpectedStop = false;
                    _lastWarmPacketTimestamp = null;
                    _state = CaptureState.WarmIdle;
                    _warmGeneration++;
                    _warmIdleSinceTimestamp = _timeProvider.GetTimestamp();
                }

                recorder.DataAvailable += OnDataAvailable;
                recorder.Stopped += OnRecordingStopped;
                recorder.Start();
                Volatile.Write(ref _isEndpointMuted, SafeIsEndpointMuted(recorder));
                ScheduleWarmTeardownForPolicyLocked(warmPolicy);
                recorder = null;
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or COMException or OperationCanceledException)
            {
                if (recorder is not null)
                {
                    try
                    {
                        await ReleaseRecorderAsync(recorder).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                    }
                }

                ClearSession();
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

        var samples = DecodeCapturedFloats(bytes, _recorder?.Format ?? CaptureFormat);
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
            : DecodeCapturedFloats(tailBytes, _recorder?.Format ?? CaptureFormat);
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
            CancelFirstPacketWatchdogTimer();
            var isBluetooth = request.IsBluetooth || _isBluetoothDevice(request.DeviceId);
            if (_state == CaptureState.WarmIdle)
            {
                var reusedWarm = false;
                var hadRecentWarmPacket = false;
                IAudioRecorderSession? warmRecorder = null;
                long attemptGeneration = 0;
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
                        _stallErrorCode = null;
                        Volatile.Write(ref _lastCaptureWasWholeZero, false);
                        Volatile.Write(ref _lastCaptureWasEndpointMuted, false);
                        _request = request;
                        _clientRequestedStop = false;
                        _unexpectedStop = false;
                        Volatile.Write(ref _backstopTriggered, 0);
                        Volatile.Write(ref _backstopSignalled, 0);
                        _warmGeneration++;
                        attemptGeneration = ++_captureAttemptGeneration;
                        hadRecentWarmPacket = _lastWarmPacketTimestamp is { } lastWarmTimestamp &&
                            _timeProvider.GetElapsedTime(lastWarmTimestamp) <= WarmRecentPacketWindow;
                        _state = CaptureState.Capturing;
                        _isCapturing = true;
                        warmRecorder = _recorder;
                        reusedWarm = true;
                    }
                }

                if (reusedWarm)
                {
                    Volatile.Write(ref _isEndpointMuted, SafeIsEndpointMuted(warmRecorder));
                    if (!hadRecentWarmPacket)
                    {
                        ArmFirstPacketWatchdog(
                            attemptGeneration,
                            isBluetooth ? BluetoothFirstPacketTimeout : WiredFirstPacketTimeout,
                            request,
                            isBluetooth,
                            allowBluetoothRetry: false);
                    }

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
            Volatile.Write(ref _lastCaptureWasEndpointMuted, false);

            var maxAttempts = isBluetooth ? 2 : 1;
            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
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
                catch (UnauthorizedAccessException)
                {
                    return Failure(AppErrorCode.AccessDenied, canRetry: true);
                }
                catch (Exception exception) when (exception is InvalidOperationException or COMException)
                {
                    if (attempt + 1 < maxAttempts)
                    {
                        await Task.Delay(BluetoothColdStartSettlingDelay, _timeProvider, cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    return Failure(AppErrorCode.AudioDeviceUnavailable, canRetry: true);
                }
                finally
                {
                    openTimer.Stop();
                    _lastDeviceOpenMilliseconds = openTimer.ElapsedMilliseconds;
                }

                long attemptGeneration;
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
                    _stallErrorCode = null;
                    _lastWarmPacketTimestamp = null;
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
                    attemptGeneration = ++_captureAttemptGeneration;
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

                    // If Bluetooth SCO initialization immediately stopped before delivering any packet,
                    // allow one bounded teardown + reopen settling retry before failing.
                    if (isBluetooth && attempt + 1 < maxAttempts && _backendStopped && Volatile.Read(ref _packets) == 0)
                    {
                        await ReleaseRecorderAsync(recorder).ConfigureAwait(false);
                        ClearSession();
                        await Task.Delay(BluetoothColdStartSettlingDelay, _timeProvider, cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    Volatile.Write(ref _isEndpointMuted, SafeIsEndpointMuted(recorder));
                    if (Volatile.Read(ref _packets) == 0 && !_backendStopped && !_unexpectedStop)
                    {
                        var canRetryInWatchdog = isBluetooth && attempt == 0;
                        var initialTimeout = canRetryInWatchdog
                            ? BluetoothSettlingProbeDelay
                            : (isBluetooth
                                ? BluetoothFirstPacketTimeout - BluetoothSettlingProbeDelay
                                : WiredFirstPacketTimeout);
                        ArmFirstPacketWatchdog(
                            attemptGeneration,
                            initialTimeout,
                            request,
                            isBluetooth,
                            allowBluetoothRetry: canRetryInWatchdog);
                    }

                    return new AudioOperationResult(Succeeded: true);
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or ArgumentException or COMException)
                {
                    await ReleaseRecorderAsync(recorder).ConfigureAwait(false);
                    ClearSession();
                    if (attempt + 1 < maxAttempts && exception is InvalidOperationException or COMException)
                    {
                        await Task.Delay(BluetoothColdStartSettlingDelay, _timeProvider, cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    return Failure(AppErrorCode.AudioDeviceUnavailable, canRetry: true);
                }
            }

            return Failure(AppErrorCode.AudioDeviceUnavailable, canRetry: true);
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
            if (_state != CaptureState.Capturing || _request is null)
            {
                return EmptyFailure(AppErrorCode.InvalidTransition);
            }

            if (_recorder is null)
            {
                if (_unexpectedStop)
                {
                    var abortedRequest = _request;
                    var abortedError = _stallErrorCode ?? AppErrorCode.AudioDeviceLost;
                    ClearSession();
                    return Interrupted(abortedRequest.SessionId, [], 0, abortedError);
                }

                return EmptyFailure(AppErrorCode.InvalidTransition);
            }

            if (_recordingStopped is null)
            {
                return EmptyFailure(AppErrorCode.InvalidTransition);
            }

            CancelFirstPacketWatchdogTimer();
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
                        var isWholeZero = !_sawNonZeroPostPreRoll && (_packets > 0 || warmSamples.Length > warmDrainedPreRoll);
                        Volatile.Write(ref _lastCaptureWasWholeZero, isWholeZero);
                        Volatile.Write(
                            ref _lastCaptureWasEndpointMuted,
                            isWholeZero && Volatile.Read(ref _isEndpointMuted));
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
            var errorCode = _stallErrorCode ?? AppErrorCode.AudioDeviceLost;
            float[] samples;
            int coldDrainedPreRoll;
            lock (_bufferGate)
            {
                samples = SnapshotSamplesLocked();
                coldDrainedPreRoll = _drainedPreRollSampleCount;
                var isWholeZero = !_sawNonZeroPostPreRoll && (_packets > 0 || samples.Length > coldDrainedPreRoll);
                Volatile.Write(ref _lastCaptureWasWholeZero, isWholeZero);
                Volatile.Write(
                    ref _lastCaptureWasEndpointMuted,
                    isWholeZero && Volatile.Read(ref _isEndpointMuted));
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
                ? Interrupted(request.SessionId, samples, coldDrainedPreRoll, errorCode)
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
        CancelFirstPacketWatchdogTimer();
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
        CancelFirstPacketWatchdogTimer();
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

            var format = _recorder?.Format ?? CaptureFormat;
            var hitBackstop = false;
            var wroteAudio = false;
            var probeMute = false;
            IAudioRecorderSession? activeRecorderForMute = null;
            DictationSessionId backstopSession = default;
            var levelPeak = 0f;
            var levelRms = 0f;

            if (IsNativeMono16KhzFloat(format))
            {
                if (args.Bytes.Length % sizeof(float) != 0)
                {
                    throw new ArgumentException("Audio data must contain complete frames.", nameof(args));
                }

                var sampleCountTotal = args.Bytes.Length / sizeof(float);
                if (args.IsSilent)
                {
                    lock (_bufferGate)
                    {
                        if (!enteredDuringCapture)
                        {
                            if (_state is CaptureState.WarmIdle or CaptureState.DrainingToWarm)
                            {
                                if (!_backendStopped && !_unexpectedStop)
                                {
                                    _lastWarmPacketTimestamp = _timeProvider.GetTimestamp();
                                    AppendSilentPreRollLocked(sampleCountTotal);
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

                        var firstLivePacket = Interlocked.Increment(ref _packets) == 1;
                        if (firstLivePacket)
                        {
                            CancelFirstPacketWatchdogTimer();
                        }

                        Interlocked.Increment(ref _silentPackets);
                        var remaining = _maxCapturedBytes - _capturedBytes.Length;
                        var writeCount = args.Bytes.Length;
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
                            WriteSilentBytesLocked(writeCount);
                            wroteAudio = true;
                            var writtenSamples = writeCount / sizeof(float);
                            var previousZero = _exactZeroRunSamples;
                            _exactZeroRunSamples += writtenSamples;
                            if (_exactZeroRunSamples / MuteProbeIntervalSamples != previousZero / MuteProbeIntervalSamples ||
                                firstLivePacket)
                            {
                                probeMute = true;
                                activeRecorderForMute = _recorder;
                            }
                        }

                        if (hitBackstop)
                        {
                            backstopSession = _request.SessionId;
                            Volatile.Write(ref _backstopTriggered, 1);
                        }
                    }
                }
                else
                {
                    var nativeFloats = MemoryMarshal.Cast<byte, float>(args.Bytes.Span);
                    MeasureNativeMono16KhzFloat(nativeFloats, out levelPeak, out levelRms);

                    lock (_bufferGate)
                    {
                        if (!enteredDuringCapture)
                        {
                            if (_state is CaptureState.WarmIdle or CaptureState.DrainingToWarm)
                            {
                                if (!_backendStopped && !_unexpectedStop)
                                {
                                    _lastWarmPacketTimestamp = _timeProvider.GetTimestamp();
                                    AppendClampedPreRollLocked(nativeFloats);
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

                        if (Interlocked.Increment(ref _packets) == 1)
                        {
                            CancelFirstPacketWatchdogTimer();
                        }

                        var remaining = _maxCapturedBytes - _capturedBytes.Length;
                        var writeCount = args.Bytes.Length;
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
                            WriteClampedNativeFloatsLocked(nativeFloats[..(writeCount / sizeof(float))]);
                            wroteAudio = true;
                            if (levelPeak > _peak)
                            {
                                _peak = levelPeak;
                            }

                            if (levelRms > _rms)
                            {
                                _rms = levelRms;
                            }

                            var previousZero = _exactZeroRunSamples;
                            foreach (var rawSample in nativeFloats[..(writeCount / sizeof(float))])
                            {
                                var sample = Math.Clamp(rawSample, -1f, 1f);
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

                            if (_exactZeroRunSamples > 0 &&
                                _exactZeroRunSamples / MuteProbeIntervalSamples != previousZero / MuteProbeIntervalSamples)
                            {
                                probeMute = true;
                                activeRecorderForMute = _recorder;
                            }
                        }

                        if (hitBackstop)
                        {
                            backstopSession = _request.SessionId;
                            Volatile.Write(ref _backstopTriggered, 1);
                        }
                    }
                }
            }
            else
            {
                var bytes = args.IsSilent ? new byte[args.Bytes.Length] : args.Bytes.ToArray();
                var converted = AudioSampleConverter.ConvertToMono16Khz(bytes, format);
                levelPeak = converted.Peak;
                levelRms = converted.RootMeanSquare;
                lock (_bufferGate)
                {
                    if (!enteredDuringCapture)
                    {
                        if (_state is CaptureState.WarmIdle or CaptureState.DrainingToWarm)
                        {
                            if (!_backendStopped && !_unexpectedStop)
                            {
                                _lastWarmPacketTimestamp = _timeProvider.GetTimestamp();
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

                    if (Interlocked.Increment(ref _packets) == 1)
                    {
                        CancelFirstPacketWatchdogTimer();
                    }

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
                            probeMute = true;
                            activeRecorderForMute = _recorder;
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
            }

            if (probeMute && activeRecorderForMute is not null)
            {
                Volatile.Write(ref _isEndpointMuted, SafeIsEndpointMuted(activeRecorderForMute));
            }

            if (wroteAudio && _state == CaptureState.Capturing)
            {
                LevelChanged?.Invoke(this, new AudioLevel(levelPeak, levelRms));
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
        CancelFirstPacketWatchdogTimer();
        var scheduleWarmCleanup = false;
        long warmGeneration = 0;
        DictationSessionId? unexpectedSessionId = null;
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
                if (_state == CaptureState.Capturing && _request is { } activeRequest)
                {
                    unexpectedSessionId = activeRequest.SessionId;
                }
            }
            else if (_state == CaptureState.WarmIdle && !_clientRequestedStop)
            {
                scheduleWarmCleanup = true;
                warmGeneration = _warmGeneration;
            }
        }

        drainToSignal?.TrySetResult();
        stoppedTcs?.TrySetResult(args.Exception);
        if (unexpectedSessionId is { } faultedSession)
        {
            UnexpectedStop?.Invoke(
                this,
                new AudioCaptureStopped(
                    faultedSession,
                    new AppError(
                        AppErrorCode.AudioDeviceLost,
                        AppErrorStage.AudioCapture,
                        CanRetry: true)));
        }

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
            return DecodeCapturedFloats(
                buffer.Array.AsSpan(buffer.Offset, byteCount),
                _recorder?.Format ?? CaptureFormat);
        }
        finally
        {
            _capturedBytes.SetLength(0);
            _capturedBytes.Capacity = 0;
        }
    }

    private static bool IsNativeMono16KhzFloat(AudioBufferFormat format) =>
        BitConverter.IsLittleEndian &&
        format.SampleRate == AudioSampleConverter.TargetSampleRate &&
        format.Channels == 1 &&
        format.Encoding == AudioSampleEncoding.Ieee754Single;

    private static float[] DecodeCapturedFloats(ReadOnlySpan<byte> bytes, AudioBufferFormat format)
    {
        if (bytes.IsEmpty)
        {
            return [];
        }

        if (IsNativeMono16KhzFloat(format))
        {
            var samples = new float[bytes.Length / sizeof(float)];
            bytes.CopyTo(MemoryMarshal.AsBytes(samples.AsSpan()));
            return samples;
        }

        return AudioSampleConverter.ConvertToMono16Khz(bytes, format).Samples;
    }

    private static void MeasureNativeMono16KhzFloat(
        ReadOnlySpan<float> samples,
        out float peak,
        out float rootMeanSquare)
    {
        if (samples.IsEmpty)
        {
            peak = 0f;
            rootMeanSquare = 0f;
            return;
        }

        double sumSquares = 0;
        peak = 0f;
        foreach (var raw in samples)
        {
            var clamped = Math.Clamp(raw, -1f, 1f);
            peak = Math.Max(peak, Math.Abs(clamped));
            sumSquares += clamped * clamped;
        }

        rootMeanSquare = (float)Math.Sqrt(sumSquares / samples.Length);
    }

    private void AppendSilentPreRollLocked(int sampleCount)
    {
        if (sampleCount <= 0)
        {
            return;
        }

        if (sampleCount >= PreRollCapacitySamples)
        {
            Array.Clear(_preRollRing);
            _preRollWriteIndex = 0;
            _preRollCount = PreRollCapacitySamples;
            return;
        }

        var firstPart = Math.Min(sampleCount, PreRollCapacitySamples - _preRollWriteIndex);
        Array.Clear(_preRollRing, _preRollWriteIndex, firstPart);
        var remaining = sampleCount - firstPart;
        if (remaining > 0)
        {
            Array.Clear(_preRollRing, 0, remaining);
        }

        _preRollWriteIndex = (_preRollWriteIndex + sampleCount) % PreRollCapacitySamples;
        _preRollCount = Math.Min(PreRollCapacitySamples, _preRollCount + sampleCount);
    }

    private void AppendClampedPreRollLocked(ReadOnlySpan<float> rawSamples)
    {
        if (rawSamples.IsEmpty)
        {
            return;
        }

        var tail = rawSamples.Length >= PreRollCapacitySamples
            ? rawSamples[^PreRollCapacitySamples..]
            : rawSamples;
        foreach (var sample in tail)
        {
            _preRollRing[_preRollWriteIndex] = Math.Clamp(sample, -1f, 1f);
            _preRollWriteIndex = (_preRollWriteIndex + 1) % PreRollCapacitySamples;
            _preRollCount = Math.Min(PreRollCapacitySamples, _preRollCount + 1);
        }
    }

    private void WriteClampedNativeFloatsLocked(ReadOnlySpan<float> rawSamples)
    {
        if (rawSamples.IsEmpty)
        {
            return;
        }

        if (BitConverter.IsLittleEndian)
        {
            var needsClamping = false;
            foreach (var sample in rawSamples)
            {
                if (!(sample is >= -1f and <= 1f))
                {
                    needsClamping = true;
                    break;
                }
            }

            if (!needsClamping)
            {
                _capturedBytes.Write(MemoryMarshal.AsBytes(rawSamples));
                return;
            }
        }

        Span<byte> scratch = stackalloc byte[sizeof(float)];
        foreach (var sample in rawSamples)
        {
            var clamped = Math.Clamp(sample, -1f, 1f);
            BinaryPrimitives.WriteInt32LittleEndian(scratch, BitConverter.SingleToInt32Bits(clamped));
            _capturedBytes.Write(scratch);
        }
    }

    private void WriteSilentBytesLocked(int byteCount)
    {
        Span<byte> zeros = stackalloc byte[1024];
        zeros.Clear();
        var remaining = byteCount;
        while (remaining > 0)
        {
            var chunk = Math.Min(remaining, zeros.Length);
            _capturedBytes.Write(zeros[..chunk]);
            remaining -= chunk;
        }
    }

    private static bool SafeIsEndpointMuted(IAudioRecorderSession? recorder)
    {
        if (recorder is null)
        {
            return false;
        }

        try
        {
            return recorder.IsEndpointMuted();
        }
        catch
        {
            return false;
        }
    }

    private void ArmFirstPacketWatchdog(
        long attemptGeneration,
        TimeSpan timeout,
        AudioCaptureRequest request,
        bool isBluetooth,
        bool allowBluetoothRetry)
    {
        CancelFirstPacketWatchdogTimer();
        _firstPacketWatchdogTimer = _timeProvider.CreateTimer(
            static state =>
            {
                var (self, gen, req, bt, allowRetry) =
                    ((WasapiAudioCapture, long, AudioCaptureRequest, bool, bool))state!;
                _ = self.OnFirstPacketWatchdogDueAsync(gen, req, bt, allowRetry);
            },
            (this, attemptGeneration, request, isBluetooth, allowBluetoothRetry),
            timeout,
            Timeout.InfiniteTimeSpan);
    }

    private void CancelFirstPacketWatchdogTimer()
    {
        var timer = Interlocked.Exchange(ref _firstPacketWatchdogTimer, null);
        timer?.Dispose();
    }

    internal async Task OnFirstPacketWatchdogDueAsync(
        long attemptGeneration,
        AudioCaptureRequest request,
        bool isBluetooth,
        bool allowBluetoothRetry)
    {
        try
        {
            await _transitionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        var stalledSession = false;
        var stallErrorCode = AppErrorCode.AudioDeviceUnavailable;
        IAudioRecorderSession? stalledRecorder = null;
        TaskCompletionSource<Exception?>? stoppedTcs = null;
        try
        {
            if (_disposed ||
                _state != CaptureState.Capturing ||
                _captureAttemptGeneration != attemptGeneration ||
                Volatile.Read(ref _packets) > 0 ||
                _unexpectedStop ||
                _clientRequestedStop)
            {
                return;
            }

            CancelFirstPacketWatchdogTimer();

            if (allowBluetoothRetry && isBluetooth)
            {
                var priorRecorder = _recorder;
                var priorStopped = _recordingStopped;
                var priorAlreadyStopped = _backendStopped;
                if (priorRecorder is not null)
                {
                    if (priorStopped is not null && !priorAlreadyStopped)
                    {
                        _clientRequestedStop = true;
                        try
                        {
                            priorRecorder.Stop();
                            await priorStopped.Task.WaitAsync(_stopTimeout, CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception exception) when (
                            exception is InvalidOperationException or COMException or TimeoutException)
                        {
                        }
                    }

                    try
                    {
                        await ReleaseRecorderAsync(priorRecorder).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or COMException)
                    {
                    }

                    lock (_bufferGate)
                    {
                        if (ReferenceEquals(_recorder, priorRecorder))
                        {
                            _recorder = null;
                            _recordingStopped = null;
                        }
                    }
                }

                await Task.Delay(BluetoothColdStartSettlingDelay, _timeProvider, CancellationToken.None)
                    .ConfigureAwait(false);
                if (_disposed || _captureAttemptGeneration != attemptGeneration)
                {
                    return;
                }

                try
                {
                    var retryRecorder = await _recorderFactory
                        .CreateAsync(request.DeviceId, CancellationToken.None)
                        .ConfigureAwait(false);
                    var abandoned = false;
                    lock (_bufferGate)
                    {
                        if (_disposed || _captureAttemptGeneration != attemptGeneration)
                        {
                            abandoned = true;
                        }
                        else
                        {
                            _recordingStopped = new TaskCompletionSource<Exception?>(
                                TaskCreationOptions.RunContinuationsAsynchronously);
                            _recorder = retryRecorder;
                            _clientRequestedStop = false;
                            _backendStopped = false;
                            _unexpectedStop = false;
                        }
                    }

                    if (abandoned)
                    {
                        await ReleaseRecorderAsync(retryRecorder).ConfigureAwait(false);
                        return;
                    }

                    retryRecorder.DataAvailable += OnDataAvailable;
                    retryRecorder.Stopped += OnRecordingStopped;
                    retryRecorder.Start();
                    Volatile.Write(ref _isEndpointMuted, SafeIsEndpointMuted(retryRecorder));

                    if (Volatile.Read(ref _packets) == 0 && !_backendStopped && !_unexpectedStop)
                    {
                        var remainingBudget = BluetoothFirstPacketTimeout - BluetoothSettlingProbeDelay;
                        if (remainingBudget > TimeSpan.Zero)
                        {
                            ArmFirstPacketWatchdog(
                                attemptGeneration,
                                remainingBudget,
                                request,
                                isBluetooth,
                                allowBluetoothRetry: false);
                        }
                    }

                    return;
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or ArgumentException or COMException or UnauthorizedAccessException)
                {
                    stallErrorCode = exception switch
                    {
                        UnauthorizedAccessException => AppErrorCode.AccessDenied,
                        ArgumentException => AppErrorCode.AudioFormatUnsupported,
                        _ => AppErrorCode.AudioDeviceUnavailable,
                    };
                }
            }

            lock (_bufferGate)
            {
                if (_disposed ||
                    _state != CaptureState.Capturing ||
                    _captureAttemptGeneration != attemptGeneration ||
                    Volatile.Read(ref _packets) > 0)
                {
                    return;
                }

                stalledSession = true;
                stalledRecorder = _recorder;
                stoppedTcs = _recordingStopped;
                _clientRequestedStop = true;
                _unexpectedStop = true;
                _backendStopped = true;
                _isCapturing = false;
                _stallErrorCode = stallErrorCode;
                _capturedBytes.SetLength(0);
                _capturedBytes.Capacity = 0;
                _drainedPreRollSampleCount = 0;
            }

            if (stalledRecorder is not null)
            {
                try
                {
                    stalledRecorder.Stop();
                }
                catch (Exception exception) when (exception is InvalidOperationException or COMException)
                {
                }
            }

            stoppedTcs?.TrySetResult(null);
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

        if (stalledSession)
        {
            UnexpectedStop?.Invoke(
                this,
                new AudioCaptureStopped(
                    request.SessionId,
                    new AppError(
                        stallErrorCode,
                        AppErrorStage.AudioCapture,
                        CanRetry: true)));
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
            _stallErrorCode = null;
            _isEndpointMuted = false;
            _warmGeneration++;
            _captureAttemptGeneration++;
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
        int drainedPreRollSampleCount = 0,
        AppErrorCode errorCode = AppErrorCode.AudioDeviceLost) => new(
        sessionId,
        samples,
        AudioSampleConverter.TargetSampleRate,
        Channels: 1,
        AudioCaptureOutcome.Interrupted,
        new AppError(
            errorCode,
            AppErrorStage.AudioCapture,
            CanRetry: samples.Length > 0 || errorCode == AppErrorCode.AudioDeviceUnavailable),
        DrainedPreRollSampleCount: drainedPreRollSampleCount);
}
