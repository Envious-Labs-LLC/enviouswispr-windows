using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;

namespace EnviousWispr.Pipeline;

/// <summary>Shell UI surface for live silent-microphone notices while a recording is running (#4841).</summary>
public interface ISilentMicNoticeEffects
{
    void ShowSilentMicNotice(MicSilenceLevel level);

    void ClearSilentMicNotice();
}

/// <summary>
/// Polls the live exact-zero run during a recording and shows/clears calm or strong silent-mic notices on level transitions.
/// </summary>
public sealed class SilentMicMonitor : IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly ISilentMicNoticeEffects _effects;
    private readonly Func<ISilentMicSampleSource?> _sampleSource;
    private readonly TimeProvider _clock;
    private CancellationTokenSource? _cancellation;
    private Task? _loop;
    private TaskCompletionSource? _done;

    public SilentMicMonitor(
        ISilentMicNoticeEffects effects,
        Func<ISilentMicSampleSource?> sampleSource,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(effects);
        ArgumentNullException.ThrowIfNull(sampleSource);
        ArgumentNullException.ThrowIfNull(clock);
        _effects = effects;
        _sampleSource = sampleSource;
        _clock = clock;
    }

    public bool IsRunning => _loop is { IsCompleted: false };

    internal Task? Loop => _loop;

    public void Start(DictationSessionId sessionId, bool warningsEnabled)
    {
        if (!warningsEnabled || _sampleSource() is not { } source)
        {
            return;
        }

        if (_loop is not null)
        {
            if (_done?.Task.IsCompleted != true)
            {
                return;
            }

            _cancellation?.Dispose();
            _cancellation = null;
            _loop = null;
            _done = null;
        }

        _cancellation = new CancellationTokenSource();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _done = done;
        _loop = RunAsync(source, sessionId, warningsEnabled, done, _cancellation.Token);
    }

    private async Task RunAsync(
        ISilentMicSampleSource source,
        DictationSessionId sessionId,
        bool warningsEnabled,
        TaskCompletionSource done,
        CancellationToken cancellationToken)
    {
        using var scope = DictationScope.Begin(sessionId.Value);
        var shownLevel = MicSilenceLevel.None;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(PollInterval, _clock, cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var run = source.CurrentExactZeroRun;
                if (run is null)
                {
                    // Capture stopped or lost; end tracking without claiming sound returned.
                    return;
                }

                var level = SilentMicNoticePolicy.Evaluate(run, warningsEnabled);
                if (level == MicSilenceLevel.None)
                {
                    if (shownLevel != MicSilenceLevel.None)
                    {
                        _effects.ClearSilentMicNotice();
                        shownLevel = MicSilenceLevel.None;
                    }
                }
                else if (level != shownLevel)
                {
                    _effects.ShowSilentMicNotice(level);
                    shownLevel = level;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Recording stopped or cancelled; never clear on exit because the pill is leaving.
        }
        finally
        {
            done.TrySetResult();
        }
    }

    public Task<StopOutcome> StopAsync() => StopAsync(deadline: null);

    public async Task<StopOutcome> StopAsync(TimeSpan? deadline, CancellationToken cancellationToken = default)
    {
        var cancellation = _cancellation;
        var done = _done;
        if (cancellation is null)
        {
            return StopOutcome.Completed;
        }

        await cancellation.CancelAsync().ConfigureAwait(false);
        if (done is not null &&
            await BoundedJoin.JoinAsync(done.Task, deadline, _clock, cancellationToken).ConfigureAwait(false) == StopOutcome.StillRunning)
        {
            return StopOutcome.StillRunning;
        }

        _cancellation = null;
        _loop = null;
        _done = null;
        cancellation.Dispose();
        return StopOutcome.Completed;
    }

    public ValueTask DisposeAsync() => new(StopAsync());
}
