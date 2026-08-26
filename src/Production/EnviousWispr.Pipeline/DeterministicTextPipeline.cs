using System.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Settings;
using EnviousWispr.PostProcessing;

namespace EnviousWispr.Pipeline;

public enum DeterministicTextStage
{
    CustomWords,
    FillerAndFalseStarts,
    SpokenEmoji,
    InverseTextNormalization,
    EmojiRestoration,
}

public enum DeterministicStageStatus
{
    Completed,
    Skipped,
    TimedOut,
    Failed,
}

public sealed record DeterministicStageReceipt(
    DeterministicTextStage Stage,
    DeterministicStageStatus Status,
    bool Changed,
    long ElapsedMilliseconds);

public sealed record DeterministicTextOptions(
    bool WordCorrectionEnabled,
    bool FillerRemovalEnabled,
    bool EmojiFormatterEnabled,
    bool SpokenPunctuationEnabled)
{
    public static DeterministicTextOptions From(DictationPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        return new DeterministicTextOptions(
            preferences.WordCorrectionEnabled,
            preferences.FillerRemovalEnabled,
            preferences.EmojiFormatterEnabled,
            preferences.SpokenPunctuationEnabled);
    }
}

public sealed record DeterministicTextRequest(
    Transcript Transcript,
    IReadOnlyList<CustomWordEntry> CustomWords,
    DeterministicTextOptions Options,
    string? PolishedText = null);

public sealed record DeterministicTextResult(
    ProcessedText Output,
    IReadOnlyList<DeterministicStageReceipt> Receipts,
    bool IsDegraded)
{
    public string DeterministicText { get; init; } = Output.Text;
}

public sealed record DeterministicTextContext(
    Transcript Transcript,
    string Text,
    IReadOnlyList<CustomWordEntry> CustomWords,
    DeterministicTextOptions Options,
    string? PolishedText = null);

public interface IDeterministicTextStep
{
    DeterministicTextStage Stage { get; }

    TimeSpan Timeout { get; }

    bool IsEnabled(DeterministicTextContext context);

    DeterministicTextContext Process(DeterministicTextContext context);
}

public sealed class DeterministicTextPipeline
{
    private readonly IDeterministicTextStep[] _steps;

    public DeterministicTextPipeline()
        : this(CreateDefaultSteps())
    {
    }

    public DeterministicTextPipeline(IReadOnlyList<IDeterministicTextStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        _steps = steps.ToArray();
    }

    public async Task<DeterministicTextResult> ProcessAsync(
        DeterministicTextRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Transcript);
        ArgumentNullException.ThrowIfNull(request.CustomWords);
        ArgumentNullException.ThrowIfNull(request.Options);

        var context = new DeterministicTextContext(
            request.Transcript,
            request.Transcript.Text,
            request.CustomWords.ToArray(),
            request.Options,
            request.PolishedText);
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

            var input = context;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                context = await Task.Run(
                        () => step.Process(input),
                        cancellationToken)
                    .WaitAsync(step.Timeout, cancellationToken)
                    .ConfigureAwait(false);
                stopwatch.Stop();
                if (context is null || context.Text is null)
                {
                    throw new InvalidOperationException("A deterministic text stage returned no context.");
                }

                receipts.Add(new DeterministicStageReceipt(
                    step.Stage,
                    DeterministicStageStatus.Completed,
                    !string.Equals(input.Text, context.Text, StringComparison.Ordinal) ||
                    !string.Equals(input.PolishedText, context.PolishedText, StringComparison.Ordinal),
                    stopwatch.ElapsedMilliseconds));
            }
            catch (TimeoutException)
            {
                stopwatch.Stop();
                context = input;
                degraded = true;
                receipts.Add(new DeterministicStageReceipt(
                    step.Stage,
                    DeterministicStageStatus.TimedOut,
                    Changed: false,
                    stopwatch.ElapsedMilliseconds));
            }
            catch (Exception exception) when (exception is not (
                OperationCanceledException or StackOverflowException or OutOfMemoryException))
            {
                stopwatch.Stop();
                context = input;
                degraded = true;
                receipts.Add(new DeterministicStageReceipt(
                    step.Stage,
                    DeterministicStageStatus.Failed,
                    Changed: false,
                    stopwatch.ElapsedMilliseconds));
            }
        }

        var deterministicText = context.Text;
        var finalText = context.PolishedText ?? deterministicText;
        return new DeterministicTextResult(
            new ProcessedText(request.Transcript.SessionId, finalText),
            receipts,
            degraded)
        {
            DeterministicText = deterministicText,
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
        var step = _steps.Single(candidate => candidate.Stage == DeterministicTextStage.EmojiRestoration);
        var input = new DeterministicTextContext(
            request.Transcript,
            deterministicResult.DeterministicText,
            request.CustomWords,
            request.Options,
            polishedText);
        var context = input;
        var stopwatch = Stopwatch.StartNew();
        var status = DeterministicStageStatus.Completed;
        var degraded = deterministicResult.IsDegraded;
        try
        {
            context = await Task.Run(() => step.Process(input), cancellationToken)
                .WaitAsync(step.Timeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            context = input;
            status = DeterministicStageStatus.TimedOut;
            degraded = true;
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or StackOverflowException or OutOfMemoryException))
        {
            context = input;
            status = DeterministicStageStatus.Failed;
            degraded = true;
        }

        stopwatch.Stop();
        var receipt = new DeterministicStageReceipt(
            DeterministicTextStage.EmojiRestoration,
            status,
            !string.Equals(input.PolishedText, context.PolishedText, StringComparison.Ordinal),
            stopwatch.ElapsedMilliseconds);
        var receipts = deterministicResult.Receipts
            .Select(existing => existing.Stage == DeterministicTextStage.EmojiRestoration
                ? receipt
                : existing)
            .ToArray();
        return new DeterministicTextResult(
            new ProcessedText(request.Transcript.SessionId, context.PolishedText ?? polishedText),
            receipts,
            degraded)
        {
            DeterministicText = deterministicResult.DeterministicText,
        };
    }

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
            new EmojiRestorationStep(),
        ];
    }

    private sealed class CustomWordStep : IDeterministicTextStep
    {
        public DeterministicTextStage Stage => DeterministicTextStage.CustomWords;

        public TimeSpan Timeout => TimeSpan.FromSeconds(3);

        public bool IsEnabled(DeterministicTextContext context) =>
            context.Options.WordCorrectionEnabled && context.CustomWords.Count > 0;

        public DeterministicTextContext Process(DeterministicTextContext context) =>
            context with { Text = CustomWordCorrector.Correct(context.Text, context.CustomWords).Text };
    }

    private sealed class FillerStep : IDeterministicTextStep
    {
        public DeterministicTextStage Stage => DeterministicTextStage.FillerAndFalseStarts;

        public TimeSpan Timeout => TimeSpan.FromMilliseconds(50);

        public bool IsEnabled(DeterministicTextContext context) => context.Options.FillerRemovalEnabled;

        public DeterministicTextContext Process(DeterministicTextContext context) => context with
        {
            Text = FillerWordRemover.Remove(context.Text, context.Transcript.DetectedLanguage),
        };
    }

    private sealed class SpokenEmojiStep(SpokenEmojiFormatter? formatter) : IDeterministicTextStep
    {
        public DeterministicTextStage Stage => DeterministicTextStage.SpokenEmoji;

        public TimeSpan Timeout => TimeSpan.FromMilliseconds(50);

        public bool IsEnabled(DeterministicTextContext context) =>
            context.Options.EmojiFormatterEnabled && formatter is not null;

        public DeterministicTextContext Process(DeterministicTextContext context) =>
            context with { Text = formatter!.Format(context.Text) };
    }

    private sealed class InverseTextNormalizationStep : IDeterministicTextStep
    {
        public DeterministicTextStage Stage => DeterministicTextStage.InverseTextNormalization;

        public TimeSpan Timeout => TimeSpan.FromMilliseconds(500);

        public bool IsEnabled(DeterministicTextContext context)
        {
            var language = context.Transcript.DetectedLanguage?.Trim();
            if (!string.IsNullOrEmpty(language))
            {
                return language.Equals("en", StringComparison.OrdinalIgnoreCase) ||
                    language.StartsWith("en-", StringComparison.OrdinalIgnoreCase) ||
                    language.StartsWith("en_", StringComparison.OrdinalIgnoreCase);
            }

            return !context.Transcript.EngineId.Contains("whisper", StringComparison.OrdinalIgnoreCase);
        }

        public DeterministicTextContext Process(DeterministicTextContext context) => context with
        {
            Text = InverseTextNormalizer.Normalize(
                context.Text,
                context.Options.SpokenPunctuationEnabled),
        };
    }

    private sealed class EmojiRestorationStep : IDeterministicTextStep
    {
        public DeterministicTextStage Stage => DeterministicTextStage.EmojiRestoration;

        public TimeSpan Timeout => TimeSpan.FromMilliseconds(50);

        public bool IsEnabled(DeterministicTextContext context) => context.PolishedText is not null;

        public DeterministicTextContext Process(DeterministicTextContext context) => context with
        {
            PolishedText = EmojiRestorer.Restore(context.PolishedText!, context.Text).Text,
        };
    }
}
