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
    public void TheFolderIsAddedByFullPathBeforeTheDefaultSearchIsSwitched()
    {
        // THE ORDER IS THE SAFETY. Switching the default drops PATH and the current directory from every
        // later by-name load; it happens only once the CUDA folder is already in the search.
        var native = new RecordingDirectories();
        var relative = Path.Combine("runtime", "cuda");

        var outcome = NativeRuntimeSearchPath.Configure(relative, _ => true, native);

        Assert.Equal(NativeRuntimeSearchPathOutcome.Added, outcome);
        Assert.Equal(
            [$"AddDllDirectory:{Path.GetFullPath(relative)}", $"SetDefaultDllDirectories:{LoadLibrarySearchDefaultDirs:X}"],
            native.Calls);
        Assert.True(native.DefaultSwitched);
        Assert.Equal([RecordingDirectories.Cookie], native.AddedCookies);
    }

    [Fact]
    public void ARefusedFolderLeavesTheDefaultSearchUntouched()
    {
        var native = new RecordingDirectories { RefuseAdd = true };

        var outcome = NativeRuntimeSearchPath.Configure(Path.GetFullPath("cuda"), _ => true, native);

        Assert.Equal(NativeRuntimeSearchPathOutcome.Refused, outcome);
        Assert.Equal([$"AddDllDirectory:{Path.GetFullPath("cuda")}"], native.Calls);
        Assert.False(native.DefaultSwitched);
        Assert.Empty(native.AddedCookies);
    }

    [Fact]
    public void ARefusedDefaultTakesTheFolderBackOutSoTheProcessIsAsItWas()
    {
        var native = new RecordingDirectories { RefuseDefault = true };

        var outcome = NativeRuntimeSearchPath.Configure(Path.GetFullPath("cuda"), _ => true, native);

        Assert.Equal(NativeRuntimeSearchPathOutcome.Refused, outcome);
        Assert.Equal(
            [
                $"AddDllDirectory:{Path.GetFullPath("cuda")}",
                $"SetDefaultDllDirectories:{LoadLibrarySearchDefaultDirs:X}",
                $"RemoveDllDirectory:{RecordingDirectories.Cookie}",
            ],
            native.Calls);
        Assert.False(native.DefaultSwitched);
        Assert.Empty(native.AddedCookies);
    }

    [Fact]
    public void TheWorkerSendsAnUnreachableCardStraightToTheProcessorWithTheFallbackReason()
    {
        // A FAILED CONFIGURATION IS HONOURED, NOT IGNORED: the worker reads the outcome and, for a card
        // engine given a folder it could not add, builds the processor engine and reports UsedFallback with
        // RuntimeProviderUnavailable - the same answer a card that failed to start gives.
        var worker = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Production", "EnviousWispr.RuntimeWorker", "Program.cs"));
        Assert.Contains("var search = NativeRuntimeSearchPath.Configure(cudaRuntimeDirectory);", worker, StringComparison.Ordinal);
        Assert.Contains("search is NativeRuntimeSearchPathOutcome.DirectoryMissing or NativeRuntimeSearchPathOutcome.Refused", worker, StringComparison.Ordinal);
        Assert.Contains("return OnTheProcessor(new ParakeetEngineFactory().Create(fallback!).Engine);", worker, StringComparison.Ordinal);
        Assert.Contains("return OnTheProcessor(new WhisperEngineFactory().Create(fallback!).Engine);", worker, StringComparison.Ordinal);
        Assert.Contains("UsedFallback: true,\n    new AppError(AppErrorCode.RuntimeProviderUnavailable", worker.ReplaceLineEndings("\n"), StringComparison.Ordinal);
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
        public const nint Cookie = 0x5A5A;

        public List<string> Calls { get; } = [];

        /// <summary>The folders still in the search: added and not removed.</summary>
        public List<nint> AddedCookies { get; } = [];

        public bool DefaultSwitched { get; private set; }

        public bool RefuseDefault { get; init; }

        public bool RefuseAdd { get; init; }

        public IntPtr AddDllDirectory(string directory)
        {
            Calls.Add($"AddDllDirectory:{directory}");
            if (RefuseAdd)
            {
                return IntPtr.Zero;
            }

            AddedCookies.Add(Cookie);
            return Cookie;
        }

        public bool SetDefaultDllDirectories(uint flags)
        {
            Calls.Add($"SetDefaultDllDirectories:{flags:X}");
            DefaultSwitched = !RefuseDefault;
            return !RefuseDefault;
        }

        public bool RemoveDllDirectory(IntPtr cookie)
        {
            Calls.Add($"RemoveDllDirectory:{cookie}");
            return AddedCookies.Remove(cookie);
        }
    }
}
