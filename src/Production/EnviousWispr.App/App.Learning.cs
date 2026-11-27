using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Learning;
using EnviousWispr.Core.Settings;
using EnviousWispr.LLM;
using EnviousWispr.Pipeline;
using EnviousWispr.PostProcessing.Learning;
using EnviousWispr.Presentation.Learning;
using EnviousWispr.Services.Input;
using EnviousWispr.Services.Learning;

namespace EnviousWispr.App;

public partial class App
{
    private JsonLearnMemoryStore? _learnMemoryStore;
    private RulesCorrectionJudge? _rulesCorrectionJudge;
    private EgOneCorrectionJudge? _egOneCorrectionJudge;
    private FallbackCorrectionJudge? _correctionJudge;
    private ILearnedWordChecker? _learnedWordChecker;
    private LearnedWordCheckStep? _learnedWordCheckStep;
    private WindowsPastedRegionObserver? _pastedRegionObserver;
    private LearnedCorrectionCoordinator? _learnedCoordinator;
    private ObservedCorrectionWatcher? _correctionWatcher;
    private OverlayLearnedCorrectionPresenter? _learnedPresenter;

    private void InitializeSelfLearning()
    {
        if (_presentation is null || _learnedCoordinator is not null)
        {
            return;
        }

        var memoryPath = Path.Combine(_dataDirectory, JsonLearnMemoryStore.FileName);
        var memoryStore = new JsonLearnMemoryStore(memoryPath);
        var rulesJudge = new RulesCorrectionJudge();
        var egOneJudge = new EgOneCorrectionJudge(
            () => _polishProvider as EgOnePolishProvider,
            canRun: () => !IsSpeechEngineBusy && !Leaving,
            resourceAdmission: _resourceArbiter,
            resourceKind: () => _polishProvider is { } provider
                ? EffectivePolishResource(provider)
                : _polishResource);
        var judge = new FallbackCorrectionJudge(() => egOneJudge, rulesJudge);
        var observer = new WindowsPastedRegionObserver();
        var coordinator = new LearnedCorrectionCoordinator(
            _presentation.Vocabulary,
            memoryStore,
            () => VocabularyPacks.ResolveEntries(_enabledVocabularyPackIds),
            _logger,
            onSettingsCommitted: PublishSelfLearningSettings);
        var presenter = new OverlayLearnedCorrectionPresenter(this, coordinator);
        coordinator.AttachPresenter(presenter);

        var watcher = new ObservedCorrectionWatcher(
            () => _settings.Preferences.Dictation.LearnFromEditsEnabled,
            () => judge,
            observer,
            () => _customWords,
            () => VocabularyPacks.ResolveEntries(_enabledVocabularyPackIds),
            coordinator,
            memoryStore,
            _logger);

        memoryStore.Changed += OnLearnMemoryChanged;

        _learnMemoryStore = memoryStore;
        _rulesCorrectionJudge = rulesJudge;
        _egOneCorrectionJudge = egOneJudge;
        _correctionJudge = judge;
        _pastedRegionObserver = observer;
        _learnedCoordinator = coordinator;
        _learnedPresenter = presenter;
        _correctionWatcher = watcher;
    }

    private LearnedWordCheckStep EnsureLearnedWordCheckStep() =>
        _learnedWordCheckStep ??= new LearnedWordCheckStep(EnsureLearnedWordChecker);

    private ILearnedWordChecker EnsureLearnedWordChecker() =>
        _learnedWordChecker ??= new FallbackLearnedWordChecker(
            () => new EgOneLearnedWordChecker(
                () => !Leaving ? _polishProvider as EgOnePolishProvider : null,
                resourceAdmission: _resourceArbiter,
                resourceKind: () => _polishProvider is { } provider
                    ? EffectivePolishResource(provider)
                    : _polishResource),
            new DeterministicLearnedWordChecker());

    private void AttachSelfLearningToWindow(MainWindow window)
    {
        window.QueryLearnMemoryState = () =>
            _learnMemoryStore is { } store
                ? (store.HasStoredMemory, store.BlockedCount)
                : (false, 0);
        window.ForgetLearnMemory = () =>
        {
            if (_learnMemoryStore is not { } store)
            {
                return false;
            }

            var cleared = store.ForgetAll();
            if (cleared)
            {
                _logger.Write(new AppLogEntry(DateTimeOffset.UtcNow, AppEventCode.LearnMemoryForgotten));
            }

            return cleared;
        };
        window.WordsRemovedFromDictionary = entries =>
            _learnedCoordinator?.RecordManualWordRemovals(entries);
        window.LearnedAliasesRemovedFromDictionary = (replacement, aliases) =>
            _learnedCoordinator?.RecordManualAliasRemovals(replacement, aliases);
        window.ExportLearnMemory = () => _learnMemoryStore?.ExportEntries();
        window.ImportLearnMemory = entries => _learnMemoryStore?.ImportEntries(entries);
        window.LearnedPillUndoRequested += RequestLearnedCorrectionUndo;
        window.LearnedPillUndoActiveRequested += RequestActiveLearnedCorrectionUndo;
        window.LearnedPillEnded += OnLearnedPillEnded;
        window.RefreshLearnMemoryUi();
    }

    private void DetachSelfLearningFromWindow(MainWindow window)
    {
        window.QueryLearnMemoryState = null;
        window.ForgetLearnMemory = null;
        window.WordsRemovedFromDictionary = null;
        window.LearnedAliasesRemovedFromDictionary = null;
        window.ExportLearnMemory = null;
        window.ImportLearnMemory = null;
        window.LearnedPillUndoRequested -= RequestLearnedCorrectionUndo;
        window.LearnedPillUndoActiveRequested -= RequestActiveLearnedCorrectionUndo;
        window.LearnedPillEnded -= OnLearnedPillEnded;
    }

    private void PublishSelfLearningSettings()
    {
        if (_presentation is not { } presentation)
        {
            return;
        }

        var latest = presentation.Settings.Current;
        _settings = latest;
        _customWords = latest.UserData.CustomWords;
        _enabledVocabularyPackIds = latest.UserData.EnabledVocabularyPackIds;
        _snippetVocabulary = Core.Settings.SnippetVocabulary.From(latest.UserData);
        PostToLiveWindow(window => window.SyncExternalSettingsUpdate());
    }

    private void OnLearnMemoryChanged() =>
        PostToLiveWindow(window => window.RefreshLearnMemoryUi());

    private void OnLearnedPillEnded(Guid pillId)
    {
        _learnedPresenter?.ClearIfMatches(pillId);
        _learnedCoordinator?.PillEnded(pillId);
    }

    private void RequestActiveLearnedCorrectionUndo()
    {
        if (_learnedCoordinator?.CurrentUndoRecord is { } active)
        {
            RequestLearnedCorrectionUndo(active.PillId);
        }
    }

    private void RequestLearnedCorrectionUndo(Guid pillId)
    {
        if (_learnedCoordinator is not { } coordinator)
        {
            return;
        }

        var record = coordinator.ClaimUndo(pillId);
        if (record is null)
        {
            return;
        }

        _learnedPresenter?.ShowUndoPending(pillId);
        _ = Task.Run(async () =>
        {
            try
            {
                await coordinator.PerformUndoAsync(record, pillId).ConfigureAwait(false);
                PostToLiveWindow(window => window.RefreshLearnMemoryUi());
            }
            catch (Exception)
            {
                _learnedPresenter?.ShowUndoError(pillId);
            }
        });
    }

    private void OnSelfLearningPasteCompleted(PasteCompletedEvent paste)
    {
        try
        {
            _correctionWatcher?.OnPasteCompleted(paste);
        }
        catch (Exception)
        {
        }
    }

    private void OnSelfLearningRecordingChanged(bool active)
    {
        if (active)
        {
            _correctionWatcher?.OnRecordingStarted();
        }
    }

    private void OnSelfLearningSettingChanged(bool enabled) =>
        _correctionWatcher?.OnLearnFromEditsChanged(enabled);

    private void DisposeSelfLearning()
    {
        if (_learnMemoryStore is { } store)
        {
            store.Changed -= OnLearnMemoryChanged;
        }

        _correctionWatcher?.OnLearnFromEditsChanged(false);
        _pastedRegionObserver?.Dispose();
    }

    private sealed class OverlayLearnedCorrectionPresenter(
        App owner,
        LearnedCorrectionCoordinator coordinator) : ILearnedCorrectionPresenter
    {
        private readonly object _sync = new();
        private LearnedCorrectionPillModel? _activeModel;

        public void Show(LearnedCorrectionPillModel pill)
        {
            lock (_sync)
            {
                _activeModel = pill;
            }

            owner.PostToLiveWindow(window =>
            {
                if (window.ShowLearnedCorrectionPill(pill))
                {
                    coordinator.PillAdmitted(pill.PillId);
                }
                else
                {
                    ClearIfMatches(pill.PillId);
                    coordinator.PillEnded(pill.PillId);
                }
            });
        }

        public void ShowUndoPending(Guid pillId) =>
            UpdatePhase(pillId, LearnedCorrectionPillPhase.UndoPending);

        public void ShowUndone(Guid pillId) =>
            UpdatePhase(pillId, LearnedCorrectionPillPhase.Undone);

        public void ShowUndoError(Guid pillId) =>
            UpdatePhase(pillId, LearnedCorrectionPillPhase.UndoError);

        public void ShowSaveError(string canonical) =>
            owner.PostToLiveWindow(window => window.ShowLearnedCorrectionSaveErrorPill(canonical));

        public void Close(Guid pillId)
        {
            ClearIfMatches(pillId);
            owner.PostToLiveWindow(window => window.CloseLearnedCorrectionPill(pillId));
        }

        public void ClearIfMatches(Guid pillId)
        {
            lock (_sync)
            {
                if (_activeModel?.PillId == pillId)
                {
                    _activeModel = null;
                }
            }
        }

        private void UpdatePhase(Guid pillId, LearnedCorrectionPillPhase phase)
        {
            LearnedCorrectionPillModel? updated = null;
            lock (_sync)
            {
                if (_activeModel is { } model && model.PillId == pillId)
                {
                    updated = model with { Phase = phase };
                    _activeModel = updated;
                }
            }

            if (updated is not null)
            {
                owner.PostToLiveWindow(window => window.UpdateLearnedCorrectionPill(updated));
            }
        }
    }
}
