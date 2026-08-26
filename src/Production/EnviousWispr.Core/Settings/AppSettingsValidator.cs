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
        HotkeyGestureParser.Parse(preferences.Dictation.PushToTalkGesture).Succeeded &&
        Enum.IsDefined(preferences.Polish.Provider) &&
        (preferences.Polish.ModelId is null ||
            (!string.IsNullOrWhiteSpace(preferences.Polish.ModelId) && preferences.Polish.ModelId.Length <= 256)) &&
        preferences.History.RetentionDays is >= 0 and <= 3_650 &&
        Enum.IsDefined(preferences.Theme);

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
            entry.Replacement.Length <= 256) &&
        userData.Snippets.All(entry =>
            entry is not null &&
            !string.IsNullOrWhiteSpace(entry.Name) &&
            entry.Name.Length <= 128 &&
            entry.Body is not null &&
            entry.Body.Length <= 10_000);
}
