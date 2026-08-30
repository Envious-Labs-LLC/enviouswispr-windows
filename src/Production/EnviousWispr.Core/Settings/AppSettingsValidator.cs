using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Input;

namespace EnviousWispr.Core.Settings;

public static class AppSettingsValidator
{
    public const int MaximumCustomWords = 10_000;
    public const int MaximumSnippets = 1_000;

    public static AppError? Validate(AppSettings? settings, AppErrorStage stage)
    {
        if (settings is null ||
            settings.SchemaVersion != AppSettings.CurrentSchemaVersion ||
            settings.LaunchCount is < 0 or int.MaxValue ||
            (settings.PreferredMicrophoneId is not null &&
                (string.IsNullOrWhiteSpace(settings.PreferredMicrophoneId) ||
                 settings.PreferredMicrophoneId.Length > 2_048)) ||
            settings.Observability is null ||
            settings.Observability.DiagnosticRetentionDays
                is < RetentionDays.DiagnosticMinimum or > RetentionDays.DiagnosticMaximum ||
            !IsValid(settings.Preferences) ||
            !IsValid(settings.UserData))
        {
            return new AppError(AppErrorCode.InvalidData, stage, CanRetry: false);
        }

        return null;
    }

    public static AppError? Validate(PortableProfile? profile, AppErrorStage stage)
    {
        if (profile is null ||
            profile.SchemaVersion != PortableProfile.CurrentSchemaVersion ||
            !IsValid(profile.Preferences) ||
            !IsValid(profile.UserData))
        {
            return new AppError(AppErrorCode.InvalidData, stage, CanRetry: false);
        }

        return null;
    }

    private static bool IsValid(UserPreferences? preferences) =>
        preferences is not null &&
        preferences.Dictation is not null &&
        preferences.Polish is not null &&
        preferences.History is not null &&
        Enum.IsDefined(preferences.Dictation.FinalEngine) &&
        Enum.IsDefined(preferences.Dictation.WhisperLanguage) &&
        Enum.IsDefined(preferences.Dictation.RecordingMode) &&
        HotkeyGestureParser.Parse(preferences.Dictation.PushToTalkGesture).Succeeded &&
        HotkeyGestureParser.Parse(preferences.Dictation.CancelGesture).Succeeded &&
        HotkeyGestureParser.Parse(preferences.Dictation.QuickAddGesture).Succeeded &&
        HasDistinctDictationGestures(preferences.Dictation) &&
        Enum.IsDefined(preferences.Polish.Provider) &&
        (preferences.Polish.ModelId is null ||
            (!string.IsNullOrWhiteSpace(preferences.Polish.ModelId) && preferences.Polish.ModelId.Length <= 256)) &&
        (preferences.Polish.OllamaEndpoint is null ||
            (!string.IsNullOrWhiteSpace(preferences.Polish.OllamaEndpoint) &&
             preferences.Polish.OllamaEndpoint.Length <= 2_048)) &&
        // A stored threshold below the policy floor is not invalid - the policy clamps it up, so
        // rejecting the file here would reset every setting a user has over one number. Only a
        // value that cannot be a duration at all is rejected.
        double.IsFinite(preferences.Dictation.AutoStopSilenceSeconds) &&
        preferences.Dictation.AutoStopSilenceSeconds >= 0 &&
        preferences.History.RetentionDays
            is >= RetentionDays.HistoryMinimum and <= RetentionDays.HistoryMaximum &&
        Enum.IsDefined(preferences.Theme) &&
        Enum.IsDefined(preferences.OverlayPosition) &&
        Enum.IsDefined(preferences.PillDesignWithoutWords) &&
        preferences.PillDesignWithoutWords is not RecordingPillDesign.ReadingWell &&
        preferences.PillDesignWithWords is RecordingPillDesign.ReadingWell &&
        Enum.IsDefined(preferences.RecordingSoundPairing);

    private static bool HasDistinctDictationGestures(DictationPreferences preferences)
    {
        var record = HotkeyGestureParser.Parse(preferences.PushToTalkGesture).Gesture;
        var cancel = HotkeyGestureParser.Parse(preferences.CancelGesture).Gesture;
        var quickAdd = HotkeyGestureParser.Parse(preferences.QuickAddGesture).Gesture;
        return record is not null && cancel is not null && quickAdd is not null &&
            record.Value != cancel.Value &&
            record.Value != quickAdd.Value &&
            cancel.Value != quickAdd.Value;
    }

    private static bool IsValid(ReusableUserData? userData) =>
        userData is not null &&
        userData.CustomWords is not null &&
        userData.Snippets is not null &&
        userData.CustomWords.Count <= MaximumCustomWords &&
        userData.Snippets.Count <= MaximumSnippets &&
        userData.CustomWords.All(entry =>
            entry is not null &&
            !string.IsNullOrWhiteSpace(entry.SpokenForm) &&
            entry.SpokenForm.Length <= 256 &&
            !string.IsNullOrWhiteSpace(entry.Replacement) &&
            entry.Replacement.Length <= 256 &&
            // A NUMBER NOBODY DEFINED IS NOT A CHOICE. An enum will hold any integer the file
            // contains, so "strictness": 99 loads, behaves as the ordinary rule and is exported as
            // "default" - a file that changed meaning on the way through and said nothing. Refusing
            // it makes the file visibly wrong instead.
            Enum.IsDefined(entry.Strictness)) &&
        userData.Snippets.All(entry =>
            entry is not null &&
            !string.IsNullOrWhiteSpace(entry.Name) &&
            entry.Name.Length <= 128 &&
            entry.Body is not null &&
            entry.Body.Length <= 10_000);
}
