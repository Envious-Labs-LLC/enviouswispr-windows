using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Preview;

namespace EnviousWispr.Pipeline;

/// <summary>
/// The shell's half of live preview: the engine it built, the capture it can sample, the setting the
/// user chose, and the surface the words go on. Nothing here decides; the controller does.
/// </summary>
public interface ILivePreviewEffects
{
    /// <summary>The user's Live Preview switch, read at the moment a recording starts.</summary>
    bool Enabled { get; }

    /// <summary>The preview engine the shell built, or null when it could not.</summary>
    ILivePreviewEngine? Engine { get; }

    /// <summary>
    /// Why <see cref="Engine"/> is null, when it is. Held by the shell rather than logged where it
    /// was discovered, and reported only at the moment somebody actually turns Live Preview on.
    /// </summary>
    AppErrorCode? EngineUnavailableReason { get; }

    /// <summary>The take so far, or null when the capture in force cannot be sampled.</summary>
    IAudioSnapshotSource? Audio { get; }

    /// <summary>The dictation being recorded right now, or null; the stop path joins its lines to it.</summary>
    DictationSessionId? RecordingSessionId { get; }

    /// <summary>Words for the screen, tagged with the dictation they belong to.</summary>
    void ShowPreview(DictationSessionId sessionId, string text);

    /// <summary>The preview surface goes blank.</summary>
    void ClearPreview();
}

/// <summary>
/// Owns a live preview from the first snapshot to the last: starting the engine, the loop that keeps
/// words on screen at the cadence, and the stop that every path - release, cancel, watchdog, lock,
/// quit - reaches. Runs without a window.
/// </summary>
/// <remarks>
/// START AND STOP ARE SERIALISED ON ONE GATE, because stopping is reached from more places than
/// starting and two of them can coincide: a watchdog firing while the release it was guarding is
/// already stopping the loop. Under the gate a second start finds the first loop and returns; a
/// second stop finds nothing and returns. Neither can observe the other half-way.
///
/// THE LOOP IS DISPLAY-ONLY. It can never change the final transcript, and a failed pass ends the loop
/// with a line in the log rather than a word on screen. Release or cancellation stops it through the
/// token, which is its normal exit and not a failure.
///
/// AN UPDATE CARRIES ITS DICTATION. The engine tags each update with the session the snapshot came
/// from; one tagged with any other dictation is not this preview's to show, and is dropped.
/// </remarks>
public sealed class LivePreviewController : IAsyncDisposable
{
    /// <summary>The most audio one pass is given; older samples have already been previewed.</summary>
    private static readonly TimeSpan MaximumWindow = TimeSpan.FromSeconds(20);

    /// <summary>Half a second of 16 kHz audio: fewer samples than this are not worth a pass.</summary>
    private const int MinimumSamplesPerPass = 8_000;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILivePreviewEffects _effects;
    private readonly IAppLogger _logger;
    private readonly TimeProvider _clock;
    private CancellationTokenSource? _cancellation;
    private Task? _loop;
    private long _sequence;
    private bool _disposed;

    public LivePreviewController(ILivePreviewEffects effects, IAppLogger logger, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(effects);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(clock);
        _effects = effects;
        _logger = logger;
        _clock = clock;
    }

    /// <summary>Whether a preview loop has been started and not yet stopped.</summary>
    public bool IsRunning => _loop is not null;

    /// <summary>The loop itself, so a test can wait for one that ends on its own rather than poll for it.</summary>
    internal Task? Loop => _loop;

    public async Task StartAsync(DictationSessionId sessionId)
    {
        // Every flow that serves a dictation opens the scope for itself. Inheriting one would in
        // fact work here - a child async flow keeps the AsyncLocal value it captured even after the
        // caller disposes its own scope - and that is exactly why this does not rely on it: the
        // join would then be a property of who happened to call whom, invisible at this method and
        // unprovable by anything. Opening it here makes it a property of this flow, which a gate
        // can check. One line per flow, and the flows are the methods that take a session id.
        using var scope = DictationScope.Begin(sessionId.Value);
        if (!_effects.Enabled)
        {
            return;
        }

        var engine = _effects.Engine;
        var audio = _effects.Audio;
        // THE USER HAS ASKED FOR THIS BY THE TIME WE GET HERE, so a refusal is news and the two
        // reasons are different facts. A missing preview model is a thing they can fix by installing
        // one; a capture source that cannot be sampled is not. Reporting them as one silent return
        // is what let somebody switch Live Preview on, watch the toggle stay on, see nothing happen,
        // and find no trace of why.
        if (engine is null)
        {
            var reason = _effects.EngineUnavailableReason ?? AppErrorCode.RuntimeProviderUnavailable;
            _logger.Write(new AppLogEntry(
                _clock.GetUtcNow(),
                AppEventCode.LivePreviewFailed,
                reason == AppErrorCode.ModelPackUnavailable
                    ? AppFailureCategory.AsrUnavailable
                    : AppFailureCategory.RuntimeProvider,
                ErrorCode: reason));
            return;
        }

        if (audio is null)
        {
            _logger.Write(new AppLogEntry(
                _clock.GetUtcNow(),
                AppEventCode.LivePreviewFailed,
                AppFailureCategory.AudioUnavailable,
                ErrorCode: AppErrorCode.AudioDeviceUnavailable));
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_loop is not null)
            {
                return;
            }

            var started = await engine.StartAsync().ConfigureAwait(false);
            if (!started.Succeeded)
            {
                _logger.Write(new AppLogEntry(
                    _clock.GetUtcNow(),
                    AppEventCode.LivePreviewFailed,
                    AppFailureCategories.For(started.Error)));
                return;
            }

            // WRITTEN BEFORE THE LOOP IS LAUNCHED, because an engine that answers synchronously runs
            // the first pass before this line, and a log whose first update precedes its start reads
            // as two previews.
            _logger.Write(new AppLogEntry(_clock.GetUtcNow(), AppEventCode.LivePreviewStarted));
            _sequence = 0;
            _cancellation = new CancellationTokenSource();
            _loop = RunAsync(sessionId, engine, audio, _cancellation.Token);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RunAsync(
        DictationSessionId sessionId,
        ILivePreviewEngine engine,
        IAudioSnapshotSource audio,
        CancellationToken cancellationToken)
    {
        // The loop inherits the start's scope as a child flow and would stay joined without this;
        // opened anyway, because the rule is one line per flow and not a property of who called whom.
        using var dictation = DictationScope.Begin(sessionId.Value);
        try
        {
            while (true)
            {
                // THE WAIT MOVED TO THE FAR SIDE OF THE WORK, WHICH IS THE WHOLE FIX. It used to sit
                // here, so the period was the interval PLUS the cost of a pass rather than the larger
                // of the two, and nothing could reach the screen before both had elapsed however fast
                // the engine became. On the measured 7.9-second take that bought exactly one update
                // and the second was not slow but impossible. Ref: #99 and `LivePreviewCadence`.
                var snapshot = audio.GetSnapshot(MaximumWindow);
                if (snapshot is null || snapshot.Samples.Length < MinimumSamplesPerPass)
                {
                    // NOT THE CADENCE, BECAUSE THIS IS NOT AN UPDATE. There is not yet enough audio to
                    // transcribe, and waiting the full interval to re-ask is what made a person watch
                    // "Listening..." for four seconds. Half the threshold this guard enforces, so the
                    // first pass cannot start more than a quarter second late.
                    await Task.Delay(TimeSpan.FromMilliseconds(250), _clock, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                var started = _clock.GetTimestamp();
                var update = await engine.PreviewAsync(
                    snapshot,
                    Interlocked.Increment(ref _sequence),
                    cancellationToken).ConfigureAwait(false);
                var passCost = _clock.GetElapsedTime(started);
                if (!update.Succeeded)
                {
                    _logger.Write(new AppLogEntry(
                        _clock.GetUtcNow(),
                        AppEventCode.LivePreviewFailed,
                        AppFailureCategories.For(update.Error),
                        (long)passCost.TotalMilliseconds));
                    return;
                }

                _logger.Write(new AppLogEntry(
                    _clock.GetUtcNow(),
                    AppEventCode.LivePreviewUpdated,
                    ElapsedMilliseconds: (long)passCost.TotalMilliseconds));
                if (update.SessionId == sessionId.Value)
                {
                    _effects.ShowPreview(sessionId, update.Text);
                }

                // A FLOOR, NOT AN ADDITION. A pass slower than the interval waits nothing and the next
                // one starts immediately; a fast one still cannot flood the screen. The engine is a
                // limb and must not spend the machine the final transcript is waiting on.
                await Task.Delay(LivePreviewCadence.DelayAfter(passCost), _clock, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Release or cancellation intentionally stops preview without affecting final ASR.
        }
        catch (Exception exception) when (exception is not (StackOverflowException or OutOfMemoryException))
        {
            _logger.Write(new AppLogEntry(
                _clock.GetUtcNow(),
                AppEventCode.LivePreviewFailed,
                AppFailureCategory.RuntimeWorker));
        }
    }

    public async Task StopAsync()
    {
        // STOPPING IS REACHED FROM MORE PLACES THAN STARTING, and one of them is quitting the app
        // from the tray mid-recording - a shutdown path that inherits nothing, where the line saying
        // the preview stopped was the last thing written about that dictation and was joined to
        // nothing. Read off the shell rather than taken as a parameter, because the callers that
        // lose the join are exactly the ones with no id to pass.
        using var dictation = _effects.RecordingSessionId is { } recording
            ? DictationScope.Begin(recording.Value)
            : NoScope.Instance;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var cancellation = _cancellation;
            var loop = _loop;
            _cancellation = null;
            _loop = null;
            cancellation?.Cancel();
            if (loop is not null)
            {
                try
                {
                    await loop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The preview task observes cancellation as its normal stop path.
                }
            }

            cancellation?.Dispose();
            if (_effects.Engine is { } engine)
            {
                await engine.StopAsync().ConfigureAwait(false);
            }

            _effects.ClearPreview();
            if (loop is not null)
            {
                _logger.Write(new AppLogEntry(_clock.GetUtcNow(), AppEventCode.LivePreviewStopped));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops whatever is running and releases the gate. The engine is the shell's to dispose.</summary>
    /// <remarks>
    /// THE LAST CALL, BY CONTRACT RATHER THAN BY ENFORCEMENT. The shell disposes this after admission
    /// has closed, the watchdog has stopped and the session gate is held, so nothing can be starting
    /// or stopping a preview at the same time; the gate is disposed on that understanding and a start
    /// or stop that arrives after it would find a disposed semaphore. A second dispose is a no-op.
    /// This is the contract the shell's own preview gate had; it is written down here because the
    /// gate now has a type of its own that somebody could reach for elsewhere.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
