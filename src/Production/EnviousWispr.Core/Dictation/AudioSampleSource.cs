namespace EnviousWispr.Core.Dictation;

/// <summary>Decoded audio, 16 kHz mono, read forward in order. The file decoder is one; a test's array is another.</summary>
public interface IAudioSampleSource
{
    /// <summary>The whole length when the container states it, for the progress bar; null when it does not.</summary>
    TimeSpan? Duration { get; }

    /// <summary>Fills up to <paramref name="buffer"/>.Length samples; 0 means the end.</summary>
    int Read(Span<float> buffer);
}
