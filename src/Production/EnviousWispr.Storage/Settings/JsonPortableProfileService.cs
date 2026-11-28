using System.Security;
using System.Text.Json;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Input;
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

            var profile = DeserializeWithMigration(json, schemaVersion);
            // As the settings load does: a profile from before the last-dictation shortcuts keeps its own keys
            // rather than being refused over the new Alt+Shift+Z default. Ref: #206.
            if (profile is { Preferences.Dictation: not null } && schemaVersion < 11)
            {
                profile = profile with
                {
                    Preferences = SettingsSchemaMigration.ResolveClashingLastDictationShortcuts(profile.Preferences)!,
                };
            }

            if (SettingsSchemaMigration.NormalizeLegacyCustomWords(profile?.UserData) is { } normalizedUserData &&
                profile is not null &&
                !ReferenceEquals(normalizedUserData, profile.UserData))
            {
                profile = profile with { UserData = normalizedUserData };
            }

            if (profile is not null && schemaVersion < PortableProfile.CurrentSchemaVersion)
            {
                if (profile.Preferences is not null)
                {
                    var updatedPreferences = SettingsSchemaMigration.SeedEgOneModelIdIfMissing(profile.Preferences)!;
                    if (!document.RootElement.TryGetProperty("preferences", out var preferencesElement) ||
                        preferencesElement.ValueKind != JsonValueKind.Object ||
                        !preferencesElement.TryGetProperty("smartInsertionEnabled", out _))
                    {
                        updatedPreferences = SettingsSchemaMigration.ApplySmartInsertionDefault(updatedPreferences);
                    }

                    profile = profile with
                    {
                        Preferences = updatedPreferences,
                    };
                }

                // Importing an older profile adopts its explicit snippet list (including empty); it must
                // never cause one-time starter seeding to inject examples on the next settings load.
                if (profile.UserData is { HasSeededStarterSnippets: false } importedUserData)
                {
                    profile = profile with
                    {
                        UserData = importedUserData.WithHasSeededStarterSnippets(true),
                    };
                }
            }

            // A PROFILE CANNOT BRING IN A CANCEL OR ADD-A-WORD KEY WITH NO KEY OF ITS OWN: the hook refuses both, and with
            // them the whole hook, so the import would leave dictation dead at the next launch. Refused here, before
            // anything is applied, rather than in the settings-file validator, where a refusal resets every setting. #66.
            var validationError = AppSettingsValidator.Validate(profile, AppErrorStage.ProfileImport) ??
                (profile?.Preferences?.Dictation is { } dictation &&
                 !(HotkeyGestureParser.ParseKeyed(dictation.CancelGesture).Succeeded &&
                   HotkeyGestureParser.ParseKeyed(dictation.QuickAddGesture).Succeeded)
                    ? new AppError(AppErrorCode.InvalidData, AppErrorStage.ProfileImport, CanRetry: false)
                    : null);
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

    private static PortableProfile? DeserializeWithMigration(string json, int schemaVersion)
    {
        if (schemaVersion == 1)
        {
            var v1 = JsonSerializer.Deserialize<LegacyPortableProfileV1>(json, JsonSettingsStore.SerializerOptions);
            return v1 is null
                ? null
                : new PortableProfile(
                    PortableProfile.CurrentSchemaVersion,
                    SettingsSchemaMigration.ConvertLegacyPreferences(v1.Preferences),
                    v1.UserData);
        }

        var legacy = JsonSerializer.Deserialize<PortableProfile>(json, JsonSettingsStore.SerializerOptions);
        if (legacy is null)
        {
            return null;
        }

        return schemaVersion switch
        {
            PortableProfile.CurrentSchemaVersion => legacy,
            3 => legacy with
            {
                SchemaVersion = PortableProfile.CurrentSchemaVersion,
                Preferences = SettingsSchemaMigration.ApplyWhisperLanguageAutomatic(legacy.Preferences),
            },
            4 => legacy with
            {
                SchemaVersion = PortableProfile.CurrentSchemaVersion,
                Preferences = SettingsSchemaMigration.ApplyLivePreviewAndOverlayDefaults(legacy.Preferences),
            },
            5 => legacy with
            {
                SchemaVersion = PortableProfile.CurrentSchemaVersion,
                Preferences = SettingsSchemaMigration.ApplyPillAndSoundDefaults(legacy.Preferences),
            },
            6 => legacy with
            {
                SchemaVersion = PortableProfile.CurrentSchemaVersion,
                Preferences = SettingsSchemaMigration.ApplyRecordingControlsDefaults(legacy.Preferences),
            },
            18 or 19 => legacy with
            {
                SchemaVersion = PortableProfile.CurrentSchemaVersion,
                Preferences = SettingsSchemaMigration.ApplyLearnAndFixDefaults(legacy.Preferences),
            },
            20 => legacy with
            {
                SchemaVersion = PortableProfile.CurrentSchemaVersion,
                Preferences = SettingsSchemaMigration.ApplyFixLearnedWordsDefault(legacy.Preferences),
            },
            >= 2 and <= 22 => legacy with { SchemaVersion = PortableProfile.CurrentSchemaVersion },
            _ => null,
        };
    }

    private sealed record LegacyPortableProfileV1(
        int SchemaVersion,
        SettingsSchemaMigration.LegacyUserPreferences Preferences,
        ReusableUserData UserData);
}
