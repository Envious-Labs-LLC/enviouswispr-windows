using System.IO;

namespace EnviousWispr.ModelDelivery;

/// <summary>
/// Safely unloads engines holding model files, removes admitted versions from the store, and cleans up legacy directories.
/// </summary>
public static class ModelPackageCleaner
{
    public static async Task<ModelPackageRemovalOutcome> RemoveAsync(
        ModelStore store,
        string modelId,
        string legacyDirectory,
        Func<Task> unloadEnginesAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyDirectory);
        ArgumentNullException.ThrowIfNull(unloadEnginesAsync);

        // Unload active worker processes BEFORE removing store/legacy files so ONNX/whisper.cpp handles are unlocked.
        await unloadEnginesAsync().ConfigureAwait(false);

        if (Directory.Exists(legacyDirectory))
        {
            try
            {
                ModelStore.EnsureDirectoryUnlocked(legacyDirectory, cancellationToken);
            }
            catch (IOException)
            {
                return new ModelPackageRemovalOutcome(false, ModelDeliveryFailure.StorageUnavailable);
            }
            catch (UnauthorizedAccessException)
            {
                return new ModelPackageRemovalOutcome(false, ModelDeliveryFailure.StorageUnavailable);
            }
        }

        var installedVersions = await store.ListInstalledAsync(modelId, cancellationToken).ConfigureAwait(false);
        foreach (var installed in installedVersions)
        {
            var removal = await store.RemoveVersionAsync(installed.ModelId, installed.Version, cancellationToken)
                .ConfigureAwait(false);
            if (!removal.Succeeded && removal.Failure != ModelDeliveryFailure.VersionNotInstalled)
            {
                return new ModelPackageRemovalOutcome(false, removal.Failure);
            }
        }

        var cleanup = await store.CleanupAsync(modelId, inactiveVersionsToKeep: 0, cancellationToken).ConfigureAwait(false);
        if (!cleanup.Succeeded)
        {
            return new ModelPackageRemovalOutcome(false, cleanup.Failure);
        }

        if (Directory.Exists(legacyDirectory))
        {
            try
            {
                Directory.Delete(legacyDirectory, recursive: true);
            }
            catch (IOException)
            {
                return new ModelPackageRemovalOutcome(false, ModelDeliveryFailure.StorageUnavailable);
            }
            catch (UnauthorizedAccessException)
            {
                return new ModelPackageRemovalOutcome(false, ModelDeliveryFailure.StorageUnavailable);
            }
        }

        return new ModelPackageRemovalOutcome(true);
    }
}

public sealed record ModelPackageRemovalOutcome(
    bool Succeeded,
    ModelDeliveryFailure? Failure = null);
