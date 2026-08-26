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
                legacy.UserData);
    }

    private static AppSettings? MigrateFromV3(string json)
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
