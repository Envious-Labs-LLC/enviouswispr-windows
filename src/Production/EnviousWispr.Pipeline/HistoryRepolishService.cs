using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.History;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Pipeline;

/// <summary>
/// Re-polishes an existing dictation history entry using the current polish provider,
/// preserving pre-AI original text and avoiding compound LLM edits.
/// </summary>
public sealed class HistoryRepolishService : IHistoryRepolishService
{
    private readonly IHistoryStore _historyStore;
    private readonly PolishExecutor _polishExecutor;
    private readonly DeterministicTextPipeline _pipeline;
    private readonly Func<PolishSetup?> _polishSetup;
    private readonly Func<DeterministicTextOptions> _textOptions;
    private readonly Func<IReadOnlyList<CustomWordEntry>> _customWords;
    private readonly Func<IReadOnlyList<string>> _enabledVocabularyPackIds;
    private readonly ITextTargetAdapter? _targetAdapter;
    private readonly Func<ITextTargetAdapter?>? _targetAdapterProvider;
    private readonly IAppLogger? _logger;
    private readonly TimeProvider _clock;

    public HistoryRepolishService(
        IHistoryStore historyStore,
        PolishExecutor polishExecutor,
        DeterministicTextPipeline pipeline,
        Func<PolishSetup?> polishSetup,
        Func<DeterministicTextOptions>? textOptions = null,
        Func<IReadOnlyList<CustomWordEntry>>? customWords = null,
        ITextTargetAdapter? targetAdapter = null,
        IAppLogger? logger = null,
        TimeProvider? clock = null,
        Func<IReadOnlyList<string>>? enabledVocabularyPackIds = null,
        Func<ITextTargetAdapter?>? targetAdapterProvider = null)
    {
        ArgumentNullException.ThrowIfNull(historyStore);
        ArgumentNullException.ThrowIfNull(polishExecutor);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(polishSetup);
        _historyStore = historyStore;
        _polishExecutor = polishExecutor;
        _pipeline = pipeline;
        _polishSetup = polishSetup;
        _textOptions = textOptions ?? (() => new DeterministicTextOptions(true, true, true, true));
        _customWords = customWords ?? (() => Array.Empty<CustomWordEntry>());
        _enabledVocabularyPackIds = enabledVocabularyPackIds ?? (() => Array.Empty<string>());
        _targetAdapter = targetAdapter;
        _targetAdapterProvider = targetAdapterProvider;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<HistoryRepolishResult> RepolishAsync(
        Guid entryId,
        bool copyToClipboard = false,
        CancellationToken cancellationToken = default)
    {
        var setup = _polishSetup();
        if (setup is null || setup.Provider is null ||
            string.Equals(setup.Provider.ProviderId, "none", StringComparison.OrdinalIgnoreCase))
        {
            RecordRefusal();
            return HistoryRepolishResult.NoProvider();
        }

        var entry = await _historyStore.GetAsync(entryId, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            RecordRefusal();
            return HistoryRepolishResult.NotFound();
        }

        return await RepolishCoreAsync(entry, setup, copyToClipboard, cancellationToken).ConfigureAwait(false);
    }

    public async Task<HistoryRepolishResult> RepolishAsync(
        DictationHistoryEntry entry,
        bool copyToClipboard = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var setup = _polishSetup();
        if (setup is null || setup.Provider is null ||
            string.Equals(setup.Provider.ProviderId, "none", StringComparison.OrdinalIgnoreCase))
        {
            RecordRefusal();
            return HistoryRepolishResult.NoProvider();
        }

        return await RepolishCoreAsync(entry, setup, copyToClipboard, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HistoryRepolishResult> RepolishCoreAsync(
        DictationHistoryEntry entry,
        PolishSetup setup,
        bool copyToClipboard,
        CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        if (entry.ExpiresAt is { } expiresAt && expiresAt <= now)
        {
            RecordRefusal();
            return HistoryRepolishResult.Expired();
        }

        if (entry.ExpiresAt is not null)
        {
            RecordRefusal();
            return HistoryRepolishResult.MustBeKept();
        }

        var startingText = entry.OriginalText ?? entry.Text;
        if (string.IsNullOrWhiteSpace(startingText))
        {
            RecordRefusal();
            return HistoryRepolishResult.Refused();
        }

        var sessionId = DictationSessionId.Create();
        var input = new ProcessedText(sessionId, startingText);

        var polishResult = await _polishExecutor
            .TryPolishAsync(setup, input, entry.LanguageCode, cancellationToken)
            .ConfigureAwait(false);

        if (polishResult is null || polishResult.UsedFallback ||
            polishResult.Status != PolishAttemptStatus.Polished ||
            string.IsNullOrWhiteSpace(polishResult.Output.Text))
        {
            RecordRefusal();
            return HistoryRepolishResult.Refused();
        }

        var polishReview = PolishOutputGuard.Review(startingText, polishResult.Output.Text);
        if (polishReview.Verdict != PolishOutputVerdict.Accepted ||
            string.IsNullOrWhiteSpace(polishReview.Text))
        {
            RecordRefusal();
            return HistoryRepolishResult.Refused();
        }

        var transcript = new Transcript(sessionId, startingText, entry.EngineId, DetectedLanguage: entry.LanguageCode);
        var deterministicOptions = _textOptions() with { CasualModeEnabled = false };
        var customWords = _customWords();
        var enabledPackIds = _enabledVocabularyPackIds();
        var request = new DeterministicTextRequest(
            transcript,
            customWords,
            deterministicOptions,
            EnabledVocabularyPackIds: enabledPackIds);
        var deterministicResult = new DeterministicTextResult(
            input,
            [],
            IsDegraded: false)
        {
            DeterministicText = startingText,
        };

        var applied = await _pipeline
            .ApplyPolishedTextAsync(request, deterministicResult, polishReview.Text, cancellationToken)
            .ConfigureAwait(false);

        var newText = applied.Output.Text;

        var updateResult = await _historyStore.UpdateRepolishedAsync(
            entry.Id,
            expectedCurrentText: entry.Text,
            newText: newText,
            wasPolished: true,
            providerId: setup.Provider.ProviderId,
            modelId: setup.ModelId,
            cancellationToken).ConfigureAwait(false);

        if (updateResult.Status == HistoryRepolishUpdateStatus.Stale)
        {
            RecordRefusal();
            return HistoryRepolishResult.Stale();
        }

        if (!updateResult.Succeeded || updateResult.UpdatedEntry is null)
        {
            RecordRefusal();
            return updateResult.Status switch
            {
                HistoryRepolishUpdateStatus.NotFound => HistoryRepolishResult.NotFound(),
                HistoryRepolishUpdateStatus.Expired => HistoryRepolishResult.Expired(),
                _ => HistoryRepolishResult.FailedResult(),
            };
        }

        var copied = false;
        var targetAdapter = _targetAdapterProvider?.Invoke() ?? _targetAdapter;
        if (copyToClipboard && targetAdapter is not null)
        {
            try
            {
                var commit = await targetAdapter.CopyOnlyAsync(
                    new ProcessedText(sessionId, newText),
                    cancellationToken).ConfigureAwait(false);
                copied = commit.Delivered || commit.ClipboardFallback;
            }
            catch (Exception)
            {
                // Clipboard copy error does not invalidate the committed repolish
            }
        }

        _logger?.Write(new AppLogEntry(_clock.GetUtcNow(), AppEventCode.HistoryEntryRepolished));
        return HistoryRepolishResult.Success(updateResult.UpdatedEntry, copied);
    }

    private void RecordRefusal() =>
        _logger?.Write(new AppLogEntry(_clock.GetUtcNow(), AppEventCode.HistoryEntryRepolishRefused));
}
