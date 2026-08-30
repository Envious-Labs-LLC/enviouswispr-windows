using EnviousWispr.Core.Errors;

namespace EnviousWispr.Core.Settings;

public enum PortableProfileImportStatus
{
    Imported,
    Invalid,
    NewerVersion,
    Unavailable,
}

public sealed record PortableProfileImportResult(
    PortableProfileImportStatus Status,
    PortableProfile? Profile = null,
    AppError? Error = null);

public sealed record PortableProfileExportResult(bool Succeeded, AppError? Error = null);

public interface IPortableProfileService
{
    Task<PortableProfileExportResult> ExportAsync(
        PortableProfile profile,
        string destinationPath,
        CancellationToken cancellationToken = default);

    Task<PortableProfileImportResult> ImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default);
}
