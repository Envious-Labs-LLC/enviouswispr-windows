using System.Diagnostics;
using System.Globalization;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Settings;
using EnviousWispr.PostProcessing;

namespace EnviousWispr.Pipeline;

/// <summary>What the snippet stage did to one take: the masked text and its records, and a content-free receipt.</summary>
/// <param name="Outcome">The masked text and one record per fired snippet; the input unchanged when nothing fired or the stage stood down.</param>
/// <param name="Receipt">Skipped, Completed, TimedOut or Failed. Changed means a snippet fired, and says nothing about which.</param>
public sealed record SnippetStageResult(SnippetExpansionOutcome Outcome, DeterministicStageReceipt Receipt)
{
    /// <summary>True when the stage stood down rather than finishing; the take is delivered without its snippets.</summary>
    public bool IsDegraded => Receipt.Status is DeterministicStageStatus.TimedOut or DeterministicStageStatus.Failed;
}

/// <summary>
/// Masks each fired snippet behind a sentinel, FIRST in the text pass, so the person's saved text never
/// reaches a stage that could alter it or a model that could rewrite it.
/// </summary>
/// <remarks>
/// FIRST ON PURPOSE, AHEAD OF CUSTOM-WORD CORRECTION (macOS <c>SnippetExpansionStep</c>,
/// Sources/EnviousWisprPipeline/SnippetExpansionStep.swift). A trigger is matched literally, so it is
/// read off the recogniser's words before the fuzzy corrector can change one of them: running after
/// correction would make whether a snippet fires depend on an unrelated word list, and filler removal
/// or spoken emoji could eat a trigger word the same way. On Windows the keyword itself is not at risk
/// from spoken punctuation - this app's nine spoken-punctuation patterns have no "backslash", and the
/// "slash" rules need the word on its own - but the order is the product's either way.
///
/// THIS STAGE ONLY MASKS. The finalizer resolves every sentinel after polish, and that resolution is
/// not a stage for the reason <see cref="SnippetFinalizer"/> gives.
///
/// A LIMB. String work, no model, no network, one second, and a stand-down delivers the take without
/// its snippets - no sentinel was issued, so nothing is owed. The clipboard is read ONCE, and only on a
/// take where a snippet using it actually fired: the match runs first against no clipboard, and only a
/// fired <c>{{clipboard}}</c> earns a read and a second pass, so a clipboard snippet that is merely saved
/// never costs a clipboard read on every dictation (macOS #3018).
/// </remarks>
public sealed class SnippetExpansionStage
{
    /// <summary>
    /// One second, the macOS measurement: its first budget, 50 ms copied from a late step, timed out on
    /// two consecutive real dictations because the first stage pays for scheduling. A runaway backstop,
    /// not a latency budget.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    private readonly SnippetExpander _expander;
    private readonly TimeProvider _clock;
    private readonly Func<CultureInfo> _culture;
    private readonly Func<CancellationToken, Task<string?>> _readClipboardText;
    private readonly TimeSpan _timeout;

    /// <param name="expander">The matcher.</param>
    /// <param name="clock">The instant and the local zone fill-ins render against, read once per take.</param>
    /// <param name="culture">The culture fill-ins render in, read once per take.</param>
    /// <param name="readClipboardText">
    /// Reads the clipboard's plain text WITHOUT CHANGING IT, or null when there is none. Called only when
    /// a fired snippet uses the clipboard.
    /// </param>
    /// <param name="timeout">
    /// <see cref="Timeout"/> in the app. Handed in, as each deterministic stage's deadline is its own
    /// property, so a test about WHAT the stage decides can lift it and a cold test runner cannot turn
    /// a slow first pass into a snippet that silently did not fire.
    /// </param>
    public SnippetExpansionStage(
        SnippetExpander expander,
        TimeProvider clock,
        Func<CultureInfo> culture,
        Func<CancellationToken, Task<string?>> readClipboardText,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(expander);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(culture);
        ArgumentNullException.ThrowIfNull(readClipboardText);
        _expander = expander;
        _clock = clock;
        _culture = culture;
        _readClipboardText = readClipboardText;
        _timeout = timeout;
    }

    public async Task<SnippetStageResult> ExpandAsync(
        string text,
        SnippetVocabulary vocabulary,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(vocabulary);
        var untouched = new SnippetExpansionOutcome(text, [], new HashSet<SnippetPlaceholder>());
        if (!vocabulary.CanFire)
        {
            return new SnippetStageResult(untouched, Receipt(DeterministicStageStatus.Skipped, false, 0));
        }

        var timer = Stopwatch.StartNew();
        // FROZEN ONCE FOR THE WHOLE TAKE, BEFORE THE PROBE PASS. The two passes vary the clipboard and
        // nothing else, so they cannot disagree about what time it is.
        var now = _clock.GetUtcNow();
        var zone = _clock.LocalTimeZone;
        var culture = _culture();
        SnippetDynamicValues Values(string? clipboard) => new(now, culture, zone, clipboard);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        try
        {
            var dry = await Task.Run(() => _expander.Expand(text, vocabulary, Values(null)), deadline.Token)
                .WaitAsync(deadline.Token)
                .ConfigureAwait(false);
            var outcome = dry;
            if (dry.DidFire && dry.UsedPlaceholders.Contains(SnippetPlaceholder.Clipboard))
            {
                var clipboard = await ReadClipboardAsync(deadline.Token).ConfigureAwait(false);
                outcome = await Task.Run(() => _expander.Expand(text, vocabulary, Values(clipboard)), deadline.Token)
                    .WaitAsync(deadline.Token)
                    .ConfigureAwait(false);
            }

            return new SnippetStageResult(
                outcome,
                Receipt(DeterministicStageStatus.Completed, outcome.DidFire, timer.ElapsedMilliseconds));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return new SnippetStageResult(untouched, Receipt(DeterministicStageStatus.TimedOut, false, timer.ElapsedMilliseconds));
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            return new SnippetStageResult(untouched, Receipt(DeterministicStageStatus.Failed, false, timer.ElapsedMilliseconds));
        }
    }

    /// <summary>The clipboard's text, or null; a clipboard another app is holding fills in as nothing rather than failing the snippet.</summary>
    private async Task<string?> ReadClipboardAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _readClipboardText(cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            return null;
        }
    }

    private static DeterministicStageReceipt Receipt(DeterministicStageStatus status, bool changed, long elapsedMilliseconds) =>
        new(DeterministicTextStage.SnippetExpansion, status, changed, elapsedMilliseconds);
}
