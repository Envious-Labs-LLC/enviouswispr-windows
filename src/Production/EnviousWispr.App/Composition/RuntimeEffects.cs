using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Preview;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Pipeline;
using EnviousWispr.PostProcessing;

namespace EnviousWispr.App.Composition;

/// <summary>The shell's half of the recording timers: the capture, the command entry, and the timeout.</summary>
internal sealed class RecordingTimerEffects(RuntimeShell shell, SessionQueue queue) : IRecordingTimerEffects
{
    public IAudioSnapshotSource? Audio => shell.Audio();

    public void Post(PushToTalkSignal signal) => _ = queue.HandAsync(signal);

    public void RecordingTimedOut(DictationSessionId sessionId) => queue.TimeOut(sessionId);
}

/// <summary>The shell's half of streaming: the final engine, the capture, and the switch it yields to.</summary>
internal sealed class StreamingTranscriptionEffects(RuntimeShell shell) : IStreamingTranscriptionEffects
{
    public bool LivePreviewEnabled => shell.LivePreviewEnabled();

    public ITranscriptionEngine? Engine => shell.Engine();

    public IAudioSnapshotSource? Audio => shell.Audio();
}

/// <summary>The shell's half of live preview: what it built, what it can sample, and the surface.</summary>
internal sealed class LivePreviewEffects(RuntimeShell shell) : ILivePreviewEffects
{
    public bool Enabled => shell.LivePreviewEnabled();

    public ILivePreviewEngine? Engine => shell.PreviewEngine();

    public AppErrorCode? EngineUnavailableReason => shell.PreviewUnavailableReason();

    public IAudioSnapshotSource? Audio => shell.Audio();

    public DictationSessionId? RecordingSessionId => shell.RecordingSessionId();

    public void ShowPreview(LivePreviewFrame frame) => shell.View.ShowPreview(frame);

    public void ClearPreview() => shell.View.ShowPreview(frame: null);
}

/// <summary>What the shell shows when persistence changes what the person should see.</summary>
internal sealed class SessionPersistenceEffects(IRuntimeView view) : ISessionPersistenceEffects
{
    public void ShowPendingRecovery(RecoveryTextRecord record)
    {
        // The record first, the window after: the order the window's queue always ran them in,
        // since showing the window was itself a further dispatch.
        view.ShowRecoveredText(new RecoveryTextLoadResult(RecoveryTextLoadStatus.Found, record));
        view.ShowMainWindow();
    }

    public void ClearRecoveredText() => view.ClearRecoveredText();

    public void NotifyHistoryChanged() => view.NotifyHistoryChanged();
}

/// <summary>The shell's half of a finalisation: the log lines and the recovery writes, nothing that decides.</summary>
internal sealed class TranscriptFinalizationEffects(IAppLogger logger, SessionPersistence persistence) : ITranscriptFinalizationEffects
{
    public void RecordDeterministicProcessingStarted() =>
        logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            AppEventCode.DeterministicProcessingStarted));

    /// <summary>Writes one line per deterministic stage, so a skipped step is visible.</summary>
    /// <remarks>
    /// SPLIT IN TWO CALLS AROUND THE OPTIONAL POLISH. The summary line says only that the pass
    /// finished and what it cost, so a pass that skipped all five stages and one that did five jobs
    /// look the same there; the per-stage lines are what tell them apart.
    /// </remarks>
    public void EmitStageReceipts(IReadOnlyList<DeterministicStageReceipt> receipts, bool emojiRestorationOnly)
    {
        foreach (var receipt in receipts)
        {
            if ((receipt.Stage == DeterministicTextStage.EmojiRestoration) != emojiRestorationOnly)
            {
                continue;
            }

            logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.DeterministicStageObserved,
                receipt.Status is DeterministicStageStatus.Failed
                    or DeterministicStageStatus.TimedOut
                    or DeterministicStageStatus.Busy
                    ? AppFailureCategory.PostProcessing
                    : AppFailureCategory.None,
                receipt.ElapsedMilliseconds,
                Stage: receipt.Stage,
                StageStatus: receipt.Status,
                Changed: receipt.Changed));
        }
    }

    public Task SaveRecoveryTextAsync(ProcessedText output, CancellationToken cancellationToken) =>
        persistence.SaveRecoveryTextAsync(output, cancellationToken);

    public void RecordPolishStarted(string providerId) =>
        logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            AppEventCode.PolishStarted,
            Provider: DiagnosticProviderIds.FromProviderId(providerId)));

    public void RecordPolishFinished(string providerId, PolishResult result, bool usedLocalRuntime, long elapsedMilliseconds) =>
        logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            result.UsedFallback ? AppEventCode.PolishDegraded : AppEventCode.PolishCompleted,
            result.UsedFallback
                ? usedLocalRuntime
                    ? AppFailureCategory.LocalPolish
                    : AppFailureCategory.CloudPolish
                : AppFailureCategory.None,
            elapsedMilliseconds,
            DiagnosticProviderIds.FromProviderId(providerId),
            result.Error?.Code));

    public void RecordPolishRefused() =>
        logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            AppEventCode.PolishOutputRefused,
            // InvalidData rather than LocalPolish or CloudPolish: the refusal is about what came
            // BACK, and either provider can produce it. Attributing it to one would make the log
            // claim a cause it does not know.
            AppFailureCategory.InvalidData));

    public void RecordDeterministicProcessingFinished(bool degraded, long elapsedMilliseconds) =>
        logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            degraded
                ? AppEventCode.DeterministicProcessingDegraded
                : AppEventCode.DeterministicProcessingCompleted,
            degraded
                ? AppFailureCategory.PostProcessing
                : AppFailureCategory.None,
            elapsedMilliseconds));
}
