using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Input;

namespace EnviousWispr.Core.Settings;

public static class AppSettingsValidator
{
    public const int MaximumCustomWords = 10_000;
    public const int MaximumSnippets = 1_000;
    public const int MaximumSnippetKeywordLength = 64;

    /// <summary>The longest trigger the store keeps; the page's box and snippet import refuse longer.</summary>
    public const int MaximumSnippetTriggerLength = 128;

    /// <summary>The longest snippet text the store keeps; the page's box and snippet import refuse longer.</summary>
    public const int MaximumSnippetBodyLength = 10_000;

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
        Enum.IsDefined(preferences.Dictation.EnglishSpelling) &&
        Enum.IsDefined(preferences.Dictation.RecordingMode) &&
        HotkeyGestureParser.Parse(preferences.Dictation.PushToTalkGesture).Succeeded &&
        HotkeyGestureParser.Parse(preferences.Dictation.CancelGesture).Succeeded &&
        HotkeyGestureParser.Parse(preferences.Dictation.QuickAddGesture).Succeeded &&
        preferences.Dictation.PasteLastGesture is not null &&
        preferences.Dictation.CopyLastGesture is not null &&
        HotkeyGestureParser.ParseOneShot(preferences.Dictation.PasteLastGesture).Succeeded &&
        HotkeyGestureParser.ParseOneShot(preferences.Dictation.CopyLastGesture).Succeeded &&
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
        if (record is null || cancel is null || quickAdd is null)
        {
            return false;
        }

        // Every BOUND gesture differs from every other; an unset last-dictation shortcut clashes with nothing.
        var bound = new List<HotkeyGesture> { record.Value, cancel.Value, quickAdd.Value };
        foreach (var optional in new[] { preferences.PasteLastGesture, preferences.CopyLastGesture })
        {
            if (HotkeyGestureParser.ParseOptional(optional).Gesture is { } gesture)
            {
                bound.Add(gesture);
            }
        }

        return bound.Distinct().Count() == bound.Count;
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
        // THE KEYWORD IS NOT HELD TO ITS SAVE-TIME RULE HERE. A keyword of two words cannot fire, and
        // the page refuses one, but refusing it at load would reset every setting a person has over
        // one word; it is bounded only so a file cannot carry an arbitrary string.
        userData.SnippetKeyword is not null &&
        userData.SnippetKeyword.Length <= MaximumSnippetKeywordLength &&
        userData.Snippets.All(entry =>
            entry is not null &&
            !string.IsNullOrWhiteSpace(entry.Name) &&
            entry.Name.Length <= MaximumSnippetTriggerLength &&
            entry.Body is not null &&
            entry.Body.Length <= MaximumSnippetBodyLength);
}
