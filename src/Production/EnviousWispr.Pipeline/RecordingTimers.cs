using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Pipeline;

/// <summary>Where a timer's verdict goes: it posts a command and returns, and never waits for what follows.</summary>
/// <remarks>
/// PRODUCERS, NOT OWNERS. The auto-stop ends a recording through the same door a key release uses,
/// by posting a Released signal to the shell's command entry, so the session state machine, the
/// hook's own recording flag, transcription, delivery and history all run exactly as they would
/// have; the watchdog posts a timeout command to the same queue, where the executor decides whether
/// the recording it was armed for is still the one recording. A parallel finish path would be a
/// second implementation of ending a dictation, and the two would drift. Both post rather than
/// await, because what they post is what stops these very loops: a loop that awaited its own
/// teardown would wait for itself.
/// </remarks>
public interface IRecordingTimerEffects
{
    /// <summary>The take so far, or null when the capture in force cannot be sampled.</summary>
    IAudioSnapshotSource? Audio { get; }

    /// <summary>Posts a push-to-talk signal and returns; the caller does not wait for it to run.</summary>
    void Post(PushToTalkSignal signal);

    /// <summary>Posts that the recording armed as <paramref name="sessionId"/> has run for as long as it is allowed, and returns.</summary>
    void RecordingTimedOut(DictationSessionId sessionId);
}

/// <summary>
/// Ends a recording that has run too long. One arming per recording; arming again cancels the last.
/// </summary>
/// <remarks>
/// THE DURATION IS THE SHELL'S TO SUPPLY, because it is read from an environment variable the UAT
/// harness sets, and reading environment is not this project's business. What lives here is the wait
/// and the one decision after it: post, or find out the watch was cancelled and say nothing. Whether
/// the recording is still the one that was armed is the executor's question, asked under the queue.
/// </remarks>
public sealed class RecordingWatchdog : IAsyncDisposable
{
    private readonly IRecordingTimerEffects _effects;
    private readonly TimeProvider _clock;
    private CancellationTokenSource? _cancellation;
    private Task? _watch;

    public RecordingWatchdog(IRecordingTimerEffects effects, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(effects);
        ArgumentNullException.ThrowIfNull(clock);
        _effects = effects;
        _clock = clock;
    }

    /// <summary>Whether a watch has been armed and not yet stopped.</summary>
    public bool IsArmed => _watch is not null;

    public void Start(DictationSessionId sessionId, TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        _watch = WatchAsync(sessionId, duration, _cancellation.Token);
    }

    private async Task WatchAsync(DictationSessionId sessionId, TimeSpan duration, CancellationToken cancellationToken)
    {
        // Every flow that serves a dictation opens the scope for itself. Inheriting one would in
        // fact work here - a child async flow keeps the AsyncLocal value it captured even after the
        // caller disposes its own scope - and that is exactly why this does not rely on it: the
        // join would then be a property of who happened to call whom, invisible at this method and
        // unprovable by anything. Opening it here makes it a property of this flow, which a gate
        // can check. One line per flow, and the flows are the methods that take a session id.
        using var dictation = DictationScope.Begin(sessionId.Value);
        try
        {
            await Task.Delay(duration, _clock, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The recording ended some other way, which is the ordinary case.
            return;
        }

        _effects.RecordingTimedOut(sessionId);
    }

    public async Task StopAsync()
    {
        var cancellation = Interlocked.Exchange(ref _cancellation, null);
        var watch = Interlocked.Exchange(ref _watch, null);
        try
        {
            cancellation?.Cancel();
            if (watch is not null)
            {
                try
                {
                    await watch.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
        finally
        {
            // DISPOSED HOWEVER THE DRAIN ENDS. The field was cleared before the wait, so a recovery
            // that faulted would otherwise leave a source nobody can reach again.
            cancellation?.Dispose();
        }
    }

    /// <summary>The stop, as the last call: the shell's shutdown has already stopped the watch by then.</summary>
    public ValueTask DisposeAsync() => new(StopAsync());
}

/// <summary>
/// Watches a running recording and ends it when the speaker has stopped, if the user asked.
/// </summary>
/// <remarks>
/// IT ENDS THE RECORDING THROUGH THE SAME DOOR A KEY RELEASE USES, by posting a Released signal, so
/// the session state machine, the hook's own recording flag, transcription, delivery and history all
/// run exactly as they would have. A parallel finish path here would be a second implementation of
/// ending a dictation, and the two would drift.
///
/// HAS-HEARD-SPEECH IS STICKY AND LIVES HERE, not in the snapshot. The buffer only holds a window, so
/// a speaker who says one word and then pauses past that window would look to a single snapshot like
/// someone who never spoke - and the policy would stop protecting them at the exact moment it should
/// fire. Once speech is heard in this recording, it stays heard.
///
/// THE SNAPSHOT WINDOW IS LONGER THAN ANY THRESHOLD IT COULD BE ASKED ABOUT. A window shorter than
/// the threshold can never contain enough silence to satisfy it, so the feature would simply never
/// fire - silently, and looking exactly like a user who had not turned it on.
/// </remarks>
public sealed class AutoStopMonitor : IAsyncDisposable
{
    /// <summary>How often the watcher asks whether the speaker has finished.</summary>
    /// <remarks>
    /// Far more often than the threshold it is testing, so the recording ends close to when the user
    /// expects rather than up to a poll late. Cheap: it reads a buffer already being written.
    /// </remarks>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>How long a pause has to be before the words in front of it are finished.</summary>
    private static readonly TimeSpan SegmentPause = TimeSpan.FromMilliseconds(400);

    private readonly IRecordingTimerEffects _effects;
    private readonly IAppLogger _logger;
    private readonly TimeProvider _clock;
    private CancellationTokenSource? _cancellation;
    private Task? _loop;

    public AutoStopMonitor(IRecordingTimerEffects effects, IAppLogger logger, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(effects);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(clock);
        _effects = effects;
        _logger = logger;
        _clock = clock;
    }

    /// <summary>Whether a watch is running and not yet stopped.</summary>
    public bool IsRunning => _loop is not null;

    /// <summary>The loop, so a test can wait for one that ends on its own rather than poll for it.</summary>
    internal Task? Loop => _loop;

    /// <summary>Starts watching, unless the user has not asked or the capture cannot be sampled.</summary>
    public void Start(DictationSessionId sessionId, DictationPreferences dictation)
    {
        ArgumentNullException.ThrowIfNull(dictation);
        if (!dictation.AutoStopEnabled ||
            dictation.RecordingMode != DictationRecordingMode.Toggle ||
            _effects.Audio is not { } snapshots)
        {
            return;
        }

        _cancellation = new CancellationTokenSource();
        _loop = RunAsync(snapshots, sessionId, dictation, _cancellation.Token);
    }

    private async Task RunAsync(
        IAudioSnapshotSource snapshots,
        DictationSessionId sessionId,
        DictationPreferences dictation,
        CancellationToken cancellationToken)
    {
        // Every flow that serves a dictation opens the scope for itself. Inheriting one would in
        // fact work here - a child async flow keeps the AsyncLocal value it captured even after the
        // caller disposes its own scope - and that is exactly why this does not rely on it: the
        // join would then be a property of who happened to call whom, invisible at this method and
        // unprovable by anything. Opening it here makes it a property of this flow, which a gate
        // can check. One line per flow, and the flows are the methods that take a session id.
        using var scope = DictationScope.Begin(sessionId.Value);
        var required = TimeSpan.FromSeconds(dictation.AutoStopSilenceSeconds);
        if (required < AutoStopPolicy.MinimumSilence)
        {
            required = AutoStopPolicy.MinimumSilence;
        }

        // Comfortably more than the threshold, so the window can always hold enough silence to
        // answer the question being asked of it.
        var window = required + required;
        var heardSpeech = false;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(PollInterval, _clock, cancellationToken).ConfigureAwait(false);

                var snapshot = snapshots.GetSnapshot(window);
                if (snapshot is null || snapshot.SessionId != sessionId)
                {
                    continue;
                }

                var segmenter = new SpeechSegmenter(snapshot.SampleRate, SegmentPause);
                var samples = snapshot.Samples.Span;
                heardSpeech |= segmenter.Segment(samples).Any(segment => segment.IsSpeech);

                var decision = AutoStopPolicy.Decide(
                    dictation.AutoStopEnabled,
                    dictation.RecordingMode == DictationRecordingMode.Toggle,
                    heardSpeech,
                    segmenter.TrailingSilence(samples),
                    required);
                if (decision != AutoStopDecision.Stop)
                {
                    continue;
                }

                _logger.Write(new AppLogEntry(_clock.GetUtcNow(), AppEventCode.AutoStopTriggered));
                // Post and return. Awaiting here would hold this loop open across the whole
                // transcription, and the loop is cancelled as part of ending the recording.
                _effects.Post(PushToTalkSignal.Released);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // The recording ended some other way, which is the ordinary case.
        }
    }

    public async Task StopAsync()
    {
        var cancellation = _cancellation;
        var loop = _loop;
        _cancellation = null;
        _loop = null;
        if (cancellation is null)
        {
            return;
        }

        try
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            if (loop is not null)
            {
                try
                {
                    await loop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
        finally
        {
            // Disposed however the drain ends; the field was cleared before the wait.
            cancellation.Dispose();
        }
    }

    /// <summary>The stop, as the last call: the shell's shutdown has already stopped the loop by then.</summary>
    public ValueTask DisposeAsync() => new(StopAsync());
}
