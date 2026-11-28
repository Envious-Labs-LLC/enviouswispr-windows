using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Settings;
using EnviousWispr.PostProcessing;

namespace EnviousWispr.Pipeline;

public sealed record DeterministicStageReceipt(
    DeterministicTextStage Stage,
    DeterministicStageStatus Status,
    bool Changed,
    long ElapsedMilliseconds);

public sealed record DeterministicTextOptions(
    bool WordCorrectionEnabled,
    bool FillerRemovalEnabled,
    bool EmojiFormatterEnabled,
    bool SpokenPunctuationEnabled,
    EnglishSpelling EnglishSpelling = EnglishSpelling.American,
    bool CasualModeEnabled = false,
    bool FixLearnedWordsEnabled = true,
    IReadOnlyDictionary<string, string>? SpokenPunctuationStartWords = null,
    bool EnglishPeriodWordEnabled = true)
{
    public static DeterministicTextOptions From(DictationPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        return new DeterministicTextOptions(
            preferences.WordCorrectionEnabled,
            preferences.FillerRemovalEnabled,
            preferences.EmojiFormatterEnabled,
            preferences.SpokenPunctuationEnabled,
            preferences.EnglishSpelling,
            preferences.CasualModeEnabled,
            preferences.FixLearnedWordsEnabled);
    }
}

public sealed record DeterministicTextRequest(
    Transcript Transcript,
    IReadOnlyList<CustomWordEntry> CustomWords,
    DeterministicTextOptions Options,
    string? PolishedText = null,
    IReadOnlyList<string>? EnabledVocabularyPackIds = null,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, VocabularyPackWordOverride>>? VocabularyPackOverrides = null);

public sealed record DeterministicTextResult(
    ProcessedText Output,
    IReadOnlyList<DeterministicStageReceipt> Receipts,
    bool IsDegraded)
{
    public string DeterministicText { get; init; } = Output.Text;
    public bool SpokenFinalFullStop { get; init; }
    public string? CleanupLanguage { get; init; }
    public bool EnglishVeto { get; init; }
    public string? TextLanguage { get; init; }
    public string? LearnLanguage { get; init; }
    public DictationConfidenceBucket ConfidenceBucket { get; init; }
    public ItnRoute? ItnRoute { get; init; }
}

public sealed record DeterministicTextContext(
    Transcript Transcript,
    string Text,
    IReadOnlyList<CustomWordEntry> CustomWords,
    DeterministicTextOptions Options,
    string? PolishedText = null,
    bool SpokenFinalFullStop = false,
    IReadOnlyList<string>? EnabledVocabularyPackIds = null,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, VocabularyPackWordOverride>>? VocabularyPackOverrides = null,
    string? CleanupLanguage = null,
    bool EnglishVeto = false,
    string? TextLanguage = null,
    string? LearnLanguage = null,
    DictationConfidenceBucket ConfidenceBucket = DictationConfidenceBucket.None,
    ItnRoute? ItnRoute = null);

public interface IDeterministicTextStep
{
    DeterministicTextStage Stage { get; }

    TimeSpan Timeout { get; }

    TimeSpan GetTimeout(DeterministicTextContext context) => Timeout;

    bool IsEnabled(DeterministicTextContext context);

    DeterministicTextContext Process(DeterministicTextContext context, CancellationToken cancellationToken);
}

public sealed class DeterministicTextPipeline
{
    private readonly IDeterministicTextStep[] _steps;
    private readonly DeterministicStageExecutor _executor;

    /// <summary>See <see cref="DeterministicStageExecutor.OutstandingInvocation"/>.</summary>
    internal Task<DeterministicTextContext>? OutstandingInvocation(IDeterministicTextStep step) =>
        _executor.OutstandingInvocation(step);

    public DeterministicTextPipeline()
        : this(CreateDefaultSteps())
    {
        // OFF EVERY TIMED PATH. Inverse text normalisation builds heavy non-backtracking patterns on
        // first use; paying that here, at construction, keeps it out of the 500 ms ITN stage where a
        // loaded runner once crossed the timeout and dropped normalisation. The app builds this once
        // at startup, so a user's first dictation is already warm. Ref: #91, #112.
        InverseTextNormalizer.Warm();
        SeamCasingOracleRuntime.Shared.Prewarm();
    }

    public DeterministicTextPipeline(IReadOnlyList<IDeterministicTextStep> steps)
        : this(steps, timeProvider: null, taskScheduler: null)
    {
    }

    internal DeterministicTextPipeline(
        IReadOnlyList<IDeterministicTextStep> steps,
        TimeProvider? timeProvider,
        TaskScheduler? taskScheduler)
    {
        ArgumentNullException.ThrowIfNull(steps);
        _steps = steps.ToArray();
        _executor = new DeterministicStageExecutor(timeProvider, taskScheduler);
    }

    public async Task<DeterministicTextResult> ProcessAsync(
        DeterministicTextRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Transcript);
        ArgumentNullException.ThrowIfNull(request.CustomWords);
        ArgumentNullException.ThrowIfNull(request.Options);

        var resolution = DeliveryLanguagePolicy.ResolveFor(request.Transcript, request.Transcript.Text);
        var route = InverseTextNormalizationGate.ResolveRoute(
            resolution.Language,
            resolution.EnglishVeto,
            DeliveryLanguagePolicy.EngineDetectsLanguage(request.Transcript));

        var context = new DeterministicTextContext(
            request.Transcript,
            request.Transcript.Text,
            request.CustomWords.ToArray(),
            request.Options,
            request.PolishedText,
            SpokenFinalFullStop: false,
            EnabledVocabularyPackIds: request.EnabledVocabularyPackIds,
            VocabularyPackOverrides: request.VocabularyPackOverrides,
            CleanupLanguage: resolution.Language,
            EnglishVeto: resolution.EnglishVeto,
            TextLanguage: resolution.TextLanguage,
            LearnLanguage: resolution.LearnLanguage,
            ConfidenceBucket: resolution.ConfidenceBucket,
            ItnRoute: route);
        var receipts = new List<DeterministicStageReceipt>(_steps.Length);
        var degraded = false;
        foreach (var step in _steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!step.IsEnabled(context))
            {
                receipts.Add(new DeterministicStageReceipt(
                    step.Stage,
                    DeterministicStageStatus.Skipped,
                    Changed: false,
                    ElapsedMilliseconds: 0));
                continue;
            }

            var execution = await _executor.ExecuteAsync(
                step,
                context,
                static (input, output) =>
                    !string.Equals(input.Text, output.Text, StringComparison.Ordinal) ||
                    !string.Equals(input.PolishedText, output.PolishedText, StringComparison.Ordinal),
                cancellationToken).ConfigureAwait(false);
            context = execution.Context;
            receipts.Add(execution.Receipt);
            degraded |= execution.IsDegraded;
        }

        var deterministicText = context.Text;
        var finalText = context.PolishedText ?? deterministicText;
        return new DeterministicTextResult(
            new ProcessedText(request.Transcript.SessionId, finalText),
            receipts,
            degraded)
        {
            DeterministicText = deterministicText,
            SpokenFinalFullStop = context.SpokenFinalFullStop,
            CleanupLanguage = context.CleanupLanguage,
            EnglishVeto = context.EnglishVeto,
            TextLanguage = context.TextLanguage,
            LearnLanguage = context.LearnLanguage,
            ConfidenceBucket = context.ConfidenceBucket,
            ItnRoute = context.ItnRoute,
        };
    }

    public async Task<DeterministicTextResult> ApplyPolishedTextAsync(
        DeterministicTextRequest request,
        DeterministicTextResult deterministicResult,
        string polishedText,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(deterministicResult);
        ArgumentNullException.ThrowIfNull(polishedText);
        var input = new DeterministicTextContext(
            request.Transcript,
            deterministicResult.DeterministicText,
            request.CustomWords,
            request.Options,
            polishedText,
            deterministicResult.SpokenFinalFullStop,
            request.EnabledVocabularyPackIds,
            request.VocabularyPackOverrides,
            deterministicResult.CleanupLanguage,
            deterministicResult.EnglishVeto,
            deterministicResult.TextLanguage,
            deterministicResult.LearnLanguage,
            deterministicResult.ConfidenceBucket,
            deterministicResult.ItnRoute);
        // THE SPELLING FIRST, THEN THE EMOJI: the restorer aligns the polish with the deterministic text, and
        // both are British by then. Ref: macOS #3124 runs the same two in the same order.
        var spelling = _steps.SingleOrDefault(candidate => candidate.Stage == DeterministicTextStage.EnglishSpellingAfterPolish);
        DeterministicStageReceipt? spellingReceipt = null;
        var spellingDegraded = false;
        if (spelling is not null && spelling.IsEnabled(input))
        {
            var spelled = await _executor.ExecuteAsync(
                spelling,
                input,
                static (before, after) =>
                    !string.Equals(before.PolishedText, after.PolishedText, StringComparison.Ordinal),
                cancellationToken).ConfigureAwait(false);
            input = spelled.Context;
            spellingReceipt = spelled.Receipt;
            spellingDegraded = spelled.IsDegraded;
        }

        var step = _steps.Single(candidate => candidate.Stage == DeterministicTextStage.EmojiRestoration);
        // INVALID RESTORATION MUST PRESERVE THE POLISH. A null context or null Text now fails just
        // like a main-loop stage, keeping the supplied polish instead of dereferencing invalid output.
        var execution = await _executor.ExecuteAsync(
            step,
            input,
            static (before, after) =>
                !string.Equals(before.PolishedText, after.PolishedText, StringComparison.Ordinal),
            cancellationToken).ConfigureAwait(false);
        var context = execution.Context;

        var casualEnding = _steps.SingleOrDefault(candidate => candidate.Stage == DeterministicTextStage.CasualEnding);
        DeterministicStageReceipt? casualEndingReceipt = null;
        var casualEndingDegraded = false;
        if (casualEnding is not null && casualEnding.IsEnabled(context))
        {
            var casualExecuted = await _executor.ExecuteAsync(
                casualEnding,
                context,
                static (before, after) =>
                    !string.Equals(before.PolishedText, after.PolishedText, StringComparison.Ordinal),
                cancellationToken).ConfigureAwait(false);
            context = casualExecuted.Context;
            casualEndingReceipt = casualExecuted.Receipt;
            casualEndingDegraded = casualExecuted.IsDegraded;
        }

        var degraded = deterministicResult.IsDegraded || spellingDegraded || execution.IsDegraded || casualEndingDegraded;
        var receipt = execution.Receipt;
        var receipts = deterministicResult.Receipts
            .Select(existing => existing.Stage switch
            {
                DeterministicTextStage.EmojiRestoration => receipt,
                DeterministicTextStage.EnglishSpellingAfterPolish when spellingReceipt is not null => spellingReceipt,
                DeterministicTextStage.CasualEnding when casualEndingReceipt is not null => casualEndingReceipt,
                _ => existing,
            })
            .ToArray();
        return new DeterministicTextResult(
            new ProcessedText(request.Transcript.SessionId, context.PolishedText ?? polishedText),
            receipts,
            degraded)
        {
            DeterministicText = deterministicResult.DeterministicText,
            SpokenFinalFullStop = deterministicResult.SpokenFinalFullStop,
            CleanupLanguage = deterministicResult.CleanupLanguage,
            EnglishVeto = deterministicResult.EnglishVeto,
            TextLanguage = deterministicResult.TextLanguage,
            LearnLanguage = deterministicResult.LearnLanguage,
            ConfidenceBucket = deterministicResult.ConfidenceBucket,
            ItnRoute = deterministicResult.ItnRoute,
        };
    }

    /// <summary>
    /// Runs every enabled stage once, in order, on a fixed sentence that exercises each of them, with
    /// no deadline, so a person's first dictation does not pay the stages' first-call cost. Returns
    /// the stages that threw; the caller decides how to report them. Ref: #239.
    /// </summary>
    /// <remarks>
    /// OUTSIDE THE EXECUTOR ON PURPOSE. Warmed through the executor, a warm-up still running when the
    /// first dictation arrives would make that dictation's stage Busy, which is the very loss this
    /// exists to prevent; and a warm-up held to the stage's deadline would stand down exactly when
    /// warming is needed most. The stages keep no state between calls (their tables and generated
    /// patterns are read-only), so a dictation that overlaps the warm-up only shares its first-call cost.
    /// A stage that fails here fails alone: the next one is still warmed from the last valid text.
    /// </remarks>
    public IReadOnlyList<DeterministicTextStage> WarmStages()
    {
        var context = WarmUpContext();
        var failed = new List<DeterministicTextStage>();
        foreach (var step in _steps)
        {
            try
            {
                if (step.IsEnabled(context))
                {
                    var next = step.Process(context, CancellationToken.None);
                    if (next is null || next.Text is null)
                    {
                        throw new InvalidOperationException("A deterministic text stage returned no context.");
                    }

                    context = next;
                }
            }
            catch (Exception exception) when (exception is not (StackOverflowException or OutOfMemoryException))
            {
                failed.Add(step.Stage);
            }
        }

        return failed;
    }

    /// <summary>
    /// A made-up sentence, never a person's words, that turns every stage on: a custom word, a filler, a
    /// spoken emoji, a number, an American spelling, and a polish that dropped the emoji for restoration.
    /// </summary>
    private static DeterministicTextContext WarmUpContext() =>
        new(
            new Transcript(DictationSessionId.Create(), WarmUpSpoken, "warm-up", DetectedLanguage: "en"),
            WarmUpSpoken,
            [new CustomWordEntry("envy wisper", "EnviousWispr")],
            new DeterministicTextOptions(
                WordCorrectionEnabled: true,
                FillerRemovalEnabled: true,
                EmojiFormatterEnabled: true,
                SpokenPunctuationEnabled: true,
                EnglishSpelling: EnglishSpelling.British,
                CasualModeEnabled: true),
            PolishedText: "Send EnviousWispr the colour version at 3:30.");

    private const string WarmUpSpoken =
        "um so send envy wisper the color version thumbs up emoji at three thirty period";

    /// <summary>
    /// The production stages in their production order, for a test that wants the real text decisions
    /// without the real deadlines: wrap each one and hand the list to the other constructor.
    /// </summary>
    internal static IReadOnlyList<IDeterministicTextStep> DefaultSteps() => CreateDefaultSteps();

    /// <remarks>
    /// EVERY DEADLINE BELOW IS SET FROM A MEASUREMENT, recorded beside it (#239). The instrument is
    /// tools/stage-budget-bench: one fresh process per stage and run, the stages built as the app builds them,
    /// the stage's first call timed whole with no deadline. "Throttled" is Idle priority, EcoQoS and the
    /// efficiency cores only; 30 runs each on a hybrid desktop processor (8 performance, 16 efficiency cores),
    /// 2026-09-25. A deadline is at least five times the slowest throttled first call, and also clears that
    /// call plus the longest garbage-collection pause seen inside one call (105 ms throttled): a collection
    /// lands in whichever stage is running, and the deadline cannot tell it from slow work. The startup
    /// warm-up (<see cref="WarmStages"/>) takes the first-call cost off a person's first dictation; the
    /// deadlines are sized to hold without it, for the dictation that arrives before the warm-up finishes.
    /// Each stays a deadline: a stage that hangs still stands down at it with the last valid text.
    /// </remarks>
    private static IReadOnlyList<IDeterministicTextStep> CreateDefaultSteps()
    {
        SpokenEmojiFormatter? emojiFormatter;
        try
        {
            emojiFormatter = SpokenEmojiFormatter.LoadBundled();
        }
        catch (InvalidOperationException)
        {
            emojiFormatter = null;
        }

        return
        [
            new CustomWordStep(),
            new FillerStep(),
            new SpokenEmojiStep(emojiFormatter),
            new InverseTextNormalizationStep(),
            new EnglishSpellingStep(BritishSpellingConverter.Shared, afterPolish: false),
            new EnglishSpellingStep(BritishSpellingConverter.Shared, afterPolish: true),
            new EmojiRestorationStep(),
            new CasualEndingStep(),
        ];
    }

    private sealed class CustomWordStep : IDeterministicTextStep
    {
        public DeterministicTextStage Stage => DeterministicTextStage.CustomWords;

        /// <summary>Throttled first call: median 24 ms, slowest 51 ms (normal 12 ms). Unchanged: 3 s is 59 times the slowest.</summary>
        public TimeSpan Timeout => TimeSpan.FromSeconds(3);

        public bool IsEnabled(DeterministicTextContext context) =>
            context.Options.WordCorrectionEnabled &&
            (context.CustomWords.Count > 0 ||
             VocabularyPacks.HasAnyActiveTerms(context.EnabledVocabularyPackIds, context.VocabularyPackOverrides));

        public DeterministicTextContext Process(DeterministicTextContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var packEntries = VocabularyPacks.ResolveEntries(context.EnabledVocabularyPackIds, context.VocabularyPackOverrides);
            return context with
            {
                Text = CustomWordCorrector.Correct(
                    context.Text,
                    context.CustomWords,
                    packEntries,
                    excludeLearnedAliases: true,
                    reserveExcludedLearnedAliases: context.Options.FixLearnedWordsEnabled).Text,
            };
        }
    }

    private sealed class FillerStep : IDeterministicTextStep
    {
        public DeterministicTextStage Stage => DeterministicTextStage.FillerAndFalseStarts;

        /// <summary>Throttled first call: median 6 ms, slowest 19 ms (normal 3 ms). 50 ms was under 5x the slowest.</summary>
        public TimeSpan Timeout => TimeSpan.FromMilliseconds(200);

        public bool IsEnabled(DeterministicTextContext context) => context.Options.FillerRemovalEnabled;

        public DeterministicTextContext Process(DeterministicTextContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return context with
            {
                Text = FillerWordRemover.Remove(context.Text, context.CleanupLanguage, context.EnglishVeto),
            };
        }
    }

    private sealed class SpokenEmojiStep(SpokenEmojiFormatter? formatter) : IDeterministicTextStep
    {
        public DeterministicTextStage Stage => DeterministicTextStage.SpokenEmoji;

        /// <summary>Throttled first call: median 4 ms, slowest 14 ms (normal 2 ms). 50 ms was under 5x the slowest.</summary>
        public TimeSpan Timeout => TimeSpan.FromMilliseconds(200);

        public bool IsEnabled(DeterministicTextContext context) =>
            context.Options.EmojiFormatterEnabled &&
            formatter is not null &&
            IsEnglishDeterministicTake(context);

        public DeterministicTextContext Process(DeterministicTextContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return context with { Text = formatter!.Format(context.Text) };
        }
    }

    private sealed class InverseTextNormalizationStep : IDeterministicTextStep
    {
        public DeterministicTextStage Stage => DeterministicTextStage.InverseTextNormalization;

        public TimeSpan Timeout => TimeSpan.FromSeconds(ComputeTimeoutSeconds(0));

        public TimeSpan GetTimeout(DeterministicTextContext context) =>
            TimeSpan.FromSeconds(ComputeTimeoutSeconds(context.Text.Length));

        internal static double ComputeTimeoutSeconds(int characterCount) =>
            Math.Min(5.0, 0.5 + characterCount / 40_000.0);

        public bool IsEnabled(DeterministicTextContext context) => true;

        public DeterministicTextContext Process(DeterministicTextContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var route = ResolveEffectiveRoute(context);
            var spokenPunctuation = context.Options.SpokenPunctuationEnabled;
            var englishStartWord = string.Empty;
            var hasEnglishStartWord = route.Kind == ItnRouteKind.English &&
                TryResolveStartWord(
                    "en",
                    context.Options.SpokenPunctuationStartWords,
                    allowDefault: false,
                    out englishStartWord) &&
                !string.IsNullOrEmpty(englishStartWord);

            var execution = InverseTextNormalizationGate.Execute(
                context.Text,
                route,
                spokenPunctuation: spokenPunctuation && !hasEnglishStartWord,
                spokenSymbols: true);
            var text = execution.Text;
            var spokenFinalFullStop = execution.SpokenFinalFullStop;

            if (spokenPunctuation)
            {
                if (hasEnglishStartWord)
                {
                    var sw = InverseTextNormalizer.ApplyStartWordPunctuation(
                        text,
                        "en",
                        englishStartWord,
                        context.Options.EnglishPeriodWordEnabled);
                    text = sw.Text;
                    spokenFinalFullStop = sw.SpokenFinalFullStop;
                }
                else if (route.Kind != ItnRouteKind.English &&
                         !context.EnglishVeto &&
                         SpokenPunctuationRules.BaseCode(context.CleanupLanguage) is { } baseCode &&
                         SpokenPunctuationRules.RulesFor(baseCode) is not null &&
                         TryResolveStartWord(
                             baseCode,
                             context.Options.SpokenPunctuationStartWords,
                             allowDefault: true,
                             out var langStartWord))
                {
                    var sw = InverseTextNormalizer.ApplyStartWordPunctuation(
                        text,
                        baseCode,
                        langStartWord,
                        context.Options.EnglishPeriodWordEnabled);
                    text = sw.Text;
                    spokenFinalFullStop = sw.SpokenFinalFullStop;
                }
            }

            return context with
            {
                Text = text,
                SpokenFinalFullStop = spokenFinalFullStop,
            };
        }
    }

    private sealed class EmojiRestorationStep : IDeterministicTextStep
    {
        public DeterministicTextStage Stage => DeterministicTextStage.EmojiRestoration;

        /// <summary>
        /// Throttled first call: median 13 ms, slowest 36 ms (normal 8 ms); 50 ms was under 5x the slowest, and
        /// CI crossed it (#239). The restorer's own 1,000-token cap bounds its work on a long dictation.
        /// </summary>
        public TimeSpan Timeout => TimeSpan.FromMilliseconds(200);

        public bool IsEnabled(DeterministicTextContext context) => context.PolishedText is not null;

        public DeterministicTextContext Process(DeterministicTextContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return context with
            {
                PolishedText = EmojiRestorer.Restore(context.PolishedText!, context.Text).Text,
            };
        }
    }

    /// <summary>American to British spelling - over the deterministic text, or over the polish. Ref: macOS #3124.</summary>
    /// <remarks>
    /// TWO INSTANCES, ONE RULE. Before polish it is the no-polish floor: polish off, skipped or failed still
    /// delivers British spelling. After polish it keeps a model that writes "color" from undoing the choice, and
    /// runs before the emoji restorer so that aligns two British texts. The person's own Custom Words are never
    /// respelled - their replacement is their spelling. A table that failed to load stands the step down and the
    /// take is delivered in American spelling: this is a limb, and the product without it is not a failure.
    /// </remarks>
    private sealed class EnglishSpellingStep(BritishSpellingConverter? converter, bool afterPolish) : IDeterministicTextStep
    {
        public DeterministicTextStage Stage => afterPolish
            ? DeterministicTextStage.EnglishSpellingAfterPolish
            : DeterministicTextStage.EnglishSpelling;

        /// <summary>
        /// 10,000 words convert in well under this; the macOS budget for them is 150 ms. Throttled first call:
        /// median 2 ms, slowest 27 ms (normal 1.5 ms), before or after polish. Unchanged.
        /// </summary>
        public TimeSpan Timeout => TimeSpan.FromMilliseconds(200);

        /// <remarks>
        /// A REPORTED LANGUAGE DECIDES; FOR AN ENGINE THAT REPORTS NONE, CHOOSING BRITISH IS THE STATEMENT. macOS offers
        /// this as "English (UK)", a language lock: choosing it is saying "I dictate in English". Parakeet on Windows
        /// cannot be locked and reports no language, so the same choice carries the same meaning here - its takes are
        /// respelled whenever British is chosen, and the setting says so on screen, because the table shares "color"
        /// and "favor" with Spanish and Portuguese. A text-level language guess was built and withdrawn in review: a
        /// word list admits Spanish "has", Portuguese "for", French "but", and fails long English passages, and that
        /// class of error has no last member. Whisper reports its language, so a Whisper take is respelled only when
        /// it is English.
        /// </remarks>
        public bool IsEnabled(DeterministicTextContext context) =>
            converter is not null &&
            context.Options.EnglishSpelling == EnglishSpelling.British &&
            IsEnglishDeterministicTake(context) &&
            (!afterPolish || context.PolishedText is not null);

        public DeterministicTextContext Process(DeterministicTextContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var activePackReplacements = context.Options.WordCorrectionEnabled
                ? VocabularyPacks.ResolveEntries(context.EnabledVocabularyPackIds, context.VocabularyPackOverrides)
                    .Select(entry => entry.Replacement)
                : [];
            var protectedWords = BritishSpellingConverter.ProtectedWords(
                context.CustomWords.Select(entry => entry.Replacement).Concat(activePackReplacements));
            return afterPolish
                ? context with { PolishedText = converter!.Convert(context.PolishedText!, protectedWords).Text }
                : context with { Text = converter!.Convert(context.Text, protectedWords).Text };
        }
    }

    private sealed class CasualEndingStep : IDeterministicTextStep
    {
        public DeterministicTextStage Stage => DeterministicTextStage.CasualEnding;

        /// <summary>Pure string work on one character at the end; runaway backstop matching macOS.</summary>
        public TimeSpan Timeout => TimeSpan.FromMilliseconds(50);

        public bool IsEnabled(DeterministicTextContext context) => context.Options.CasualModeEnabled;

        public DeterministicTextContext Process(DeterministicTextContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var route = ResolveEffectiveRoute(context);
            var language = route.Kind == ItnRouteKind.Language
                ? route.LanguageCode
                : context.CleanupLanguage;
            var text = CasualEnding.RemoveFinalFullStop(context.Text, context.SpokenFinalFullStop, language);
            var polished = context.PolishedText is not null
                ? CasualEnding.RemoveFinalFullStop(context.PolishedText, context.SpokenFinalFullStop, language)
                : null;
            return context with
            {
                Text = text,
                PolishedText = polished,
            };
        }
    }

    private static ItnRoute ResolveEffectiveRoute(DeterministicTextContext context)
    {
        if (context.ItnRoute is { } existing)
        {
            return existing;
        }

        var resolution = DeliveryLanguagePolicy.ResolveFor(context.Transcript, context.Text);
        return InverseTextNormalizationGate.ResolveRoute(
            resolution.Language,
            resolution.EnglishVeto,
            DeliveryLanguagePolicy.EngineDetectsLanguage(context.Transcript));
    }

    private static bool IsEnglishDeterministicTake(DeterministicTextContext context) =>
        !context.EnglishVeto && ResolveEffectiveRoute(context).Kind == ItnRouteKind.English;

    private static bool TryResolveStartWord(
        string languageCode,
        IReadOnlyDictionary<string, string>? overrides,
        bool allowDefault,
        out string startWord)
    {
        startWord = string.Empty;
        var forms = SpokenPunctuationRules.StartWordFormsFor(languageCode);
        if (forms is null)
        {
            return false;
        }

        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
            {
                if (!string.Equals(SpokenPunctuationRules.BaseCode(key), languageCode, StringComparison.Ordinal))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(value))
                {
                    break;
                }

                if (SpokenPunctuationRules.TryValidateStartWord(value, languageCode, forms, out var validated))
                {
                    startWord = validated;
                    return true;
                }

                break;
            }
        }

        if (!allowDefault)
        {
            return false;
        }

        startWord = SpokenPunctuationRules.DefaultStartWord(languageCode) ?? string.Empty;
        return startWord.Length > 0;
    }
}
