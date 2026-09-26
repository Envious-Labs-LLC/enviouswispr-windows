using System.Diagnostics;
using System.Security.Cryptography;
using EnviousWispr.Services.AppImport;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// The private copy of another app's store, shared by the word and snippet imports: trusted only when its bytes are
/// the source's bytes, and never left behind - a copy a scanner held is swept at the next launch, and the sweep touches
/// nothing but its own folders.
/// </summary>
public sealed class AppImportCopyTests : IDisposable
{
    private const string Sql = "SELECT phrase FROM Dictionary ORDER BY id";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ew-copy-" + Guid.NewGuid().ToString("N"));
    private readonly string _store;
    private readonly string _parent;

    public AppImportCopyTests()
    {
        _store = Path.Combine(_root, "store");
        _parent = Path.Combine(_root, "scratch-parent");
        Directory.CreateDirectory(_store);
        Directory.CreateDirectory(_parent);
    }

    [Fact]
    public void AStableStoreIsReadFromACopyThatIsThenRemoved()
    {
        var path = Fixture();

        var (rows, excluded) = WisprFlowDatabase.Read(path, Sql, row => row.RequiredText(0), _parent, afterCopy: null);

        Assert.Equal(["alpha", "beta"], rows);
        Assert.Equal(0, excluded);
        Assert.Empty(Directory.GetFileSystemEntries(_parent));
    }

    [Fact]
    public void BytesThatChangeDuringTheCopyAtTheSameLengthAndTimeAreRefused()
    {
        var path = Fixture();
        var length = new FileInfo(path).Length;
        var changes = 0;

        // Each attempt, after the copy: one byte of the last page is changed in place, the length is untouched and
        // the write time is put back exactly - the rewrite a size-and-time check cannot see.
        void Rewrite()
        {
            var written = File.GetLastWriteTimeUtc(path);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                stream.Position = length - 16;
                var value = stream.ReadByte();
                stream.Position = length - 16;
                stream.WriteByte((byte)(value ^ 0x5A));
            }

            File.SetLastWriteTimeUtc(path, written);
            changes++;
        }

        Assert.Throws<RivalStoreUnreadableException>(() =>
            WisprFlowDatabase.Read(path, Sql, row => row.RequiredText(0), _parent, Rewrite));

        Assert.Equal(3, changes); // every one of the three attempts saw a change and refused it
        Assert.Equal(length, new FileInfo(path).Length);
        Assert.Empty(Directory.GetFileSystemEntries(_parent));
    }

    [Fact]
    public void AHeldCopyStaysUntilTheNextSweepWhichTouchesNothingElse()
    {
        // Outside the parent, and inside it under a name the sweep did not make: both must survive.
        var outside = Path.Combine(_root, "outside.txt");
        File.WriteAllText(outside, "not ours");
        var foreign = Path.Combine(_parent, "keep-me");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "flow.sqlite"), "not a copy");
        var linkedTarget = Path.Combine(_root, "linked-target");
        Directory.CreateDirectory(linkedTarget);
        File.WriteAllText(Path.Combine(linkedTarget, "flow.sqlite"), "reached only through a link");
        var before = Snapshot(_root, except: _parent);

        var scratch = AppImportScratch.NewFolder(_parent);
        Assert.StartsWith(Path.Combine(_parent, "import-"), scratch, StringComparison.Ordinal);
        var attempt = Path.Combine(scratch, "0");
        Directory.CreateDirectory(attempt);
        var copy = Path.Combine(attempt, "flow.sqlite");
        File.WriteAllText(copy, "somebody's words");
        Junction(Path.Combine(scratch, "1"), linkedTarget);

        using (new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(AppImportScratch.Remove(scratch));
        }

        Assert.True(File.Exists(copy)); // left behind, as a scanner would leave it

        Assert.True(AppImportScratch.SweepLeftovers(_parent));

        Assert.False(Directory.Exists(scratch));
        Assert.Equal(["keep-me"], Directory.GetFileSystemEntries(_parent).Select(Path.GetFileName));
        Assert.Equal("not a copy", File.ReadAllText(Path.Combine(foreign, "flow.sqlite")));
        Assert.Equal(before, Snapshot(_root, except: _parent));
    }

    [Fact]
    public void ASweepWithNoParentFolderIsClean() =>
        Assert.True(AppImportScratch.SweepLeftovers(Path.Combine(_root, "never-made")));

    [Fact]
    public void TheScratchParentIsOneAppOwnedFolderInTemp() =>
        Assert.Equal(Path.Combine(Path.GetTempPath(), "EnviousWispr-app-import"), AppImportScratch.DefaultParent);

    public void Dispose()
    {
        // Test-owned tree only; a junction is removed as a link before anything is walked.
        foreach (var directory in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories).Reverse())
        {
            if (new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                Directory.Delete(directory);
            }
        }

        Directory.Delete(_root, recursive: true);
    }

    private string Fixture()
    {
        var path = Path.Combine(_store, "flow.sqlite");
        WindowsSqlite.ExecuteOnNewDatabase(
            path,
            "CREATE TABLE Dictionary (id TEXT PRIMARY KEY, phrase TEXT NOT NULL);" +
            "INSERT INTO Dictionary VALUES ('1', 'alpha'); INSERT INTO Dictionary VALUES ('2', 'beta');");
        return path;
    }

    private static void Junction(string link, string target)
    {
        using var made = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;
        made.WaitForExit();
        Assert.True(new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint), "mklink /J did not create a junction");
    }

    private static SortedDictionary<string, string> Snapshot(string root, string except) => new(
        Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Where(file => !file.StartsWith(except, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                file => Path.GetRelativePath(root, file),
                file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))),
        StringComparer.Ordinal);
}
