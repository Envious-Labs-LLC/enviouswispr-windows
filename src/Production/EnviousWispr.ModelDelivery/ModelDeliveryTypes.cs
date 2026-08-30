namespace EnviousWispr.ModelDelivery;

public enum ModelDeliveryFailure
{
    None,
    InvalidManifest,
    UntrustedManifest,
    UnsupportedManifest,
    AppVersionTooOld,
    InsufficientDisk,
    NetworkUnavailable,
    SourceRejected,
    IntegrityMismatch,
    StorageUnavailable,
    VersionNotInstalled,
    Cancelled,
}

public enum ModelDeliveryEventCode
{
    ManifestAccepted,
    DownloadStarted,
    DownloadResumed,
    SourceFailed,
    ArtifactVerified,
    ModelAdmitted,
    ModelActivated,
    ModelRemoved,
    CleanupCompleted,
    OperationFailed,
    OperationCancelled,
}

public sealed record ModelDeliveryEvent(
    DateTimeOffset Timestamp,
    ModelDeliveryEventCode Code,
    ModelDeliveryFailure Failure = ModelDeliveryFailure.None,
    long? CompletedBytes = null,
    long? TotalBytes = null);

public interface IModelDeliveryObserver
{
    void Observe(ModelDeliveryEvent deliveryEvent);
}

public sealed class NullModelDeliveryObserver : IModelDeliveryObserver
{
    public static NullModelDeliveryObserver Instance { get; } = new();

    private NullModelDeliveryObserver()
    {
    }

    public void Observe(ModelDeliveryEvent deliveryEvent)
    {
    }
}

public sealed record ModelDeliveryResult(
    bool Succeeded,
    ModelDeliveryFailure Failure = ModelDeliveryFailure.None,
    InstalledModelVersion? Installed = null,
    long RequiredBytes = 0,
    long AvailableBytes = 0);

public sealed record InstalledModelVersion(
    string ModelId,
    string Version,
    string ManifestDigest,
    string DirectoryPath,
    ModelLicenseNotice License);

public interface IDiskSpaceProbe
{
    long GetAvailableBytes(string path);
}

public sealed class WindowsDiskSpaceProbe : IDiskSpaceProbe
{
    public long GetAvailableBytes(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new IOException("The model store does not have a filesystem root.");
        }

        return new DriveInfo(root).AvailableFreeSpace;
    }
}

public sealed record ModelDeliveryOptions(
    long DiskReserveBytes = 256L * 1024 * 1024,
    int MaximumAttemptsPerSource = 4,
    TimeSpan? RequestTimeout = null,
    Func<int, TimeSpan>? RetryDelay = null)
{
    internal TimeSpan EffectiveRequestTimeout => RequestTimeout ?? TimeSpan.FromSeconds(60);

    internal TimeSpan DelayForAttempt(int retryNumber) => RetryDelay?.Invoke(retryNumber) ??
        TimeSpan.FromMilliseconds(Random.Shared.NextDouble() *
            Math.Min(8000, 1000 * Math.Pow(2, Math.Max(0, retryNumber - 1))));
}
