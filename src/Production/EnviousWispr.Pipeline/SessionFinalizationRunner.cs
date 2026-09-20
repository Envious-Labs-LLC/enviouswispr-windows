using System.Diagnostics;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Pipeline;

/// <summary>How a finalisation ended.</summary>
public enum FinalizationOutcome
{
    /// <summary>No speech engine was available; the capture is acknowledged and the session reset.</summary>
    EngineUnavailable,

    /// <summary>The speech engine failed; the session is reset and the words are not lost to recovery.</summary>
    TranscriptionFailed,

    /// <summary>The text reached the app it was started in, or the clipboard caught it.</summary>
    Delivered,

    /// <summary>Delivery was attempted and refused; the text is held for the person.</summary>
    DeliveryRefused,

    /// <summary>Nothing was delivered by design: no text, no delivery target, or the session had moved on.</summary>
    Held,

    /// <summary>An Escape Recovery: the words are kept, nothing is delivered.</summary>
    EscapeRecovery,
}

/// <summary>Everything the runner knows at the end, for the shell to render and log from.</summary>
public sealed record FinalizationReport(
    FinalizationOutcome Outcome,
    Transcript? Transcript = null,
    FinalizedTranscript? Finalized = null,
    DeliveryResult? Delivery = null,
    AppError? TranscriptionError = null,
    long TranscriptionMilliseconds = 0);

/// <summary>
/// The shell's half of a finalisation: what is rendered, what is logged, and the two operations that
/// still live there until their own steps move them - the speech engine call with its head start, and
/// the audio archive.
/// </summary>
public interface ISessionFinalizationEffects
{
    /// <summary>The speech engine in force, or null when none is loaded.</summary>
    ITranscriptionEngine? Engine { get; }

    /// <summary>The delivery route, or null when the shell has none.</summary>
    ITextDelivery? Delivery { get; }

    /// <summary>The language delivery should be told, or null when the engine's answer is not to be trusted.</summary>
    string? DeliveryLanguage(Transcript transcript);

    /// <summary>Transcribes the capture, using any streaming head start the shell collected. Step 9 moves this.</summary>
    Task<Transcript> TranscribeAsync(ITranscriptionEngine engine, CapturedAudio audio, CancellationToken cancellationToken);

    /// <summary>
    /// The settings the text decisions run under, read AFTER transcription, at the moment they are
    /// needed. A custom word or a cleanup switch saved while the speech engine was still working
    /// reaches this dictation, which is what the shell always did by reading its fields there.
    /// </summary>
    FinalizationOptions CurrentOptions();

    void ClearEscapeRecoveryForSession();

    void ArchiveAudio(CapturedAudio audio);

    void RecordTranscriptionUnavailable();

    void ShowTranscriptionUnavailable();

    void ShowTranscribing();

    void RecordTranscriptionStarted();

    void RecordTranscriptionFinished(Transcript transcript, long elapsedMilliseconds);

    void RecordTranscriptionFailed(AppError? failure, long elapsedMilliseconds);

    void ShowTranscriptionFailed();

    void ShowDelivering();

    void RecordDeliveryStarted();

    void RecordDelivery(DeliveryResult delivery, long elapsedMilliseconds);

    /// <summary>The delivered outcome on screen, with the language offer that may follow it.</summary>
    void ReportDelivery(DeliveryResult delivery, string? language);

    void ShowEscapeRecoveryFinished();

    /// <summary>The final status line for a dictation that was not delivered.</summary>
    void ShowHeldStatus(FinalizationReport report);

    /// <summary>The whole wait, whichever way the finalisation left.</summary>
    void RecordDictationCompleted(long waitMilliseconds);
}

/// <summary>What the runner needs from the shell's settings at the moment a finalisation starts.</summary>
public sealed record FinalizationOptions(
    IReadOnlyList<CustomWordEntry> CustomWords,
    DeterministicTextOptions TextOptions,
    PolishSetup? Polish);

/// <summary>
/// Turns a finished capture into delivered text: the speech engine, the text decisions, delivery,
/// persistence and the session's completion, in the order the shell had. Runs without a window.
/// </summary>
/// <remarks>
/// EVERY LINE WRITTEN FROM HERE ON SAYS WHICH DICTATION IT BELONGED TO. The scope is ambient, so the
/// finalizer, persistence and delivery are joined without being handed anything, and one added next
/// month is joined on arrival.
///
/// THE WHOLE WAIT IS REPORTED, NOT THE SUM OF THE STAGES. A sum reports zero for every await between
/// them, which is exactly where an unexplained delay would hide. It is reported in a finally, so a
/// path that throws still reports what the user waited before it did - and the failure path is the
/// slowest.
///
/// DELIVERY IS ATTEMPTED ONLY WHEN THERE IS SOMETHING TO DELIVER AND SOMEWHERE TO DELIVER IT. An
/// Escape Recovery, blank text, a shell with no delivery route, or a session that has already moved on
/// each hold the text instead. A refused delivery holds it too, and says so on screen.
/// </remarks>
public sealed class SessionFinalizationRunner
{
    private readonly PushToTalkSessionController _controller;
    private readonly TranscriptFinalizer _finalizer;
    private readonly SessionPersistence _persistence;
    private readonly ISessionFinalizationEffects _effects;
    private readonly TimeProvider _clock;

    public SessionFinalizationRunner(
        PushToTalkSessionController controller,
        TranscriptFinalizer finalizer,
        SessionPersistence persistence,
        ISessionFinalizationEffects effects,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(finalizer);
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(effects);
        ArgumentNullException.ThrowIfNull(clock);
        _controller = controller;
        _finalizer = finalizer;
        _persistence = persistence;
        _effects = effects;
        _clock = clock;
    }

    public async Task<FinalizationReport> RunAsync(
        DictationSessionId sessionId,
        CapturedAudio audio,
        bool recoveryOnly,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audio);
        using var dictation = DictationScope.Begin(sessionId.Value);
        _effects.ClearEscapeRecoveryForSession();
        var engine = _effects.Engine;
        if (engine is null)
        {
            _effects.RecordTranscriptionUnavailable();
            await CompleteAndResetAsync(sessionId).ConfigureAwait(false);
            _effects.ShowTranscriptionUnavailable();
            return new FinalizationReport(FinalizationOutcome.EngineUnavailable);
        }

        _effects.ShowTranscribing();
        _effects.RecordTranscriptionStarted();
        var waitTimer = Stopwatch.StartNew();
        _effects.ArchiveAudio(audio);
        var timer = Stopwatch.StartNew();
        try
        {
            var transcript = await _effects.TranscribeAsync(engine, audio, cancellationToken).ConfigureAwait(false);
            timer.Stop();
            _effects.RecordTranscriptionFinished(transcript, timer.ElapsedMilliseconds);
            var options = _effects.CurrentOptions();
            var finalized = await _finalizer
                .FinalizeAsync(transcript, options.CustomWords, options.TextOptions, options.Polish, cancellationToken)
                .ConfigureAwait(false);
            var processed = finalized.Processed;

            if (!recoveryOnly &&
                !string.IsNullOrWhiteSpace(processed.Output.Text) &&
                _effects.Delivery is { } delivery &&
                _controller.CurrentSession is { } pendingSession)
            {
                var deliveryTransition = await _controller
                    .BeginDeliveryAsync(sessionId, cancellationToken)
                    .ConfigureAwait(false);
                if (deliveryTransition.Kind == SessionTransitionKind.Delivering)
                {
                    _effects.ShowDelivering();
                    _effects.RecordDeliveryStarted();
                    var deliveryTimer = Stopwatch.StartNew();
                    var language = _effects.DeliveryLanguage(transcript);
                    var result = await delivery.DeliverAsync(
                        new TextDeliveryRequest(
                            processed.Output,
                            pendingSession.Target,
                            language,
                            pendingSession.DeliveryOptions),
                        cancellationToken).ConfigureAwait(false);
                    deliveryTimer.Stop();
                    _effects.RecordDelivery(result, deliveryTimer.ElapsedMilliseconds);
                    if (result.Delivered || result.ClipboardFallback)
                    {
                        await _persistence.ClearRecoveryTextAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        _persistence.ShowPendingRecovery();
                    }

                    await _persistence.SaveHistoryAsync(
                            transcript,
                            processed.Output.Text,
                            HistoryWriteIntent.Delivered(finalized.WasPolished, result.Delivered))
                        .ConfigureAwait(false);
                    await CompleteAndResetAsync(sessionId).ConfigureAwait(false);
                    _effects.ReportDelivery(result, language);
                    return new FinalizationReport(
                        result.Delivered || result.ClipboardFallback
                            ? FinalizationOutcome.Delivered
                            : FinalizationOutcome.DeliveryRefused,
                        transcript,
                        finalized,
                        result,
                        TranscriptionMilliseconds: timer.ElapsedMilliseconds);
                }
            }

            await _persistence.SaveHistoryAsync(
                    transcript,
                    processed.Output.Text,
                    recoveryOnly
                        ? HistoryWriteIntent.EscapeRecovery(finalized.WasPolished, _clock.GetUtcNow().AddHours(24))
                        : HistoryWriteIntent.Held(finalized.WasPolished))
                .ConfigureAwait(false);
            await CompleteAndResetAsync(sessionId).ConfigureAwait(false);
            _persistence.ShowPendingRecovery();
            if (recoveryOnly && !string.IsNullOrWhiteSpace(processed.Output.Text))
            {
                _effects.ShowEscapeRecoveryFinished();
            }

            var report = new FinalizationReport(
                recoveryOnly ? FinalizationOutcome.EscapeRecovery : FinalizationOutcome.Held,
                transcript,
                finalized,
                TranscriptionMilliseconds: timer.ElapsedMilliseconds);
            _effects.ShowHeldStatus(report);
            return report;
        }
        catch (TranscriptionEngineException exception)
        {
            timer.Stop();
            _effects.RecordTranscriptionFailed(exception.Error, timer.ElapsedMilliseconds);
            await CompleteAndResetAsync(sessionId).ConfigureAwait(false);
            _effects.ShowTranscriptionFailed();
            return new FinalizationReport(
                FinalizationOutcome.TranscriptionFailed,
                TranscriptionError: exception.Error,
                TranscriptionMilliseconds: timer.ElapsedMilliseconds);
        }
        finally
        {
            waitTimer.Stop();
            _effects.RecordDictationCompleted(waitTimer.ElapsedMilliseconds);
        }
    }

    private async Task CompleteAndResetAsync(DictationSessionId sessionId)
    {
        await _controller.CompleteAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        await _controller.ResetAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
