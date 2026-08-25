using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace EnviousWispr.Audio;

/// 16 kHz mono float32 capture from the default render-device mic — exactly
/// the Mac's capture spec (AudioCaptureManager.swift: 16 kHz mono float32).
/// Push-to-talk: Start() on key-down, Stop() on key-up; no VAD in v1 (the
/// user's key press IS the endpoint — VAD is the Mac's silence trimmer, a
/// later porting item).
public sealed class CaptureService : IDisposable
{
    private const int SampleRate = 16000;

    private WasapiCapture? _capture;
    private readonly List<float> _buffer = new();
    private readonly object _gate = new();
    private long _capturedMs;

    public bool IsCapturing { get; private set; }
    public long CapturedMs => _capturedMs;

    public void Start()
    {
        if (IsCapturing) return;
        lock (_gate)
        {
            _buffer.Clear();
            _capturedMs = 0;

            // WASAPI shared mode: request the capture spec format; the audio
            // engine converts from the device mix format (the Mac's capture
            // path does the same via AVAudioEngine).
            var device = new MMDeviceEnumerator()
                .GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            _capture = new WasapiCapture(device);
            _capture.WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1); // set before StartRecording
            _capture.DataAvailable += OnData;
            _capture.RecordingStopped += OnStopped;
            _capture.StartRecording();
            IsCapturing = true;
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var floats = new float[e.BytesRecorded / 4];
        Buffer.BlockCopy(e.Buffer, 0, floats, 0, e.BytesRecorded);
        lock (_gate)
        {
            _buffer.AddRange(floats);
            _capturedMs = _buffer.Count / SampleRate * 1000L;
        }
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
            throw new InvalidOperationException("capture stopped with error", e.Exception);
    }

    /// Stops capture and returns the accumulated 16 kHz mono samples.
    public float[] Stop()
    {
        WasapiCapture? capture;
        lock (_gate)
        {
            if (!IsCapturing) return [];
            IsCapturing = false;
            capture = _capture;
            _capture = null;
        }
        capture?.StopRecording();
        capture?.Dispose();

        lock (_gate)
        {
            var samples = _buffer.ToArray();
            _buffer.Clear();
            return samples;
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
