using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.IO;
using EnviousWispr.Core.Polish;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Services.Settings;

public sealed class JsonSettingsStore : ISettingsStore
{
    private const int LastDictationShortcutsSchema = 17;
    private const int SmartInsertionDefaultSchema = 31;

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

    internal static readonly JsonSerializerOptions CompactSerializerOptions = new(SerializerOptions)
    {
        WriteIndented = false,
    };

    private readonly string _settingsPath;
    private readonly string? _userDataPath;
    private readonly string? _splitMarkerPath;
    private readonly object _stateLock = new();
    private AppSettings? _lastSavedHeader;
    private ReusableUserData? _lastSavedUserData;
    private bool _needsSplitMigration;

    public JsonSettingsStore(string settingsPath)
        : this(settingsPath, userDataPath: null)
    {
    }

    public JsonSettingsStore(string settingsPath, string? userDataPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        _settingsPath = Path.GetFullPath(settingsPath);
        _userDataPath = string.IsNullOrWhiteSpace(userDataPath) ? null : Path.GetFullPath(userDataPath);
        _splitMarkerPath = _userDataPath is null ? null : _userDataPath + ".split";
    }

    public Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => LoadCoreAsync(cancellationToken), cancellationToken);

    private async Task<SettingsLoadResult> LoadCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                if (_userDataPath is not null && File.Exists(_userDataPath))
                {
                    var recoveredUserData = await ReadUserDataFileAsync(_userDataPath, cancellationToken).ConfigureAwait(false);
                    if (recoveredUserData is null)
                    {
                        return Invalid();
                    }

                    var recovered = ApplyUserDataPostLoadNormalization(AppSettings.FreshInstall with { UserData = recoveredUserData });
                    var recoveredValidationError = AppSettingsValidator.Validate(recovered, AppErrorStage.SettingsLoad);
                    if (recoveredValidationError is not null)
                    {
                        return Invalid(error: recoveredValidationError);
                    }

                    UpdateSavedSnapshots(CreateSlimSettings(recovered), recovered.UserData, needsSplitMigration: true);
                    return new SettingsLoadResult(recovered, SettingsLoadStatus.Missing);
                }

                UpdateSavedSnapshots(null, null, needsSplitMigration: false);
                // THE ONE PLACE A FRESH INSTALL IS KNOWN: no file has ever been written here. It gets the example
                // snippets; every other status below starts from the plain defaults (SnippetStarters).
                return new SettingsLoadResult(AppSettings.FreshInstall, SettingsLoadStatus.Missing);
            }

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

            var settings = DeserializeWithMigration(json, schemaVersion);
            if (settings is null)
            {
                return Invalid(schemaVersion);
            }

            if (schemaVersion < AppSettings.CurrentSchemaVersion)
            {
                settings = SeedEgOneModelIdIfMissing(settings);
            }

            var userDataFileExisted = _userDataPath is not null && File.Exists(_userDataPath);
            if (_userDataPath is not null && !userDataFileExisted && _splitMarkerPath is not null && File.Exists(_splitMarkerPath))
            {
                // The profile was already split into slim settings.json + user-data.json, and user-data.json
                // is now missing. Do NOT treat slim settings.json as a legacy profile (which would overwrite
                // user data with empty lists on next save); report storage unavailable instead.
                UpdateSavedSnapshots(null, null, needsSplitMigration: false);
                return Unavailable(AppErrorCode.StorageUnavailable);
            }

            if (userDataFileExisted)
            {
                var splitUserData = await ReadUserDataFileAsync(_userDataPath!, cancellationToken).ConfigureAwait(false);
                if (splitUserData is null)
                {
                    return Invalid(schemaVersion);
                }

                settings = settings with { UserData = splitUserData };
            }

            // A FILE WRITTEN BEFORE SEPARATE USAGE METRICS AND CRASH REPORTS EXISTED stored a single
            // shareAnonymousTelemetry boolean. When sendCrashReports was not yet present in the file,
            // carry that prior remote-sharing choice into SendCrashReports so an existing user who
            // had opted out of remote telemetry stays opted out of crash reports on upgrade (#40).
            if (settings.Observability is not null &&
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
            if (settings.Preferences.Dictation is not null && schemaVersion < LastDictationShortcutsSchema)
            {
                settings = settings with
                {
                    Preferences = SettingsSchemaMigration.ResolveClashingLastDictationShortcuts(settings.Preferences)!,
                };
            }

            if (settings.Preferences is not null &&
                schemaVersion < SmartInsertionDefaultSchema &&
                (!document.RootElement.TryGetProperty("preferences", out var preferencesElement) ||
                 preferencesElement.ValueKind != JsonValueKind.Object ||
                 !preferencesElement.TryGetProperty("smartInsertionEnabled", out _)))
            {
                settings = settings with
                {
                    Preferences = SettingsSchemaMigration.ApplySmartInsertionDefault(settings.Preferences),
                };
            }

            settings = ApplyUserDataPostLoadNormalization(settings);

            var validationError = AppSettingsValidator.Validate(settings, AppErrorStage.SettingsLoad);
            if (validationError is not null)
            {
                return Invalid(schemaVersion, validationError);
            }

            if (_userDataPath is not null)
            {
                if (userDataFileExisted)
                {
                    UpdateSavedSnapshots(CreateSlimSettings(settings), settings.UserData, needsSplitMigration: false);
                }
                else
                {
                    UpdateSavedSnapshots(null, null, needsSplitMigration: true);
                }
            }

            var status = schemaVersion == AppSettings.CurrentSchemaVersion
                ? SettingsLoadStatus.Loaded
                : SettingsLoadStatus.Migrated;
            return new SettingsLoadResult(settings, status, SourceSchemaVersion: schemaVersion);
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

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Task.Run(() => SaveCoreAsync(settings, cancellationToken), cancellationToken);
    }

    private async Task SaveCoreAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (AppSettingsValidator.Validate(settings, AppErrorStage.SettingsSave) is not null)
        {
            throw new ArgumentException("Settings must use the current schema and contain valid values.", nameof(settings));
        }

        if (_userDataPath is null)
        {
            await WriteAtomicallyAsync(settings, _settingsPath, cancellationToken).ConfigureAwait(false);
            return;
        }

        var slimSettings = CreateSlimSettings(settings);
        AppSettings? lastHeader;
        ReusableUserData? lastUserData;
        bool needsSplitMigration;
        lock (_stateLock)
        {
            lastHeader = _lastSavedHeader;
            lastUserData = _lastSavedUserData;
            needsSplitMigration = _needsSplitMigration;
        }

        var writeUserData = needsSplitMigration ||
            !File.Exists(_userDataPath) ||
            lastUserData is null ||
            (!ReferenceEquals(settings.UserData, lastUserData) && !Equals(settings.UserData, lastUserData));
        var writeSettings = needsSplitMigration ||
            !File.Exists(_settingsPath) ||
            lastHeader is null ||
            !Equals(slimSettings, lastHeader);

        // Write dedicated user-data store FIRST so an interrupted legacy migration leaves complete
        // words and snippets on disk rather than an emptied monolithic settings.json. If both files
        // are updated in the same commit and writing settings.json fails or is cancelled, roll back
        // user-data.json so the next launch never sees a partially applied profile.
        byte[]? previousUserDataBytes = null;
        var trackRollback = writeUserData && writeSettings;
        var userDataExistedBefore = File.Exists(_userDataPath);
        if (trackRollback && userDataExistedBefore)
        {
            previousUserDataBytes = await File.ReadAllBytesAsync(_userDataPath, cancellationToken).ConfigureAwait(false);
        }

        if (writeUserData)
        {
            await WriteAtomicallyAsync(
                settings.UserData,
                _userDataPath,
                fence: null,
                CompactSerializerOptions,
                cancellationToken).ConfigureAwait(false);
        }

        if (writeSettings)
        {
            try
            {
                await WriteAtomicallyAsync(slimSettings, _settingsPath, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (trackRollback)
                {
                    await RestoreOrDeleteUserDataAsync(_userDataPath, previousUserDataBytes).ConfigureAwait(false);
                }

                throw;
            }
        }

        EnsureSplitMarker();
        UpdateSavedSnapshots(slimSettings, settings.UserData, needsSplitMigration: false);
    }

    public Task<SettingsResetResult> ResetAsync(
        AppSettings replacement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        return Task.Run(() => ResetCoreAsync(replacement, cancellationToken), cancellationToken);
    }

    private async Task<SettingsResetResult> ResetCoreAsync(
        AppSettings replacement,
        CancellationToken cancellationToken)
    {
        if (AppSettingsValidator.Validate(replacement, AppErrorStage.SettingsReset) is not null)
        {
            throw new ArgumentException("Replacement settings must use the current schema and contain valid values.", nameof(replacement));
        }

        var directory = GetParentDirectory(_settingsPath);
        Directory.CreateDirectory(directory);
        var backupPath = _settingsPath + ".previous";
        var preservedSettingsData = File.Exists(_settingsPath);
        var userDataBackupPath = _userDataPath is null ? null : _userDataPath + ".previous";
        var preservedUserData = _userDataPath is not null && File.Exists(_userDataPath);
        var preservedPreviousData = preservedSettingsData || preservedUserData;

        if (preservedSettingsData)
        {
            File.Copy(_settingsPath, backupPath, overwrite: true);
        }

        if (preservedUserData && userDataBackupPath is not null)
        {
            File.Copy(_userDataPath!, userDataBackupPath, overwrite: true);
        }

        try
        {
            if (_userDataPath is null)
            {
                await WriteAtomicallyAsync(replacement, _settingsPath, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var slimReplacement = CreateSlimSettings(replacement);
                await WriteAtomicallyAsync(
                    replacement.UserData,
                    _userDataPath,
                    fence: null,
                    CompactSerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await WriteAtomicallyAsync(slimReplacement, _settingsPath, cancellationToken).ConfigureAwait(false);
                EnsureSplitMarker();
                UpdateSavedSnapshots(slimReplacement, replacement.UserData, needsSplitMigration: false);
            }
        }
        catch
        {
            if (preservedSettingsData && File.Exists(backupPath))
            {
                File.Copy(backupPath, _settingsPath, overwrite: true);
            }

            if (_userDataPath is not null)
            {
                if (preservedUserData && userDataBackupPath is not null && File.Exists(userDataBackupPath))
                {
                    File.Copy(userDataBackupPath, _userDataPath, overwrite: true);
                }
                else if (!preservedUserData)
                {
                    TryDeleteTemporaryFile(_userDataPath);
                    if (_splitMarkerPath is not null)
                    {
                        TryDeleteTemporaryFile(_splitMarkerPath);
                    }
                }
            }

            throw;
        }

        return new SettingsResetResult(preservedPreviousData);
    }

    private static async Task<ReusableUserData?> ReadUserDataFileAsync(string userDataPath, CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(userDataPath, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<ReusableUserData>(json, SerializerOptions);
    }

    private static AppSettings ApplyUserDataPostLoadNormalization(AppSettings settings)
    {
        if (SettingsSchemaMigration.NormalizeLegacyCustomWords(settings.UserData) is { } normalized &&
            !ReferenceEquals(normalized, settings.UserData))
        {
            settings = settings with { UserData = normalized };
        }

        if (settings.UserData is { HasSeededStarterSnippets: false } unseededUserData)
        {
            var seededUserData = unseededUserData.Snippets.Count == 0
                ? unseededUserData.WithSnippets(SnippetStarters.All).WithHasSeededStarterSnippets(true)
                : unseededUserData.WithHasSeededStarterSnippets(true);
            settings = settings with { UserData = seededUserData };
        }

        return settings;
    }

    private static AppSettings CreateSlimSettings(AppSettings settings) =>
        settings with
        {
            UserData = ReusableUserData.Empty.WithHasSeededStarterSnippets(settings.UserData.HasSeededStarterSnippets),
        };

    private void UpdateSavedSnapshots(AppSettings? slimHeader, ReusableUserData? userData, bool needsSplitMigration)
    {
        lock (_stateLock)
        {
            _lastSavedHeader = slimHeader;
            _lastSavedUserData = userData;
            _needsSplitMigration = needsSplitMigration;
        }
    }

    private void EnsureSplitMarker()
    {
        if (_splitMarkerPath is null || File.Exists(_splitMarkerPath))
        {
            return;
        }

        var markerDirectory = GetParentDirectory(_splitMarkerPath);
        Directory.CreateDirectory(markerDirectory);
        File.WriteAllText(_splitMarkerPath, "v1");
    }

    private static async Task RestoreOrDeleteUserDataAsync(string userDataPath, byte[]? previousBytes)
    {
        try
        {
            if (previousBytes is null)
            {
                if (File.Exists(userDataPath))
                {
                    File.Delete(userDataPath);
                }

                return;
            }

            await AtomicFileWriter.WriteBytesAsync(
                userDataPath,
                previousBytes,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
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

    internal static Task WriteAtomicallyAsync<T>(
        T value,
        string destinationPath,
        CancellationToken cancellationToken) =>
        WriteAtomicallyAsync(value, destinationPath, fence: null, serializerOptions: null, cancellationToken);

    /// <summary>
    /// Writes the value to a temporary file and makes it the record in one move - under the fence
    /// when one is given, so a record the exit has abandoned is never replaced. False when the fence
    /// refused; the temporary file is deleted either way.
    /// </summary>
    internal static Task<bool> WriteAtomicallyAsync<T>(
        T value,
        string destinationPath,
        PublicationFence? fence,
        CancellationToken cancellationToken) =>
        WriteAtomicallyAsync(value, destinationPath, fence, serializerOptions: null, cancellationToken);

    internal static Task<bool> WriteAtomicallyAsync<T>(
        T value,
        string destinationPath,
        PublicationFence? fence,
        JsonSerializerOptions? serializerOptions,
        CancellationToken cancellationToken) =>
        AtomicFileWriter.WriteJsonAsync(
            destinationPath,
            value,
            serializerOptions ?? SerializerOptions,
            commitMove: fence is null ? null : fence.TryCommit,
            cancellationToken: cancellationToken);

    internal static Task WriteLinesAtomicallyAsync(
        IEnumerable<string> lines,
        string destinationPath,
        CancellationToken cancellationToken) =>
        AtomicFileWriter.WriteLinesAsync(destinationPath, lines, cancellationToken: cancellationToken);

    private static AppSettings? DeserializeWithMigration(string json, int schemaVersion)
    {
        if (schemaVersion == 1)
        {
            var v1 = JsonSerializer.Deserialize<LegacySettingsV1>(json, SerializerOptions);
            return v1 is null
                ? null
                : AppSettings.Default with
                {
                    LaunchCount = v1.LaunchCount,
                    HasCompletedOnboarding = v1.HasCompletedOnboarding,
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

        if (schemaVersion == 2)
        {
            var v2 = JsonSerializer.Deserialize<LegacySettingsV2>(json, SerializerOptions);
            return v2 is null
                ? null
                : new AppSettings(
                    AppSettings.CurrentSchemaVersion,
                    v2.LaunchCount,
                    v2.HasCompletedOnboarding,
                    SettingsSchemaMigration.ConvertLegacyPreferences(v2.Preferences),
                    v2.UserData,
                    PreferredMicrophoneId: null,
                    Observability: ObservabilityPreferences.Default);
        }

        var legacy = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
        if (legacy is null)
        {
            return null;
        }

        return schemaVersion switch
        {
            AppSettings.CurrentSchemaVersion => legacy,
            3 => legacy with
            {
                SchemaVersion = AppSettings.CurrentSchemaVersion,
                Observability = ObservabilityPreferences.Default,
            },
            4 => legacy with
            {
                SchemaVersion = AppSettings.CurrentSchemaVersion,
                PreferredMicrophoneId = null,
                Observability = ObservabilityPreferences.Default,
            },
            5 => legacy with
            {
                SchemaVersion = AppSettings.CurrentSchemaVersion,
                Observability = ObservabilityPreferences.Default,
                Preferences = SettingsSchemaMigration.ApplyWhisperLanguageAutomatic(legacy.Preferences),
            },
            6 => legacy with
            {
                SchemaVersion = AppSettings.CurrentSchemaVersion,
                Observability = ObservabilityPreferences.Default,
            },
            7 => legacy with
            {
                SchemaVersion = AppSettings.CurrentSchemaVersion,
                Preferences = SettingsSchemaMigration.ApplyLivePreviewAndOverlayDefaults(legacy.Preferences),
            },
            8 => legacy with
            {
                SchemaVersion = AppSettings.CurrentSchemaVersion,
                Preferences = SettingsSchemaMigration.ApplyPillAndSoundDefaults(legacy.Preferences),
            },
            9 => legacy with
            {
                SchemaVersion = AppSettings.CurrentSchemaVersion,
                Preferences = SettingsSchemaMigration.ApplyRecordingControlsDefaults(legacy.Preferences),
            },
            10 => legacy with
            {
                SchemaVersion = AppSettings.CurrentSchemaVersion,
                Preferences = SettingsSchemaMigration.ApplyAutoStopDefaults(legacy.Preferences),
            },
            11 => legacy with
            {
                SchemaVersion = AppSettings.CurrentSchemaVersion,
                LastSeenReleaseNotes = null,
            },
            20 or 22 => SeedEgOneModelIdIfMissing(legacy) with { SchemaVersion = AppSettings.CurrentSchemaVersion },
            25 or 26 => legacy with
            {
                SchemaVersion = AppSettings.CurrentSchemaVersion,
                Preferences = SettingsSchemaMigration.ApplyLearnAndFixDefaults(legacy.Preferences),
            },
            27 => legacy with
            {
                SchemaVersion = AppSettings.CurrentSchemaVersion,
                Preferences = SettingsSchemaMigration.ApplyFixLearnedWordsDefault(legacy.Preferences),
            },
            >= 12 and < AppSettings.CurrentSchemaVersion => legacy with { SchemaVersion = AppSettings.CurrentSchemaVersion },
            _ => null,
        };
    }

    private static AppSettings SeedEgOneModelIdIfMissing(AppSettings settings) =>
        SeedEgOneModelIdIfMissing(settings.Preferences) is { } updatedPreferences &&
        !ReferenceEquals(updatedPreferences, settings.Preferences)
            ? settings with { Preferences = updatedPreferences }
            : settings;

    internal static UserPreferences? SeedEgOneModelIdIfMissing(UserPreferences? preferences) =>
        SettingsSchemaMigration.SeedEgOneModelIdIfMissing(preferences);

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

    private sealed record LegacySettingsV2(
        int SchemaVersion,
        int LaunchCount,
        bool HasCompletedOnboarding,
        SettingsSchemaMigration.LegacyUserPreferences Preferences,
        ReusableUserData UserData);
}
