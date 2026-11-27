using EnviousWispr.Core.Input;
using EnviousWispr.Core.Polish;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Services.Settings;

/// <summary>
/// Shared historical schema transforms normalized across both <see cref="JsonSettingsStore"/>
/// and <see cref="JsonPortableProfileService"/> so settings upgrades and portable profile imports
/// apply identical defaults, key-clash resolution, and dictionary normalization.
/// </summary>
internal static class SettingsSchemaMigration
{
    internal sealed record LegacyDictationPreferences(
        FinalAsrEngine FinalEngine,
        string PushToTalkGesture);

    internal sealed record LegacyUserPreferences(
        LegacyDictationPreferences Dictation,
        PolishPreferences Polish,
        HistoryPreferences History,
        AppTheme Theme);

    internal static UserPreferences ConvertLegacyPreferences(LegacyUserPreferences legacy) =>
        new(
            new DictationPreferences(
                legacy.Dictation.FinalEngine,
                legacy.Dictation.PushToTalkGesture,
                WordCorrectionEnabled: true,
                FillerRemovalEnabled: true,
                EmojiFormatterEnabled: true,
                SpokenPunctuationEnabled: false),
            legacy.Polish,
            legacy.History,
            legacy.Theme);

    internal static UserPreferences ApplyWhisperLanguageAutomatic(UserPreferences preferences) =>
        preferences with
        {
            Dictation = preferences.Dictation with
            {
                WhisperLanguage = LanguageCatalog.AutomaticCode,
            },
        };

    internal static UserPreferences ApplyLivePreviewAndOverlayDefaults(UserPreferences preferences) =>
        preferences with
        {
            LivePreviewEnabled = UserPreferences.Default.LivePreviewEnabled,
            OverlayPosition = UserPreferences.Default.OverlayPosition,
        };

    internal static UserPreferences ApplyPillAndSoundDefaults(UserPreferences preferences) =>
        preferences with
        {
            PillDesignWithoutWords = UserPreferences.Default.PillDesignWithoutWords,
            PillDesignWithWords = UserPreferences.Default.PillDesignWithWords,
            PlayRecordingSounds = UserPreferences.Default.PlayRecordingSounds,
            RecordingSoundPairing = UserPreferences.Default.RecordingSoundPairing,
        };

    internal static UserPreferences ApplyRecordingControlsDefaults(UserPreferences preferences) =>
        preferences with
        {
            Dictation = preferences.Dictation with
            {
                RecordingMode = DictationPreferences.Default.RecordingMode,
                CancelGesture = DictationPreferences.Default.CancelGesture,
                EscapeRecoveryEnabled = DictationPreferences.Default.EscapeRecoveryEnabled,
                QuickAddGesture = DictationPreferences.Default.QuickAddGesture,
            },
        };

    internal static UserPreferences ApplyAutoStopDefaults(UserPreferences preferences) =>
        preferences with
        {
            Dictation = preferences.Dictation with
            {
                AutoStopEnabled = DictationPreferences.Default.AutoStopEnabled,
                AutoStopSilenceSeconds = DictationPreferences.Default.AutoStopSilenceSeconds,
            },
        };

    internal static UserPreferences ApplyLearnAndFixDefaults(UserPreferences preferences) =>
        preferences with
        {
            Dictation = preferences.Dictation with
            {
                LearnFromEditsEnabled = true,
                FixLearnedWordsEnabled = true,
            },
        };

    internal static UserPreferences ApplyFixLearnedWordsDefault(UserPreferences preferences) =>
        preferences with
        {
            Dictation = preferences.Dictation with
            {
                FixLearnedWordsEnabled = true,
            },
        };

    internal static UserPreferences? SeedEgOneModelIdIfMissing(UserPreferences? preferences)
    {
        var polish = preferences?.Polish;
        if (polish is not null &&
            polish.Provider == PolishProvider.EgOne &&
            polish.EgOneModelId is null &&
            EgOneModelCatalog.TryNormalize(polish.ModelId, out var normalized))
        {
            return preferences! with
            {
                Polish = polish with { EgOneModelId = normalized },
            };
        }

        return preferences;
    }

    internal static UserPreferences? ResolveClashingLastDictationShortcuts(UserPreferences? preferences) =>
        preferences?.Dictation is not null
            ? preferences with
            {
                Dictation = preferences.Dictation.WithoutClashingLastDictationShortcuts(),
            }
            : preferences;

    internal static ReusableUserData? NormalizeLegacyCustomWords(ReusableUserData? userData)
    {
        if (userData is { CustomWords.Count: > 1 })
        {
            var normalized = CustomWordEntry.NormalizeLegacyEntries(userData.CustomWords);
            if (!ReferenceEquals(normalized, userData.CustomWords))
            {
                return userData.WithCustomWords(normalized);
            }
        }

        return userData;
    }
}
