using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;

namespace EnviousWispr.Core.Audio;

public readonly record struct AudioDeviceId(string Value);

public sealed record AudioDeviceInfo(
    AudioDeviceId Id,
    string DisplayName,
    bool IsDefault,
    bool IsActive);

public enum AudioDeviceChangeKind
{
    Added,
    Removed,
    StateChanged,
    DefaultChanged,
}

public sealed record AudioDeviceChange(
    AudioDeviceId Id,
    AudioDeviceChangeKind Kind,
    bool AffectsCapture);

public sealed record AudioCaptureRequest(
    DictationSessionId SessionId,
    AudioDeviceId? DeviceId = null);

public readonly record struct AudioLevel(float Peak, float RootMeanSquare)
{
    public static AudioLevel Silent { get; } = new(0, 0);
}

public sealed record AudioOperationResult(bool Succeeded, AppError? Error = null);

public sealed record AudioSnapshot(
    DictationSessionId SessionId,
    ReadOnlyMemory<float> Samples,
    int SampleRate,
    int Channels);

/// <summary>
/// Reports how long the last start spent opening the device versus starting the stream.
/// </summary>
/// <remarks>
/// EXISTED TO DECIDE WHETHER WARMING IS WORTH BUILDING AT ALL, AND IT HAS NOW DECIDED IT. Holding a
/// microphone open between dictations only helps if OPENING is the slow half. Measured on the
/// Windows development machine against a Logitech BRIO on 2026-08-30, ten consecutive starts:
///
///   device open   median 0 ms, mean 1.5 ms, worst 15 ms
///   stream start  median 13 ms, mean 13.4 ms, worst 20 ms
///
/// ON THIS EVIDENCE OPENING IS FREE, SO WARMING BUYS ALMOST NOTHING. The half a warm device would
/// remove is the one already costing about a millisecond and a half. Fifteen milliseconds in total
/// is a fraction of a syllable, so holding a microphone open permanently would trade a
/// microphone-in-use light that never goes out for a saving nobody can perceive.
///
/// WHAT THE EVIDENCE IS NOT, STATED BECAUSE A NUMBER READS AS SETTLED AND THIS ONE IS NOT. It is one
/// USB microphone on one machine, ten starts in a row with the device warm from the run before, and
/// the second number stops when the stream START RETURNS rather than when the first audio buffer
/// arrives. A continuously warm stream could in principle remove both calls, and none of this says
/// anything about a Bluetooth headset, a laptop's built-in microphone, or a device left idle for an
/// hour. Treat it as reason not to build warming NEXT, not as a closed question: the measurement
/// that would close it is key press to first buffer, cold and after an idle, across those devices.
///
/// IT DOES NOT SETTLE PRE-ROLL, which is a different question. People begin speaking slightly before
/// they press, and a ring buffer would help with that however fast the device opens. What this rules
/// out is warming as the reason to build one.
///
/// A SPLIT, NOT A TOTAL. The total was already known and was never the question. Two numbers that
/// add up to it is the only shape that answers "which half would warming remove".
/// </remarks>
public interface ICaptureStartTimings
{
    /// <summary>Milliseconds spent opening the device on the last start, or null if none has run.</summary>
    long? LastDeviceOpenMilliseconds { get; }

    /// <summary>Milliseconds spent starting the stream on the last start, or null if none has run.</summary>
    long? LastStreamStartMilliseconds { get; }
}

public interface IAudioSnapshotSource
{
    AudioSnapshot? GetSnapshot(TimeSpan maximumDuration);
}

public interface IAudioCapture : IAsyncDisposable
{
    event EventHandler<AudioLevel>? LevelChanged;

    bool IsCapturing { get; }

    Task<AudioOperationResult> StartAsync(
        AudioCaptureRequest request,
        CancellationToken cancellationToken = default);

    Task<CapturedAudio> StopAsync(CancellationToken cancellationToken = default);

    Task<AudioOperationResult> CancelAsync(CancellationToken cancellationToken = default);
}

public interface IAudioDeviceCatalog : IDisposable
{
    event EventHandler<AudioDeviceChange>? DevicesChanged;

    Task<IReadOnlyList<AudioDeviceInfo>> GetCaptureDevicesAsync(
        CancellationToken cancellationToken = default);
}
