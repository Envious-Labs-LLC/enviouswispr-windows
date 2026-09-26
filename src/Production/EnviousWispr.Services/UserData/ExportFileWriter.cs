using System.Text;

namespace EnviousWispr.Services.UserData;

/// <summary>Writes an export the person asked for without ever writing through the file they chose.</summary>
/// <remarks>
/// A NEW FILE, THEN A NEW DIRECTORY ENTRY. The text goes to a temporary file in the destination's own folder, is
/// flushed to the disk, and then <see cref="File.Move(string, string, bool)"/> with overwrite puts that file at the
/// chosen name (MoveFileEx with REPLACE_EXISTING). The chosen name is re-pointed at the new file; the old file is
/// never opened for writing. That is the difference that matters:
///
/// - A destination that is a HARD LINK to another file (say the app's settings, reached from outside the data
///   folder, which the folder guard cannot see) has its link replaced, and the other name keeps its content.
///   Writing through the chosen name in place would truncate the shared file and erase whatever it holds.
/// - A crash or a full disk part-way through leaves the old file whole and a temporary beside it, never a half
///   written export under the chosen name.
///
/// <see cref="File.Replace(string, string, string?)"/> was not used: it requires the destination to exist and
/// carries the destination's identity over to the new file, which is the opposite of what a link needs. The same
/// folder is required so the move is a rename on one volume rather than a copy.
///
/// The temporary is deleted on every failure, and the failure is rethrown for the page to report.
/// </remarks>
public static class ExportFileWriter
{
    public static async Task WriteReplacingAsync(string destination, string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentNullException.ThrowIfNull(text);
        var full = Path.GetFullPath(destination);
        var folder = Path.GetDirectoryName(full)
            ?? throw new IOException("The export destination has no folder.");
        // A SHORT FIXED NAME, NOT THE DESTINATION'S NAME PLUS A SUFFIX: a chosen name near the 255-character limit
        // on one path component would otherwise make the temporary's name too long, and the export would fail.
        var temporary = Path.Combine(folder, $".ew-export-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, full, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // The export already failed and says so; a temporary that cannot be removed is left, named as one.
            }

            throw;
        }
    }
}
