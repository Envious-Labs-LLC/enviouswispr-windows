using System.Security;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Errors;
using EnviousWispr.Services.Settings;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Services.Diagnostics;

public sealed class JsonDiagnosticExportService(string sourcePath) : IDiagnosticExportService
{
    private readonly string _sourcePath = Path.GetFullPath(
        string.IsNullOrWhiteSpace(sourcePath)
            ? throw new ArgumentException("A diagnostics source path is required.", nameof(sourcePath))
            : sourcePath);

    public async Task<DiagnosticExportResult> ExportAsync(
        string destinationPath,
        int retentionDays,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(retentionDays, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(retentionDays, RetentionDays.DiagnosticMaximum);
        var destination = Path.GetFullPath(destinationPath);
        if (string.Equals(destination, _sourcePath, StringComparison.OrdinalIgnoreCase))
        {
            return Failure(AppErrorCode.InvalidData);
        }

        try
        {
            var cutoff = now - TimeSpan.FromDays(retentionDays);
            var records = File.Exists(_sourcePath)
                ? File.ReadLines(_sourcePath)
                    .Select(line => JsonLineFileLogger.TryParseRecord(line, out var record) ? record : null)
                    .Where(record => record is not null && record.Timestamp >= cutoff)
                    // THE EXPORT KEEPS THE DICTATION JOIN. It is the user's own log, written on
                    // their machine and exported by them to diagnose their own problem, and an
                    // export less useful than the file it came from is not worth having. The join
                    // adds no identity: it says which of these lines belong together and nothing
                    // about who, what, or where. The rule about identifiers is about the record
                    // that crosses the network on its own, which this is not.
                    .Cast<LocalDiagnosticLine>()
                    .ToArray()
                : [];
            var lines = records.Select(JsonLineFileLogger.Serialize);
            await JsonSettingsStore.WriteLinesAtomicallyAsync(
                lines,
                destination,
                cancellationToken).ConfigureAwait(false);
            return new DiagnosticExportResult(true, records.Length);
        }
        catch (IOException)
        {
            return Failure(AppErrorCode.StorageUnavailable);
        }
        catch (UnauthorizedAccessException)
        {
            return Failure(AppErrorCode.AccessDenied);
        }
        catch (SecurityException)
        {
            return Failure(AppErrorCode.AccessDenied);
        }
    }

    private static DiagnosticExportResult Failure(AppErrorCode code) => new(
        false,
        Error: new AppError(code, AppErrorStage.DiagnosticExport, CanRetry: code != AppErrorCode.InvalidData));
}
