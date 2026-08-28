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
/// EXISTS TO DECIDE WHETHER WARMING IS WORTH BUILDING AT ALL. Holding a microphone open between
/// dictations only helps if OPENING is the slow half. Nobody has measured which half is slow, and
/// the answer decides three things at once: whether the feature buys anything, whether it can be
/// built the way it is described, and whether the privacy question it raises is worth asking.
///
/// A SPLIT, NOT A TOTAL. The total is already known and is not the question. Two numbers that add up
/// to it is the only shape that answers "which half would warming remove".
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
