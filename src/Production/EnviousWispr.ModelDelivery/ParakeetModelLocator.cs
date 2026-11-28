using EnviousWispr.Core.Runtime;

namespace EnviousWispr.ModelDelivery;

public enum ParakeetModelSource
{
    None,
    Override,
    UltraStore,
    UltraLegacyData,
    UltraDevelopment,
    LegacyV3DonorStore,
    LegacyV3DonorData,
}

public sealed record ParakeetModelResolution(
    string? DirectoryPath,
    ParakeetModelSource Source)
{
    public bool IsInstalled => DirectoryPath is not null;

    public bool IsUltra => Source is
        ParakeetModelSource.Override or
        ParakeetModelSource.UltraStore or
        ParakeetModelSource.UltraLegacyData or
        ParakeetModelSource.UltraDevelopment;

    public bool IsUltraStoreOrData => Source is
        ParakeetModelSource.UltraStore or
        ParakeetModelSource.UltraLegacyData;

    public bool IsLegacyDonor => Source is
        ParakeetModelSource.LegacyV3DonorStore or
        ParakeetModelSource.LegacyV3DonorData;

    public bool ShouldTriggerSilentUpgrade => IsLegacyDonor;
}

public enum ParakeetUpgradeAction
{
    None,
    RetireLegacyV3,
    StartSilentUpgrade,
}

/// <summary>
/// Resolves the active Parakeet model directory with seamless donor fallback from legacy
/// <see cref="ParakeetModelIds.LegacyV3"/> to <see cref="ParakeetModelIds.Final"/>, and safely
/// retires data-folder/store legacy v3 packages once Ultra is installed.
/// </summary>
public static class ParakeetModelLocator
{
    public static bool IsDirectoryUnderLegacyV3(string? directoryPath, string storeRoot)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || string.IsNullOrWhiteSpace(storeRoot))
        {
            return false;
        }

        var legacyPrefix = Path.GetFullPath(Path.Combine(storeRoot, ParakeetModelIds.LegacyV3))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedActive = Path.GetFullPath(directoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(normalizedActive, legacyPrefix, StringComparison.OrdinalIgnoreCase) ||
            normalizedActive.StartsWith(legacyPrefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<ParakeetUpgradeAction> EvaluateUpgradeActionAsync(
        ModelStore store,
        string storeRoot,
        string? configuredOverrideDirectory,
        string? developmentUltraDirectory,
        string? developmentLegacyDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeRoot);

        var hasLegacy = await HasLegacyPackageInStoreOrDataAsync(
            store,
            storeRoot,
            developmentLegacyDirectory,
            cancellationToken).ConfigureAwait(false);
        if (!hasLegacy)
        {
            return ParakeetUpgradeAction.None;
        }

        if (await IsUltraInstalledInStoreOrDataAsync(store, storeRoot, cancellationToken).ConfigureAwait(false))
        {
            return ParakeetUpgradeAction.RetireLegacyV3;
        }

        var resolution = await ResolveAsync(
            store,
            storeRoot,
            configuredOverrideDirectory,
            developmentUltraDirectory,
            cancellationToken).ConfigureAwait(false);
        return resolution.ShouldTriggerSilentUpgrade && BundledModelManifests.TryRead(ParakeetModelIds.Final) is not null
            ? ParakeetUpgradeAction.StartSilentUpgrade
            : ParakeetUpgradeAction.None;
    }

    public static bool IsModelComplete(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var parakeet = new LocalParakeetModelProbe().Probe(directory);
        return parakeet.Int8Complete || parakeet.Fp32Complete;
    }

    public static async Task<ParakeetModelResolution> ResolveAsync(
        ModelStore store,
        string storeRoot,
        string? configuredOverrideDirectory,
        string? developmentUltraDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeRoot);

        if (!string.IsNullOrWhiteSpace(configuredOverrideDirectory) &&
            Directory.Exists(configuredOverrideDirectory))
        {
            return new ParakeetModelResolution(
                Path.GetFullPath(configuredOverrideDirectory),
                ParakeetModelSource.Override);
        }

        var ultraActive = await store.OpenActiveOfflineAsync(ParakeetModelIds.Final, cancellationToken)
            .ConfigureAwait(false);
        if (ultraActive.Succeeded &&
            ultraActive.Installed is { DirectoryPath: { } ultraStoreDirectory } &&
            Directory.Exists(ultraStoreDirectory) &&
            IsModelComplete(ultraStoreDirectory))
        {
            return new ParakeetModelResolution(
                Path.GetFullPath(ultraStoreDirectory),
                ParakeetModelSource.UltraStore);
        }

        var ultraLegacyData = Path.Combine(storeRoot, ParakeetModelIds.Final);
        if (Directory.Exists(ultraLegacyData) && IsModelComplete(ultraLegacyData))
        {
            return new ParakeetModelResolution(
                Path.GetFullPath(ultraLegacyData),
                ParakeetModelSource.UltraLegacyData);
        }

        if (!string.IsNullOrWhiteSpace(developmentUltraDirectory) &&
            Directory.Exists(developmentUltraDirectory) &&
            IsModelComplete(developmentUltraDirectory))
        {
            return new ParakeetModelResolution(
                Path.GetFullPath(developmentUltraDirectory),
                ParakeetModelSource.UltraDevelopment);
        }

        // Seamless v3 -> Ultra donor fallback: if Ultra is absent, serve store or data-folder v3
        // immediately so dictation keeps working while Ultra upgrades silently in the background.
        var donorActive = await store.OpenActiveOfflineAsync(ParakeetModelIds.LegacyV3, cancellationToken)
            .ConfigureAwait(false);
        if (donorActive.Succeeded &&
            donorActive.Installed is { DirectoryPath: { } donorStoreDirectory } &&
            Directory.Exists(donorStoreDirectory) &&
            IsModelComplete(donorStoreDirectory))
        {
            return new ParakeetModelResolution(
                Path.GetFullPath(donorStoreDirectory),
                ParakeetModelSource.LegacyV3DonorStore);
        }

        var donorLegacyData = Path.Combine(storeRoot, ParakeetModelIds.LegacyV3);
        if (Directory.Exists(donorLegacyData) && IsModelComplete(donorLegacyData))
        {
            return new ParakeetModelResolution(
                Path.GetFullPath(donorLegacyData),
                ParakeetModelSource.LegacyV3DonorData);
        }

        return new ParakeetModelResolution(null, ParakeetModelSource.None);
    }

    public static async Task<bool> IsUltraInstalledInStoreOrDataAsync(
        ModelStore store,
        string storeRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeRoot);

        var ultraActive = await store.OpenActiveOfflineAsync(ParakeetModelIds.Final, cancellationToken)
            .ConfigureAwait(false);
        if (ultraActive.Succeeded &&
            ultraActive.Installed is { DirectoryPath: { } ultraStoreDirectory } &&
            Directory.Exists(ultraStoreDirectory) &&
            IsModelComplete(ultraStoreDirectory))
        {
            return true;
        }

        var ultraLegacyData = Path.Combine(storeRoot, ParakeetModelIds.Final);
        return Directory.Exists(ultraLegacyData) && IsModelComplete(ultraLegacyData);
    }

    public static async Task<bool> HasAnyInstalledPackageAsync(
        ModelStore store,
        string storeRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeRoot);

        if ((await store.ListInstalledAsync(ParakeetModelIds.Final, cancellationToken).ConfigureAwait(false)).Count > 0 ||
            (await store.ListInstalledAsync(ParakeetModelIds.LegacyV3, cancellationToken).ConfigureAwait(false)).Count > 0)
        {
            return true;
        }

        return Directory.Exists(Path.Combine(storeRoot, ParakeetModelIds.Final)) ||
            Directory.Exists(Path.Combine(storeRoot, ParakeetModelIds.LegacyV3));
    }

    public static async Task<bool> HasLegacyPackageInStoreOrDataAsync(
        ModelStore store,
        string storeRoot,
        string? developmentLegacyDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeRoot);

        if ((await store.ListInstalledAsync(ParakeetModelIds.LegacyV3, cancellationToken).ConfigureAwait(false)).Count > 0)
        {
            return true;
        }

        return TryGetSafeLegacyDataDirectory(storeRoot, developmentLegacyDirectory, out var legacyDataDir) &&
            Directory.Exists(legacyDataDir);
    }

    /// <summary>
    /// Retires store-managed and data-folder <see cref="ParakeetModelIds.LegacyV3"/> files once
    /// <see cref="ParakeetModelIds.Final"/> is verified installed in the store or data folder.
    /// Never deletes a checkout development directory.
    /// </summary>
    public static async Task<ModelPackageRemovalOutcome> RetireLegacyV3AfterUltraInstalledAsync(
        ModelStore store,
        string storeRoot,
        Func<Task> unloadEnginesAsync,
        string? developmentLegacyDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeRoot);
        ArgumentNullException.ThrowIfNull(unloadEnginesAsync);

        if (!await IsUltraInstalledInStoreOrDataAsync(store, storeRoot, cancellationToken).ConfigureAwait(false))
        {
            return new ModelPackageRemovalOutcome(false, ModelDeliveryFailure.VersionNotInstalled);
        }

        return await RemoveLegacyV3FromStoreOrDataAsync(
            store,
            storeRoot,
            unloadEnginesAsync,
            developmentLegacyDirectory,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes store-managed and data-folder <see cref="ParakeetModelIds.LegacyV3"/> files
    /// (used both after Ultra upgrade and when the user explicitly removes Parakeet).
    /// </summary>
    public static async Task<ModelPackageRemovalOutcome> RemoveLegacyV3FromStoreOrDataAsync(
        ModelStore store,
        string storeRoot,
        Func<Task> unloadEnginesAsync,
        string? developmentLegacyDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeRoot);
        ArgumentNullException.ThrowIfNull(unloadEnginesAsync);

        if (!TryGetSafeLegacyDataDirectory(storeRoot, developmentLegacyDirectory, out var legacyDataDir))
        {
            return new ModelPackageRemovalOutcome(true);
        }

        var hasLegacyStore = (await store.ListInstalledAsync(ParakeetModelIds.LegacyV3, cancellationToken)
            .ConfigureAwait(false)).Count > 0;
        var hasLegacyDir = Directory.Exists(legacyDataDir);
        if (!hasLegacyStore && !hasLegacyDir)
        {
            return new ModelPackageRemovalOutcome(true);
        }

        if (hasLegacyDir && IsReparsePoint(legacyDataDir))
        {
            return new ModelPackageRemovalOutcome(false, ModelDeliveryFailure.StorageUnavailable);
        }

        return await ModelPackageCleaner.RemoveAsync(
            store,
            ParakeetModelIds.LegacyV3,
            legacyDataDir,
            unloadEnginesAsync,
            cancellationToken).ConfigureAwait(false);
    }

    public static bool TryGetSafeLegacyDataDirectory(
        string storeRoot,
        string? developmentLegacyDirectory,
        out string legacyDataDirectory)
    {
        var normalizedStoreRoot = Path.GetFullPath(storeRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(normalizedStoreRoot, ParakeetModelIds.LegacyV3));
        var expectedPrefix = normalizedStoreRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            legacyDataDirectory = string.Empty;
            return false;
        }

        if (!string.IsNullOrWhiteSpace(developmentLegacyDirectory))
        {
            var normalizedDev = Path.GetFullPath(developmentLegacyDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(candidate, normalizedDev, StringComparison.OrdinalIgnoreCase))
            {
                legacyDataDirectory = string.Empty;
                return false;
            }
        }

        legacyDataDirectory = candidate;
        return true;
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
