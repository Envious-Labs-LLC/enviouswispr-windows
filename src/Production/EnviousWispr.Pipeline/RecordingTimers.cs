using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Errors;
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
    /// <summary>A signal on the recording's behalf; the queue ignores it if that recording has ended by the time it runs.</summary>
    void Post(PushToTalkSignal signal, DictationSessionId forSession);

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
    private TaskCompletionSource? _done;
    /// <summary>Watches a start replaced before they had finished: cancelled, still owned, joined and disposed by the next stop.</summary>
    private readonly List<(CancellationTokenSource Cancellation, TaskCompletionSource Done)> _retired = [];

    public RecordingWatchdog(IRecordingTimerEffects effects, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(effects);
        ArgumentNullException.ThrowIfNull(clock);
        _effects = effects;
        _clock = clock;
    }

    /// <summary>Whether a watch has been armed and not yet stopped.</summary>
    public bool IsArmed => _watch is { IsCompleted: false };

    public void Start(DictationSessionId sessionId, TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        _cancellation?.Cancel();
        // A WATCH REPLACED BEFORE IT FINISHED IS RETIRED, NOT DROPPED. Its callback may still be
        // running; its source is not disposed under it, and the next stop joins it - so a stop that
        // reports completion has seen every watch this owner ever armed finish, not only the last.
        if (_cancellation is { } previous && _done is { } previousDone)
        {
            if (previousDone.Task.IsCompleted)
            {
                previous.Dispose();
            }
            else
            {
                _retired.Add((previous, previousDone));
            }
        }

        _cancellation = new CancellationTokenSource();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _done = done;
        _watch = WatchAsync(sessionId, duration, done, _cancellation.Token);
    }

    private async Task WatchAsync(DictationSessionId sessionId, TimeSpan duration, TaskCompletionSource done, CancellationToken cancellationToken)
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
            done.TrySetResult();
            return;
        }

        // THE CALLBACK IS THE LAST THING THE WATCH DOES, and the join covers it: the timeout it
        // hands over is a command on the session's queue, submitted and not awaited, so the stop that
        // command makes joins a watch that has already returned.
        try
        {
            _effects.RecordingTimedOut(sessionId);
        }
        finally
        {
            done.TrySetResult();
        }
    }

    /// <summary>Disarms the watch and waits for it, however long that takes.</summary>
    public Task<StopOutcome> StopAsync() => StopAsync(deadline: null);

    /// <summary>Disarms the watch and waits up to the deadline; a watch still running past it stays owned.</summary>
    public async Task<StopOutcome> StopAsync(TimeSpan? deadline)
    {
        var budget = new StopBudget(deadline, _clock);
        var cancellation = _cancellation;
        var done = _done;
        cancellation?.Cancel();
        if (done is not null &&
            await BoundedJoin.JoinAsync(done.Task, budget.Remaining, _clock).ConfigureAwait(false) == StopOutcome.StillRunning)
        {
            return StopOutcome.StillRunning;
        }

        // DISPOSED ONLY ONCE THE WATCH IS OVER. A source disposed under a watch still running is a
        // fault nobody can reach again; one kept past a deadline is joined by the next stop.
        _cancellation = null;
        _watch = null;
        _done = null;
        cancellation?.Dispose();

        // THE RETIRED WATCHES ARE JOINED TOO, inside the same budget; the ones that finished are
        // disposed and let go of, the rest stay owned and the stop says so.
        foreach (var retired in _retired.ToArray())
        {
            if (await BoundedJoin.JoinAsync(retired.Done.Task, budget.Remaining, _clock).ConfigureAwait(false) == StopOutcome.StillRunning)
            {
                return StopOutcome.StillRunning;
            }

            retired.Cancellation.Dispose();
            _retired.Remove(retired);
        }

        return StopOutcome.Completed;
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
    private TaskCompletionSource? _done;

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

        // REFUSED WHILE THE LAST LOOP IS STILL OWNED: a loop a bounded stop left behind would post
        // its release into this recording. The next stop joins what is left.
        if (_loop is not null)
        {
            _logger.Write(new AppLogEntry(
                _clock.GetUtcNow(),
                AppEventCode.AutoStopTriggered,
                AppFailureCategory.RuntimeWorker,
                ErrorCode: AppErrorCode.RuntimeResourceBusy));
            return;
        }

        _cancellation = new CancellationTokenSource();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _done = done;
        _loop = RunAsync(snapshots, sessionId, dictation, done, _cancellation.Token);
    }

    private async Task RunAsync(
        IAudioSnapshotSource snapshots,
        DictationSessionId sessionId,
        DictationPreferences dictation,
        TaskCompletionSource done,
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

                // NOT POSTED ONCE THE RECORDING IS ENDING. The decision above was made on a take that
                // is over if a stop has been asked for meanwhile; a release posted now would be for
                // the next recording.
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                _logger.Write(new AppLogEntry(_clock.GetUtcNow(), AppEventCode.AutoStopTriggered));
                // Post and return. Awaiting the post would hold this loop open across the whole
                // transcription; the release it posts is a command on the session's queue, submitted
                // and not awaited, so the stop that command makes joins a loop that has returned.
                _effects.Post(PushToTalkSignal.Released, sessionId);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // The recording ended some other way, which is the ordinary case.
        }
        finally
        {
            done.TrySetResult();
        }
    }

    /// <summary>Ends the watch and waits for it, however long that takes.</summary>
    public Task<StopOutcome> StopAsync() => StopAsync(deadline: null);

    /// <summary>Ends the watch and waits up to the deadline; a loop still running past it stays owned.</summary>
    public async Task<StopOutcome> StopAsync(TimeSpan? deadline)
    {
        var cancellation = _cancellation;
        var done = _done;
        if (cancellation is null)
        {
            return StopOutcome.Completed;
        }

        await cancellation.CancelAsync().ConfigureAwait(false);
        if (done is not null &&
            await BoundedJoin.JoinAsync(done.Task, deadline, _clock).ConfigureAwait(false) == StopOutcome.StillRunning)
        {
            return StopOutcome.StillRunning;
        }

        // Disposed only once the loop is over; one kept past a deadline is joined by the next stop.
        _cancellation = null;
        _loop = null;
        _done = null;
        cancellation.Dispose();
        return StopOutcome.Completed;
    }

    /// <summary>The stop, as the last call: the shell's shutdown has already stopped the loop by then.</summary>
    public ValueTask DisposeAsync() => new(StopAsync());
}
