using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Polish;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Services.Settings;

public sealed class JsonSettingsStore : ISettingsStore
{
    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters =
        {
            new FinalAsrEngineJsonConverter(),
            new JsonStringEnumConverter(),
        },
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
            // THE ONE PLACE A FRESH INSTALL IS KNOWN: no file has ever been written here. It gets the example
            // snippets; every other status below starts from the plain defaults (SnippetStarters).
            return new SettingsLoadResult(AppSettings.FreshInstall, SettingsLoadStatus.Missing);
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
                14 => MigrateFromV14(json),
                15 => MigrateFromV15(json),
                16 => MigrateFromV16(json),
                17 => MigrateFromV17(json),
                18 => MigrateFromV18(json),
                19 => MigrateFromV19(json),
                20 => MigrateFromV20(json),
                21 => MigrateFromV21(json),
                22 => MigrateFromV22(json),
                23 => MigrateFromV23(json),
                24 => MigrateFromV24(json),
                25 => MigrateFromV25(json),
                26 => MigrateFromV26(json),
                27 => MigrateFromV27(json),
                28 => MigrateFromV28(json),
                AppSettings.CurrentSchemaVersion => JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions),
                _ => null,
            };
            if (settings is not null && schemaVersion < AppSettings.CurrentSchemaVersion)
            {
                settings = SeedEgOneModelIdIfMissing(settings);
            }
            // A FILE WRITTEN BEFORE SEPARATE USAGE METRICS AND CRASH REPORTS EXISTED stored a single
            // shareAnonymousTelemetry boolean. When sendCrashReports was not yet present in the file,
            // carry that prior remote-sharing choice into SendCrashReports so an existing user who
            // had opted out of remote telemetry stays opted out of crash reports on upgrade (#40).
            if (settings is { Observability: not null } &&
                schemaVersion < AppSettings.CurrentSchemaVersion &&
                document.RootElement.TryGetProperty("observability", out var observabilityElement) &&
                observabilityElement.ValueKind == JsonValueKind.Object &&
                !observabilityElement.TryGetProperty("sendCrashReports", out _))
            {
                settings = settings with
                {
                    Observability = settings.Observability with
                    {
                        SendCrashReports = settings.Observability.ShareAnonymousTelemetry,
                    },
                };
            }

            // A FILE FROM BEFORE THE LAST-DICTATION SHORTCUTS gains their defaults on load; one that already gave
            // Alt+Shift+Z to another key keeps its own binding rather than being refused and reset. Ref: #206.
            if (settings is { Preferences.Dictation: not null } && schemaVersion < LastDictationShortcutsSchema)
            {
                settings = settings with
                {
                    Preferences = settings.Preferences with
                    {
                        Dictation = settings.Preferences.Dictation.WithoutClashingLastDictationShortcuts(),
                    },
                };
            }

            if (settings?.UserData is { CustomWords.Count: > 1 } userData)
            {
                var normalized = CustomWordEntry.NormalizeLegacyEntries(userData.CustomWords);
                if (!ReferenceEquals(normalized, userData.CustomWords))
                {
                    settings = settings with { UserData = userData.WithCustomWords(normalized) };
                }
            }

            if (settings?.UserData is { HasSeededStarterSnippets: false } unseededUserData)
            {
                var seededUserData = unseededUserData.Snippets.Count == 0
                    ? unseededUserData.WithSnippets(SnippetStarters.All).WithHasSeededStarterSnippets(true)
                    : unseededUserData.WithHasSeededStarterSnippets(true);
                settings = settings with { UserData = seededUserData };
            }

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

    internal static Task WriteAtomicallyAsync<T>(
        T value,
        string destinationPath,
        CancellationToken cancellationToken) =>
        WriteAtomicallyAsync(value, destinationPath, fence: null, cancellationToken);

    /// <summary>
    /// Writes the value to a temporary file and makes it the record in one move - under the fence
    /// when one is given, so a record the exit has abandoned is never replaced. False when the fence
    /// refused; the temporary file is deleted either way.
    /// </summary>
    internal static async Task<bool> WriteAtomicallyAsync<T>(
        T value,
        string destinationPath,
        PublicationFence? fence,
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
            if (fence is null)
            {
                File.Move(temporaryPath, destinationPath, overwrite: true);
                return true;
            }

            return fence.TryCommit(() => File.Move(temporaryPath, destinationPath, overwrite: true));
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
                // A SCHEMA-1 FILE IS AN EXISTING USER, and it stored no recording key because every install then
                // recorded on F8. The upgrade keeps them on F8 rather than the Ctrl+Win a fresh install starts on (#66).
                Preferences = UserPreferences.Default with
                {
                    Dictation = DictationPreferences.Default with
                    {
                        PushToTalkGesture = "F8",
                    },
                },
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
                        WhisperLanguage = LanguageCatalog.AutomaticCode,
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

    /// <summary>Takes a settings file written before the app offered to pin a language.</summary>
    /// <remarks>
    /// NOTHING IS WRITTEN, AND A MISSING HISTORY IS THE HONEST VALUE. Nobody has been offered
    /// anything, because there was nothing to offer them, so the count that deserializes from an
    /// absent field is already correct. The step exists so the file is recorded as read at the new
    /// shape rather than refused.
    /// </remarks>
    /// <summary>Takes a settings file written before English could be spelled the British way.</summary>
    /// <remarks>
    /// NOTHING IS REWRITTEN. Every take so far was delivered in American spelling, and the value an absent field
    /// deserializes to - American - keeps doing exactly that. The step records that the file was read at the new
    /// shape, so an older build refuses a newer file by its version rather than tripping on an unknown field.
    /// </remarks>
    /// <summary>The schema that introduced the Paste and Copy Last Dictation shortcuts.</summary>
    private const int LastDictationShortcutsSchema = 17;

    /// <summary>Takes a settings file written before the last-dictation shortcuts existed.</summary>
    /// <remarks>
    /// The absent fields deserialize to their defaults - Paste Last on Alt+Shift+Z, Copy Last unset - and the load
    /// then settles any clash with the person's own keys (see the caller).
    /// </remarks>
    private static AppSettings? MigrateFromV16(string json)
    {
        var legacy = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
        return legacy is null
            ? null
            : legacy with { SchemaVersion = AppSettings.CurrentSchemaVersion };
    }

    /// <summary>Takes a settings file written before the snippet keyword could be chosen.</summary>
    /// <remarks>
    /// NOTHING IS REWRITTEN. Every snippet so far fired on "backslash" - the only word there was - and an
    /// absent keyword reads as exactly that. The version moves so an older build refuses the newer file
    /// by its number rather than tripping on the unknown member and resetting every setting.
    /// </remarks>
    private static AppSettings? MigrateFromV17(string json)
    {
        var legacy = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
        return legacy is null
            ? null
            : legacy with { SchemaVersion = AppSettings.CurrentSchemaVersion };
    }

    /// <summary>Takes a settings file written before Casual mode existed.</summary>
    /// <remarks>
    /// Absent CasualModeEnabled deserializes to false, matching the default.
    /// </remarks>
    private static AppSettings? MigrateFromV18(string json)
    {
        var legacy = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
        return legacy is null
            ? null
            : legacy with { SchemaVersion = AppSettings.CurrentSchemaVersion };
    }

    /// <summary>Takes a settings file written before the two-engine Live Preview choice existed.</summary>
    /// <remarks>
    /// Absent LivePreviewEngine deserializes to Parakeet, matching the workhorse default (#127).
    /// </remarks>
    private static AppSettings? MigrateFromV19(string json)
    {
        var legacy = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
        return legacy is null
            ? null
            : legacy with { SchemaVersion = AppSettings.CurrentSchemaVersion };
    }

    /// <summary>Takes a settings file written before separate PostHog usage metrics and Sentry crash reports existed (#40).</summary>
    /// <remarks>
    /// Preserves any explicit user preference stored in the existing profile while defaulting the new
    /// SendCrashReports switch to true, matching macOS PrivacySettingsView.
    /// </remarks>
    private static AppSettings? MigrateFromV20(string json)
    {
        var legacy = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
        return legacy is null
            ? null
            : SeedEgOneModelIdIfMissing(legacy) with { SchemaVersion = AppSettings.CurrentSchemaVersion };
    }

    /// <summary>Takes a settings file written before dedicated EG-1 size persistence existed (schema v23).</summary>
    /// <remarks>
    /// Previously EG-1 shared <see cref="PolishPreferences.ModelId"/>, so switching to another AI Polish
    /// provider cleared or overwrote the selected EG-1 size. When upgrading from v22 with EG-1 active and a
    /// known EG-1 identifier in <see cref="PolishPreferences.ModelId"/>, seed <see cref="PolishPreferences.EgOneModelId"/>.
    /// </remarks>
    private static AppSettings? MigrateFromV22(string json)
    {
        var legacy = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
        return legacy is null
            ? null
            : SeedEgOneModelIdIfMissing(legacy) with { SchemaVersion = AppSettings.CurrentSchemaVersion };
    }

    /// <summary>Takes a settings file written before the full 99+ language catalog and recent languages existed (schema v24).</summary>
    /// <remarks>
    /// <see cref="WhisperLanguageJsonConverter"/> translates legacy enum names ("Automatic", "English", "French",
    /// "German", "Spanish") and numbers (0..4) into ISO language codes on load.
    /// </remarks>
    private static AppSettings? MigrateFromV23(string json)
    {
        var legacy = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
        return legacy is null
            ? null
            : legacy with { SchemaVersion = AppSettings.CurrentSchemaVersion };
    }

    /// <summary>Takes a settings file written before local EG-1 idle memory sleep existed (schema v25).</summary>
    /// <remarks>
    /// Absent <c>unloadPolicy</c> deserializes to <see cref="PolishUnloadPolicy.Never"/>, matching macOS
    /// and preserving existing behavior while bumping the schema version so older builds reject politely.
    /// </remarks>
    private static AppSettings? MigrateFromV24(string json)
    {
        var legacy = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
        return legacy is null
            ? null
            : legacy with { SchemaVersion = AppSettings.CurrentSchemaVersion };
    }

    /// <summary>Takes a settings file written before configurable start-vs-release paste targeting existed (schema v26).</summary>
    /// <remarks>
    /// Absent <c>pasteTargetMode</c> deserializes to <see cref="PasteTargetMode.WhereDictationStarted"/>,
    /// preserving start-of-dictation pull-back as the safe default for existing users.
    /// </remarks>
    private static AppSettings? MigrateFromV25(string json)
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
                        LearnFromEditsEnabled = true,
                        FixLearnedWordsEnabled = true,
                    },
                },
            };
    }

    /// <summary>Takes a settings file written before starter snippet seeding tracking existed (schema v28).</summary>
    private static AppSettings? MigrateFromV28(string json)
    {
        var legacy = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
        return legacy is null
            ? null
            : legacy with
            {
                SchemaVersion = AppSettings.CurrentSchemaVersion,
            };
    }

    /// <summary>Takes a settings file written before runtime Fix words it learned toggle existed (schema v27).</summary>
    /// <remarks>
    /// Enables <see cref="DictationPreferences.FixLearnedWordsEnabled"/> by default to match macOS,
    /// preserving every prior word, shortcut, and preference while bumping the schema version.
    /// </remarks>
    private static AppSettings? MigrateFromV27(string json)
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
                        FixLearnedWordsEnabled = true,
                    },
                },
            };
    }

    /// <summary>Takes a settings file written before Self-Learning Dictionary existed (schema v27).</summary>
    /// <remarks>
    /// Enables <see cref="DictationPreferences.LearnFromEditsEnabled"/> by default to match macOS,
    /// preserving every prior word, shortcut, and preference while bumping the schema version.
    /// </remarks>
    private static AppSettings? MigrateFromV26(string json)
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
                        LearnFromEditsEnabled = true,
                        FixLearnedWordsEnabled = true,
                    },
                },
            };
    }

    private static AppSettings SeedEgOneModelIdIfMissing(AppSettings settings) =>
        SeedEgOneModelIdIfMissing(settings.Preferences) is { } updatedPreferences &&
        !ReferenceEquals(updatedPreferences, settings.Preferences)
            ? settings with { Preferences = updatedPreferences }
            : settings;

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

    /// <summary>Takes a settings file written before multi-alias dictionary entries, categories, and isolated vocabulary packs existed.</summary>
    private static AppSettings? MigrateFromV21(string json)
    {
        var legacy = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
        return legacy is null
            ? null
            : legacy with { SchemaVersion = AppSettings.CurrentSchemaVersion };
    }

    private static AppSettings? MigrateFromV15(string json)
    {
        var legacy = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
        return legacy is null
            ? null
            : legacy with { SchemaVersion = AppSettings.CurrentSchemaVersion };
    }

    private static AppSettings? MigrateFromV14(string json)
    {
        var legacy = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
        return legacy is null
            ? null
            : legacy with { SchemaVersion = AppSettings.CurrentSchemaVersion };
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
