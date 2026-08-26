using System.Security;
using System.Text.Json;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Services.Settings;

public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
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
            var settings = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
            if (settings is null || !IsValid(settings))
            {
                return new SettingsLoadResult(AppSettings.Default, SettingsLoadStatus.Invalid);
            }

            return new SettingsLoadResult(settings, SettingsLoadStatus.Loaded);
        }
        catch (JsonException)
        {
            return new SettingsLoadResult(AppSettings.Default, SettingsLoadStatus.Invalid);
        }
        catch (IOException)
        {
            return new SettingsLoadResult(AppSettings.Default, SettingsLoadStatus.Unavailable);
        }
        catch (UnauthorizedAccessException)
        {
            return new SettingsLoadResult(AppSettings.Default, SettingsLoadStatus.Unavailable);
        }
        catch (SecurityException)
        {
            return new SettingsLoadResult(AppSettings.Default, SettingsLoadStatus.Unavailable);
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!IsValid(settings))
        {
            throw new ArgumentException("Settings must use the current schema and contain valid values.", nameof(settings));
        }

        var directory = Path.GetDirectoryName(_settingsPath)
            ?? throw new InvalidOperationException("The settings path must have a parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_settingsPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var json = JsonSerializer.Serialize(settings, SerializerOptions);
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static bool IsValid(AppSettings settings) =>
        settings is
        {
            SchemaVersion: AppSettings.CurrentSchemaVersion,
            LaunchCount: >= 0 and < int.MaxValue,
        };
}
