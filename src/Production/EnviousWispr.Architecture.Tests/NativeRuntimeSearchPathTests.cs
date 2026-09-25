using EnviousWispr.ASR;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// The CUDA folder reaches the speech engines' native libraries through the process's default DLL search,
/// which a packaged process honours, and never through PATH, which it does not.
/// </summary>
public sealed class NativeRuntimeSearchPathTests
{
    private const uint LoadLibrarySearchDefaultDirs = 0x00001000;
    private static readonly string[] EngineProjects = ["EnviousWispr.RuntimeWorker", "EnviousWispr.ASR"];

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoDirectoryLeavesTheProcessAlone(string? directory)
    {
        var native = new RecordingDirectories();

        var outcome = NativeRuntimeSearchPath.Configure(directory, _ => true, native);

        Assert.Equal(NativeRuntimeSearchPathOutcome.NoDirectory, outcome);
        Assert.Empty(native.Calls);
    }

    [Fact]
    public void AMissingDirectoryLeavesTheProcessAlone()
    {
        var native = new RecordingDirectories();

        var outcome = NativeRuntimeSearchPath.Configure(Path.GetFullPath(Path.Combine("C:", "missing")), _ => false, native);

        Assert.Equal(NativeRuntimeSearchPathOutcome.DirectoryMissing, outcome);
        Assert.Empty(native.Calls);
    }

    [Fact]
    public void TheDefaultSearchIsSetBeforeTheFolderIsAddedAndTheFolderIsAddedByFullPath()
    {
        // THE ORDER IS THE MECHANISM. A folder added while the process still uses the standard search is seen
        // only by loads that ask for user folders themselves, which neither engine's loader does.
        var native = new RecordingDirectories();
        var relative = Path.Combine("runtime", "cuda");

        var outcome = NativeRuntimeSearchPath.Configure(relative, _ => true, native);

        Assert.Equal(NativeRuntimeSearchPathOutcome.Added, outcome);
        Assert.Equal(
            [$"SetDefaultDllDirectories:{LoadLibrarySearchDefaultDirs:X}", $"AddDllDirectory:{Path.GetFullPath(relative)}"],
            native.Calls);
    }

    [Fact]
    public void ARefusedDefaultIsReportedAndNothingIsAdded()
    {
        var native = new RecordingDirectories { RefuseDefault = true };

        var outcome = NativeRuntimeSearchPath.Configure(Path.GetFullPath("cuda"), _ => true, native);

        Assert.Equal(NativeRuntimeSearchPathOutcome.Refused, outcome);
        Assert.Equal([$"SetDefaultDllDirectories:{LoadLibrarySearchDefaultDirs:X}"], native.Calls);
    }

    [Fact]
    public void ARefusedFolderIsReported()
    {
        var native = new RecordingDirectories { RefuseAdd = true };

        Assert.Equal(
            NativeRuntimeSearchPathOutcome.Refused,
            NativeRuntimeSearchPath.Configure(Path.GetFullPath("cuda"), _ => true, native));
    }

    [Fact]
    public void NoWorkerOrEngineSourceSearchesForTheCudaRuntimeThroughPath()
    {
        // THE REGRESSION THIS REPLACES: the worker and the Parakeet engine prepended the CUDA folder to PATH,
        // which a packaged process never searches for a DLL, so the Store build fell back to the processor.
        // Any use of PATH in the code that loads the engines brings that back.
        var root = RepositoryRoot();
        var offenders = EngineProjects
            .SelectMany(project => Directory.EnumerateFiles(
                Path.Combine(root, "src", "Production", project),
                "*.cs",
                SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains("\"PATH\"", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path))
            .ToArray();

        Assert.Empty(offenders);

        var worker = File.ReadAllText(Path.Combine(root, "src", "Production", "EnviousWispr.RuntimeWorker", "Program.cs"));
        var creation = worker.IndexOf("static TranscriptionEngineCreation? CreateTranscriptionEngine(", StringComparison.Ordinal);
        var configure = worker.IndexOf("NativeRuntimeSearchPath.Configure(", StringComparison.Ordinal);
        var firstEngine = worker.IndexOf("new ParakeetEngineOptions(", StringComparison.Ordinal);
        Assert.True(creation >= 0 && configure > creation && configure < firstEngine, "The worker must configure the DLL search at the top of engine creation, before any engine is built.");
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EnviousWispr.Windows.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Expected to find EnviousWispr.Windows.slnx.");
    }

    private sealed class RecordingDirectories : INativeDllDirectories
    {
        public List<string> Calls { get; } = [];

        public bool RefuseDefault { get; init; }

        public bool RefuseAdd { get; init; }

        public bool SetDefaultDllDirectories(uint flags)
        {
            Calls.Add($"SetDefaultDllDirectories:{flags:X}");
            return !RefuseDefault;
        }

        public bool AddDllDirectory(string directory)
        {
            Calls.Add($"AddDllDirectory:{directory}");
            return !RefuseAdd;
        }
    }
}
