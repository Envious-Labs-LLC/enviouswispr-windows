using System.Security;
using System.Text.Json;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Services.Settings;

public sealed class JsonPortableProfileService : IPortableProfileService
{
    public async Task<PortableProfileExportResult> ExportAsync(
        PortableProfile profile,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var validationError = AppSettingsValidator.Validate(profile, AppErrorStage.ProfileExport);
        if (validationError is not null)
        {
            return new PortableProfileExportResult(Succeeded: false, validationError);
        }

        try
        {
            await JsonSettingsStore.WriteAtomicallyAsync(
                profile,
                Path.GetFullPath(destinationPath),
                cancellationToken).ConfigureAwait(false);
            return new PortableProfileExportResult(Succeeded: true);
        }
        catch (IOException)
        {
            return Failure(AppErrorCode.StorageUnavailable, AppErrorStage.ProfileExport);
        }
        catch (UnauthorizedAccessException)
        {
            return Failure(AppErrorCode.AccessDenied, AppErrorStage.ProfileExport);
        }
        catch (SecurityException)
        {
            return Failure(AppErrorCode.AccessDenied, AppErrorStage.ProfileExport);
        }
    }

    public async Task<PortableProfileImportResult> ImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        try
        {
            var json = await File.ReadAllTextAsync(Path.GetFullPath(sourcePath), cancellationToken)
                .ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("schemaVersion", out var versionElement) ||
                !versionElement.TryGetInt32(out var schemaVersion) ||
                schemaVersion < 1)
            {
                return Invalid();
            }

            if (schemaVersion > PortableProfile.CurrentSchemaVersion)
            {
                return new PortableProfileImportResult(
                    PortableProfileImportStatus.NewerVersion,
                    Error: new AppError(
                        AppErrorCode.NewerSchema,
                        AppErrorStage.ProfileImport,
                        CanRetry: false));
            }

            var profile = schemaVersion switch
            {
                1 => MigrateFromV1(json),
                2 => MigrateFromV2(json),
                3 => MigrateFromV3(json),
                4 => MigrateFromV4(json),
                5 => MigrateFromV5(json),
                6 => MigrateFromV6(json),
                7 => MigrateFromV7(json),
                PortableProfile.CurrentSchemaVersion => JsonSerializer.Deserialize<PortableProfile>(
                    json,
                    JsonSettingsStore.SerializerOptions),
                _ => null,
            };
            var validationError = AppSettingsValidator.Validate(profile, AppErrorStage.ProfileImport);
            return validationError is null
                ? new PortableProfileImportResult(PortableProfileImportStatus.Imported, profile)
                : Invalid(validationError);
        }
        catch (JsonException)
        {
            return Invalid();
        }
        catch (IOException)
        {
            return Unavailable(AppErrorCode.StorageUnavailable);
        }
        catch (UnauthorizedAccessException)
        {
            return Unavailable(AppErrorCode.AccessDenied);
        }
        catch (SecurityException)
        {
            return Unavailable(AppErrorCode.AccessDenied);
        }
    }

    private static PortableProfileExportResult Failure(AppErrorCode code, AppErrorStage stage) => new(
        Succeeded: false,
        new AppError(code, stage, CanRetry: true));

    private static PortableProfileImportResult Invalid(AppError? error = null) => new(
        PortableProfileImportStatus.Invalid,
        Error: error ?? new AppError(
            AppErrorCode.InvalidData,
            AppErrorStage.ProfileImport,
            CanRetry: false));

    private static PortableProfileImportResult Unavailable(AppErrorCode code) => new(
        PortableProfileImportStatus.Unavailable,
        Error: new AppError(code, AppErrorStage.ProfileImport, CanRetry: true));

    private static PortableProfile? MigrateFromV1(string json)
    {
        var legacy = JsonSerializer.Deserialize<LegacyPortableProfileV1>(
            json,
            JsonSettingsStore.SerializerOptions);
        return legacy is null
            ? null
            : new PortableProfile(
                PortableProfile.CurrentSchemaVersion,
                new UserPreferences(
                    new DictationPreferences(
                        legacy.Preferences.Dictation.FinalEngine,
                        legacy.Preferences.Dictation.PushToTalkGesture,
                        WordCorrectionEnabled: true,
                        FillerRemovalEnabled: true,
                        EmojiFormatterEnabled: true,
                        SpokenPunctuationEnabled: false),
                    legacy.Preferences.Polish,
                    legacy.Preferences.History,
                    legacy.Preferences.Theme),
                legacy.UserData);
    }

    private static PortableProfile? MigrateFromV2(string json)
    {
        var legacy = JsonSerializer.Deserialize<PortableProfile>(
            json,
            JsonSettingsStore.SerializerOptions);
        return legacy is null
            ? null
            : legacy with { SchemaVersion = PortableProfile.CurrentSchemaVersion };
    }

    private static PortableProfile? MigrateFromV3(string json)
    {
        var legacy = JsonSerializer.Deserialize<PortableProfile>(
            json,
            JsonSettingsStore.SerializerOptions);
        return legacy is null
            ? null
            : legacy with
            {
                SchemaVersion = PortableProfile.CurrentSchemaVersion,
                Preferences = legacy.Preferences with
                {
                    Dictation = legacy.Preferences.Dictation with
                    {
                        WhisperLanguage = WhisperLanguagePreference.Automatic,
                    },
                },
            };
    }

    private static PortableProfile? MigrateFromV4(string json)
    {
        var legacy = JsonSerializer.Deserialize<PortableProfile>(
            json,
            JsonSettingsStore.SerializerOptions);
        return legacy is null
            ? null
            : legacy with
            {
                SchemaVersion = PortableProfile.CurrentSchemaVersion,
                Preferences = legacy.Preferences with
                {
                    LivePreviewEnabled = UserPreferences.Default.LivePreviewEnabled,
                    OverlayPosition = UserPreferences.Default.OverlayPosition,
                },
            };
    }

    private static PortableProfile? MigrateFromV5(string json)
    {
        var legacy = JsonSerializer.Deserialize<PortableProfile>(
            json,
            JsonSettingsStore.SerializerOptions);
        return legacy is null
            ? null
            : legacy with
            {
                SchemaVersion = PortableProfile.CurrentSchemaVersion,
                Preferences = legacy.Preferences with
                {
                    PillDesignWithoutWords = UserPreferences.Default.PillDesignWithoutWords,
                    PillDesignWithWords = UserPreferences.Default.PillDesignWithWords,
                    PlayRecordingSounds = UserPreferences.Default.PlayRecordingSounds,
                    RecordingSoundPairing = UserPreferences.Default.RecordingSoundPairing,
                },
            };
    }

    /// <summary>Takes a profile written before custom words could be matched loosely.</summary>
    /// <remarks>See <c>JsonSettingsStore.MigrateFromV12</c>; a missing strictness is the old rule.</remarks>
    private static PortableProfile? MigrateFromV7(string json)
    {
        var legacy = JsonSerializer.Deserialize<PortableProfile>(
            json,
            JsonSettingsStore.SerializerOptions);
        return legacy is null
            ? null
            : legacy with { SchemaVersion = PortableProfile.CurrentSchemaVersion };
    }

    private static PortableProfile? MigrateFromV6(string json)
    {
        var legacy = JsonSerializer.Deserialize<PortableProfile>(
            json,
            JsonSettingsStore.SerializerOptions);
        return legacy is null
            ? null
            : legacy with
            {
                SchemaVersion = PortableProfile.CurrentSchemaVersion,
                Preferences = legacy.Preferences with
                {
                    Dictation = legacy.Preferences.Dictation with
                    {
                        RecordingMode = DictationPreferences.Default.RecordingMode,
                        CancelGesture = DictationPreferences.Default.CancelGesture,
                        EscapeRecoveryEnabled = DictationPreferences.Default.EscapeRecoveryEnabled,
                        QuickAddGesture = DictationPreferences.Default.QuickAddGesture,
                    },
                },
            };
    }

    private sealed record LegacyDictationPreferences(
        FinalAsrEngine FinalEngine,
        string PushToTalkGesture);

    private sealed record LegacyUserPreferences(
        LegacyDictationPreferences Dictation,
        PolishPreferences Polish,
        HistoryPreferences History,
        AppTheme Theme);

    private sealed record LegacyPortableProfileV1(
        int SchemaVersion,
        LegacyUserPreferences Preferences,
        ReusableUserData UserData);
}
