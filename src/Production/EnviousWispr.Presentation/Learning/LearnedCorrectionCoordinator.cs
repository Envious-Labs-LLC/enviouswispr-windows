using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Learning;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Presentation.Learning;

/// <summary>
/// Owns saving judged corrections immediately into Your Words, tracking the single live
/// Undo snapshot while the Undo pill is shown, and recording user rejections into <see cref="ILearnMemoryStore"/>.
/// Mirrors macOS <c>LearnedCorrectionCoordinator</c>.
/// </summary>
public sealed class LearnedCorrectionCoordinator : ILearnedCorrectionCoordinator
{
    public sealed record UndoRecord(
        Guid PillId,
        CustomWordEntry? PreSave,
        CustomWordEntry PostSave,
        LearnedCorrectionPillKind Kind,
        string PairKey,
        LearnVia Via)
    {
        public bool Admitted { get; set; }
    }

    private readonly VocabularyPresenter _vocabulary;
    private readonly ILearnMemoryStore _memory;
    private readonly Func<IReadOnlyList<CustomWordEntry>> _packTerms;
    private readonly IAppLogger? _logger;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _sync = new();

    private ILearnedCorrectionPresenter? _presenter;
    private UndoRecord? _undoRecord;

    public LearnedCorrectionCoordinator(
        VocabularyPresenter vocabulary,
        ILearnMemoryStore memory,
        Func<IReadOnlyList<CustomWordEntry>> packTerms,
        IAppLogger? logger = null,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(vocabulary);
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(packTerms);

        _vocabulary = vocabulary;
        _memory = memory;
        _packTerms = packTerms;
        _logger = logger;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public UndoRecord? CurrentUndoRecord
    {
        get
        {
            lock (_sync)
            {
                return _undoRecord;
            }
        }
    }

    public void AttachPresenter(ILearnedCorrectionPresenter presenter)
    {
        ArgumentNullException.ThrowIfNull(presenter);
        _presenter = presenter;
    }

    public async Task<LearnedCorrectionOutcome> LearnAsync(
        string original,
        string corrected,
        LearnTargetState expectedTarget,
        LearnVia via,
        Func<bool>? stillWanted = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(original);
        ArgumentException.ThrowIfNullOrWhiteSpace(corrected);
        ArgumentNullException.ThrowIfNull(expectedTarget);

        var trimmedOriginal = original.Trim();
        var trimmedCorrected = corrected.Trim();
        var pairKey = CorrectionPairKey.Make(trimmedOriginal, trimmedCorrected);

        if (stillWanted is not null && !stillWanted())
        {
            return new LearnedCorrectionOutcome.NoLongerWanted();
        }

        if (_memory.IsBlocked(pairKey))
        {
            return new LearnedCorrectionOutcome.Blocked();
        }

        var saveResult = await _vocabulary.SaveLearnedWordAsync(
            trimmedOriginal,
            trimmedCorrected,
            expectedTarget,
            _packTerms(),
            _clock()).ConfigureAwait(false);

        if (!saveResult.Saved)
        {
            Log(AppEventCode.LearnSaveFailed, AppFailureCategory.StorageUnavailable);
            _presenter?.ShowSaveError(trimmedCorrected);
            return new LearnedCorrectionOutcome.Refused("save_refused");
        }

        var outcome = saveResult.Value;
        switch (outcome.Status)
        {
            case LearnedSaveStatus.AlreadyCovered:
                return new LearnedCorrectionOutcome.AlreadyCovered();
            case LearnedSaveStatus.TargetGone:
                Log(AppEventCode.LearnSaveFailed, AppFailureCategory.StorageUnavailable);
                _presenter?.ShowSaveError(trimmedCorrected);
                return new LearnedCorrectionOutcome.Refused("target_gone");
            case LearnedSaveStatus.AliasOwnedElsewhere:
                Log(AppEventCode.LearnSaveFailed, AppFailureCategory.StorageUnavailable);
                _presenter?.ShowSaveError(trimmedCorrected);
                return new LearnedCorrectionOutcome.Refused("alias_owned_elsewhere");
            case LearnedSaveStatus.Refused:
                Log(AppEventCode.LearnSaveFailed, AppFailureCategory.StorageUnavailable);
                _presenter?.ShowSaveError(trimmedCorrected);
                return new LearnedCorrectionOutcome.Refused("write_failed");
        }

        var postSave = outcome.After!;
        var kind = outcome.Before is null && expectedTarget is LearnTargetState.NewWord
            ? LearnedCorrectionPillKind.Added
            : LearnedCorrectionPillKind.Updated;

        var pill = new LearnedCorrectionPillModel(
            Guid.NewGuid(),
            postSave.Id,
            postSave.Replacement,
            kind,
            LearnedCorrectionPillPhase.Learned);

        lock (_sync)
        {
            _undoRecord = new UndoRecord(pill.PillId, outcome.Before, postSave, kind, pairKey, via);
        }

        Log(AppEventCode.LearnAdded);
        _presenter?.Show(pill);
        return new LearnedCorrectionOutcome.Learned(pill);
    }

    public void PillAdmitted(Guid pillId)
    {
        var shouldLog = false;
        lock (_sync)
        {
            if (_undoRecord is { } record && record.PillId == pillId && !record.Admitted)
            {
                record.Admitted = true;
                shouldLog = true;
            }
        }

        if (shouldLog)
        {
            Log(AppEventCode.LearnUndoShown);
        }
    }

    public void PillEnded(Guid pillId)
    {
        lock (_sync)
        {
            if (_undoRecord?.PillId == pillId)
            {
                _undoRecord = null;
            }
        }
    }

    public UndoRecord? ClaimUndo(Guid pillId)
    {
        UndoRecord? claimed = null;
        lock (_sync)
        {
            if (_undoRecord is { } record && record.PillId == pillId)
            {
                claimed = record;
                _undoRecord = null;
            }
        }

        if (claimed is null)
        {
            _presenter?.Close(pillId);
        }

        return claimed;
    }

    public async Task<LearnedCorrectionUndoOutcome> PerformUndoAsync(UndoRecord record, Guid pillId)
    {
        ArgumentNullException.ThrowIfNull(record);

        var undoResult = await _vocabulary.UndoLearnedWordAsync(record.PostSave, record.PreSave)
            .ConfigureAwait(false);

        if (!undoResult.Saved || undoResult.Value == LearnedUndoWriteStatus.Refused)
        {
            Log(AppEventCode.LearnUndone, AppFailureCategory.StorageUnavailable);
            _presenter?.ShowUndoError(pillId);
            return LearnedCorrectionUndoOutcome.Failed;
        }

        if (undoResult.Value == LearnedUndoWriteStatus.NotApplied)
        {
            _presenter?.Close(pillId);
            return LearnedCorrectionUndoOutcome.AlreadyChanged;
        }

        var mem = _memory.RecordUndo(record.PairKey);
        Log(AppEventCode.LearnUndone);
        if (mem.Committed)
        {
            Log(AppEventCode.LearnRefusedByUser);
        }

        _presenter?.ShowUndone(pillId);
        return LearnedCorrectionUndoOutcome.Undone;
    }

    public void RecordManualWordRemovals(IReadOnlyList<CustomWordEntry> removedEntries)
    {
        ArgumentNullException.ThrowIfNull(removedEntries);
        foreach (var entry in removedEntries)
        {
            if (entry.LearnedAliases.Count == 0)
            {
                continue;
            }

            foreach (var alias in entry.LearnedAliases)
            {
                var pairKey = CorrectionPairKey.Make(alias, entry.Replacement);
                if (_memory.RecordUndo(pairKey).Committed)
                {
                    Log(AppEventCode.LearnRefusedByUser);
                }
            }
        }
    }

    public void RecordManualAliasRemovals(string replacement, IReadOnlyList<string> removedLearnedAliases)
    {
        if (string.IsNullOrWhiteSpace(replacement) || removedLearnedAliases.Count == 0)
        {
            return;
        }

        foreach (var alias in removedLearnedAliases)
        {
            var pairKey = CorrectionPairKey.Make(alias, replacement);
            if (_memory.RecordUndo(pairKey).Committed)
            {
                Log(AppEventCode.LearnRefusedByUser);
            }
        }
    }

    private void Log(AppEventCode code, AppFailureCategory failure = AppFailureCategory.None) =>
        _logger?.Write(new AppLogEntry(DateTimeOffset.UtcNow, code, failure));
}
