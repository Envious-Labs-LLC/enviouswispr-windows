namespace EnviousWispr.App.Contracts;

/// <summary>Attaches domain host contracts to MainWindow and symmetrically tears down every event and delegate on Dispose.</summary>
internal static class MainWindowHostBinder
{
    public static IDisposable Attach(MainWindow window, MainWindowHostContracts contracts)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(contracts);
        return new Binding(window, contracts);
    }

    private sealed class Binding : IDisposable
    {
        private readonly MainWindow _window;
        private readonly WindowDictationHost _dictation;
        private readonly WindowHistoryHost _history;
        private readonly WindowVocabularyHost _vocabulary;
        private readonly WindowModelHost _models;
        private readonly WindowSystemHost _system;
        private int _disposed;

        public Binding(MainWindow window, MainWindowHostContracts contracts)
        {
            _window = window;
            _dictation = contracts.Dictation;
            _history = contracts.History;
            _vocabulary = contracts.Vocabulary;
            _models = contracts.Models;
            _system = contracts.System;

            AttachDictation();
            AttachHistory();
            AttachVocabulary();
            AttachModels();
            AttachSystem();
        }

        private void AttachDictation()
        {
            Subscribe(h => _window.SessionStatusChanged += h, _dictation.SessionStatusChanged);
            Subscribe(h => _window.RecordingSoundPlaybackFailed += h, _dictation.RecordingSoundPlaybackFailed);
            Subscribe(h => _window.AudioDevicesChanged += h, _dictation.AudioDevicesChanged);
            Subscribe(h => _window.DictationEnded += h, _dictation.DictationEnded);
            Subscribe(h => _window.TranscriptionRetryRequested += h, _dictation.TranscriptionRetryRequested);
            Subscribe(h => _window.ManualDictationToggleRequested += h, _dictation.ManualDictationToggleRequested);
            Subscribe(h => _window.KeybindCaptureActiveChanged += h, _dictation.KeybindCaptureActiveChanged);
            Subscribe(h => _window.OnboardingKeyRehearsalActiveChanged += h, _dictation.OnboardingKeyRehearsalActiveChanged);
            _window.ApplyOnboardingRecordingKey = _dictation.ApplyOnboardingRecordingKey;
            _window.TranscribeFile = _dictation.TranscribeFile;
        }

        private void AttachHistory()
        {
            Subscribe(h => _window.RecoveryCleared += h, _history.RecoveryCleared);
            Subscribe(h => _window.RecoveryUndoRequested += h, _history.RecoveryUndoRequested);
            Subscribe(h => _window.HistoryPasteRequested += h, _history.HistoryPasteRequested);
            _window.DictationRecordingsDirectory = _history.DictationRecordingsDirectory;
        }

        private void AttachVocabulary()
        {
            Subscribe(h => _window.SnippetImportReported += h, _vocabulary.SnippetImportReported);
            Subscribe(h => _window.WordImportReported += h, _vocabulary.WordImportReported);
            Subscribe(h => _window.MishearingSuggestionsRequested += h, _vocabulary.MishearingSuggestionsRequested);
            _window.QueryLearnMemoryState = _vocabulary.QueryLearnMemoryState;
            _window.ForgetLearnMemory = _vocabulary.ForgetLearnMemory;
            _window.WordsRemovedFromDictionary = _vocabulary.WordsRemovedFromDictionary;
            _window.LearnedAliasesRemovedFromDictionary = _vocabulary.LearnedAliasesRemovedFromDictionary;
            _window.ExportLearnMemory = _vocabulary.ExportLearnMemory;
            _window.ImportLearnMemory = _vocabulary.ImportLearnMemory;
            Subscribe(h => _window.LearnedPillUndoRequested += h, _vocabulary.LearnedPillUndoRequested);
            Subscribe(h => _window.LearnedPillUndoActiveRequested += h, _vocabulary.LearnedPillUndoActiveRequested);
            Subscribe(h => _window.LearnedPillEnded += h, _vocabulary.LearnedPillEnded);
        }

        private void AttachModels()
        {
            Subscribe(h => _window.ModelDownloadRequested += h, _models.ModelDownloadRequested);
            Subscribe(h => _window.ModelDownloadCancelRequested += h, _models.ModelDownloadCancelRequested);
            Subscribe(h => _window.OnboardingDualDownloadRequested += h, _models.OnboardingDualDownloadRequested);
            Subscribe(h => _window.OnboardingDualDownloadCancelRequested += h, _models.OnboardingDualDownloadCancelRequested);
            Subscribe(h => _window.OnboardingDualDownloadSkipPolishRequested += h, _models.OnboardingDualDownloadSkipPolishRequested);
            Subscribe(h => _window.OnboardingDualDownloadRetryRequested += h, _models.OnboardingDualDownloadRetryRequested);
            _window.ApplyOnboardingEnginesChoice = _models.ApplyOnboardingEnginesChoice;
            _window.ProbeOnboardingModelsInstalled = _models.ProbeOnboardingModelsInstalled;
            Subscribe(h => _window.EgOneModelDownloadRequested += h, _models.EgOneModelDownloadRequested);
            Subscribe(h => _window.EgOneModelDownloadCancelRequested += h, _models.EgOneModelDownloadCancelRequested);
            Subscribe(h => _window.EgOneModelRemoveRequested += h, _models.EgOneModelRemoveRequested);
            Subscribe(h => _window.SpeechEngineDownloadRequested += h, _models.SpeechEngineDownloadRequested);
            Subscribe(h => _window.SpeechEngineRemoveRequested += h, _models.SpeechEngineRemoveRequested);
            _window.IsSpeechEngineBusy = _models.IsSpeechEngineBusy;
            Subscribe(h => _window.PreviewModelDownloadRequested += h, _models.PreviewModelDownloadRequested);
            Subscribe(h => _window.PreviewModelRemoveRequested += h, _models.PreviewModelRemoveRequested);
            Subscribe(h => _window.GraphicsRuntimeDownloadRequested += h, _models.GraphicsRuntimeDownloadRequested);
            Subscribe(h => _window.GraphicsRuntimeDownloadCancelRequested += h, _models.GraphicsRuntimeDownloadCancelRequested);
        }

        private void AttachSystem()
        {
            Subscribe(h => _window.SettingsChanged += h, _system.SettingsChanged);
            Subscribe(h => _window.DiagnosticsExportCompleted += h, _system.DiagnosticsExportCompleted);
            Subscribe(h => _window.UpdateCheckRequested += h, _system.UpdateCheckRequested);
            Subscribe(h => _window.UpdateApplyRequested += h, _system.UpdateApplyRequested);
            Subscribe(h => _window.StartupToggleRequested += h, _system.StartupToggleRequested);
            _window.LoadStartupRegistration = _system.LoadStartupRegistration;
            _window.ExportUserData = _system.ExportUserData;
            _window.DeleteAllData = _system.DeleteAllData;
            Subscribe(h => _window.SpeedCheckRequested += h, _system.SpeedCheckRequested);
            _window.FeedbackDataDirectory = _system.FeedbackDataDirectory;
            _window.FeedbackDraftStore = _system.FeedbackDraftStore;
            _window.BuildFeedbackDiagnosticsSnapshot = _system.BuildFeedbackDiagnosticsSnapshot;
            _window.SubmitFeedback = _system.SubmitFeedback;
            _window.FlushFeedbackOutbox = _system.FlushFeedbackOutbox;
            _window.CheckUndeliverableFeedback = _system.CheckUndeliverableFeedback;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            DetachDictation();
            DetachHistory();
            DetachVocabulary();
            DetachModels();
            DetachSystem();
        }

        private void DetachDictation()
        {
            Unsubscribe(h => _window.SessionStatusChanged -= h, _dictation.SessionStatusChanged);
            Unsubscribe(h => _window.RecordingSoundPlaybackFailed -= h, _dictation.RecordingSoundPlaybackFailed);
            Unsubscribe(h => _window.AudioDevicesChanged -= h, _dictation.AudioDevicesChanged);
            Unsubscribe(h => _window.DictationEnded -= h, _dictation.DictationEnded);
            Unsubscribe(h => _window.TranscriptionRetryRequested -= h, _dictation.TranscriptionRetryRequested);
            Unsubscribe(h => _window.ManualDictationToggleRequested -= h, _dictation.ManualDictationToggleRequested);
            Unsubscribe(h => _window.KeybindCaptureActiveChanged -= h, _dictation.KeybindCaptureActiveChanged);
            Unsubscribe(h => _window.OnboardingKeyRehearsalActiveChanged -= h, _dictation.OnboardingKeyRehearsalActiveChanged);
            _window.ApplyOnboardingRecordingKey = null;
            _window.TranscribeFile = null;
        }

        private void DetachHistory()
        {
            Unsubscribe(h => _window.RecoveryCleared -= h, _history.RecoveryCleared);
            Unsubscribe(h => _window.RecoveryUndoRequested -= h, _history.RecoveryUndoRequested);
            Unsubscribe(h => _window.HistoryPasteRequested -= h, _history.HistoryPasteRequested);
            _window.DictationRecordingsDirectory = null;
        }

        private void DetachVocabulary()
        {
            Unsubscribe(h => _window.SnippetImportReported -= h, _vocabulary.SnippetImportReported);
            Unsubscribe(h => _window.WordImportReported -= h, _vocabulary.WordImportReported);
            Unsubscribe(h => _window.MishearingSuggestionsRequested -= h, _vocabulary.MishearingSuggestionsRequested);
            _window.QueryLearnMemoryState = null;
            _window.ForgetLearnMemory = null;
            _window.WordsRemovedFromDictionary = null;
            _window.LearnedAliasesRemovedFromDictionary = null;
            _window.ExportLearnMemory = null;
            _window.ImportLearnMemory = null;
            Unsubscribe(h => _window.LearnedPillUndoRequested -= h, _vocabulary.LearnedPillUndoRequested);
            Unsubscribe(h => _window.LearnedPillUndoActiveRequested -= h, _vocabulary.LearnedPillUndoActiveRequested);
            Unsubscribe(h => _window.LearnedPillEnded -= h, _vocabulary.LearnedPillEnded);
        }

        private void DetachModels()
        {
            Unsubscribe(h => _window.ModelDownloadRequested -= h, _models.ModelDownloadRequested);
            Unsubscribe(h => _window.ModelDownloadCancelRequested -= h, _models.ModelDownloadCancelRequested);
            Unsubscribe(h => _window.OnboardingDualDownloadRequested -= h, _models.OnboardingDualDownloadRequested);
            Unsubscribe(h => _window.OnboardingDualDownloadCancelRequested -= h, _models.OnboardingDualDownloadCancelRequested);
            Unsubscribe(h => _window.OnboardingDualDownloadSkipPolishRequested -= h, _models.OnboardingDualDownloadSkipPolishRequested);
            Unsubscribe(h => _window.OnboardingDualDownloadRetryRequested -= h, _models.OnboardingDualDownloadRetryRequested);
            _window.ApplyOnboardingEnginesChoice = null;
            _window.ProbeOnboardingModelsInstalled = null;
            Unsubscribe(h => _window.EgOneModelDownloadRequested -= h, _models.EgOneModelDownloadRequested);
            Unsubscribe(h => _window.EgOneModelDownloadCancelRequested -= h, _models.EgOneModelDownloadCancelRequested);
            Unsubscribe(h => _window.EgOneModelRemoveRequested -= h, _models.EgOneModelRemoveRequested);
            Unsubscribe(h => _window.SpeechEngineDownloadRequested -= h, _models.SpeechEngineDownloadRequested);
            Unsubscribe(h => _window.SpeechEngineRemoveRequested -= h, _models.SpeechEngineRemoveRequested);
            _window.IsSpeechEngineBusy = null;
            Unsubscribe(h => _window.PreviewModelDownloadRequested -= h, _models.PreviewModelDownloadRequested);
            Unsubscribe(h => _window.PreviewModelRemoveRequested -= h, _models.PreviewModelRemoveRequested);
            Unsubscribe(h => _window.GraphicsRuntimeDownloadRequested -= h, _models.GraphicsRuntimeDownloadRequested);
            Unsubscribe(h => _window.GraphicsRuntimeDownloadCancelRequested -= h, _models.GraphicsRuntimeDownloadCancelRequested);
        }

        private void DetachSystem()
        {
            Unsubscribe(h => _window.SettingsChanged -= h, _system.SettingsChanged);
            Unsubscribe(h => _window.DiagnosticsExportCompleted -= h, _system.DiagnosticsExportCompleted);
            Unsubscribe(h => _window.UpdateCheckRequested -= h, _system.UpdateCheckRequested);
            Unsubscribe(h => _window.UpdateApplyRequested -= h, _system.UpdateApplyRequested);
            Unsubscribe(h => _window.StartupToggleRequested -= h, _system.StartupToggleRequested);
            _window.LoadStartupRegistration = null;
            _window.ExportUserData = null;
            _window.DeleteAllData = null;
            Unsubscribe(h => _window.SpeedCheckRequested -= h, _system.SpeedCheckRequested);
            _window.FeedbackDataDirectory = null;
            _window.FeedbackDraftStore = null;
            _window.BuildFeedbackDiagnosticsSnapshot = null;
            _window.SubmitFeedback = null;
            _window.FlushFeedbackOutbox = null;
            _window.CheckUndeliverableFeedback = null;
        }

        private static void Subscribe<TDelegate>(Action<TDelegate> add, TDelegate? handler)
            where TDelegate : class
        {
            if (handler is not null)
            {
                add(handler);
            }
        }

        private static void Unsubscribe<TDelegate>(Action<TDelegate> remove, TDelegate? handler)
            where TDelegate : class
        {
            if (handler is not null)
            {
                remove(handler);
            }
        }
    }
}
