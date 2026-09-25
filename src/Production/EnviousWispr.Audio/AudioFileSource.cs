using EnviousWispr.Core.Dictation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace EnviousWispr.Audio;

/// <summary>An audio file decoded by Windows Media Foundation, as 16 kHz mono for the engines. Ref: #211.</summary>
/// <remarks>
/// WHATEVER WINDOWS CAN PLAY: WAV, MP3, M4A/AAC, WMA, FLAC - Media Foundation's own decoders, nothing shipped with the
/// app. Channels are averaged to one and the rate is resampled to 16 kHz, the only format every final engine takes.
/// Read forward in pieces, so an hour-long file never sits in memory whole.
/// </remarks>
public sealed class AudioFileSource : IAudioSampleSource, IDisposable
{
    public const int SampleRate = 16_000;

    private readonly MediaFoundationReader _reader;
    private readonly ISampleProvider _samples;

    private AudioFileSource(MediaFoundationReader reader)
    {
        _reader = reader;
        ISampleProvider samples = reader.ToSampleProvider();
        if (samples.WaveFormat.Channels > 1)
        {
            samples = new AveragingMonoProvider(samples);
        }

        _samples = samples.WaveFormat.SampleRate == SampleRate
            ? samples
            : new WdlResamplingSampleProvider(samples, SampleRate);
        Duration = reader.TotalTime > TimeSpan.Zero ? reader.TotalTime : null;
    }

    public TimeSpan? Duration { get; }

    /// <summary>Opens a file, or explains why it cannot be read as audio.</summary>
    public static AudioFileSource? TryOpen(string path, out AudioFileOpenFailure failure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        failure = AudioFileOpenFailure.None;
        if (!File.Exists(path))
        {
            failure = AudioFileOpenFailure.Missing;
            return null;
        }

        try
        {
            return new AudioFileSource(new MediaFoundationReader(path));
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or
                                          InvalidOperationException or
                                          ArgumentException or
                                          IOException or
                                          UnauthorizedAccessException)
        {
            failure = exception is UnauthorizedAccessException or IOException
                ? AudioFileOpenFailure.Unreadable
                : AudioFileOpenFailure.NotAudio;
            return null;
        }
    }

    public int Read(Span<float> buffer) => _samples.Read(buffer);

    public void Dispose() => _reader.Dispose();

    /// <summary>Every channel averaged into one, for any channel count.</summary>
    private sealed class AveragingMonoProvider(ISampleProvider source) : ISampleProvider
    {
        private float[] _interleaved = [];

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);

        public int Read(Span<float> buffer)
        {
            var channels = source.WaveFormat.Channels;
            var needed = buffer.Length * channels;
            if (_interleaved.Length < needed)
            {
                _interleaved = new float[needed];
            }

            var read = source.Read(_interleaved.AsSpan(0, needed));
            var frames = read / channels;
            for (var frame = 0; frame < frames; frame++)
            {
                var sum = 0f;
                for (var channel = 0; channel < channels; channel++)
                {
                    sum += _interleaved[(frame * channels) + channel];
                }

                buffer[frame] = sum / channels;
            }

            return frames;
        }
    }
}

public enum AudioFileOpenFailure
{
    None,

    /// <summary>The file is not there any more.</summary>
    Missing,

    /// <summary>The file exists and could not be read - locked, or no permission.</summary>
    Unreadable,

    /// <summary>Windows has no decoder for it: not audio, or a format this PC cannot play.</summary>
    NotAudio,
}
