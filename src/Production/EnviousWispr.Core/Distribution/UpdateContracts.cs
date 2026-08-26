namespace EnviousWispr.Core.Distribution;

public enum UpdateOperationStatus
{
    Ready,
    DevelopmentBuild,
    NotConfigured,
    BusyDictating,
    NoUpdate,
    DownloadedAndVerified,
    RejectedHash,
    RejectedSignature,
    RejectedPublisher,
    RejectedChannel,
    Failed,
}

public sealed record UpdateOperationResult(
    UpdateOperationStatus Status,
    string? Version = null)
{
    public bool CanApply => Status == UpdateOperationStatus.DownloadedAndVerified;
}

public sealed record UpdateArtifactAdmission(
    string ArtifactPath,
    string ExpectedSha256,
    string RequiredPublisherSubject,
    ReleaseIdentity Identity);

public sealed record UpdateArtifactAdmissionResult(
    UpdateOperationStatus Status,
    string? ActualSha256 = null,
    int VerifiedPortableExecutableCount = 0)
{
    public bool IsAccepted => Status == UpdateOperationStatus.DownloadedAndVerified;
}
