using System.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Pipeline;

/// <summary>
/// The content-free lines and the recovery writes a finalisation makes on its way, owned by the shell.
/// None of them decides anything about the text.
/// </summary>
public interface ITranscriptFinalizationEffects : IPolishAttemptEffects
{
    void RecordDeterministicProcessingStarted();

    /// <summary>One line per finished stage; the restoration stage reports on its own, later.</summary>
    void EmitStageReceipts(IReadOnlyList<DeterministicStageReceipt> receipts, bool emojiRestorationOnly);

    /// <summary>The best text so far, kept where a crash cannot lose it.</summary>
    Task SaveRecoveryTextAsync(ProcessedText output, CancellationToken cancellationToken);

    /// <summary>What came back from polish was not worth showing anyone.</summary>
    void RecordPolishRefused();

    void RecordDeterministicProcessingFinished(bool degraded, long elapsedMilliseconds);
}

/// <summary>What a finalisation decided: the text to deliver, and how it got there.</summary>
/// <param name="Request">The deterministic request the text came from, kept for the record.</param>
/// <param name="Processed">The text after every stage that ran, and their receipts.</param>
/// <param name="Polish">The polish attempt, or null when there was no provider or nothing to polish.</param>
/// <param name="PolishVerdict">Whether the polished text was accepted; Accepted when no polish ran.</param>
public sealed record FinalizedTranscript(
    DeterministicTextRequest Request,
    DeterministicTextResult Processed,
    PolishResult? Polish,
    PolishOutputVerdict PolishVerdict)
{
    /// <summary>True when the delivered text is the polished one rather than the deterministic one.</summary>
    public bool WasPolished =>
        Polish is { Status: PolishAttemptStatus.Polished } && PolishVerdict == PolishOutputVerdict.Accepted;
}

/// <summary>
/// Turns a transcript into the text that will be delivered: the deterministic stages, an optional
/// polish, the review that decides whether the polish is worth using, and the restoration of what the
/// model was not allowed to touch. The order is the product's, from <c>pipeline.md</c>.
/// </summary>
/// <remarks>
/// A MODEL THAT COMES OFF THE RAILS RETURNS A CONFIDENT STRING RATHER THAN AN ERROR, so a polish
/// result that says it succeeded has proven nothing yet. The review is the only place that asks whether
/// what came back is worth showing anyone. Polish is a limb and the transcript is the heart: a refusal
/// leaves the user with the cleaned text they already had, the same outcome as any other limb failure.
///
/// THE FOUR STAGES THAT ARE FINISHED REPORT NOW; THE ONE THAT IS NOT WAITS. Applying the polish runs
/// emoji restoration again and REPLACES its receipt, so reporting that stage early recorded Skipped and
/// left a later restoration failure hidden behind a healthy line. Split rather than all-after-polish,
/// because the log is append-ordered and read oldest-first: holding the four back and stamping them
/// with an earlier time put older lines after newer ones whenever polish was slow.
/// </remarks>
public sealed class TranscriptFinalizer
{
    private readonly DeterministicTextPipeline _pipeline;
    private readonly PolishExecutor _polish;
    private readonly ITranscriptFinalizationEffects _effects;

    public TranscriptFinalizer(
        DeterministicTextPipeline pipeline,
        PolishExecutor polish,
        ITranscriptFinalizationEffects effects)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(polish);
        ArgumentNullException.ThrowIfNull(effects);
        _pipeline = pipeline;
        _polish = polish;
        _effects = effects;
    }

    public async Task<FinalizedTranscript> FinalizeAsync(
        Transcript transcript,
        IReadOnlyList<CustomWordEntry> customWords,
        DeterministicTextOptions options,
        PolishSetup? polish,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(customWords);
        ArgumentNullException.ThrowIfNull(options);

        _effects.RecordDeterministicProcessingStarted();
        var processingTimer = Stopwatch.StartNew();
        var request = new DeterministicTextRequest(transcript, customWords, options);
        var processed = await _pipeline.ProcessAsync(request, cancellationToken).ConfigureAwait(false);
        processingTimer.Stop();
        var deterministicMilliseconds = processingTimer.ElapsedMilliseconds;
        _effects.EmitStageReceipts(processed.Receipts, emojiRestorationOnly: false);
        await _effects.SaveRecoveryTextAsync(processed.Output, cancellationToken).ConfigureAwait(false);

        var polishResult = await _polish
            .TryPolishAsync(polish, processed.Output, transcript.DetectedLanguage, cancellationToken)
            .ConfigureAwait(false);
        var polishReview = polishResult is null || polishResult.UsedFallback
            ? new PolishOutputReview(PolishOutputVerdict.Accepted, string.Empty)
            : PolishOutputGuard.Review(processed.Output.Text, polishResult.Output.Text);
        var polishVerdict = polishReview.Verdict;
        if (polishVerdict != PolishOutputVerdict.Accepted)
        {
            _effects.RecordPolishRefused();
        }

        if (polishResult is not null && !polishResult.UsedFallback &&
            polishVerdict == PolishOutputVerdict.Accepted)
        {
            // THE REVIEWED TEXT, NOT WHAT THE PROVIDER SENT. The guard strips what the model wrote
            // ABOUT the text - "Sure, here is the cleaned transcript:" - and using the raw string here
            // would put that chatter in somebody's document with their words.
            processed = await _pipeline
                .ApplyPolishedTextAsync(request, processed, polishReview.Text, cancellationToken)
                .ConfigureAwait(false);
            await _effects.SaveRecoveryTextAsync(processed.Output, cancellationToken).ConfigureAwait(false);
        }

        _effects.EmitStageReceipts(processed.Receipts, emojiRestorationOnly: true);
        var restorationMilliseconds = processed.Receipts
            .Where(receipt => receipt.Stage is
                DeterministicTextStage.EnglishSpellingAfterPolish or
                DeterministicTextStage.EmojiRestoration)
            .Sum(receipt => receipt.ElapsedMilliseconds);
        // THE SUMMARY IS LAST, BECAUSE IT IS THE ONLY LINE THAT CAN STILL BE WRONG. IsDegraded turns
        // true when restoration times out or fails, and the duration has to include the restoration
        // that a failure happened inside. Written before polish, a degraded pass reported itself as clean.
        _effects.RecordDeterministicProcessingFinished(
            processed.IsDegraded,
            deterministicMilliseconds + restorationMilliseconds);

        return new FinalizedTranscript(request, processed, polishResult, polishVerdict);
    }
}
