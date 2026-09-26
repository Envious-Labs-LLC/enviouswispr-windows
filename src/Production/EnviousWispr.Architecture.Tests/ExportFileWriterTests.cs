using System.Runtime.InteropServices;
using EnviousWispr.Services.UserData;

namespace EnviousWispr.Architecture.Tests;

/// <summary>An export replaces the chosen name with a new file; it never writes through the file that was there.</summary>
public sealed class ExportFileWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ew-export-" + Guid.NewGuid().ToString("N"));

    public ExportFileWriterTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task AHardLinkChosenAsTheDestinationLeavesTheFileItSharesUntouched()
    {
        var sentinel = Path.Combine(_root, "settings.json");
        var chosen = Path.Combine(_root, "EnviousWispr Snippets.json");
        await File.WriteAllTextAsync(sentinel, "{\"schemaVersion\":18}");
        Assert.True(CreateHardLinkW(chosen, sentinel, IntPtr.Zero), "the hard link was not created");

        // THE PRECONDITION IS ASSERTED: the two names really are one file before the export.
        await File.AppendAllTextAsync(chosen, " ");
        Assert.Equal("{\"schemaVersion\":18} ", await File.ReadAllTextAsync(sentinel));
        await File.WriteAllTextAsync(sentinel, "{\"schemaVersion\":18}");

        await ExportFileWriter.WriteReplacingAsync(chosen, "{\"keyword\":\"backslash\"}");

        Assert.Equal("{\"schemaVersion\":18}", await File.ReadAllTextAsync(sentinel));
        Assert.Equal("{\"keyword\":\"backslash\"}", await File.ReadAllTextAsync(chosen));
        Assert.Equal(["EnviousWispr Snippets.json", "settings.json"], Names());
    }

    [Fact]
    public async Task ANewDestinationIsWrittenWholeAndNothingElseIsLeftBeside()
    {
        var chosen = Path.Combine(_root, "EnviousWispr-words.csv");

        await ExportFileWriter.WriteReplacingAsync(chosen, "when I say,write,how closely\ncolour,color,default");

        Assert.Equal("when I say,write,how closely\ncolour,color,default", await File.ReadAllTextAsync(chosen));
        Assert.Equal(["EnviousWispr-words.csv"], Names());
    }

    [Fact]
    public async Task AFailedReplaceLeavesNoTemporaryBehind()
    {
        // A FOLDER AT THE CHOSEN NAME: the temporary is written, then the move onto a directory fails.
        var chosen = Path.Combine(_root, "taken");
        Directory.CreateDirectory(chosen);

        var failure = await Record.ExceptionAsync(() => ExportFileWriter.WriteReplacingAsync(chosen, "text"));
        Assert.True(failure is IOException or UnauthorizedAccessException, $"unexpected {failure?.GetType().Name ?? "success"}");

        Assert.Equal(["taken"], Names());
        Assert.True(Directory.Exists(chosen));
    }

    public void Dispose()
    {
        foreach (var file in Directory.GetFiles(_root))
        {
            File.Delete(file);
        }

        foreach (var folder in Directory.GetDirectories(_root))
        {
            Directory.Delete(folder);
        }

        Directory.Delete(_root);
    }

    private string[] Names() =>
        Directory.GetFileSystemEntries(_root).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newLink, string existing, IntPtr security);
}
