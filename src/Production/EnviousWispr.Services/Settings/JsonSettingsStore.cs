using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Services.Settings;

public sealed class JsonSettingsStore : ISettingsStore
{
    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _settingsPath;

    public JsonSettingsStore(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        _settingsPath = Path.GetFullPath(settingsPath);
    }

    public async Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_settingsPath))
        {
            return new SettingsLoadResult(AppSettings.Default, SettingsLoadStatus.Missing);
        }

        try
        {
            var json = await File.ReadAllTextAsync(_settingsPath, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("schemaVersion", out var versionElement) ||
                !versionElement.TryGetInt32(out var schemaVersion) ||
                schemaVersion < 1)
            {
                return Invalid();
            }

            if (schemaVersion > AppSettings.CurrentSchemaVersion)
            {
                return new SettingsLoadResult(
                    AppSettings.Default,
                    SettingsLoadStatus.NewerVersion,
                    new AppError(AppErrorCode.NewerSchema, AppErrorStage.SettingsLoad, CanRetry: false),
                    schemaVersion);
            }

            var settings = schemaVersion switch
            {
                1 => MigrateFromV1(json),
                2 => MigrateFromV2(json),
                3 => MigrateFromV3(json),
                4 => MigrateFromV4(json),
                5 => MigrateFromV5(json),
                6 => MigrateFromV6(json),
                7 => MigrateFromV7(json),
                8 => MigrateFromV8(json),
                9 => MigrateFromV9(json),
                10 => MigrateFromV10(json),
                11 => MigrateFromV11(json),
                12 => MigrateFromV12(json),
                13 => MigrateFromV13(json),
                AppSettings.CurrentSchemaVersion => JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions),
                _ => null,
            };
            var validationError = AppSettingsValidator.Validate(settings, AppErrorStage.SettingsLoad);
            if (validationError is not null)
            {
                return Invalid(schemaVersion, validationError);
            }

            var status = schemaVersion == AppSettings.CurrentSchemaVersion
                ? SettingsLoadStatus.Loaded
                : SettingsLoadStatus.Migrated;
            return new SettingsLoadResult(settings!, status, SourceSchemaVersion: schemaVersion);
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

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (AppSettingsValidator.Validate(settings, AppErrorStage.SettingsSave) is not null)
        {
            throw new ArgumentException("Settings must use the current schema and contain valid values.", nameof(settings));
        }

        await WriteAtomicallyAsync(settings, _settingsPath, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SettingsResetResult> ResetAsync(
        AppSettings replacement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (AppSettingsValidator.Validate(replacement, AppErrorStage.SettingsReset) is not null)
        {
            throw new ArgumentException("Replacement settings must use the current schema and contain valid values.", nameof(replacement));
        }

        var directory = GetParentDirectory(_settingsPath);
        Directory.CreateDirectory(directory);
        var backupPath = _settingsPath + ".previous";
        var preservedPreviousData = File.Exists(_settingsPath);

        if (preservedPreviousData)
        {
            File.Copy(_settingsPath, backupPath, overwrite: true);
        }

        try
        {
            await WriteAtomicallyAsync(replacement, _settingsPath, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (preservedPreviousData && File.Exists(backupPath))
            {
                File.Copy(backupPath, _settingsPath, overwrite: true);
            }

            throw;
        }

        return new SettingsResetResult(preservedPreviousData);
    }

    internal static async Task WriteAtomicallyAsync<T>(
        T value,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var directory = GetParentDirectory(destinationPath);
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            var json = JsonSerializer.Serialize(value, SerializerOptions);
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    internal static async Task WriteLinesAtomicallyAsync(
        IEnumerable<string> lines,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var directory = GetParentDirectory(destinationPath);
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllLinesAsync(temporaryPath, lines, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private static AppSettings? MigrateFromV1(string json)
    {
        var legacy = JsonSerializer.Deserialize<LegacySettingsV1>(json, SerializerOptions);
        return legacy is null
            ? null
            : AppSettings.Default with
            {
                LaunchCount = legacy.LaunchCount,
                HasCompletedOnboarding = legacy.HasCompletedOnboarding,
            };
    }

    private static AppSettings? MigrateFromV2(string json)
    {
        var legacy = JsonSerializer.Deserialize<LegacySettingsV2>(json, SerializerOptions);
        return legacy is null
            ? null
            : new AppSettings(
                AppSettings.CurrentSchemaVersion,
                legacy.LaunchCount,
                legacy.HasCompletedOnboarding,
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
                legacy.UserData,
                PreferredMicrophoneId: null,
                Observability: ObservabilityPreferences.Default);
    }

    private static AppSettings? MigrateFromV3(string json)
    {
        var legacy = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
        return legacy is null
            ? null
            : legacy with
            {
                SchemaVersion = AppSettings.CurrentSchemaVersion,
                Observability = ObservabilityPreferences.Default,
            };
    }

    private static AppSettings? MigrateFromV4(string json)
    {
        var legacy = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
        return legacy is null
            ? null
            : legacy with
            {
                SchemaVersion = AppSettings.CurrentSchemaVersion,
                PreferredMicrophoneId = null,
                Observability = ObservabilityPreferences.Default,
            };
    }

    private static AppSettings? MigrateFromV5(string json)
    {
        var legacy = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
        return legacy is null
            ? null
            : legacy with
            {
                SchemaVersion = AppSettings.CurrentSchemaVersion,
                Observability = ObservabilityPreferences.Default,
                Preferences = legacy.Preferences with
                {
                    Dictation = legacy.Preferences.Dictation with
                    {
                        WhisperLanguage = WhisperLanguagePreference.Automatic,
                    },
                },
            };
    }

    private static AppSettings? MigrateFromV6(string json)
    {
        var legacy = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
        return legacy is null
            ? null
            : legacy with
            {
                SchemaVersion = AppSettings.CurrentSchemaVersion,
                Observability = ObservabilityPreferences.Default,
            };
    }

    private static AppSettings? MigrateFromV7(string json)
    {
        var legacy = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
        return legacy is null
            ? null
            : legacy with
            {
                SchemaVersion = AppSettings.CurrentSchemaVersion,
                Preferences = legacy.Preferences with
                {
                    LivePreviewEnabled = UserPreferences.Default.LivePreviewEnabled,
                    OverlayPosition = UserPreferences.Default.OverlayPosition,
                },
            };
    }

    private static AppSettings? MigrateFromV8(string json)
    {
        var legacy = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
        return legacy is null
            ? null
            : legacy with
            {
                SchemaVersion = AppSettings.CurrentSchemaVersion,
                Preferences = legacy.Preferences with
                {
                    PillDesignWithoutWords = UserPreferences.Default.PillDesignWithoutWords,
                    PillDesignWithWords = UserPreferences.Default.PillDesignWithWords,
                    PlayRecordingSounds = UserPreferences.Default.PlayRecordingSounds,
                    RecordingSoundPairing = UserPreferences.Default.RecordingSoundPairing,
                },
            };
    }

    private static AppSettings? MigrateFromV9(string json)
    {
        var legacy = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
        return legacy is null
            ? null
            : legacy with
            {
                SchemaVersion = AppSettings.CurrentSchemaVersion,
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

    /// <summary>
    /// A settings file written before auto-stop existed.
    /// </summary>
    /// <remarks>
    /// The two new fields are optional with defaults, so deserialising a v10 file already fills
    /// them correctly. The migration is here anyway, and it is not ceremony: it takes the DEFAULTS
    /// explicitly, so a future change to those defaults reaches an upgrading user rather than
    /// leaving them on whatever the record happened to declare when their file was written.
    ///
    /// The schema version had to move regardless. Writing the new fields under the old version
    /// would make an older build reject the file as unreadable and reset the user's settings,
    /// because this store refuses unmapped members.
    /// </remarks>
    private static AppSettings? MigrateFromV10(string json)
    {
        var legacy = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
        return legacy is null
            ? null
            : legacy with
            {
                SchemaVersion = AppSettings.CurrentSchemaVersion,
                Preferences = legacy.Preferences with
                {
                    Dictation = legacy.Preferences.Dictation with
                    {
                        AutoStopEnabled = DictationPreferences.Default.AutoStopEnabled,
                        AutoStopSilenceSeconds = DictationPreferences.Default.AutoStopSilenceSeconds,
                    },
                },
            };
    }

    /// <summary>Adds the release-notes mark to a file written before it existed.</summary>
    /// <remarks>
    /// A NEW FIELD IS A NEW SCHEMA, EVEN WHEN IT HAS A DEFAULT. This store refuses a file it does not
    /// recognise, so a version-11 app reading a version-11 file that carries lastSeenReleaseNotes
    /// treats it as corruption and resets the settings - which is what a ROLLBACK looks like from the
    /// user's side: every choice they made, gone. Bumping the number is what makes the older app
    /// refuse the file politely instead, as a newer schema it cannot read.
    ///
    /// NULL IS THE RIGHT VALUE HERE AND IT IS SET ON PURPOSE. Somebody upgrading has not read THIS
    /// build's notes, so the mark should be up - which is exactly what null means.
    /// </remarks>
    private static AppSettings? MigrateFromV11(string json)
    {
        var legacy = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
        return legacy is null
            ? null
            : legacy with
            {
                SchemaVersion = AppSettings.CurrentSchemaVersion,
                LastSeenReleaseNotes = null,
            };
    }

    /// <summary>Takes a settings file written before custom words could be matched loosely.</summary>
    /// <remarks>
    /// NOTHING IS REWRITTEN, AND THAT IS THE CORRECT MIGRATION rather than a missing one. Every word
    /// in an older file was corrected by one rule, so the honest value for all of them is the one
    /// that keeps doing exactly that - which is what a missing strictness deserializes to. The step
    /// exists to record that the file has been read at the new shape, so a user is not asked to
    /// prove anything about words they added before the question existed.
    /// </remarks>
    private static AppSettings? MigrateFromV12(string json)
    {
        var legacy = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
        return legacy is null
            ? null
            : legacy with { SchemaVersion = AppSettings.CurrentSchemaVersion };
    }

    /// <summary>Takes a settings file written before copy-only could be asked for.</summary>
    /// <remarks>
    /// NOTHING IS REWRITTEN. Everyone who has used this app so far has had their text pasted, and a
    /// migration that turned that into copy-only would change what happens to the next thing they
    /// dictate. Missing means off, which is what they already had.
    /// </remarks>
    private static AppSettings? MigrateFromV13(string json)
    {
        var legacy = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
        return legacy is null
            ? null
            : legacy with { SchemaVersion = AppSettings.CurrentSchemaVersion };
    }

    private static SettingsLoadResult Invalid(
        int? sourceSchemaVersion = null,
        AppError? error = null) => new(
            AppSettings.Default,
            SettingsLoadStatus.Invalid,
            error ?? new AppError(AppErrorCode.InvalidData, AppErrorStage.SettingsLoad, CanRetry: false),
            sourceSchemaVersion);

    private static SettingsLoadResult Unavailable(AppErrorCode code) => new(
        AppSettings.Default,
        SettingsLoadStatus.Unavailable,
        new AppError(code, AppErrorStage.SettingsLoad, CanRetry: true));

    private static string GetParentDirectory(string path) =>
        Path.GetDirectoryName(path)
        ?? throw new InvalidOperationException("The storage path must have a parent directory.");

    private static void TryDeleteTemporaryFile(string temporaryPath)
    {
        try
        {
            File.Delete(temporaryPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (SecurityException)
        {
        }
    }

    private sealed record LegacySettingsV1(
        int SchemaVersion,
        int LaunchCount,
        bool HasCompletedOnboarding);

    private sealed record LegacyDictationPreferences(
        FinalAsrEngine FinalEngine,
        string PushToTalkGesture);

    private sealed record LegacyUserPreferences(
        LegacyDictationPreferences Dictation,
        PolishPreferences Polish,
        HistoryPreferences History,
        AppTheme Theme);

    private sealed record LegacySettingsV2(
        int SchemaVersion,
        int LaunchCount,
        bool HasCompletedOnboarding,
        LegacyUserPreferences Preferences,
        ReusableUserData UserData);
}
