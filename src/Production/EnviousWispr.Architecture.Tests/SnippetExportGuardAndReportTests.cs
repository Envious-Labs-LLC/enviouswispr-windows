using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Settings;
using EnviousWispr.Services.Diagnostics;
using EnviousWispr.Services.UserData;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// An export never lands in the app's own data folder, however the path is spelled; and a snippet import is logged
/// as counts and categories that survive to the disk and are dropped whole when out of range.
/// </summary>
public sealed class SnippetExportGuardAndReportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ew-guard-" + Guid.NewGuid().ToString("N"));
    private readonly string _data;

    public SnippetExportGuardAndReportTests()
    {
        _data = Path.Combine(_root, "EnviousWispr-Data-Folder");
        Directory.CreateDirectory(_data);
        Directory.CreateDirectory(_data + "-other");
        File.WriteAllText(Path.Combine(_data, "settings.json"), "{}");
    }

    [Fact]
    public void TheSettingsFileAndAnythingInTheDataFolderAreRefusedHoweverTheyAreSpelled()
    {
        Assert.True(ExportDestinationGuard.IsInsideDataDirectory(Path.Combine(_data, "settings.json"), _data));
        Assert.True(ExportDestinationGuard.IsInsideDataDirectory(_data, _data));
        Assert.True(ExportDestinationGuard.IsInsideDataDirectory(Path.Combine(_data.ToUpperInvariant(), "SETTINGS.JSON"), _data));
        Assert.True(ExportDestinationGuard.IsInsideDataDirectory(_data + @"\sub\..\settings.json", _data));
        Assert.True(ExportDestinationGuard.IsInsideDataDirectory(Path.Combine(_data, "not-yet", "EnviousWispr Snippets.json"), _data));
        Assert.True(ExportDestinationGuard.IsInsideDataDirectory(Path.Combine(_data, "settings.json"), _data + @"\"));
    }

    [Fact]
    public void ASiblingFolderWithTheSamePrefixAndThePlacesAroundItAreAllowed()
    {
        Assert.False(ExportDestinationGuard.IsInsideDataDirectory(Path.Combine(_data + "-other", "settings.json"), _data));
        Assert.False(ExportDestinationGuard.IsInsideDataDirectory(Path.Combine(_root, "EnviousWispr Snippets.json"), _data));
        Assert.False(ExportDestinationGuard.IsInsideDataDirectory(_data + ".json", _data));
    }

    [Fact]
    public void AJunctionIntoTheDataFolderIsSeenThrough()
    {
        var link = Path.Combine(_root, "shortcut");
        var made = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{_data}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;
        made.WaitForExit();

        // THE PRECONDITION IS ASSERTED: without a real junction this would test an ordinary missing folder.
        Assert.True(new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint), "mklink /J did not create a junction");
        Assert.True(File.Exists(Path.Combine(link, "settings.json")));
        Assert.True(ExportDestinationGuard.IsInsideDataDirectory(Path.Combine(link, "settings.json"), _data));
        Assert.True(ExportDestinationGuard.IsInsideDataDirectory(Path.Combine(_data, "settings.json"), link));
    }

    [Fact]
    public void AnEightDotThreeShortNameIsSeenThroughWhereTheVolumeHasOne()
    {
        var buffer = new char[1024];
        var length = GetShortPathNameW(_data, buffer, (uint)buffer.Length);
        var shortName = new string(buffer, 0, (int)length);
        if (length == 0 || string.Equals(shortName, _data, StringComparison.OrdinalIgnoreCase))
        {
            // This volume keeps no short names, so there is no second spelling to test; nothing here can pass for it.
            return;
        }

        Assert.True(ExportDestinationGuard.IsInsideDataDirectory(Path.Combine(shortName, "settings.json"), _data));
        Assert.True(ExportDestinationGuard.IsInsideDataDirectory(Path.Combine(_data, "settings.json"), shortName));
    }

    // ---- The import report ----------------------------------------------------------------------------------

    [Fact]
    public void AReviewIsCountedFromItsRowsAsTheyStoodWhenItEnded()
    {
        var rows = SnippetImportReview.BuildRows(
            [
                new SnippetImportCandidate("my email", "a"),
                new SnippetImportCandidate("sig", "b"),
                new SnippetImportCandidate("SIG", "c"),
                new SnippetImportCandidate("phone", "d"),
                new SnippetImportCandidate("zoom", "e"),
            ],
            [new SnippetEntry("My Email", "mine")]).ToArray();
        rows[3] = rows[3].WithAdd(false);

        var report = DiagnosticSnippetImport.ForReview(
            DiagnosticSnippetImportSource.FileCsv, DiagnosticSnippetImportOutcome.Completed, excluded: 2, rows, added: 2);

        Assert.Equal(
            new DiagnosticSnippetImport(DiagnosticSnippetImportSource.FileCsv, DiagnosticSnippetImportOutcome.Completed, null, 5, 2, 1, 1, 1, 2),
            report);
    }

    [Theory]
    [InlineData("paste", DiagnosticSnippetImportSource.Paste)]
    [InlineData("file_json", DiagnosticSnippetImportSource.FileJson)]
    [InlineData(".CSV", DiagnosticSnippetImportSource.FileCsv)]
    [InlineData(".md", DiagnosticSnippetImportSource.FileText)]
    [InlineData("wispr_flow", DiagnosticSnippetImportSource.WisprFlow)]
    [InlineData(".docx", DiagnosticSnippetImportSource.FileOther)]
    public void EverySourceIsAClosedCategoryNeverAName(string source, DiagnosticSnippetImportSource expected) =>
        Assert.Equal(expected, DiagnosticSnippetImport.SourceFor(source));

    [Fact]
    public void AReportReachesTheDiskAsCountsAndNothingElse()
    {
        var report = new DiagnosticSnippetImport(
            DiagnosticSnippetImportSource.Paste, DiagnosticSnippetImportOutcome.Failed, SnippetImportFailure.Malformed, Excluded: 3);
        var record = PrivacySafeDiagnosticRecord.From(new AppLogEntry(
            DateTimeOffset.UnixEpoch, AppEventCode.SnippetsImported, AppFailureCategory.InvalidData, SnippetImport: report));

        var line = JsonLineFileLogger.Serialize(LocalDiagnosticLine.From(record, null));

        Assert.Equal(report, record.SnippetImport);
        Assert.Contains(
            "\"snippetImport\":{\"source\":\"Paste\",\"outcome\":\"Failed\",\"failure\":\"Malformed\",\"candidates\":0,\"added\":0,\"skippedExisting\":0,\"skippedDuplicateBatch\":0,\"skippedUnticked\":0,\"excluded\":3}",
            line,
            StringComparison.Ordinal);
        Assert.True(JsonLineFileLogger.TryParseRecord(line, out var parsed));
        Assert.Equal(report, parsed!.SnippetImport);
    }

    [Fact]
    public void AnOutOfRangeReportIsDroppedWholeAndARefusedLineIsNotRead()
    {
        foreach (var bad in new[]
        {
            new DiagnosticSnippetImport(DiagnosticSnippetImportSource.Paste, DiagnosticSnippetImportOutcome.Completed, Added: -1),
            new DiagnosticSnippetImport(DiagnosticSnippetImportSource.Paste, DiagnosticSnippetImportOutcome.Completed, Candidates: 100_001),
            new DiagnosticSnippetImport((DiagnosticSnippetImportSource)97, DiagnosticSnippetImportOutcome.Completed),
            new DiagnosticSnippetImport(DiagnosticSnippetImportSource.Paste, (DiagnosticSnippetImportOutcome)97),
            new DiagnosticSnippetImport(DiagnosticSnippetImportSource.Paste, DiagnosticSnippetImportOutcome.Failed, (SnippetImportFailure)97),
        })
        {
            var record = PrivacySafeDiagnosticRecord.From(new AppLogEntry(
                DateTimeOffset.UnixEpoch, AppEventCode.SnippetsImported, SnippetImport: bad));
            Assert.Null(record.SnippetImport);
        }

        Assert.NotNull(PrivacySafeDiagnosticRecord.From(new AppLogEntry(
            DateTimeOffset.UnixEpoch,
            AppEventCode.SnippetsImported,
            SnippetImport: new DiagnosticSnippetImport(DiagnosticSnippetImportSource.Paste, DiagnosticSnippetImportOutcome.Completed, Candidates: 100_000))).SnippetImport);

        const string Tampered =
            "{\"timestamp\":\"2026-09-26T00:00:00+00:00\",\"event\":\"SnippetsImported\",\"failure\":\"None\",\"snippetImport\":{\"source\":\"Paste\",\"outcome\":97,\"candidates\":1}}";
        const string Negative =
            "{\"timestamp\":\"2026-09-26T00:00:00+00:00\",\"event\":\"SnippetsImported\",\"failure\":\"None\",\"snippetImport\":{\"source\":\"Paste\",\"outcome\":\"Completed\",\"added\":-4}}";
        const string Smuggled =
            "{\"timestamp\":\"2026-09-26T00:00:00+00:00\",\"event\":\"SnippetsImported\",\"failure\":\"None\",\"snippetImport\":{\"source\":\"Paste\",\"outcome\":\"Completed\",\"trigger\":\"my email\"}}";
        Assert.False(JsonLineFileLogger.TryParseRecord(Tampered, out _));
        Assert.False(JsonLineFileLogger.TryParseRecord(Negative, out _));
        Assert.False(JsonLineFileLogger.TryParseRecord(Smuggled, out _));
    }

    public void Dispose()
    {
        var link = Path.Combine(_root, "shortcut");
        if (Directory.Exists(link))
        {
            Directory.Delete(link); // removes the junction only, never what it points at
        }

        File.Delete(Path.Combine(_data, "settings.json"));
        Directory.Delete(_data);
        Directory.Delete(_data + "-other");
        Directory.Delete(_root);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, [Out] char[] shortPath, uint length);
}
