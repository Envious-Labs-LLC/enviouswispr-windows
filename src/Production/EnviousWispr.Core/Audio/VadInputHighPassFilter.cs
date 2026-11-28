namespace EnviousWispr.Core.Audio;

/// <summary>
/// Two cascaded first-order 250 Hz IIR high-pass sections matching macOS <c>VADInputHighPass</c>.
/// Applied strictly ahead of voice activity and silence detection so sub-vocal rumble and DC offset
/// never falsify loudness floors, while leaving speech recognition PCM untouched.
/// </summary>
public sealed class VadInputHighPassFilter
{
    public const double CutoffHz = 250.0;
    public const int SectionCount = 2;

    private readonly double _alpha;
    private readonly double[] _previousInputs = new double[SectionCount];
    private readonly double[] _previousOutputs = new double[SectionCount];

    public VadInputHighPassFilter(int sampleRate, double cutoffHz = CutoffHz)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(cutoffHz, 0);

        var rc = 1.0 / (2.0 * Math.PI * cutoffHz);
        var dt = 1.0 / sampleRate;
        _alpha = rc / (rc + dt);
    }

    /// <summary>Resets filter delay memory back to zero.</summary>
    public void Reset()
    {
        Array.Clear(_previousInputs);
        Array.Clear(_previousOutputs);
    }

    /// <summary>Filters a single sample through all cascaded sections.</summary>
    public float ProcessSample(float sample)
    {
        double value = sample;
        for (var section = 0; section < SectionCount; section++)
        {
            var output = _alpha * (_previousOutputs[section] + value - _previousInputs[section]);
            _previousInputs[section] = value;
            _previousOutputs[section] = output;
            value = output;
        }

        return (float)value;
    }

    /// <summary>Writes high-pass-filtered samples from <paramref name="input"/> into <paramref name="output"/>.</summary>
    public void Process(ReadOnlySpan<float> input, Span<float> output)
    {
        if (output.Length < input.Length)
        {
            throw new ArgumentException("Output span must be at least as long as input.", nameof(output));
        }

        for (var index = 0; index < input.Length; index++)
        {
            output[index] = ProcessSample(input[index]);
        }
    }
}
