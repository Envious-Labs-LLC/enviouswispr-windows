using System.Diagnostics;
using System.Text;
using EnviousWispr.Core.Diagnostics;
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

    void RecordLearnedWordCheck(long elapsedMilliseconds, DiagnosticProvider? provider, bool changed)
    {
    }

    void RecordDeterministicProcessingFinished(bool degraded, long elapsedMilliseconds, bool empty = false);
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
    private readonly LearnedWordCheckStep? _learnedWordCheck;

    public TranscriptFinalizer(
        DeterministicTextPipeline pipeline,
        PolishExecutor polish,
        ITranscriptFinalizationEffects effects,
        SnippetExpansionStage snippets,
        LearnedWordCheckStep? learnedWordCheck = null)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(polish);
        ArgumentNullException.ThrowIfNull(effects);
        ArgumentNullException.ThrowIfNull(snippets);
        _pipeline = pipeline;
        _polish = polish;
        _effects = effects;
        _snippets = snippets;
        _learnedWordCheck = learnedWordCheck;
    }

    public Task<FinalizedTranscript> FinalizeAsync(
        Transcript transcript,
        IReadOnlyList<CustomWordEntry> customWords,
        DeterministicTextOptions options,
        SnippetVocabulary snippets,
        PolishSetup? polish,
        CancellationToken cancellationToken) =>
        FinalizeAsync(
            transcript,
            customWords,
            options,
            snippets,
            polish,
            targetAppName: null,
            cancellationToken);

    public Task<FinalizedTranscript> FinalizeAsync(
        Transcript transcript,
        IReadOnlyList<CustomWordEntry> customWords,
        DeterministicTextOptions options,
        SnippetVocabulary snippets,
        PolishSetup? polish,
        string? targetAppName,
        CancellationToken cancellationToken) =>
        FinalizeAsync(
            transcript,
            customWords,
            options,
            snippets,
            polish,
            targetAppName,
            enabledVocabularyPackIds: null,
            cancellationToken);

    public Task<FinalizedTranscript> FinalizeAsync(
        Transcript transcript,
        IReadOnlyList<CustomWordEntry> customWords,
        DeterministicTextOptions options,
        SnippetVocabulary snippets,
        PolishSetup? polish,
        string? targetAppName,
        IReadOnlyList<string>? enabledVocabularyPackIds,
        CancellationToken cancellationToken) =>
        FinalizeAsync(
            transcript,
            customWords,
            options,
            snippets,
            polish,
            targetAppName,
            enabledVocabularyPackIds,
            vocabularyPackOverrides: null,
            cancellationToken);

    public Task<FinalizedTranscript> FinalizeAsync(
        Transcript transcript,
        IReadOnlyList<CustomWordEntry> customWords,
        DeterministicTextOptions options,
        SnippetVocabulary snippets,
        PolishSetup? polish,
        string? targetAppName,
        IReadOnlyList<string>? enabledVocabularyPackIds,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, VocabularyPackWordOverride>>? vocabularyPackOverrides,
        CancellationToken cancellationToken) =>
        FinalizeAsync(
            transcript,
            customWords,
            options,
            snippets,
            polish,
            targetAppName,
            enabledVocabularyPackIds,
            vocabularyPackOverrides,
            persistRecoveryText: true,
            cancellationToken);

    public async Task<FinalizedTranscript> FinalizeAsync(
        Transcript transcript,
        IReadOnlyList<CustomWordEntry> customWords,
        DeterministicTextOptions options,
        SnippetVocabulary snippets,
        PolishSetup? polish,
        string? targetAppName,
        IReadOnlyList<string>? enabledVocabularyPackIds,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, VocabularyPackWordOverride>>? vocabularyPackOverrides,
        bool persistRecoveryText,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(customWords);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(snippets);

        _effects.RecordDeterministicProcessingStarted();
        var processingTimer = Stopwatch.StartNew();
        var request = new DeterministicTextRequest(
            transcript,
            customWords,
            options,
            EnabledVocabularyPackIds: enabledVocabularyPackIds,
            VocabularyPackOverrides: vocabularyPackOverrides);
        var expansion = await _snippets.ExpandAsync(transcript.Text, snippets, cancellationToken).ConfigureAwait(false);
        var records = expansion.Outcome.Records;
        var masked = records.Count == 0
            ? request
            : request with { Transcript = transcript with { Text = expansion.Outcome.Text } };
        var processed = await _pipeline.ProcessAsync(masked, cancellationToken).ConfigureAwait(false);
        var rolledBack = false;
        if (records.Count > 0 &&
            string.IsNullOrWhiteSpace(SnippetFinalizer.Resolve(processed.DeterministicText, null, records).Text))
        {
            // NOT SILENCE. A snippet whose whole text was a fill-in with nothing to fill it - an empty
            // clipboard - would deliver nothing at all; the take delivers the spoken words instead, as
            // macOS does, so a dictation is never lost to an empty snippet.
            records = [];
            rolledBack = true;
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

        if (_learnedWordCheck is not null &&
            options.WordCorrectionEnabled &&
            options.FixLearnedWordsEnabled &&
            !expansion.IsDegraded &&
            !rolledBack)
        {
            var checkOutcome = await _learnedWordCheck.ProcessAsync(
                processed.Output.Text,
                customWords,
                processed.LearnLanguage ?? processed.CleanupLanguage ?? transcript.DetectedLanguage ?? transcript.RecognitionLanguage,
                cancellationToken).ConfigureAwait(false);
            if (checkOutcome.Flagged > 0)
            {
                _effects.RecordLearnedWordCheck(
                    checkOutcome.ElapsedMilliseconds,
                    DiagnosticProviderIds.FromProviderId(checkOutcome.ProviderId),
                    checkOutcome.Applied > 0);
            }

            if (checkOutcome.Applied > 0)
            {
                processed = processed with
                {
                    Output = processed.Output with { Text = checkOutcome.Text },
                    DeterministicText = checkOutcome.Text,
                };
            }
        }

        var deterministic = SnippetFinalizer.Resolve(processed.DeterministicText, null, records).Text;
        if (persistRecoveryText)
        {
            await _effects.SaveRecoveryTextAsync(processed.Output with { Text = deterministic }, cancellationToken)
                .ConfigureAwait(false);
        }

        // A TAKE THAT IS NOTHING BUT SNIPPETS HAS NOTHING TO POLISH. A model handed a bare sentinel can
        // only return it or lose it, so it is not asked.
        //
        // NOR DOES AN ULTRA-SHORT TAKE (<= 3 words, or < 10 non-whitespace chars for unsegmented scripts):
        // LLMs hallucinate or treat 1-3 word dictations as prompts rather than text to clean (#1022).
        //
        // NOR DOES A TAKE WHOSE SNIPPET STAGE STOOD DOWN OR WAS ROLLED BACK. Its text still carries the
        // keyword and the trigger as spoken, so a snippet may have been meant - and without a mask, a
        // polish would send those words to the model and could rewrite them. The cleaned spoken text
        // is delivered instead, the stage's own floor.
        var resolvedLanguage = processed.CleanupLanguage ?? processed.TextLanguage ?? transcript.DetectedLanguage;
        var polishSetup = expansion.IsDegraded || rolledBack ||
            !HasMinimumPolishLength(processed.Output.Text, resolvedLanguage) ||
            (records.Count > 0 && !HasWordsOutsideSentinels(processed.Output.Text, records))
            ? null
            : polish;
        var polishResult = await _polish
            .TryPolishAsync(
                polishSetup,
                processed.Output,
                resolvedLanguage,
                targetAppName,
                transcript.RecognitionLanguage,
                cancellationToken)
            .ConfigureAwait(false);
        var polishReview = polishResult is null || polishResult.UsedFallback
            ? new PolishOutputReview(PolishOutputVerdict.Accepted, string.Empty)
            : PolishOutputGuard.Review(processed.Output.Text, polishResult.Output.Text, inferQuestionFromOpening: true);
        var polishVerdict = polishReview.Verdict;
        if (polishVerdict != PolishOutputVerdict.Accepted)
        {
            _effects.RecordPolishRefused();
        }

        var delivered = deterministic;
        var polishedCasualStepExecuted = false;
        if (polishResult is not null && !polishResult.UsedFallback &&
            polishVerdict == PolishOutputVerdict.Accepted)
        {
            // THE REVIEWED TEXT, NOT WHAT THE PROVIDER SENT. The guard strips what the model wrote
            // ABOUT the text - "Sure, here is the cleaned transcript:" - and using the raw string here
            // would put that chatter in somebody's document with their words.
            processed = await _pipeline
                .ApplyPolishedTextAsync(masked, processed, polishReview.Text, cancellationToken)
                .ConfigureAwait(false);
            polishedCasualStepExecuted = request.Options.CasualModeEnabled;

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
                if (persistRecoveryText)
                {
                    await _effects.SaveRecoveryTextAsync(processed.Output with { Text = delivered }, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        processed = processed with
        {
            Output = processed.Output with { Text = delivered },
            DeterministicText = deterministic,
        };
        var restorationReceipts = polishedCasualStepExecuted
            ? processed.Receipts
            : processed.Receipts.Where(receipt => receipt.Stage is not DeterministicTextStage.CasualEnding).ToArray();
        _effects.EmitStageReceipts(restorationReceipts, emojiRestorationOnly: true);
        var restorationMilliseconds = processed.Receipts
            .Where(receipt => receipt.Stage is
                DeterministicTextStage.EnglishSpellingAfterPolish or
                DeterministicTextStage.EmojiRestoration ||
                (polishedCasualStepExecuted && receipt.Stage is DeterministicTextStage.CasualEnding))
            .Sum(receipt => receipt.ElapsedMilliseconds);
        // THE SUMMARY IS LAST, BECAUSE IT IS THE ONLY LINE THAT CAN STILL BE WRONG. IsDegraded turns
        // true when restoration times out or fails, and the duration has to include the restoration
        // that a failure happened inside. Written before polish, a degraded pass reported itself as clean.
        _effects.RecordDeterministicProcessingFinished(
            processed.IsDegraded,
            deterministicMilliseconds + restorationMilliseconds,
            string.IsNullOrWhiteSpace(processed.Output.Text));

        return new FinalizedTranscript(request, processed, polishResult, polishVerdict)
        {
            SnippetsExpanded = records.Count,
        };
    }

    private const int MinimumWordsForPolish = 3;
    private const int MinimumUnsegmentedCharsForPolish = 10;

    private static readonly HashSet<string> UnsegmentedLanguages = new(
        ["ja", "zh", "yue", "th", "lo"],
        StringComparer.OrdinalIgnoreCase);

    internal static bool HasMinimumPolishLength(string text, string? language)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var baseLang = SpokenPunctuationRules.BaseCode(language);
        if (baseLang is not null && UnsegmentedLanguages.Contains(baseLang))
        {
            var nonWhitespaceRunes = 0;
            foreach (var rune in text.EnumerateRunes())
            {
                if (!Rune.IsWhiteSpace(rune))
                {
                    nonWhitespaceRunes++;
                    if (nonWhitespaceRunes >= MinimumUnsegmentedCharsForPolish)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        var words = 0;
        var inWord = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                inWord = false;
            }
            else if (!inWord)
            {
                inWord = true;
                words++;
                if (words > MinimumWordsForPolish)
                {
                    return true;
                }
            }
        }

        return false;
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
