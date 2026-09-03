using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using System.Security.Cryptography;

namespace EnviousWispr.App;

internal sealed class PublicFixtureAudioCapture : IAudioCapture, IAudioSnapshotSource
{
    private const int RequiredSampleRate = 16_000;
    private static readonly HashSet<string> ReviewedFixtureHashes = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "0F56F001F964D2288851A5E4063781CB5793D25F1B4FD9B55607E79873B4B20C",
        "84DEFDC828EF59CEC10364354FBC284BC2CC683FDD4A5EDD5863B7BB2C6123A8",
        "9EF96AFDCB339AEAA6EE9E1F012689A9D1CD262B112868917BD3774A7F2EC85C",
        "8A350C47AFD69D9FC7F01F6A7BDFA4CCEC3515E2D0BDEF218E7E2C17468F99B4",
    };

    private readonly float[] _samples;
    private AudioCaptureRequest? _request;
    private bool _disposed;

    private PublicFixtureAudioCapture(float[] samples)
    {
        _samples = samples;
    }

    public event EventHandler<AudioLevel>? LevelChanged;

    public bool IsCapturing => _request is not null;

    public static bool TryCreate(string? path, out PublicFixtureAudioCapture? capture)
    {
        capture = null;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var file = new FileInfo(fullPath);
            if (file.Length is <= 0 or > 1_000_000)
            {
                return false;
            }

            using var stream = file.OpenRead();
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            if (!ReviewedFixtureHashes.Contains(hash))
            {
                return false;
            }

            capture = new PublicFixtureAudioCapture(ReadWaveFile(fullPath));
            return true;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            return false;
        }
    }

    public Task<AudioOperationResult> StartAsync(
        AudioCaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_request is not null)
        {
            return Task.FromResult(Failure(AppErrorCode.CaptureAlreadyActive));
        }

        _request = request;
        LevelChanged?.Invoke(this, MeasureLevel(_samples));
        return Task.FromResult(new AudioOperationResult(Succeeded: true));
    }

    public Task<CapturedAudio> StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_request is not { } request)
        {
            return Task.FromResult(new CapturedAudio(
                new DictationSessionId(Guid.Empty),
                ReadOnlyMemory<float>.Empty,
                RequiredSampleRate,
                Channels: 1,
                AudioCaptureOutcome.Interrupted,
                new AppError(
                    AppErrorCode.InvalidTransition,
                    AppErrorStage.AudioCapture,
                    CanRetry: false)));
        }

        _request = null;
        LevelChanged?.Invoke(this, AudioLevel.Silent);
        return Task.FromResult(new CapturedAudio(
            request.SessionId,
            _samples,
            RequiredSampleRate,
            Channels: 1));
    }

    public Task<AudioOperationResult> CancelAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        _request = null;
        LevelChanged?.Invoke(this, AudioLevel.Silent);
        return Task.FromResult(new AudioOperationResult(Succeeded: true));
    }

    public AudioSnapshot? GetSnapshot(TimeSpan maximumDuration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumDuration, TimeSpan.Zero);
        if (_request is not { } request)
        {
            return null;
        }

        // THE TWIN OF THE CLAMP IN `WasapiAudioCapture`, and it has to move with it. Both answer the
        // same contract for the same callers, so a duration that is safe against a live microphone
        // and fatal against a fixture would make the harness disagree with the app about whether a
        // dictation works. Same reason, written once there. Ref: #96.
        var requestedSamples = Math.Ceiling(maximumDuration.TotalSeconds * RequiredSampleRate);
        var maximumSamples = requestedSamples >= int.MaxValue
            ? int.MaxValue
            : Math.Max(1, (int)requestedSamples);
        var offset = Math.Max(0, _samples.Length - maximumSamples);
        return new AudioSnapshot(
            request.SessionId,
            _samples.AsMemory(offset),
            RequiredSampleRate,
            Channels: 1);
    }

    public ValueTask DisposeAsync()
    {
        _request = null;
        _disposed = true;
        return ValueTask.CompletedTask;
    }

    private static AudioOperationResult Failure(AppErrorCode code) => new(
        Succeeded: false,
        new AppError(code, AppErrorStage.AudioCapture, CanRetry: false));

    private static AudioLevel MeasureLevel(float[] samples)
    {
        if (samples.Length == 0)
        {
            return AudioLevel.Silent;
        }

        double squareSum = 0;
        var peak = 0f;
        foreach (var sample in samples)
        {
            var absolute = Math.Abs(sample);
            peak = Math.Max(peak, absolute);
            squareSum += sample * sample;
        }

        return new AudioLevel(peak, (float)Math.Sqrt(squareSum / samples.Length));
    }

    private static float[] ReadWaveFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 12 ||
            !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
            !bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("The reviewed UAT fixture is not a RIFF WAVE file.");
        }

        byte[]? format = null;
        var position = 12;
        while (position + 8 <= bytes.Length)
        {
            var chunkId = bytes.AsSpan(position, 4);
            var chunkSize = BitConverter.ToInt32(bytes, position + 4);
            position += 8;
            if (chunkSize < 0 || position + chunkSize > bytes.Length)
            {
                throw new InvalidDataException("The reviewed UAT fixture has an invalid chunk.");
            }

            if (chunkId.SequenceEqual("fmt "u8))
            {
                format = bytes.AsSpan(position, chunkSize).ToArray();
            }
            else if (chunkId.SequenceEqual("data"u8) && format is { Length: >= 16 })
            {
                return DecodeSamples(bytes, position, chunkSize, format);
            }

            position += chunkSize + (chunkSize % 2);
        }

        throw new InvalidDataException("The reviewed UAT fixture has no supported audio data.");
    }

    private static float[] DecodeSamples(
        byte[] bytes,
        int position,
        int chunkSize,
        byte[] format)
    {
        var audioFormat = BitConverter.ToInt16(format, 0);
        var channels = BitConverter.ToInt16(format, 2);
        var sourceRate = BitConverter.ToInt32(format, 4);
        var bitsPerSample = BitConverter.ToInt16(format, 14);
        var bytesPerSample = bitsPerSample / 8;
        if (channels <= 0 || sourceRate <= 0 || bytesPerSample <= 0)
        {
            throw new InvalidDataException("The reviewed UAT fixture has an invalid format.");
        }

        var sampleCount = chunkSize / bytesPerSample / channels;
        var samples = new float[sampleCount];
        for (var index = 0; index < sampleCount; index++)
        {
            var sampleOffset = position + (index * bytesPerSample * channels);
            samples[index] = audioFormat switch
            {
                1 when bitsPerSample == 16 => BitConverter.ToInt16(bytes, sampleOffset) / 32768f,
                7 when bitsPerSample == 8 => DecodeMuLaw(bytes[sampleOffset]),
                _ => throw new InvalidDataException(
                    "The reviewed UAT fixture uses an unsupported codec."),
            };
        }

        return sourceRate == RequiredSampleRate
            ? samples
            : Resample(samples, sourceRate, RequiredSampleRate);
    }

    private static float DecodeMuLaw(byte value)
    {
        var decoded = (byte)~value;
        var sign = (decoded & 0x80) == 0 ? 1 : -1;
        var exponent = (decoded >> 4) & 0x07;
        var mantissa = decoded & 0x0F;
        var magnitude = (((mantissa << 3) + 0x84) << exponent) - 0x84;
        return sign * magnitude / 32768f;
    }

    private static float[] Resample(float[] source, int sourceRate, int destinationRate)
    {
        var destinationLength = checked((int)Math.Round(
            source.Length * (destinationRate / (double)sourceRate)));
        var destination = new float[destinationLength];
        for (var index = 0; index < destinationLength; index++)
        {
            var sourcePosition = index * (sourceRate / (double)destinationRate);
            var lower = Math.Min((int)sourcePosition, source.Length - 1);
            var upper = Math.Min(lower + 1, source.Length - 1);
            var fraction = sourcePosition - lower;
            destination[index] = (float)(source[lower] + ((source[upper] - source[lower]) * fraction));
        }

        return destination;
    }
}
