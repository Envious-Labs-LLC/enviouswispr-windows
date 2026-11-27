using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using EnviousWispr.Core.IO;
using EnviousWispr.Core.Networking;

namespace EnviousWispr.Architecture.Tests;

public sealed class ServicesConsolidationAndBoundariesTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "ew-wave3c-tests-" + Guid.NewGuid().ToString("N"));

    public ServicesConsolidationAndBoundariesTests() => Directory.CreateDirectory(_tempRoot);

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task AtomicFileWriterWritesUtf8WithoutBomAndSupportsLinesBytesJsonAndLongNames()
    {
        var nestedDir = Path.Combine(_tempRoot, "nested", "sub");
        var textPath = Path.Combine(nestedDir, "sample.txt");
        Assert.True(await AtomicFileWriter.WriteTextAsync(textPath, "hello world"));
        var raw = await File.ReadAllBytesAsync(textPath);
        Assert.False(raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF);
        Assert.Equal("hello world", File.ReadAllText(textPath));

        var linesPath = Path.Combine(nestedDir, "lines.log");
        Assert.True(AtomicFileWriter.WriteLines(linesPath, ["alpha", "beta"]));
        Assert.Equal(["alpha", "beta"], File.ReadAllLines(linesPath));

        var bytesPath = Path.Combine(nestedDir, "blob.bin");
        Assert.True(AtomicFileWriter.WriteBytes(bytesPath, [1, 2, 3, 4]));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(bytesPath));

        var jsonPath = Path.Combine(nestedDir, "doc.json");
        Assert.True(await AtomicFileWriter.WriteJsonAsync(jsonPath, new SamplePayload(42, "ok")));
        var loaded = JsonSerializer.Deserialize<SamplePayload>(File.ReadAllText(jsonPath));
        Assert.Equal(new SamplePayload(42, "ok"), loaded);

        var longNamePath = Path.Combine(_tempRoot, new string('a', 245) + ".json");
        Assert.True(await AtomicFileWriter.WriteTextAsync(longNamePath, "{}"));
        Assert.Equal("{}", File.ReadAllText(longNamePath));
    }

    [Fact]
    public async Task AtomicFileWriterCleansUpTemporaryOnFenceRefusalStreamErrorAndMissingParent()
    {
        var destination = Path.Combine(_tempRoot, "guarded.json");
        await File.WriteAllTextAsync(destination, "original");

        var committed = await AtomicFileWriter.WriteTextAsync(
            destination,
            "replacement",
            commitMove: static _ => false);
        Assert.False(committed);
        Assert.Equal("original", await File.ReadAllTextAsync(destination));
        Assert.Empty(Directory.GetFiles(_tempRoot, "*.tmp"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AtomicFileWriter.WriteWithStreamAsync(
                destination,
                static (_, _) => throw new InvalidOperationException("stream failed")));
        Assert.Equal("original", await File.ReadAllTextAsync(destination));
        Assert.Empty(Directory.GetFiles(_tempRoot, "*.tmp"));

        var missingParent = Path.Combine(_tempRoot, "does-not-exist", "file.txt");
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            AtomicFileWriter.WriteTextAsync(missingParent, "text", createParentDirectory: false));
    }

    [Fact]
    public async Task SharedHttpClientsReusesHandlersByProfileAndNeverDisposesPoolsOrCustomHandler()
    {
        var defaultHandler = SharedHttpClients.GetHandler(HttpClientProfile.Default);
        var noRedirect = SharedHttpClients.GetHandler(HttpClientProfile.NoRedirect);
        var noCookies = SharedHttpClients.GetHandler(HttpClientProfile.NoRedirectNoCookies);

        Assert.True(defaultHandler.PooledConnectionLifetime > TimeSpan.Zero);
        Assert.False(noRedirect.AllowAutoRedirect);
        Assert.False(noCookies.AllowAutoRedirect);
        Assert.False(noCookies.UseCookies);

        using (var c1 = SharedHttpClients.CreateClient(profile: HttpClientProfile.NoRedirect))
        {
            Assert.NotNull(c1);
        }

        using var c2 = SharedHttpClients.CreateClient(profile: HttpClientProfile.NoRedirect);
        Assert.Same(noRedirect, SharedHttpClients.GetHandler(HttpClientProfile.NoRedirect));

        var handler = new CountingHandler();
        using (var first = SharedHttpClients.CreateClient(handler))
        {
            using var r1 = await first.GetAsync("https://example.invalid/one");
            Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        }

        Assert.False(handler.Disposed);
        using var second = SharedHttpClients.CreateClient(handler);
        using var r2 = await second.GetAsync("https://example.invalid/two");
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public void ZeroDuplicateAtomicOverwriteWritersOrAdHocHttpClientsRemainAcrossExtractedModules()
    {
        var prodDir = FindProductionDirectory();
        string[] projects =
        [
            "EnviousWispr.Core",
            "EnviousWispr.LLM",
            "EnviousWispr.Runtime",
            "EnviousWispr.Storage",
            "EnviousWispr.Services",
        ];

        var duplicateOverwriteMoves = new List<string>();
        var adHocHttpClients = new List<string>();

        foreach (var project in projects)
        {
            var projectRoot = Path.Combine(prodDir, project);
            foreach (var file in EnumerateSourceFiles(projectRoot))
            {
                var fileName = Path.GetFileName(file);
                var text = File.ReadAllText(file);

                if (!string.Equals(fileName, "AtomicFileWriter.cs", StringComparison.Ordinal) &&
                    text.Contains("File.Move(", StringComparison.Ordinal) &&
                    text.Contains("overwrite: true", StringComparison.Ordinal) &&
                    text.Contains(".tmp", StringComparison.Ordinal))
                {
                    duplicateOverwriteMoves.Add($"{project}/{fileName}");
                }

                if (!string.Equals(fileName, "SharedHttpClients.cs", StringComparison.Ordinal) &&
                    (text.Contains("new HttpClient(", StringComparison.Ordinal) ||
                     text.Contains("new HttpClient()", StringComparison.Ordinal) ||
                     text.Contains("new HttpClient ", StringComparison.Ordinal) ||
                     text.Contains("new HttpClientHandler", StringComparison.Ordinal)))
                {
                    adHocHttpClients.Add($"{project}/{fileName}");
                }
            }
        }

        Assert.Empty(duplicateOverwriteMoves);
        Assert.Empty(adHocHttpClients);
    }

    [Fact]
    public void ExtractedProjectsHaveCleanDesktopAndPackageBoundaries()
    {
        var prodDir = FindProductionDirectory();
        var runtimeCsproj = XDocument.Load(Path.Combine(prodDir, "EnviousWispr.Runtime", "EnviousWispr.Runtime.csproj"));
        var storageCsproj = XDocument.Load(Path.Combine(prodDir, "EnviousWispr.Storage", "EnviousWispr.Storage.csproj"));
        var servicesCsproj = XDocument.Load(Path.Combine(prodDir, "EnviousWispr.Services", "EnviousWispr.Services.csproj"));

        foreach (var nonDesktop in new[] { runtimeCsproj, storageCsproj })
        {
            Assert.Empty(nonDesktop.Descendants("UseWindowsForms"));
            Assert.DoesNotContain(
                "Microsoft.WindowsDesktop.App.WPF",
                nonDesktop.Descendants("FrameworkReference").Select(x => x.Attribute("Include")?.Value));
        }

        var servicesPackages = servicesCsproj
            .Descendants("PackageReference")
            .Select(x => x.Attribute("Include")?.Value)
            .ToArray();
        Assert.DoesNotContain("Microsoft.Data.Sqlite", servicesPackages);
        Assert.DoesNotContain("System.Management", servicesPackages);
    }

    private static IEnumerable<string> EnumerateSourceFiles(string directory) =>
        Directory
            .EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
                !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string FindProductionDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EnviousWispr.Windows.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "src", "Production");
    }

    private sealed record SamplePayload(int Version, string Status);

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool Disposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
