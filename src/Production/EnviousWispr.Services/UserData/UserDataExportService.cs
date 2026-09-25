using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.History;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Settings;
using EnviousWispr.Services.Settings;

namespace EnviousWispr.Services.UserData;

/// <summary>What an export of the person's data wrote.</summary>
/// <param name="Succeeded">True only when the finished file is at the chosen path.</param>
/// <param name="HistoryEntries">How many dictations went into the file.</param>
/// <param name="IncludedRecoveredText">Whether an unfinished dictation's recovery copy went in.</param>
/// <param name="Error">Why it failed, content-free.</param>
public sealed record UserDataExportResult(
    bool Succeeded,
    int HistoryEntries = 0,
    bool IncludedRecoveredText = false,
    AppError? Error = null);

/// <summary>
/// "Export my data": one .zip holding the portable profile, the dictation history as readable JSON,
/// and the recovered unfinished dictation when there is one.
/// </summary>
/// <remarks>
/// EVERYTHING IS READ THROUGH ITS OWN STORE, NEVER COPIED AS A FILE. The history file is plain JSON
/// today, but the recovery copy is DPAPI-protected to this Windows account and a raw copy would be
/// unreadable anywhere else; reading both through their stores means the file holds what the person
/// sees in the app, in a shape they can open, whatever either store does to its file later.
///
/// WHAT IS NEVER IN IT is decided by what is written, not by what is filtered out: three named entries
/// and a note, built from the profile, the history and the recovery record. No path under the data
/// folder is ever opened here, so models, runtimes, diagnostics, the audio archive and run state cannot
/// reach the file, and no credential store is handed in, so no key can. The profile is the same one
/// "Export profile" writes, which already leaves out keys and machine-local choices.
///
/// A FAILURE LEAVES NOTHING AT THE CHOSEN PATH. The archive is written to a temporary file beside the
/// destination and moved over it in one step once it is complete; the temporary file is deleted on
/// every path out.
/// </remarks>
public static class UserDataExportService
{
    /// <summary>The profile entry: the same document "Export profile" writes, so "Import profile" takes it.</summary>
    public const string ProfileEntryName = "profile.json";

    /// <summary>The dictation history, readable.</summary>
    public const string HistoryEntryName = "history.json";

    /// <summary>The recovered unfinished dictation, present only when there is one.</summary>
    public const string RecoveredEntryName = "recovered-dictation.json";

    /// <summary>A plain-text note saying what the file holds and what it does not.</summary>
    public const string ReadMeEntryName = "README.txt";

    private const int HistorySchemaVersion = 1;

    // THE NOTE IS THE SAME PROMISE THE BACKUP PAGE MAKES, kept beside the data it describes.
    private const string ReadMe =
        "EnviousWispr data export\r\n"
        + "\r\n"
        + "profile.json: your settings, your words and your snippets. Import it on the Backup page with Import profile.\r\n"
        + "history.json: your saved dictations, as readable text.\r\n"
        + "recovered-dictation.json: an unfinished dictation EnviousWispr was holding for you, when there was one.\r\n"
        + "\r\n"
        + "Not included: API keys, downloaded speech and polish models, diagnostic logs, saved recordings, "
        + "and this PC's microphone choice.\r\n";

    private static readonly JsonSerializerOptions ReadableJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Reads the history and recovery copy through their stores and writes the archive to <paramref name="destinationPath"/>.</summary>
    /// <param name="profile">The portable profile, as "Export profile" builds it.</param>
    /// <param name="history">The history store; read with the person's own retention, as the History page reads it.</param>
    /// <param name="retentionDays">The history retention the load prunes by.</param>
    /// <param name="recovery">The recovery store; no copy is left out; a copy that cannot be read fails the export.</param>
    /// <param name="destinationPath">The file the person chose.</param>
    /// <param name="now">The clock the history retention and the export stamp read.</param>
    public static async Task<UserDataExportResult> ExportAsync(
        PortableProfile profile,
        IHistoryStore history,
        int retentionDays,
        IRecoveryTextStore recovery,
        string destinationPath,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var validationError = AppSettingsValidator.Validate(profile, AppErrorStage.ProfileExport);
        if (validationError is not null)
        {
            return new UserDataExportResult(false, Error: validationError);
        }

        // A HISTORY THAT CANNOT BE READ FAILS THE EXPORT. Writing a file without it would hand the
        // person an archive that looks complete and is missing the thing they most likely wanted.
        var loaded = await history.LoadAsync(retentionDays, now, cancellationToken).ConfigureAwait(false);
        if (loaded.Status is HistoryLoadStatus.Invalid or HistoryLoadStatus.Unavailable)
        {
            return new UserDataExportResult(
                false,
                Error: loaded.Error ?? new AppError(AppErrorCode.StorageUnavailable, AppErrorStage.ProfileExport, CanRetry: true));
        }

        // ONLY "THERE IS NONE" LEAVES IT OUT. A recovery copy that exists and cannot be read - damaged,
        // unavailable, protected to another Windows account - is the person's words, and an archive
        // without it would look complete while missing them. So that fails the export, as an unreadable
        // history does, and so does a Found that carries no record.
        var recovered = await recovery.LoadAsync(cancellationToken).ConfigureAwait(false);
        RecoveryTextRecord? recoveredRecord;
        switch (recovered)
        {
            case { Status: RecoveryTextLoadStatus.Missing }:
                recoveredRecord = null;
                break;
            case { Status: RecoveryTextLoadStatus.Found, Record: { } record }:
                recoveredRecord = record;
                break;
            default:
                return new UserDataExportResult(
                    false,
                    Error: recovered.Error ?? new AppError(AppErrorCode.InvalidData, AppErrorStage.ProfileExport, CanRetry: false));
        }

        var destination = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(destination);
        if (string.IsNullOrEmpty(directory))
        {
            return Failure(AppErrorCode.InvalidData);
        }

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);
                WriteEntry(archive, ProfileEntryName, JsonSerializer.Serialize(profile, JsonSettingsStore.SerializerOptions));
                WriteEntry(
                    archive,
                    HistoryEntryName,
                    JsonSerializer.Serialize(
                        new HistoryExport(
                            HistorySchemaVersion,
                            now,
                            loaded.Entries.Select(entry => new HistoryExportEntry(
                                entry.Id,
                                entry.CreatedAt,
                                entry.Text,
                                entry.EngineId,
                                entry.WasPolished,
                                entry.WasDelivered,
                                entry.ExpiresAt)).ToArray()),
                        ReadableJson));
                if (recoveredRecord is not null)
                {
                    WriteEntry(
                        archive,
                        RecoveredEntryName,
                        JsonSerializer.Serialize(
                            new RecoveredExport(recoveredRecord.CreatedAt, recoveredRecord.Text),
                            ReadableJson));
                }

                WriteEntry(archive, ReadMeEntryName, ReadMe);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destination, overwrite: true);
            return new UserDataExportResult(true, loaded.Entries.Count, recoveredRecord is not null);
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
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void WriteEntry(ZipArchive archive, string name, string text)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(text);
    }

    private static UserDataExportResult Failure(AppErrorCode code) =>
        new(false, Error: new AppError(code, AppErrorStage.ProfileExport, CanRetry: true));

    private sealed record HistoryExport(int SchemaVersion, DateTimeOffset ExportedAt, IReadOnlyList<HistoryExportEntry> Entries);

    private sealed record HistoryExportEntry(
        Guid Id,
        DateTimeOffset CreatedAt,
        string Text,
        string EngineId,
        bool WasPolished,
        bool WasDelivered,
        DateTimeOffset? ExpiresAt);

    private sealed record RecoveredExport(DateTimeOffset CreatedAt, string Text);
}
