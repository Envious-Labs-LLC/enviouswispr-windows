using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using EnviousWispr.Services.AppImport;
using Microsoft.Win32.SafeHandles;

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
    public void ALinkInTheParentThatTheSweepDidNotNameSurvivesWithItsTarget()
    {
        var target = Path.Combine(_root, "link-target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "flow.sqlite"), "reached only through a link");
        var link = Path.Combine(_parent, "keep-me");
        Junction(link, target);
        var owned = AppImportScratch.NewFolder(_parent);
        Directory.CreateDirectory(Path.Combine(owned, "0"));
        File.WriteAllText(Path.Combine(owned, "0", "flow.sqlite"), "a leftover copy");

        var clean = AppImportScratch.SweepLeftovers(_parent);

        Assert.True(clean);
        Assert.False(Directory.Exists(owned));
        Assert.True(new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint), "the unowned link was removed");
        Assert.Equal("reached only through a link", File.ReadAllText(Path.Combine(target, "flow.sqlite")));
        Assert.Equal(["keep-me"], Directory.GetFileSystemEntries(_parent).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("import-")]
    [InlineData("import-not-a-guid-at-all-but-thirty-two")]
    [InlineData("import-0123456789abcdef0123456789abcdef0")]
    [InlineData("Import-0123456789abcdef0123456789abcdef")]
    public void OnlyTheExactNameTheReaderWritesIsOwned(string name) =>
        Assert.False(AppImportScratch.IsOwnedFolderName(name));

    [Fact]
    public void TheNameTheReaderWritesIsOwned() =>
        Assert.True(AppImportScratch.IsOwnedFolderName(Path.GetFileName(AppImportScratch.NewFolder(_parent))));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AParentThatCannotBeListedIsAFailureNotAClean(bool callbackThrows)
    {
        var owned = AppImportScratch.NewFolder(_parent);
        Directory.CreateDirectory(Path.Combine(owned, "0"));
        var copy = Path.Combine(owned, "0", "flow.sqlite");
        File.WriteAllText(copy, "a leftover copy");
        using var caller = WindowsIdentity.GetCurrent();
        var user = caller.User!.Value;
        Icacls($"\"{_parent}\" /deny *{user}:(RD)");
        try
        {
            var deniedBeforeScope = DirectoryListingIsDenied(_parent);
            var assertionsRan = false;
            var callbackFailure = new InvalidOperationException("Deliberate permission-probe callback failure.");

            void VerifyDeniedParent()
            {
                // Metadata is readable: the sweep must reach its LIST failure, not its earlier Unknown probe.
                _ = File.GetAttributes(_parent);
                // The denial is still proved, even when the runner normally bypasses ACLs for backup.
                Assert.Throws<UnauthorizedAccessException>(() => Directory.GetFileSystemEntries(_parent));
                Assert.False(AppImportScratch.SweepLeftovers(_parent));
                assertionsRan = true;
                if (callbackThrows)
                {
                    throw callbackFailure;
                }
            }

            if (callbackThrows)
            {
                var caught = Assert.Throws<InvalidOperationException>(() => WithoutFilePrivileges(VerifyDeniedParent));
                Assert.Same(callbackFailure, caught);
            }
            else
            {
                WithoutFilePrivileges(VerifyDeniedParent);
            }

            Assert.True(assertionsRan);
            Assert.Equal(deniedBeforeScope, DirectoryListingIsDenied(_parent));
        }
        finally
        {
            Icacls($"\"{_parent}\" /remove:d *{user}");
        }

        Assert.True(File.Exists(copy));
        Assert.Equal("a leftover copy", File.ReadAllText(copy));
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

    private static bool DirectoryListingIsDenied(string directory)
    {
        try
        {
            _ = Directory.GetFileSystemEntries(directory);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void WithoutFilePrivileges(Action assertions)
    {
        // SSH can enable SeBackupPrivilege, making an explicit listing deny ineffective. Restrict a COPY,
        // not the shared process token: parallel tests retain their caller, and RunImpersonated restores this
        // synchronous callback's context on return or throw. SIDs stay the same; traversal stays enabled.
        using var caller = WindowsIdentity.GetCurrent(
            TokenAccessLevels.Query | TokenAccessLevels.Duplicate | TokenAccessLevels.Impersonate);
        const uint disableMaxPrivilege = 0x1;
        var created = NativeMethods.CreateRestrictedToken(
            caller.AccessToken, disableMaxPrivilege,
            0, IntPtr.Zero, 0, IntPtr.Zero, 0, IntPtr.Zero, out var restricted);
        var error = Marshal.GetLastWin32Error();
        using (restricted)
        {
            if (!created)
            {
                throw new Win32Exception(error);
            }

            // An invalid handle means "run as the original caller" to RunImpersonated, so it is not a scope.
            if (restricted.IsInvalid)
            {
                throw new InvalidOperationException("The permission-probe token was invalid.");
            }

            WindowsIdentity.RunImpersonated(restricted, assertions);
        }
    }

    private static class NativeMethods
    {
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateRestrictedToken(
            SafeAccessTokenHandle existingToken, uint flags,
            uint disableSidCount, IntPtr sidsToDisable,
            uint deletePrivilegeCount, IntPtr privilegesToDelete,
            uint restrictedSidCount, IntPtr sidsToRestrict,
            out SafeAccessTokenHandle newToken);
    }

    private static void Icacls(string arguments)
    {
        using var run = Process.Start(new ProcessStartInfo("icacls.exe", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;
        run.WaitForExit();
        Assert.Equal(0, run.ExitCode);
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
