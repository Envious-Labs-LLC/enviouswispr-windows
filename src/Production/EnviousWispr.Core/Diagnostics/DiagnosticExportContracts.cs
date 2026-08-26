using EnviousWispr.Core.Errors;

namespace EnviousWispr.Core.Diagnostics;

public sealed record DiagnosticExportResult(
    bool Succeeded,
    int ExportedRecordCount = 0,
    AppError? Error = null);

public interface IDiagnosticExportService
{
    Task<DiagnosticExportResult> ExportAsync(
        string destinationPath,
        int retentionDays,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}
