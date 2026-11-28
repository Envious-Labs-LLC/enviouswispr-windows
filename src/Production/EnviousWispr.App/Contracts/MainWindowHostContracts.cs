using EnviousWispr.Audio;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Distribution;
using EnviousWispr.Core.Feedback;
using EnviousWispr.Core.Learning;
using EnviousWispr.Core.Onboarding;
using EnviousWispr.Core.Polish;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;
using EnviousWispr.Services.Feedback;
using EnviousWispr.Services.UserData;

namespace EnviousWispr.App.Contracts;

internal sealed record WindowDictationHost(
    Action<DictationStatus>? SessionStatusChanged = null,
    Action<RecordingSoundPairing, RecordingSoundMoment>? RecordingSoundPlaybackFailed = null,
    Action<AudioDeviceChange>? AudioDevicesChanged = null,
    Action? DictationEnded = null,
    Action? TranscriptionRetryRequested = null,
    Action? ManualDictationToggleRequested = null,
    Action<bool>? KeybindCaptureActiveChanged = null,
    Action<bool>? OnboardingKeyRehearsalActiveChanged = null,
    Func<string, DictationRecordingMode, Task<(bool Applied, string? Error)>>? ApplyOnboardingRecordingKey = null,
    Func<string, IProgress<FileTranscriptionProgress>, CancellationToken, Task<FileTranscriptionResult>>? TranscribeFile = null);

internal sealed record WindowHistoryHost(
    Action? RecoveryCleared = null,
    Action? RecoveryUndoRequested = null,
    Action<Guid>? HistoryPasteRequested = null,
    string? DictationRecordingsDirectory = null);

internal sealed record WindowVocabularyHost(
    Action<DiagnosticSnippetImport>? SnippetImportReported = null,
    Action<DiagnosticWordImport>? WordImportReported = null,
    Func<(bool HasStoredMemory, int BlockedCount)>? QueryLearnMemoryState = null,
    Func<bool>? ForgetLearnMemory = null,
    Action<IReadOnlyList<CustomWordEntry>>? WordsRemovedFromDictionary = null,
    Action<string, IReadOnlyList<string>>? LearnedAliasesRemovedFromDictionary = null,
    Func<IReadOnlyDictionary<string, LearnMemoryEntry>?>? ExportLearnMemory = null,
    Action<IReadOnlyDictionary<string, LearnMemoryEntry>>? ImportLearnMemory = null,
    Action<Guid>? LearnedPillUndoRequested = null,
    Action? LearnedPillUndoActiveRequested = null,
    Action<Guid>? LearnedPillEnded = null);

internal sealed record WindowModelHost(
    Action? ModelDownloadRequested = null,
    Action? ModelDownloadCancelRequested = null,
    Action<string, string?, bool>? OnboardingDualDownloadRequested = null,
    Action? OnboardingDualDownloadCancelRequested = null,
    Action? OnboardingDualDownloadSkipPolishRequested = null,
    Action? OnboardingDualDownloadRetryRequested = null,
    Func<FinalAsrEngine, OnboardingPolishChoiceKind, string?, string?, Task>? ApplyOnboardingEnginesChoice = null,
    Func<FinalAsrEngine, string?, Task<(bool SpeechInstalled, bool PolishInstalled)>>? ProbeOnboardingModelsInstalled = null,
    Action<string>? EgOneModelDownloadRequested = null,
    Action? EgOneModelDownloadCancelRequested = null,
    Action<string>? EgOneModelRemoveRequested = null,
    Func<IReadOnlyDictionary<string, EgOneServerActivity>>? GetEgOneServerActivities = null,
    Action<FinalAsrEngine>? SpeechEngineDownloadRequested = null,
    Action<FinalAsrEngine>? SpeechEngineRemoveRequested = null,
    Func<bool>? IsSpeechEngineBusy = null,
    Action? PreviewModelDownloadRequested = null,
    Action? PreviewModelRemoveRequested = null,
    Action? GraphicsRuntimeDownloadRequested = null,
    Action? GraphicsRuntimeDownloadCancelRequested = null);

internal sealed record WindowSystemHost(
    Action<AppSettings>? SettingsChanged = null,
    Action<bool, int>? DiagnosticsExportCompleted = null,
    Action? UpdateCheckRequested = null,
    Action? UpdateApplyRequested = null,
    Action<bool>? StartupToggleRequested = null,
    Func<Task<StartupRegistrationResult>>? LoadStartupRegistration = null,
    Func<PortableProfile, int, string, Task<UserDataExportResult>>? ExportUserData = null,
    Func<SessionHoldAttempt?>? DeleteAllData = null,
    Action? SpeedCheckRequested = null,
    string? FeedbackDataDirectory = null,
    FeedbackDraftStore? FeedbackDraftStore = null,
    Func<FeedbackDiagnosticsSnapshot>? BuildFeedbackDiagnosticsSnapshot = null,
    Func<FeedbackSubmission, CancellationToken, Task<FeedbackSendResult>>? SubmitFeedback = null,
    Func<CancellationToken, Task>? FlushFeedbackOutbox = null,
    Func<CancellationToken, Task<bool>>? CheckUndeliverableFeedback = null);

internal sealed record MainWindowHostContracts(
    WindowDictationHost Dictation,
    WindowHistoryHost History,
    WindowVocabularyHost Vocabulary,
    WindowModelHost Models,
    WindowSystemHost System);
