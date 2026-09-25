using System.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Settings;
using EnviousWispr.PostProcessing;

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
/// <param name="Request">The deterministic request the text came from, kept for the record: the recogniser's words, never a masked copy.</param>
/// <param name="Processed">The text after every stage that ran, and their receipts, with every snippet in place.</param>
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

    /// <summary>How many snippets the delivered text carries. Delivery leaves the words of a take with any as they are.</summary>
    public int SnippetsExpanded { get; init; }
}

/// <summary>
/// Turns a transcript into the text that will be delivered: the snippet mask, the deterministic stages,
/// an optional polish, the review that decides whether the polish is worth using, the restoration of
/// what the model was not allowed to touch, and the snippets put back. The order is the product's, from
/// <c>pipeline.md</c>.
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
///
/// A SNIPPET IS MASKED BEFORE THE FIRST STAGE AND PUT BACK BEFORE ANYTHING READS THE TEXT. The polish
/// provider - EG-1, Ollama or a cloud model - receives a sentinel where the saved text belongs, so it
/// can never see or rewrite it (macOS #628). The sentinels are resolved here, before the recovery copy
/// is written and before this returns, so the runner, History, the recovery store and delivery only
/// ever hold the person's own text. A polish that lost or doubled a sentinel is refused whole.
/// </remarks>
public sealed class TranscriptFinalizer
{
    private readonly DeterministicTextPipeline _pipeline;
    private readonly PolishExecutor _polish;
    private readonly ITranscriptFinalizationEffects _effects;
    private readonly SnippetExpansionStage _snippets;

    public TranscriptFinalizer(
        DeterministicTextPipeline pipeline,
        PolishExecutor polish,
        ITranscriptFinalizationEffects effects,
        SnippetExpansionStage snippets)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(polish);
        ArgumentNullException.ThrowIfNull(effects);
        ArgumentNullException.ThrowIfNull(snippets);
        _pipeline = pipeline;
        _polish = polish;
        _effects = effects;
        _snippets = snippets;
    }

    public async Task<FinalizedTranscript> FinalizeAsync(
        Transcript transcript,
        IReadOnlyList<CustomWordEntry> customWords,
        DeterministicTextOptions options,
        SnippetVocabulary snippets,
        PolishSetup? polish,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(customWords);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(snippets);

        _effects.RecordDeterministicProcessingStarted();
        var processingTimer = Stopwatch.StartNew();
        var request = new DeterministicTextRequest(transcript, customWords, options);
        var expansion = await _snippets.ExpandAsync(transcript.Text, snippets, cancellationToken).ConfigureAwait(false);
        var records = expansion.Outcome.Records;
        var masked = records.Count == 0
            ? request
            : request with { Transcript = transcript with { Text = expansion.Outcome.Text } };
        var processed = await _pipeline.ProcessAsync(masked, cancellationToken).ConfigureAwait(false);
        if (records.Count > 0 &&
            string.IsNullOrWhiteSpace(SnippetFinalizer.Resolve(processed.DeterministicText, null, records).Text))
        {
            // NOT SILENCE. A snippet whose whole text was a fill-in with nothing to fill it - an empty
            // clipboard - would deliver nothing at all; the take delivers the spoken words instead, as
            // macOS does, so a dictation is never lost to an empty snippet.
            records = [];
            masked = request;
            expansion = expansion with { Receipt = expansion.Receipt with { Changed = false } };
            processed = await _pipeline.ProcessAsync(request, cancellationToken).ConfigureAwait(false);
        }

        processed = processed with
        {
            Receipts = [expansion.Receipt, .. processed.Receipts],
            IsDegraded = processed.IsDegraded || expansion.IsDegraded,
        };
        processingTimer.Stop();
        var deterministicMilliseconds = processingTimer.ElapsedMilliseconds;
        _effects.EmitStageReceipts(processed.Receipts, emojiRestorationOnly: false);
        var deterministic = SnippetFinalizer.Resolve(processed.DeterministicText, null, records).Text;
        await _effects.SaveRecoveryTextAsync(processed.Output with { Text = deterministic }, cancellationToken)
            .ConfigureAwait(false);

        // A TAKE THAT IS NOTHING BUT SNIPPETS HAS NOTHING TO POLISH. A model handed a bare sentinel can
        // only return it or lose it, so it is not asked - macOS reaches the same outcome through its
        // short-text bypass, which this app does not have.
        var polishSetup = records.Count > 0 && !HasWordsOutsideSentinels(processed.Output.Text, records)
            ? null
            : polish;
        var polishResult = await _polish
            .TryPolishAsync(polishSetup, processed.Output, transcript.DetectedLanguage, cancellationToken)
            .ConfigureAwait(false);
        var polishReview = polishResult is null || polishResult.UsedFallback
            ? new PolishOutputReview(PolishOutputVerdict.Accepted, string.Empty)
            : PolishOutputGuard.Review(processed.Output.Text, polishResult.Output.Text);
        var polishVerdict = polishReview.Verdict;
        if (polishVerdict != PolishOutputVerdict.Accepted)
        {
            _effects.RecordPolishRefused();
        }

        var delivered = deterministic;
        if (polishResult is not null && !polishResult.UsedFallback &&
            polishVerdict == PolishOutputVerdict.Accepted)
        {
            // THE REVIEWED TEXT, NOT WHAT THE PROVIDER SENT. The guard strips what the model wrote
            // ABOUT the text - "Sure, here is the cleaned transcript:" - and using the raw string here
            // would put that chatter in somebody's document with their words.
            processed = await _pipeline
                .ApplyPolishedTextAsync(masked, processed, polishReview.Text, cancellationToken)
                .ConfigureAwait(false);

            // RESOLVED AFTER THE LAST WRITER. The spelling and emoji restoration over the polish ran on
            // the masked text, so whether every sentinel survived is asked of what they produced.
            var resolution = SnippetFinalizer.Resolve(processed.DeterministicText, processed.Output.Text, records);
            if (resolution.RejectedPolish)
            {
                polishVerdict = PolishOutputVerdict.RefusedSnippetLost;
                _effects.RecordPolishRefused();
            }
            else
            {
                delivered = resolution.PolishedText!;
                await _effects.SaveRecoveryTextAsync(processed.Output with { Text = delivered }, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        processed = processed with
        {
            Output = processed.Output with { Text = delivered },
            DeterministicText = deterministic,
        };
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

        return new FinalizedTranscript(request, processed, polishResult, polishVerdict)
        {
            SnippetsExpanded = records.Count,
        };
    }

    /// <summary>Whether anything a model could improve is left once every sentinel is taken out.</summary>
    private static bool HasWordsOutsideSentinels(string text, IReadOnlyList<SnippetExpansionRecord> records)
    {
        var rest = records.Aggregate(
            text,
            (current, record) => current.Replace(record.Sentinel, " ", StringComparison.Ordinal));
        return rest.Any(char.IsLetterOrDigit);
    }
}
