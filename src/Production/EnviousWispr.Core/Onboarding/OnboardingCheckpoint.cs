using EnviousWispr.Core.Polish;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Core.Onboarding;

/// <summary>Steps in the Windows first-run onboarding journey aligned with macOS #4528.</summary>
public enum OnboardingStep
{
    Welcome,
    Language,
    SpeechPick,
    SpeechCompare,
    PolishIntro,
    PolishPick,
    PolishSeeAll,
    Downloads,
    Permissions,
    Key,
    GettingReady,
    Practice,
    Closing,
    Done,
}

/// <summary>Customer choice for AI Polish selected during setup (never contains credentials).</summary>
public enum OnboardingPolishChoiceKind
{
    None,
    EgOne,
    Ollama,
    OpenAI,
    Claude,
    Gemini,
}

/// <summary>Non-secret user choices recorded across onboarding steps. Never stores API keys or tokens.</summary>
public sealed record OnboardingChoices(
    string LanguageCode = LanguageCatalog.AutomaticCode,
    FinalAsrEngine SpeechEngine = FinalAsrEngine.Parakeet,
    string SpeechModelId = ParakeetModelIds.Final,
    OnboardingPolishChoiceKind PolishChoice = OnboardingPolishChoiceKind.EgOne,
    string? EgOneModelId = EgOneModelCatalog.MiniId,
    string? CloudModelId = null,
    DictationRecordingMode RecordingMode = DictationRecordingMode.PushToTalk,
    string? HotkeyGestureText = null);

public enum OnboardingPracticeOutcome
{
    None,
    Worked,
    Skipped,
    TimedOut,
}

/// <summary>
/// Durable checkpoint written atomically during first-run setup.
/// Contains step position and non-secret choices only (never API keys or hardware IDs).
/// </summary>
public sealed record OnboardingCheckpoint(
    int SchemaVersion,
    OnboardingStep Step,
    IReadOnlyList<OnboardingStep> BackStack,
    OnboardingChoices Choices,
    bool PracticeSucceeded,
    OnboardingPracticeOutcome PracticeOutcome,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset StartedAtUtc)
{
    public const int CurrentSchemaVersion = 1;

    public static OnboardingCheckpoint Empty(DateTimeOffset nowUtc) => new(
        SchemaVersion: CurrentSchemaVersion,
        Step: OnboardingStep.Welcome,
        BackStack: [],
        Choices: new OnboardingChoices(),
        PracticeSucceeded: false,
        PracticeOutcome: OnboardingPracticeOutcome.None,
        UpdatedAtUtc: nowUtc,
        StartedAtUtc: nowUtc);
}

public sealed record OnboardingRestoreDecision(
    bool ShouldResume,
    OnboardingStep Step,
    IReadOnlyList<OnboardingStep> BackStack,
    OnboardingChoices Choices,
    bool PracticeSucceeded,
    OnboardingPracticeOutcome PracticeOutcome);

/// <summary>Pure resolver deciding how a saved onboarding checkpoint resumes on launch.</summary>
public static class OnboardingRestore
{
    public static OnboardingRestoreDecision Resolve(
        OnboardingCheckpoint? checkpoint,
        bool hasCompletedOnboarding,
        OnboardingStep? resumeStepOverride = null)
    {
        if (hasCompletedOnboarding ||
            checkpoint is null ||
            checkpoint.SchemaVersion != OnboardingCheckpoint.CurrentSchemaVersion ||
            !Enum.IsDefined(checkpoint.Step) ||
            checkpoint.Step == OnboardingStep.Done)
        {
            return new OnboardingRestoreDecision(
                ShouldResume: false,
                Step: OnboardingStep.Welcome,
                BackStack: [],
                Choices: new OnboardingChoices(),
                PracticeSucceeded: false,
                PracticeOutcome: OnboardingPracticeOutcome.None);
        }

        var rawStep = resumeStepOverride is { } forced && Enum.IsDefined(forced) && forced != OnboardingStep.Done
            ? forced
            : checkpoint.Step;

        var normalizedStep = rawStep switch
        {
            OnboardingStep.SpeechCompare => OnboardingStep.SpeechPick,
            OnboardingStep.PolishSeeAll => OnboardingStep.PolishPick,
            OnboardingStep.Practice when checkpoint.PracticeSucceeded => OnboardingStep.Closing,
            _ => rawStep,
        };

        var sanitizedStack = SanitizeBackStack(checkpoint.BackStack, normalizedStep);
        var sanitizedChoices = SanitizeChoices(checkpoint.Choices);

        return new OnboardingRestoreDecision(
            ShouldResume: true,
            Step: normalizedStep,
            BackStack: sanitizedStack,
            Choices: sanitizedChoices,
            PracticeSucceeded: checkpoint.PracticeSucceeded,
            PracticeOutcome: Enum.IsDefined(checkpoint.PracticeOutcome)
                ? checkpoint.PracticeOutcome
                : OnboardingPracticeOutcome.None);
    }

    private static OnboardingStep[] SanitizeBackStack(
        IReadOnlyList<OnboardingStep>? rawStack,
        OnboardingStep currentStep)
    {
        if (rawStack is null || rawStack.Count == 0)
        {
            return [];
        }

        var cleaned = new List<OnboardingStep>(rawStack.Count);
        foreach (var candidate in rawStack)
        {
            if (!Enum.IsDefined(candidate) ||
                candidate is OnboardingStep.Done or OnboardingStep.SpeechCompare or OnboardingStep.PolishSeeAll ||
                candidate == currentStep)
            {
                continue;
            }

            if (cleaned.Count > 0 && cleaned[^1] == candidate)
            {
                continue;
            }

            cleaned.Add(candidate);
        }

        return [.. cleaned];
    }

    private static OnboardingChoices SanitizeChoices(OnboardingChoices? raw)
    {
        if (raw is null)
        {
            return new OnboardingChoices();
        }

        var normalizedLanguage = LanguageCatalog.AutomaticCode;
        if (!string.IsNullOrWhiteSpace(raw.LanguageCode) &&
            !string.Equals(raw.LanguageCode.Trim(), LanguageCatalog.AutomaticCode, StringComparison.OrdinalIgnoreCase) &&
            LanguageCatalog.TryGetEntry(raw.LanguageCode, out var langEntry))
        {
            normalizedLanguage = langEntry.Code;
        }

        var engine = Enum.IsDefined(raw.SpeechEngine) ? raw.SpeechEngine : FinalAsrEngine.Parakeet;
        var speechModelId = NormalizeSpeechModelId(engine, raw.SpeechModelId);

        var polishChoice = Enum.IsDefined(raw.PolishChoice)
            ? raw.PolishChoice
            : OnboardingPolishChoiceKind.EgOne;

        string? egOneModelId = polishChoice == OnboardingPolishChoiceKind.EgOne
            ? EgOneModelCatalog.NormalizeOrDefault(raw.EgOneModelId)
            : null;

        var recordingMode = Enum.IsDefined(raw.RecordingMode)
            ? raw.RecordingMode
            : DictationRecordingMode.PushToTalk;

        var hotkeyText = string.IsNullOrWhiteSpace(raw.HotkeyGestureText)
            ? null
            : raw.HotkeyGestureText.Trim();

        return new OnboardingChoices(
            LanguageCode: normalizedLanguage,
            SpeechEngine: engine,
            SpeechModelId: speechModelId,
            PolishChoice: polishChoice,
            EgOneModelId: egOneModelId,
            CloudModelId: string.IsNullOrWhiteSpace(raw.CloudModelId) ? null : raw.CloudModelId.Trim(),
            RecordingMode: recordingMode,
            HotkeyGestureText: hotkeyText);
    }

    public static string NormalizeSpeechModelId(FinalAsrEngine engine, string? rawModelId)
    {
        if (engine == FinalAsrEngine.Whisper)
        {
            return WhisperModelIds.Final;
        }

        if (string.IsNullOrWhiteSpace(rawModelId) ||
            string.Equals(rawModelId.Trim(), ParakeetModelIds.LegacyV3, StringComparison.OrdinalIgnoreCase))
        {
            return ParakeetModelIds.Final;
        }

        return ParakeetModelIds.Final;
    }
}
