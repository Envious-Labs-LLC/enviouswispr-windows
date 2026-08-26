using EnviousWispr.Core.Distribution;
using Velopack;

namespace EnviousWispr.App;

internal sealed class VelopackUpdateService
{
    private const string RequiredPublisherSubject = "Envious Labs";

    private readonly ReleaseIdentity _identity;
    private readonly IUpdateArtifactValidator _validator;
    private readonly UpdateManager? _manager;
    private UpdateInfo? _pendingUpdate;

    public VelopackUpdateService(
        ReleaseIdentity identity,
        Uri? endpoint,
        IUpdateArtifactValidator validator)
    {
        _identity = identity;
        _validator = validator;
        if (endpoint is not null)
        {
            _manager = new UpdateManager(
                endpoint.AbsoluteUri,
                new UpdateOptions
                {
                    ExplicitChannel = identity.ChannelName,
                    AllowVersionDowngrade = false,
                    // A reconstructed delta package is semantically identical but does not retain
                    // the release feed's byte-for-byte SHA-256. Full packages let the independent
                    // admission check verify the exact advertised artifact before apply.
                    MaximumDeltasBeforeFallback = -1,
                });
        }
    }

    public bool IsConfigured => _manager is not null;

    public string CurrentVersion => _manager?.CurrentVersion?.ToString() ?? "development";

    public async Task<UpdateOperationResult> CheckDownloadAndVerifyAsync(
        CancellationToken cancellationToken = default)
    {
        var manager = _manager;
        if (manager is null)
        {
            return new UpdateOperationResult(UpdateOperationStatus.NotConfigured);
        }

        if (!manager.IsInstalled)
        {
            return new UpdateOperationResult(UpdateOperationStatus.DevelopmentBuild);
        }

        if (!string.Equals(manager.AppId, _identity.PackageId, StringComparison.Ordinal))
        {
            return new UpdateOperationResult(UpdateOperationStatus.RejectedChannel);
        }

        try
        {
            var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update is null)
            {
                return new UpdateOperationResult(UpdateOperationStatus.NoUpdate, CurrentVersion);
            }

            if (update.IsDowngrade ||
                !string.Equals(
                    update.TargetFullRelease.PackageId,
                    _identity.PackageId,
                    StringComparison.Ordinal))
            {
                return new UpdateOperationResult(UpdateOperationStatus.RejectedChannel);
            }

            var updaterBackup = BackupCurrentUpdater();
            var admitted = false;
            try
            {
                await manager.DownloadUpdatesAsync(
                        update,
                        progress: null,
                        cancellationToken)
                    .ConfigureAwait(false);

                var packagePath = ResolveDownloadedPackagePath(update.TargetFullRelease.FileName);
                var admission = await _validator.ValidateAsync(
                        new UpdateArtifactAdmission(
                            packagePath,
                            update.TargetFullRelease.SHA256,
                            RequiredPublisherSubject,
                            _identity),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!admission.IsAccepted)
                {
                    TryDeleteRejectedPackage(packagePath);
                    return new UpdateOperationResult(admission.Status);
                }

                admitted = true;
                _pendingUpdate = update;
                return new UpdateOperationResult(
                    UpdateOperationStatus.DownloadedAndVerified,
                    update.TargetFullRelease.Version.ToString());
            }
            finally
            {
                if (!admitted)
                {
                    RestoreUpdater(updaterBackup);
                }

                DeleteUpdaterBackup(updaterBackup);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            return new UpdateOperationResult(UpdateOperationStatus.Failed);
        }
    }

    public bool TryApplyPendingAndRestart()
    {
        var update = Interlocked.Exchange(ref _pendingUpdate, null);
        if (_manager is null || update is null)
        {
            return false;
        }

        _manager.WaitExitThenApplyUpdates(
            update.TargetFullRelease,
            silent: false,
            restart: true,
            restartArgs: null);
        return true;
    }

    private static string ResolveDownloadedPackagePath(string fileName)
    {
        var currentDirectory = new DirectoryInfo(
            Path.GetFullPath(AppContext.BaseDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar)));
        var installRoot = currentDirectory.Parent
            ?? throw new InvalidOperationException("The Velopack install root is unavailable.");
        var packagesRoot = Path.GetFullPath(Path.Combine(installRoot.FullName, "packages"));
        var candidate = Path.GetFullPath(Path.Combine(packagesRoot, Path.GetFileName(fileName)));
        if (!candidate.StartsWith(
                packagesRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The downloaded update path escaped the package directory.");
        }

        return candidate;
    }

    private static UpdaterBackup BackupCurrentUpdater()
    {
        var installRoot = ResolveInstallRoot();
        var updaterPath = Path.Combine(installRoot.FullName, "Update.exe");
        if (!File.Exists(updaterPath))
        {
            throw new FileNotFoundException(
                "The installed Velopack updater is unavailable; update admission cannot proceed.",
                updaterPath);
        }

        var backupRoot = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "EnviousWisprUpdateAdmission"));
        Directory.CreateDirectory(backupRoot);
        var backupPath = Path.Combine(backupRoot, $"{Guid.NewGuid():N}.update-backup");
        File.Copy(updaterPath, backupPath, overwrite: false);
        return new UpdaterBackup(updaterPath, backupPath);
    }

    private static void RestoreUpdater(UpdaterBackup backup)
    {
        if (!File.Exists(backup.BackupPath))
        {
            return;
        }

        File.Copy(backup.BackupPath, backup.UpdaterPath, overwrite: true);
    }

    private static void DeleteUpdaterBackup(UpdaterBackup backup)
    {
        if (!File.Exists(backup.BackupPath))
        {
            return;
        }

        var backupRoot = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "EnviousWisprUpdateAdmission")) + Path.DirectorySeparatorChar;
        var backupPath = Path.GetFullPath(backup.BackupPath);
        if (backupPath.StartsWith(backupRoot, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(backupPath);
        }
    }

    private static void TryDeleteRejectedPackage(string packagePath)
    {
        var packagesRoot = Path.GetFullPath(Path.Combine(ResolveInstallRoot().FullName, "packages")) +
                           Path.DirectorySeparatorChar;
        var resolvedPackage = Path.GetFullPath(packagePath);
        if (resolvedPackage.StartsWith(packagesRoot, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(resolvedPackage))
        {
            File.Delete(resolvedPackage);
        }
    }

    private static DirectoryInfo ResolveInstallRoot()
    {
        var currentDirectory = new DirectoryInfo(
            Path.GetFullPath(AppContext.BaseDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar)));
        return currentDirectory.Parent
               ?? throw new InvalidOperationException("The Velopack install root is unavailable.");
    }

    private sealed record UpdaterBackup(string UpdaterPath, string BackupPath);
}
