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
