namespace EnviousWispr;

/// Pipeline orchestration: hotkey → capture → ASR → EG-1 polish → paste.
/// Mirrors the Mac's stage contract (PostHog medians 0.61 s no-polish /
/// 1.65 s on-device polish are the reference bars).
///
/// Polish is a SILENT limb: any failure degrades to the raw ASR text,
/// never to an error state (EG-1 connector contract).
public enum PipelineState { Idle, Recording, Transcribing, Polishing, Done, Error }

public sealed record StageTimings(long? AsrMs, long? PolishMs, long TotalMs, bool Polished);

public sealed class DictationPipeline : IAsyncDisposable
{
    private readonly Audio.CaptureService _capture;
    private readonly Asr.ParakeetEngine _asr;
    private readonly Polish.EgOneServer? _server;
    private readonly Func<Polish.EgOnePolisher?> _polisherProvider; // lazy: EG-1 loads after the app starts
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private PipelineState _state = PipelineState.Idle;
    private string _lastText = "";

    public PipelineState State
    {
        get => _state;
        private set { _state = value; StateChanged?.Invoke(value); }
    }

    public string LastText => _lastText;
    public StageTimings? LastTimings { get; private set; }
    public event Action<PipelineState>? StateChanged;
    public event Action<string>? Log;

    public DictationPipeline(Audio.CaptureService capture, Asr.ParakeetEngine asr,
        Polish.EgOneServer? server, Func<Polish.EgOnePolisher?> polisherProvider)
    {
        _capture = capture;
        _asr = asr;
        _server = server;
        _polisherProvider = polisherProvider;
    }

    public void HotKeyDown()
    {
        if (_state != PipelineState.Idle) return;
        _capture.Start();
        State = PipelineState.Recording;
        Log?.Invoke("recording started");
    }

    public void HotKeyUp()
    {
        if (_state != PipelineState.Recording) return;
        var samples = _capture.Stop();
        _ = Task.Run(() => RunTranscriptionAsync(samples));
    }

    private async Task RunTranscriptionAsync(float[] samples)
    {
        if (!await _runGate.WaitAsync(0)) return; // previous run still going
        var started = Environment.TickCount64;
        try
        {
            if (samples.Length < 1600) // < 100 ms — treat as an accidental tap
            {
                Log?.Invoke("tap too short, ignored");
                State = PipelineState.Idle;
                return;
            }

            State = PipelineState.Transcribing;
            var asr = _asr.Recognize(samples);
            Log?.Invoke($"asr: {asr.Text.Length} chars in {asr.ElapsedMs} ms: {Truncate(asr.Text, 80)}");

            string finalText = asr.Text;
            long? polishMs = null;
            var polished = false;

            if (_polisherProvider() is not null && !string.IsNullOrWhiteSpace(asr.Text))
            {
                State = PipelineState.Polishing;
                var t0 = Environment.TickCount64;
                var polishedText = await _polisherProvider()!.PolishAsync(asr.Text);
                polishMs = Environment.TickCount64 - t0;
                if (polishedText is not null)
                {
                    finalText = polishedText;
                    polished = true;
                    Log?.Invoke($"polish: {polishMs} ms: {Truncate(finalText, 80)}");
                }
                else
                {
                    Log?.Invoke($"polish: skipped ({polishMs} ms), using raw ASR text");
                }
            }

            _lastText = finalText;
            Input.TextInserter.Paste(finalText);
            LastTimings = new StageTimings(asr.ElapsedMs, polishMs, Environment.TickCount64 - started, polished);
            State = PipelineState.Done;
            Log?.Invoke($"done in {Environment.TickCount64 - started} ms (asr {asr.ElapsedMs} ms, polish {polishMs?.ToString() ?? "n/a"} ms){(polished ? "" : ", raw")}");
            // Back to idle so the next dictation can start; the overlay
            // shows the Done state briefly before it flips.
            await Task.Delay(900);
            if (State == PipelineState.Done) State = PipelineState.Idle;
        }
        catch (Exception ex)
        {
            State = PipelineState.Error;
            Log?.Invoke($"error: {ex.Message}");
            await Task.Delay(1500);
            State = PipelineState.Idle;
        }
        finally
        {
            _runGate.Release();
        }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    public async ValueTask DisposeAsync()
    {
        if (_server is not null) await _server.DisposeAsync();
        _capture.Dispose();
        _asr.Dispose();
        _runGate.Dispose();
    }
}
