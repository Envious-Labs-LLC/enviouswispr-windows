using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Pipeline;

/// <summary>What a recording's background work is told when it starts: how long the watchdog allows, and where the auto-stop's preferences are read.</summary>
/// <remarks>
/// THE WATCHDOG'S LIMIT IS A VALUE, READ AS THE RECORDING STARTS; THE PREFERENCES ARE READ AT THE
/// AUTO-STOP'S OWN BOUNDARY. The shell always read them immediately before starting the auto-stop,
/// after the preview had been asked to start, and a save landing in between governs the recording;
/// the observation is handed over as a function so that boundary is kept. Neither is the executor's
/// to know how to read: the limit can be shortened by a journey harness through the environment.
/// </remarks>
public sealed record RecordingBackgroundSettings(TimeSpan WatchdogDuration, Func<DictationPreferences> Dictation);

/// <summary>The four things that run beside a recording, started and stopped in one order.</summary>
/// <remarks>
/// A SEAM FOR THE EXECUTOR'S OWN TESTS, NOT A SECOND OWNER. <see cref="SessionBackgroundWork"/> is the
/// one production implementation and the order lives there, under its own tests; the executor's
/// unit tests substitute a tracing fake so they can assert what the executor asks for and when,
/// without four controllers behind it.
/// </remarks>
public interface ISessionBackgroundWork
{
    /// <summary>The recording has started: arm the watchdog, start the preview, the auto-stop and the streaming head start.</summary>
    Task StartAsync(DictationSessionId sessionId, RecordingBackgroundSettings settings);

    /// <summary>The recording is over: stop streaming, the auto-stop and the preview, in that order. The watchdog is stopped separately, first.</summary>
    Task StopAsync();

    /// <summary>Stops the background work and waits up to the deadline for each owner; what did not finish stays owned and is reported.</summary>
    Task<BackgroundStopReport> StopAsync(TimeSpan deadline);

    /// <summary>A terminal has arrived: the watchdog must not fire into a recording that is already ending.</summary>
    Task StopWatchdogAsync();

    /// <summary>The watchdog stopped under a deadline, for the teardown; a watch still running past it stays owned and is reported.</summary>
    Task<StopOutcome> StopWatchdogAsync(TimeSpan deadline);
}

/// <summary>Owns the order in which the preview, the streaming head start and the two timers start and stop around a recording.</summary>
/// <remarks>
/// THE ORDER WAS THE SHELL'S LAST PIECE OF THE WORKFLOW, and it is a decision, not an effect: the
/// watchdog is armed first, before anything else is asked, so a recording is guarded from its first
/// moment; the preview's start returns once its loop is launched, not when its worker answers, so
/// nothing here waits on a worker (step 8 of #148); and on the way out streaming stops first (its
/// head start is what the final transcription consumes), then the auto-stop, then the preview,
/// which may be waiting on a worker and is the slowest to answer - the order the shell always had.
/// The regrade of #148 named this ordering, held behind an effects port, as the reason the shell
/// was still part of the pipeline; it is the pipeline's now, and tested as such.
///
/// THE CONTROLLERS ARE OWNED ELSEWHERE. The shell constructs them, because their effects reach into
/// its window and worker adapters, and disposes them at exit; this orders them. Each stop is
/// idempotent, so a stop after a recovery or a teardown that already stopped it is harmless.
/// </remarks>
public sealed class SessionBackgroundWork : ISessionBackgroundWork
{
    private readonly RecordingWatchdog _watchdog;
    private readonly LivePreviewController _preview;
    private readonly AutoStopMonitor _autoStop;
    private readonly StreamingTranscriptionController _streaming;
    private readonly TimeProvider _clock;

    /// <param name="clock">The clock the bounded stop measures its one deadline on: the owners' own, so a test can cross it.</param>
    public SessionBackgroundWork(
        RecordingWatchdog watchdog,
        LivePreviewController preview,
        AutoStopMonitor autoStop,
        StreamingTranscriptionController streaming,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(watchdog);
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(autoStop);
        ArgumentNullException.ThrowIfNull(streaming);
        ArgumentNullException.ThrowIfNull(clock);
        _watchdog = watchdog;
        _preview = preview;
        _autoStop = autoStop;
        _streaming = streaming;
        _clock = clock;
    }

    public async Task StartAsync(DictationSessionId sessionId, RecordingBackgroundSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        using var dictation = DictationScope.Begin(sessionId.Value);
        _watchdog.Start(sessionId, settings.WatchdogDuration);
        await _preview.StartAsync(sessionId).ConfigureAwait(false);
        _autoStop.Start(sessionId, settings.Dictation());
        _streaming.Start(sessionId);
    }

    public async Task StopAsync()
    {
        await _streaming.StopAsync().ConfigureAwait(false);
        await _autoStop.StopAsync().ConfigureAwait(false);
        await _preview.StopAsync().ConfigureAwait(false);
    }

    /// <remarks>
    /// ONE DEADLINE FOR THE THREE, EACH GIVEN WHAT IS LEFT. Three bounded joins in the order the
    /// unbounded stop has always used; an owner that spends the deadline leaves the next ones zero,
    /// and a stop given zero still cancels and observes - it reports a loop that has finished as
    /// finished - so the report is honest about every owner and the three together never take longer
    /// than the shutdown allowed.
    /// </remarks>
    public async Task<BackgroundStopReport> StopAsync(TimeSpan deadline)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(deadline, TimeSpan.Zero);
        var budget = new StopBudget(deadline, _clock);
        var streaming = await _streaming.StopAsync(budget.Left).ConfigureAwait(false);
        var autoStop = await _autoStop.StopAsync(budget.Left).ConfigureAwait(false);
        var preview = await _preview.StopAsync(budget.Left).ConfigureAwait(false);
        return new BackgroundStopReport(streaming, autoStop, preview);
    }

    public Task StopWatchdogAsync() => _watchdog.StopAsync();

    public Task<StopOutcome> StopWatchdogAsync(TimeSpan deadline)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(deadline, TimeSpan.Zero);
        return _watchdog.StopAsync(deadline);
    }
}
