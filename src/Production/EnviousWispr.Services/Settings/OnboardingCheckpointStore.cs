using System.Text.Json;
using EnviousWispr.Core.Onboarding;

namespace EnviousWispr.Services.Settings;

public enum OnboardingCheckpointLoadStatus
{
    Missing,
    Loaded,
    Corrupt,
}

public sealed record OnboardingCheckpointLoadResult(
    OnboardingCheckpointLoadStatus Status,
    OnboardingCheckpoint? Checkpoint = null);

public interface IOnboardingCheckpointStore
{
    Task<OnboardingCheckpointLoadResult> LoadAsync(CancellationToken cancellationToken = default);

    Task<bool> SaveAsync(OnboardingCheckpoint checkpoint, CancellationToken cancellationToken = default);

    Task<bool> ClearAsync(CancellationToken cancellationToken = default);
}

/// <summary>Atomic local JSON store for in-progress onboarding checkpoints.</summary>
public sealed class OnboardingCheckpointStore : IOnboardingCheckpointStore, IDisposable
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public OnboardingCheckpointStore(string? filePath = null)
    {
        _filePath = string.IsNullOrWhiteSpace(filePath)
            ? DefaultFilePath()
            : Path.GetFullPath(filePath);
    }

    public static string DefaultFilePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EnviousWispr",
        "onboarding-checkpoint.json");

    public async Task<OnboardingCheckpointLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_filePath))
            {
                return new OnboardingCheckpointLoadResult(OnboardingCheckpointLoadStatus.Missing);
            }

            await using var stream = new FileStream(
                _filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);
            var checkpoint = await JsonSerializer.DeserializeAsync<OnboardingCheckpoint>(
                stream,
                JsonSettingsStore.SerializerOptions,
                cancellationToken).ConfigureAwait(false);

            if (checkpoint is null ||
                checkpoint.SchemaVersion != OnboardingCheckpoint.CurrentSchemaVersion ||
                !Enum.IsDefined(checkpoint.Step))
            {
                return new OnboardingCheckpointLoadResult(OnboardingCheckpointLoadStatus.Corrupt);
            }

            return new OnboardingCheckpointLoadResult(OnboardingCheckpointLoadStatus.Loaded, checkpoint);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new OnboardingCheckpointLoadResult(OnboardingCheckpointLoadStatus.Corrupt);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> SaveAsync(
        OnboardingCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await JsonSettingsStore.WriteAtomicallyAsync(
                checkpoint,
                _filePath,
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(_filePath))
            {
                File.Delete(_filePath);
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
