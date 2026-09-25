using System.Globalization;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.History;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Preview;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;
using EnviousWispr.PostProcessing;

namespace EnviousWispr.App.Composition;

/// <summary>What the long-lived session owners show the person: the window's preview, recovery and history surfaces.</summary>
/// <remarks>
/// EACH SINK IS ONE DISPATCH TO THE WINDOW AND NOTHING DECIDED, as with <see cref="ISessionView"/>.
/// The preview text, the recovered record and the history change are handed over as they are.
/// </remarks>
public interface IRuntimeView
{
    /// <summary>A frame for the preview screen, or null to clear it. The frame's <see cref="LivePreviewFrame.IsCurrent"/> is asked at the draw.</summary>
    void ShowPreview(LivePreviewFrame? frame);

    void ShowRecoveredText(RecoveryTextLoadResult result);

    void ClearRecoveredText();

    void NotifyHistoryChanged();

    void ShowMainWindow();

    /// <summary>A record press was refused because <paramref name="holder"/> had the session; the pill says so.</summary>
    void ShowSessionBusy(SessionHolder holder);
}

/// <summary>The shell's leaf reads the long-lived session owners need; nothing here sequences anything.</summary>
/// <remarks>
/// <c>ClipboardText</c> reads the clipboard's plain text without changing it, for a snippet that pastes
/// what was copied; it is asked only on a take where such a snippet fired.
///
/// EVERY READ IS MADE AT THE CALL. The capture changes with each hook start, the engines load and
/// unload, the preferences are saved while a recording runs, and the coordinator exists only while a
/// session controller does. Capturing any of them when the owners were built would hand a loop a
/// stale instance; the risk note of this step names exactly that.
/// </remarks>
public sealed record RuntimeShell(
    IRuntimeView View,
    Func<bool> LivePreviewEnabled,
    Func<HistoryPreferences> History,
    Func<IReadOnlyList<CustomWordEntry>> CustomWords,
    Func<IAudioSnapshotSource?> Audio,
    Func<ITranscriptionEngine?> Engine,
    Func<ILivePreviewEngine?> PreviewEngine,
    Func<AppErrorCode?> PreviewUnavailableReason,
    Func<DictationSessionId?> RecordingSessionId,
    Func<DictationSessionCoordinator?> Coordinator,
    Func<bool> Leaving,
    Func<CancellationToken, Task<string?>> ClipboardText);

/// <summary>Everything the long-lived session owners are built from.</summary>
public sealed record RuntimeCompositionParts(
    IRecoveryTextStore RecoveryStore,
    IHistoryStore HistoryStore,
    DeterministicTextPipeline TextPipeline,
    IRuntimeResourceAdmission PolishAdmission,
    IAppLogger Logger,
    RuntimeShell Shell,
    TimeProvider Clock);

/// <summary>The shared queue as the hook and the timers reach it: one method to submit a key, one for the watchdog's timeout.</summary>
internal sealed class SessionQueue(RuntimeShell shell, IAppLogger logger)
{
    /// <summary>Hands a key's signal to the shared queue and returns once it has run or been refused: the hook's route and the auto-stop's are this one method.</summary>
    /// <remarks>
    /// A RELEASE THAT ARRIVES WHILE THE PRESS IS STILL STARTING IS KEPT, NOT DROPPED. The shell used
    /// to probe its session gate with a zero timeout and return silently when it was held - and it is
    /// held for the whole of starting a recording, microphone and live preview included, so a quick
    /// tap on a busy machine lost its key-up and the recording ran on until the next press (#86). The
    /// coordinator queues the terminal signal behind the press and runs it exactly once when the
    /// press is done. A press during another command is still refused, explicitly (Busy), because a
    /// press that ran later would open a microphone nobody asked for.
    ///
    /// THE HOOK AND THE AUTO-STOP LOOP FIRE AND FORGET THIS TASK. The executor recovers its own
    /// failures; anything that escapes it would otherwise fault a task nobody awaits and vanish.
    /// Content-free, like every line in this log. Refused once the shell is leaving, as the shell's
    /// handler always refused it.
    /// </remarks>
    /// <param name="forSession">For a signal a loop posted on a recording's behalf - the auto-stop's release - that recording; the queue ignores the signal if it has ended by the time it runs. Null for a key.</param>
    public async Task HandAsync(PushToTalkSignal signal, DictationSessionId? forSession)
    {
        if (shell.Leaving() || shell.Coordinator() is not { } coordinator)
        {
            return;
        }

        try
        {
            var result = await coordinator.SubmitAsync(signal, forSession).ConfigureAwait(false);
            // A PRESS REFUSED FOR A HOLDER IS SAID, NOT SWALLOWED. A person who pressed the key during a file
            // transcription saw nothing happen at all; macOS names the job on its pill. A press refused for a
            // dictation's own command stays silent - that dictation's pill is already on screen. Ref: #211.
            if (result is { Disposition: SessionCommandDisposition.Busy, BusyHolder: { } holder } &&
                signal == PushToTalkSignal.Pressed)
            {
                shell.View.ShowSessionBusy(holder);
            }

            if (result.WasQueued)
            {
                logger.Write(new AppLogEntry(DateTimeOffset.UtcNow, AppEventCode.DictationSignalQueued));
            }
        }
        catch (Exception exception) when (exception is not (StackOverflowException or OutOfMemoryException))
        {
            logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.UnhandledFailure,
                AppFailureCategory.Unknown));
        }
    }

    /// <summary>The watchdog's timeout, as a command on the same queue.</summary>
    public void TimeOut(DictationSessionId sessionId)
    {
        if (!shell.Leaving() && shell.Coordinator() is { } coordinator)
        {
            _ = coordinator.TimeOutAsync(sessionId);
        }
    }
}

/// <summary>The session owners that outlive any one session: built once, stopped around each recording, disposed with the shell.</summary>
public sealed class SessionRuntime
{
    private readonly SessionQueue _queue;
    private readonly TimeProvider _clock;

    internal SessionRuntime(
        SessionQueue queue,
        SessionPersistence persistence,
        TranscriptFinalizer finalizer,
        LivePreviewController preview,
        StreamingTranscriptionController streaming,
        RecordingWatchdog watchdog,
        AutoStopMonitor autoStop,
        TimeProvider clock)
    {
        _queue = queue;
        Persistence = persistence;
        Finalizer = finalizer;
        Preview = preview;
        Streaming = streaming;
        Watchdog = watchdog;
        AutoStop = autoStop;
        _clock = clock;
    }

    public SessionPersistence Persistence { get; }

    public TranscriptFinalizer Finalizer { get; }

    public LivePreviewController Preview { get; }

    public StreamingTranscriptionController Streaming { get; }

    public RecordingWatchdog Watchdog { get; }

    public AutoStopMonitor AutoStop { get; }

    /// <summary>The order around a recording, for the session the executor is about to own.</summary>
    public SessionBackgroundWork Background() => new(Watchdog, Preview, AutoStop, Streaming, _clock);

    /// <summary>A key's signal, on the same queue the timers use.</summary>
    public Task SubmitAsync(PushToTalkSignal signal) => _queue.HandAsync(signal, forSession: null);

    /// <summary>The queue itself, for a test that drives the timers' route with a recording's name on the signal.</summary>
    internal SessionQueue Queue => _queue;
}

/// <summary>Joins the long-lived session owners the way the shell always built them, without the shell.</summary>
/// <remarks>
/// BUILT ONCE, BEFORE ANY SESSION. The persistence owner, the finaliser and the four background
/// owners were constructed in the shell's constructor against adapters that read the shell's fields;
/// the same construction now takes the concrete stores and a set of leaf reads, so the tests can
/// build these owners and drive them against the composed session.
/// </remarks>
public static class RuntimeComposition
{
    public static SessionRuntime Compose(RuntimeCompositionParts parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var persistence = new SessionPersistence(
            parts.RecoveryStore,
            parts.HistoryStore,
            parts.Logger,
            parts.Clock,
            parts.Shell.History,
            new SessionPersistenceEffects(parts.Shell.View));
        var finalizationEffects = new TranscriptFinalizationEffects(parts.Logger, persistence);
        var finalizer = new TranscriptFinalizer(
            parts.TextPipeline,
            new PolishExecutor(
                parts.PolishAdmission,
                finalizationEffects,
                // Read at the call, as before: a word taught mid-dictation reaches this polish.
                parts.Shell.CustomWords),
            finalizationEffects,
            // THE CULTURE AND ZONE ARE READ WHEN A SNIPPET FIRES, not now: a fill-in renders in the
            // Windows region the person has at the moment they speak, frozen for that one take.
            new SnippetExpansionStage(
                new SnippetExpander(),
                parts.Clock,
                () => CultureInfo.CurrentCulture,
                parts.Shell.ClipboardText,
                SnippetExpansionStage.Timeout));
        var preview = new LivePreviewController(new LivePreviewEffects(parts.Shell), parts.Logger, parts.Clock);
        var streaming = new StreamingTranscriptionController(new StreamingTranscriptionEffects(parts.Shell), parts.Logger, parts.Clock);
        var queue = new SessionQueue(parts.Shell, parts.Logger);
        var timers = new RecordingTimerEffects(parts.Shell, queue);
        return new SessionRuntime(
            queue,
            persistence,
            finalizer,
            preview,
            streaming,
            new RecordingWatchdog(timers, parts.Clock),
            new AutoStopMonitor(timers, parts.Logger, parts.Clock),
            parts.Clock);
    }
}
