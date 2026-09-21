using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Presentation;

namespace EnviousWispr.Presentation;

/// <summary>How a microphone test ended.</summary>
public enum MicrophoneTestOutcome
{
    /// <summary>One is already listening; the press was ignored.</summary>
    AlreadyRunning,

    /// <summary>A dictation is recording; a test would fail it or be failed by it.</summary>
    RecordingInProgress,

    /// <summary>Windows would not open the device.</summary>
    DeviceWouldNotOpen,

    /// <summary>The device stopped part way through: unplugged, or taken by another app.</summary>
    StoppedPartWay,

    /// <summary>Something with a better claim on the microphone stopped the test.</summary>
    Cancelled,

    /// <summary>The test ran its course; <see cref="MicrophoneTestResult.Verdict"/> says what arrived.</summary>
    Completed,
}

/// <param name="Verdict">One sentence about what arrived, for a completed test; null otherwise.</param>
public sealed record MicrophoneTestResult(MicrophoneTestOutcome Outcome, string? Verdict = null);

/// <summary>One meter frame from a running test: the loudest level in its interval, normalised for drawing.</summary>
/// <param name="TestId">Which test it belongs to; a frame from a test that has ended is not drawn.</param>
public readonly record struct MicrophoneTestFrame(int TestId, float Level);

/// <summary>Opens the microphone for a moment and says what actually arrives, without the page.</summary>
/// <remarks>
/// THIS IS THE PAGE WHERE SOMEBODY CONFIRMS THEIR MICROPHONE WORKS, AND IT COULD NOT TELL THEM. It
/// named a device and stopped, so an app receiving pure digital silence looked exactly like one that
/// was working. That is not hypothetical: it happened on the development machine, the meter sat at
/// its floor for seventy frames, nothing transcribed, and it took a day of measuring to find. A
/// person would have seen it here in three seconds.
///
/// A TEST AND A DICTATION MUST NOT BOTH OPEN THE MICROPHONE. A dictation outranks a test, always:
/// somebody who presses their record key wants to dictate, and a test still holding the device would
/// either fail their recording or be failed by it. So a test refuses to start while a recording is
/// running, and a recording that starts cancels a test through <see cref="Cancel"/>.
///
/// ONE FRAME PER METER INTERVAL, NOT ONE PER AUDIO PACKET. Capture reports a level about two hundred
/// times a second; posting every one to a UI thread kept its queue permanently busy on a real machine
/// and no frame was ever drawn. The sampler keeps the loudest of each interval, and the frame carries
/// the test it belongs to, because a frame already posted outlives the unsubscribe and could relight
/// the meter after the test had cleared it.
///
/// A TEST RUNS INSIDE THE PRESENTATION'S GATE. The exit must not leave a device open behind it: a
/// test takes a lease, listens under the closing token as well as its own, and a press after the
/// close, or a test the close stops, ends <see cref="MicrophoneTestOutcome.Cancelled"/> with the
/// capture disposed - the same outcome a recording's better claim produces.
/// </remarks>
public sealed class MicrophoneTestController
{
    /// <summary>How long a test listens for.</summary>
    /// <remarks>
    /// LONG ENOUGH TO SAY SOMETHING AND SHORT ENOUGH THAT NOBODY WAITS. Three seconds is about one
    /// sentence, which is what somebody naturally does when a button says to speak.
    /// </remarks>
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(3);

    private readonly Func<IMicrophoneTestCapture> _openCapture;
    private readonly TimeProvider _clock;
    private readonly PresentationAdmission _admission;
    private readonly object _lock = new();
    private CancellationTokenSource? _running;
    private int _testId;

    /// <param name="openCapture">Makes a fresh capture for each test; the controller disposes it.</param>
    /// <param name="clock">The clock the listening interval is measured on. A test drives this.</param>
    /// <param name="admission">The presentation's gate; one of this controller's own, never closed, when it stands alone.</param>
    public MicrophoneTestController(Func<IMicrophoneTestCapture> openCapture, TimeProvider? clock = null, PresentationAdmission? admission = null)
    {
        ArgumentNullException.ThrowIfNull(openCapture);
        _openCapture = openCapture;
        _clock = clock ?? TimeProvider.System;
        _admission = admission ?? new PresentationAdmission();
    }

    /// <summary>Raised on the capture's thread, at most once per meter interval, while a test listens.</summary>
    public event EventHandler<MicrophoneTestFrame>? Frame;

    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return _running is not null;
            }
        }
    }

    /// <summary>Whether a frame's test is the one still running. A late frame answers false and is not drawn.</summary>
    public bool IsCurrent(int testId)
    {
        lock (_lock)
        {
            return _running is not null && testId == _testId;
        }
    }

    /// <summary>Stops a test, because something with a better claim wants the device. Safe when none is running.</summary>
    public void Cancel()
    {
        CancellationTokenSource? running;
        lock (_lock)
        {
            running = _running;
        }

        try
        {
            running?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The test finished between the read and the call, which is the outcome asked for.
        }
    }

    /// <summary>Runs one test on the chosen device, or says why it will not.</summary>
    /// <param name="recordingInProgress">Whether a dictation holds, or is about to hold, the microphone.</param>
    public async Task<MicrophoneTestResult> RunAsync(AudioDeviceId? device, bool recordingInProgress)
    {
        if (!_admission.TryEnter(out var lease))
        {
            return new MicrophoneTestResult(MicrophoneTestOutcome.Cancelled);
        }

        using (lease)
        {
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lease.Closing);
            int testId;
            lock (_lock)
            {
                if (_running is not null)
                {
                    cancellation.Dispose();
                    return new MicrophoneTestResult(MicrophoneTestOutcome.AlreadyRunning);
                }

                if (recordingInProgress)
                {
                    cancellation.Dispose();
                    return new MicrophoneTestResult(MicrophoneTestOutcome.RecordingInProgress);
                }

                _running = cancellation;
                testId = ++_testId;
            }

            try
            {
                return await ListenAsync(testId, device, cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new MicrophoneTestResult(MicrophoneTestOutcome.Cancelled);
            }
            finally
            {
                lock (_lock)
                {
                    // THE ID MOVES ON AS THE TEST ENDS, so a frame posted in its last moments is
                    // refused by IsCurrent whichever side of the unsubscribe it was raised on.
                    _running = null;
                    _testId++;
                }

                cancellation.Dispose();
            }
        }
    }

    private async Task<MicrophoneTestResult> ListenAsync(int testId, AudioDeviceId? device, CancellationToken cancellationToken)
    {
        await using var capture = _openCapture();
        var meterClock = _clock.GetTimestamp();
        var meterFrames = new MicrophoneMeterFrameSampler();
        capture.LevelChanged += OnLevel;
        try
        {
            // THE TOKEN GOES ALL THE WAY IN. A recording cancels a running test, and a test that does
            // not forward its own cancellation would keep opening a device the app has already decided
            // somebody else should have.
            var started = await capture
                .StartAsync(new AudioCaptureRequest(DictationSessionId.Create(), device), cancellationToken)
                .ConfigureAwait(false);
            if (!started.Succeeded)
            {
                return new MicrophoneTestResult(MicrophoneTestOutcome.DeviceWouldNotOpen);
            }

            await Task.Delay(Duration, _clock, cancellationToken).ConfigureAwait(false);
            // STOPPING IS NOT CANCELLED, DELIBERATELY, AND IT IS THE ONE EXCEPTION. A cancelled stop
            // leaves the device open, which is the opposite of what a cancel is for: the whole reason a
            // recording cancels a test is to take the microphone back.
            var captured = await capture.StopAsync(CancellationToken.None).ConfigureAwait(false);

            // WHAT THE STOP SAID, BEFORE WHAT THE PACKETS SAID. A device that vanished after one loud
            // packet leaves counts that read as healthy, so throwing away the outcome let an
            // interrupted test report a working microphone.
            if (captured.Outcome != AudioCaptureOutcome.Completed)
            {
                return new MicrophoneTestResult(MicrophoneTestOutcome.StoppedPartWay);
            }

            // THE ROOT-MEAN-SQUARE, NOT THE PEAK, because that is the number the recording meter is
            // driven from. A verdict read off the peak could call a microphone healthy while the meter
            // it is meant to explain sits flat.
            return new MicrophoneTestResult(
                MicrophoneTestOutcome.Completed,
                MicrophoneTestVerdict.For(capture.LastPacketCount, capture.LastSilentPacketCount, capture.LastRootMeanSquare));
        }
        finally
        {
            capture.LevelChanged -= OnLevel;
        }

        void OnLevel(object? sender, AudioLevel level)
        {
            if (!meterFrames.TryTakeFrame(level.RootMeanSquare, _clock.GetElapsedTime(meterClock), out var loudest))
            {
                return;
            }

            Frame?.Invoke(this, new MicrophoneTestFrame(testId, RecordingLevelHistory.Normalize(loudest)));
        }
    }
}
