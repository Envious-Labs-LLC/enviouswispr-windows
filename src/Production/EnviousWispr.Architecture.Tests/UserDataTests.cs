using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.History;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Settings;
using EnviousWispr.Services.History;
using EnviousWispr.Services.Reliability;
using EnviousWispr.Services.Settings;
using EnviousWispr.Services.UserData;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// "Export my data" and "Delete all EnviousWispr data" (#42): what the export holds and never holds, and
/// what the erasure removes, keeps, and refuses to reach. Every case runs against a real temporary
/// folder, and every expectation is a literal - never a value the code under test produced.
/// </summary>
public sealed class UserDataTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    // ---- Export ----------------------------------------------------------------------------------

    [Fact]
    public async Task TheExportHoldsTheProfileTheHistoryAndTheRecoveredTextAndNothingElse()
    {
        using var temp = new TemporaryDirectory();
        var data = temp.Folder("data");
        const string planted = "PLANTED-SECRET-sk-live-0000";
        // WHAT A DATA FOLDER REALLY HOLDS BESIDE THE STORES, each carrying a marker the export must never
        // contain: a model, a runtime, a diagnostic log, a saved recording, the run state.
        WriteFile(data, @"models\parakeet\encoder.onnx", planted);
        WriteFile(data, @"runtime\llama-server.exe", planted);
        WriteFile(data, @"diagnostics\app.jsonl", planted);
        WriteFile(data, @"audio-archive\take.wav", planted);
        WriteFile(data, "run-state.json", planted);

        var history = new JsonHistoryStore(Path.Combine(data, "history.json"));
        Assert.True((await history.AddAsync(Entry("First synthetic dictation.", Now.AddHours(-2)), 30, Now)).Succeeded);
        Assert.True((await history.AddAsync(Entry("Second synthetic dictation, with émoji 🙂.", Now.AddHours(-1)), 30, Now)).Succeeded);
        using var recovery = new WindowsRecoveryTextStore(Path.Combine(data, "recovery.json"));
        const string recovered = "Synthetic unfinished dictation held for recovery.";
        Assert.True(await recovery.SaveAsync(new RecoveryTextRecord(DictationSessionId.Create(), Now, recovered)));
        // The precondition the decryption claim rests on: the recovery file on disk is NOT readable text.
        Assert.DoesNotContain(recovered, await File.ReadAllTextAsync(Path.Combine(data, "recovery.json")), StringComparison.Ordinal);

        var settings = JsonSettingsStoreTests.CreatePopulatedSettings();
        var destination = Path.Combine(temp.Folder("out"), "my-data.zip");
        var result = await UserDataExportService.ExportAsync(
            settings.ToPortableProfile(), history, 30, recovery, destination, Now);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.HistoryEntries);
        Assert.True(result.IncludedRecoveredText);
        var entries = ReadArchive(destination);
        Assert.Equal(
            ["README.txt", "history.json", "profile.json", "recovered-dictation.json"],
            entries.Keys.Order(StringComparer.Ordinal).ToArray());

        // Readable, through the stores: the history text and the DECRYPTED recovery text are in the file.
        using (var historyJson = JsonDocument.Parse(entries["history.json"]))
        {
            var texts = historyJson.RootElement.GetProperty("entries").EnumerateArray()
                .Select(entry => entry.GetProperty("text").GetString()!)
                .ToArray();
            Assert.Equal(["Second synthetic dictation, with émoji 🙂.", "First synthetic dictation."], texts);
        }

        using (var recoveredJson = JsonDocument.Parse(entries["recovered-dictation.json"]))
        {
            Assert.Equal(recovered, recoveredJson.RootElement.GetProperty("text").GetString());
        }

        // Nothing from beside the stores reached any entry.
        Assert.All(entries.Values, text => Assert.DoesNotContain(planted, text, StringComparison.Ordinal));
        // The machine-local microphone never leaves, as with Export profile.
        Assert.All(entries.Values, text => Assert.DoesNotContain("synthetic-device-id", text, StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destination)!, "*.tmp"));
    }

    [Fact]
    public async Task TheExportedProfileIsTheOneImportProfileTakes()
    {
        using var temp = new TemporaryDirectory();
        var settings = JsonSettingsStoreTests.CreatePopulatedSettings();
        var destination = Path.Combine(temp.Folder("out"), "my-data.zip");

        var result = await UserDataExportService.ExportAsync(
            settings.ToPortableProfile(),
            new JsonHistoryStore(Path.Combine(temp.Folder("data"), "history.json")),
            30,
            new MissingRecovery(),
            destination,
            Now);

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.HistoryEntries);
        Assert.False(result.IncludedRecoveredText);
        var entries = ReadArchive(destination);
        Assert.DoesNotContain("recovered-dictation.json", entries.Keys);
        var extracted = Path.Combine(temp.Path, "profile.json");
        await File.WriteAllTextAsync(extracted, entries["profile.json"]);
        var imported = await new JsonPortableProfileService().ImportAsync(extracted);
        Assert.Equal(PortableProfileImportStatus.Imported, imported.Status);
        Assert.Equal(settings.ToPortableProfile(), imported.Profile);
    }

    [Fact]
    public void NoCredentialStoreIsReachableFromTheExport()
    {
        // A KEY HAS NO ROUTE IN BECAUSE NOTHING THAT HOLDS ONE IS HANDED OVER. Keys live only in Credential
        // Manager, behind IApiKeyStore; the export takes the profile, the history store and the recovery
        // store, and the data-folder test above proves nothing else on disk reaches the file. Asked of the
        // signature itself, so a parameter added later that could carry a key fails here.
        var parameters = typeof(UserDataExportService).GetMethods()
            .Where(method => method.Name == nameof(UserDataExportService.ExportAsync))
            .SelectMany(method => method.GetParameters())
            .ToArray();

        Assert.NotEmpty(parameters);
        Assert.DoesNotContain(parameters, parameter =>
            typeof(EnviousWispr.Core.Credentials.IApiKeyStore).IsAssignableFrom(parameter.ParameterType) ||
            parameter.ParameterType.IsAssignableFrom(typeof(EnviousWispr.Services.Credentials.WindowsCredentialApiKeyStore)));
        Assert.Equal(
            [typeof(PortableProfile), typeof(IHistoryStore), typeof(int), typeof(IRecoveryTextStore), typeof(string), typeof(DateTimeOffset), typeof(CancellationToken)],
            parameters.Select(parameter => parameter.ParameterType).ToArray());
    }

    [Fact]
    public async Task AnUnreadableHistoryFailsTheExportAndLeavesTheChosenFileAsItWas()
    {
        using var temp = new TemporaryDirectory();
        var data = temp.Folder("data");
        await File.WriteAllTextAsync(Path.Combine(data, "history.json"), "{ not json");
        var outFolder = temp.Folder("out");
        var destination = Path.Combine(outFolder, "my-data.zip");
        await File.WriteAllTextAsync(destination, "the person's earlier file");

        var result = await UserDataExportService.ExportAsync(
            JsonSettingsStoreTests.CreatePopulatedSettings().ToPortableProfile(),
            new JsonHistoryStore(Path.Combine(data, "history.json")),
            30,
            new MissingRecovery(),
            destination,
            Now);

        Assert.False(result.Succeeded);
        Assert.Equal("the person's earlier file", await File.ReadAllTextAsync(destination));
        Assert.Equal([destination], Directory.GetFiles(outFolder));
    }

    [Fact]
    public async Task AnExportThatCannotBeWrittenLeavesNoFileAtAll()
    {
        using var temp = new TemporaryDirectory();
        var destination = Path.Combine(temp.Path, "no-such-folder", "my-data.zip");

        var result = await UserDataExportService.ExportAsync(
            JsonSettingsStoreTests.CreatePopulatedSettings().ToPortableProfile(),
            new JsonHistoryStore(Path.Combine(temp.Folder("data"), "history.json")),
            30,
            new MissingRecovery(),
            destination,
            Now);

        Assert.False(result.Succeeded);
        Assert.False(File.Exists(destination));
        Assert.False(Directory.Exists(Path.GetDirectoryName(destination)));
    }

    // ---- Erasure -----------------------------------------------------------------------------------

    [Fact]
    public void ErasureEmptiesNestedFoldersAndReadOnlyFilesAndKeepsTheRoot()
    {
        using var temp = new TemporaryDirectory();
        var root = temp.Folder("data");
        WriteFile(root, "settings.json", "{}");
        WriteFile(root, @"models\parakeet\versions\1\encoder.onnx", "weights");
        WriteFile(root, @"models\parakeet\versions\1\decoder.onnx", "weights");
        WriteFile(root, @"diagnostics\app.jsonl", "{}");
        var readOnly = WriteFile(root, "history.json", "{}");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        var hidden = WriteFile(root, ".history.json.tmp", "x");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        Assert.True(File.GetAttributes(readOnly).HasFlag(FileAttributes.ReadOnly), "the read-only precondition landed");

        var report = DataDirectoryEraser.Erase(root);

        // 6 files, and 5 folders: models, parakeet, versions, 1, diagnostics.
        Assert.Equal(new DataDeletionReport(Removed: 11, Remaining: 0, Refused: 0), report);
        Assert.True(report.Complete);
        Assert.True(Directory.Exists(root));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root, "*", new EnumerationOptions { AttributesToSkip = 0, RecurseSubdirectories = true }));
    }

    [Fact]
    public void AJunctionPointingOutsideTheRootIsRemovedAndItsTargetSurvives()
    {
        using var temp = new TemporaryDirectory();
        var root = temp.Folder("data");
        var outside = temp.Folder("somebody-elses-folder");
        var precious = WriteFile(outside, @"nested\precious.txt", "keep me");
        var link = Path.Combine(root, "models");
        CreateJunction(link, outside);
        var nestedLink = Path.Combine(temp.Folder(@"data\runtime"), "cuda");
        CreateJunction(nestedLink, outside);
        WriteFile(root, "settings.json", "{}");

        var report = DataDirectoryEraser.Erase(root);

        Assert.True(report.Complete);
        Assert.Equal(0, report.Remaining);
        Assert.False(Directory.Exists(link));
        Assert.False(Directory.Exists(nestedLink));
        Assert.Equal("keep me", File.ReadAllText(precious));
        Assert.Equal(
            [precious],
            Directory.GetFiles(outside, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void ARootThatIsItselfAJunctionIsRefusedAndNothingBehindItIsTouched()
    {
        using var temp = new TemporaryDirectory();
        var elsewhere = temp.Folder("real-folder");
        var precious = WriteFile(elsewhere, "precious.txt", "keep me");
        var root = Path.Combine(temp.Path, "data");
        CreateJunction(root, elsewhere);

        var report = DataDirectoryEraser.Erase(root);

        Assert.False(report.Complete);
        Assert.Equal(1, report.Refused);
        Assert.Equal(0, report.Removed);
        Assert.Equal("keep me", File.ReadAllText(precious));
    }

    [Fact]
    public void ASiblingWhoseNameStartsWithTheRootsIsNeverReached()
    {
        using var temp = new TemporaryDirectory();
        var root = temp.Folder("EnviousWispr");
        WriteFile(root, "settings.json", "{}");
        var sibling = WriteFile(temp.Folder("EnviousWispr-Backup"), "settings.json", "sibling");

        var report = DataDirectoryEraser.Erase(Path.Combine(temp.Path, "EnviousWispr", "..", "EnviousWispr") + Path.DirectorySeparatorChar);

        Assert.True(report.Complete);
        Assert.Equal(1, report.Removed);
        Assert.Equal("sibling", File.ReadAllText(sibling));
    }

    [Theory]
    [InlineData(@"C:\Data\EnviousWispr", @"C:\Data\EnviousWispr\settings.json", true)]
    [InlineData(@"C:\Data\EnviousWispr", @"C:\Data\EnviousWispr\models\a\b.onnx", true)]
    [InlineData(@"C:\Data\EnviousWispr\", @"C:\data\enviouswispr\Settings.json", true)]
    [InlineData(@"C:\Data\EnviousWispr", @"C:\Data\EnviousWispr", false)]
    [InlineData(@"C:\Data\EnviousWispr", @"C:\Data\EnviousWispr\", false)]
    [InlineData(@"C:\Data\EnviousWispr", @"C:\Data\EnviousWispr\..\Other\file.txt", false)]
    [InlineData(@"C:\Data\EnviousWispr", @"C:\Data\EnviousWispr\models\..\..\Other", false)]
    [InlineData(@"C:\Data\EnviousWispr", @"C:\Data\EnviousWispr-Backup\settings.json", false)]
    [InlineData(@"C:\Data\EnviousWispr", @"C:\Data\EnviousWisprX", false)]
    [InlineData(@"C:\Data\EnviousWispr", @"D:\Data\EnviousWispr\settings.json", false)]
    [InlineData(@"C:\Data\EnviousWispr", @"C:\Data", false)]
    public void OnlyAPathStrictlyInsideTheRootIsInside(string root, string candidate, bool inside)
    {
        Assert.Equal(inside, DataDirectoryEraser.IsStrictlyInside(root, candidate));
    }

    [Fact]
    public void ALockedFileIsCountedNotClaimedAndTheNextLaunchIsToldOnce()
    {
        using var temp = new TemporaryDirectory();
        var root = temp.Folder("data");
        WriteFile(root, "settings.json", "{}");
        var locked = WriteFile(root, @"models\encoder.onnx", "weights");

        DataDeletionReport report;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            report = DataDirectoryEraser.Erase(root);
        }

        // The locked file and the folder that could not be removed around it.
        Assert.False(report.Complete);
        Assert.Equal(2, report.Remaining);
        Assert.Equal(1, report.Removed);
        Assert.True(File.Exists(locked));

        DataDirectoryEraser.WriteLeftover(root, new DataDeletionLeftover(report.Remaining, CredentialsRemaining: true));
        var note = File.ReadAllText(Path.Combine(root, DataDirectoryEraser.LeftoverFileName));
        Assert.DoesNotContain(root, note, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("encoder", note, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(new DataDeletionLeftover(2, true), DataDirectoryEraser.TakeLeftover(root));
        Assert.Null(DataDirectoryEraser.TakeLeftover(root));

        // Once the handle is gone, a second walk finishes the job.
        var second = DataDirectoryEraser.Erase(root);
        Assert.True(second.Complete);
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public void AMissingRootIsNothingToDeleteAndNotAFailure()
    {
        using var temp = new TemporaryDirectory();

        var report = DataDirectoryEraser.Erase(Path.Combine(temp.Path, "never-created"));

        Assert.Equal(new DataDeletionReport(0, 0, 0), report);
        Assert.Null(DataDirectoryEraser.TakeLeftover(Path.Combine(temp.Path, "never-created")));
    }

    // ---- Helpers -----------------------------------------------------------------------------------

    private static DictationHistoryEntry Entry(string text, DateTimeOffset at) =>
        DictationHistoryEntry.Create(at, text, "parakeet", wasPolished: false, wasDelivered: true);

    private static Dictionary<string, string> ReadArchive(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        return archive.Entries.ToDictionary(
            entry => entry.FullName,
            entry =>
            {
                using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                return reader.ReadToEnd();
            },
            StringComparer.Ordinal);
    }

    private static string WriteFile(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>A directory junction, which Windows lets an ordinary user create; asserted to exist as a link before use.</summary>
    private static void CreateJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, "mklink /J failed: " + process.StandardError.ReadToEnd());
        Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint), "the junction precondition landed");
    }

    private sealed class MissingRecovery : IRecoveryTextStore
    {
        public Task<RecoveryTextLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RecoveryTextLoadResult(RecoveryTextLoadStatus.Missing));

        public Task<bool> SaveAsync(RecoveryTextRecord record, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> ClearAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// A folder of this test's own. Links inside it are removed as links first, so the cleanup never
    /// depends on how a recursive delete treats a junction.
    /// </summary>
    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "EnviousWispr.Tests", "user-data-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Folder(string relative)
        {
            var folder = System.IO.Path.Combine(Path, relative);
            Directory.CreateDirectory(folder);
            return folder;
        }

        public void Dispose()
        {
            RemoveLinks(Path);
            if (Directory.Exists(Path))
            {
                foreach (var file in Directory.GetFiles(Path, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(Path, recursive: true);
            }
        }

        private static void RemoveLinks(string directory)
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            foreach (var child in new DirectoryInfo(directory).EnumerateDirectories("*", new EnumerationOptions { AttributesToSkip = 0 }))
            {
                if (child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    Directory.Delete(child.FullName, recursive: false);
                }
                else
                {
                    RemoveLinks(child.FullName);
                }
            }
        }
    }
}
