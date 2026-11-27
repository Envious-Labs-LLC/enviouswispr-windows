using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using EnviousWispr.Core.Settings;
using EnviousWispr.PostProcessing;
using EnviousWispr.Services.Settings;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EnviousWispr.Architecture.Tests;

public sealed class YourWordsScaleAndStorageTests
{
    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void DictionaryAndSnippetsListsVirtualizeInsideBoundedGridViewportsWithoutItemTransitions()
    {
        var root = FindRepositoryRoot();
        var doc = XDocument.Load(Path.Combine(root, "src", "Production", "EnviousWispr.App", "MainWindow.xaml"));

        foreach (var pageName in new[] { "DictionaryPage", "SnippetsPage" })
        {
            var page = FindNamed(doc, pageName);
            Assert.Equal("Grid", page.Name.LocalName);
            Assert.DoesNotContain(
                page.Ancestors(),
                ancestor => ancestor.Name.LocalName == "ScrollViewer");
        }

        foreach (var listName in new[] { "DictionaryList", "SnippetList" })
        {
            var listView = FindNamed(doc, listName);
            Assert.DoesNotContain(
                listView.Ancestors(),
                ancestor => ancestor.Name.LocalName == "ScrollViewer");

            var panel = listView.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "ItemsStackPanel");
            Assert.NotNull(panel);

            var transitions = listView.Elements()
                .Single(e => e.Name.LocalName == "ListView.ItemContainerTransitions")
                .Elements()
                .Single(e => e.Name.LocalName == "TransitionCollection");
            Assert.Empty(transitions.Elements());
        }
    }

    [Fact]
    public void VocabularyAndSnippetMutationsDoNotResetWholeSettingsOrRebuildTwice()
    {
        var root = FindRepositoryRoot();
        var appDir = Path.Combine(root, "src", "Production", "EnviousWispr.App");

        var vocabularyMethods = new[]
        {
            ("MainWindow.Dictionary.cs", "CommitVocabularyAsync"),
            ("MainWindow.WordsAndSnippets.cs", "ShowWordEditorDialogAsync"),
            ("MainWindow.WordsAndSnippets.cs", "ShowSnippetEditorDialogAsync"),
            ("MainWindow.Snippets.cs", "ReviewSnippetImportAsync"),
            ("MainWindow.WordsFromApps.cs", "LoadAndReviewWordsAsync"),
        };

        foreach (var (fileName, methodName) in vocabularyMethods)
        {
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(appDir, fileName)), path: fileName);
            var methods = tree.GetRoot().DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Where(m => m.Identifier.ValueText == methodName)
                .ToArray();
            Assert.NotEmpty(methods);

            foreach (var method in methods)
            {
                var invocations = method.DescendantNodes()
                    .OfType<InvocationExpressionSyntax>()
                    .Select(i => i.Expression.ToString())
                    .ToArray();
                Assert.DoesNotContain(invocations, name => name.EndsWith("ApplySettingsToControls", StringComparison.Ordinal));
                Assert.DoesNotContain(invocations, name => name.EndsWith("RefreshReusableUserDataViews", StringComparison.Ordinal));
            }
        }

        var packCardTree = CSharpSyntaxTree.ParseText(
            File.ReadAllText(Path.Combine(appDir, "MainWindow.Dictionary.cs")),
            path: "MainWindow.Dictionary.cs");
        var packMethod = packCardTree.GetRoot().DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "BuildVocabularyPackCard");
        var packCalls = packMethod.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(i => i.Expression.ToString())
            .ToArray();
        Assert.DoesNotContain(packCalls, name => name.EndsWith("ApplySettingsToControls", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SplitJsonSettingsStoreMigratesMonolithicSettingsAndIsolatesDeltaWrites()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var settingsPath = Path.Combine(directory, "settings.json");
            var userDataPath = Path.Combine(directory, "user-data.json");

            // 1. Seed a legacy monolithic settings file (only settings.json exists).
            var initialWords = Enumerable.Range(0, 200)
                .Select(i => new CustomWordEntry($"spoken{i}", $"Canonical{i}"))
                .ToArray();
            var initialSnippets = Enumerable.Range(0, 50)
                .Select(i => new SnippetEntry($"trig {i}", $"body {i}"))
                .ToArray();
            var legacyMonolithic = AppSettings.Default with
            {
                UserData = new ReusableUserData(
                    initialWords,
                    initialSnippets,
                    snippetKeyword: "slash",
                    enabledVocabularyPackIds: ["tech"],
                    hasSeededStarterSnippets: true),
            };
            await new JsonSettingsStore(settingsPath).SaveAsync(legacyMonolithic);
            Assert.False(File.Exists(userDataPath));

            // 2. Load via split store -> recovers full userData from legacy monolithic settings.json.
            var splitStore = new JsonSettingsStore(settingsPath, userDataPath);
            var loaded = await splitStore.LoadAsync();
            Assert.Equal(SettingsLoadStatus.Loaded, loaded.Status);
            Assert.Equal(200, loaded.Settings.UserData.CustomWords.Count);
            Assert.Equal(50, loaded.Settings.UserData.Snippets.Count);

            // 3. First save migrates cleanly: writes user-data.json first and slims settings.json.
            var nextSettings = loaded.Settings with { LaunchCount = 1 };
            await splitStore.SaveAsync(nextSettings);
            Assert.True(File.Exists(userDataPath));

            var slimNode = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!;
            Assert.Empty(slimNode["userData"]!["customWords"]!.AsArray());
            Assert.Empty(slimNode["userData"]!["snippets"]!.AsArray());

            var reloaded = await new JsonSettingsStore(settingsPath, userDataPath).LoadAsync();
            Assert.Equal(200, reloaded.Settings.UserData.CustomWords.Count);
            Assert.Equal(50, reloaded.Settings.UserData.Snippets.Count);
            Assert.Equal("slash", reloaded.Settings.UserData.SnippetKeyword);
            Assert.Equal(["tech"], reloaded.Settings.UserData.EnabledVocabularyPackIds);

            // 4. Updating ONLY preferences leaves user-data.json untouched.
            var userDataWriteTimeBefore = File.GetLastWriteTimeUtc(userDataPath);
            await Task.Delay(35);
            var prefChanged = reloaded.Settings with { LaunchCount = 2 };
            await splitStore.SaveAsync(prefChanged);
            Assert.Equal(userDataWriteTimeBefore, File.GetLastWriteTimeUtc(userDataPath));

            // 5. Updating ONLY userData leaves settings.json untouched.
            var settingsWriteTimeBefore = File.GetLastWriteTimeUtc(settingsPath);
            await Task.Delay(35);
            var wordAdded = prefChanged with
            {
                UserData = prefChanged.UserData.WithCustomWords(
                    [.. prefChanged.UserData.CustomWords, new CustomWordEntry("extraone", "ExtraOne")]),
            };
            await splitStore.SaveAsync(wordAdded);
            Assert.Equal(settingsWriteTimeBefore, File.GetLastWriteTimeUtc(settingsPath));

            var finalReload = await new JsonSettingsStore(settingsPath, userDataPath).LoadAsync();
            Assert.Equal(201, finalReload.Settings.UserData.CustomWords.Count);
            Assert.Equal(2, finalReload.Settings.LaunchCount);

            // 6. Reset backs up both files and restores cleanly.
            var reset = await splitStore.ResetAsync(AppSettings.FreshInstall);
            Assert.True(reset.PreservedPreviousData);
            Assert.True(File.Exists(settingsPath + ".previous"));
            Assert.True(File.Exists(userDataPath + ".previous"));
            var afterReset = await splitStore.LoadAsync();
            Assert.Empty(afterReset.Settings.UserData.CustomWords);
            Assert.Equal(SnippetStarters.All.Count, afterReset.Settings.UserData.Snippets.Count);
        });
    }

    [Fact]
    public async Task SplitJsonSettingsStoreReportsInvalidWhenUserDataFileIsCorrupt()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var settingsPath = Path.Combine(directory, "settings.json");
            var userDataPath = Path.Combine(directory, "user-data.json");
            var splitStore = new JsonSettingsStore(settingsPath, userDataPath);
            await splitStore.SaveAsync(AppSettings.Default);
            await File.WriteAllTextAsync(userDataPath, "{corrupt-json");

            var reloaded = await splitStore.LoadAsync();
            Assert.Equal(SettingsLoadStatus.Invalid, reloaded.Status);
        });
    }

    [Fact]
    public async Task SplitJsonSettingsStoreRoundTripsFiveThousandWordsAndOneThousandSnippetsFast()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var settingsPath = Path.Combine(directory, "settings.json");
            var userDataPath = Path.Combine(directory, "user-data.json");
            var store = new JsonSettingsStore(settingsPath, userDataPath);

            var words = Enumerable.Range(0, 5_000)
                .Select(i => new CustomWordEntry($"spokenword{i}", $"CanonicalWord{i}"))
                .ToArray();
            var snippets = Enumerable.Range(0, 1_000)
                .Select(i => new SnippetEntry($"trigger {i}", $"Expanded snippet body number {i}"))
                .ToArray();
            var bigSettings = AppSettings.Default with
            {
                UserData = new ReusableUserData(words, snippets, hasSeededStarterSnippets: true),
            };

            await store.SaveAsync(bigSettings);
            var sw = Stopwatch.StartNew();
            var loaded = await new JsonSettingsStore(settingsPath, userDataPath).LoadAsync();
            sw.Stop();

            Assert.Equal(SettingsLoadStatus.Loaded, loaded.Status);
            Assert.Equal(5_000, loaded.Settings.UserData.CustomWords.Count);
            Assert.Equal(1_000, loaded.Settings.UserData.Snippets.Count);
            Assert.True(sw.ElapsedMilliseconds < 2_000, $"Expected fast load, took {sw.ElapsedMilliseconds} ms.");
        });
    }

    [Fact]
    public void IndexedCustomWordCorrectorPreservesHyphenatedUnicodeAndPhraseParityAndScalesFlat()
    {
        CustomWordEntry[] words =
        [
            new CustomWordEntry("foo", "Foo"),
            new CustomWordEntry("bar-foo", "BarFoo"),
            new CustomWordEntry("south bae", "South Bay", MatchStrictness.Loose),
            new CustomWordEntry("naïve café", "Naïve Café"),
            new CustomWordEntry("kubernetes", "Kubernetes"),
        ];

        var sample = "Use foo and bar-foo near south bae at naïve café or kubernetis today.";
        var corrected = CustomWordCorrector.Correct(sample, words);
        Assert.Equal("Use Foo and BarFoo near South Bay at Naïve Café or Kubernetes today.", corrected.Text);
        Assert.Equal(5, corrected.ReplacementCount);

        // 5,000-word scale benchmark
        var largeVocabulary = Enumerable.Range(0, 5_000)
            .Select(i => new CustomWordEntry($"mishearingterm{i}", $"CanonicalTerm{i}"))
            .Append(new CustomWordEntry("bar-baz", "BarBaz"))
            .Append(new CustomWordEntry("envious wispr", "EnviousWispr"))
            .ToArray();

        var input = "Please check mishearingterm4222 and bar-baz with mishearingterm1099 and envious wispr asap.";
        // Warm up index cache
        var warmup = CustomWordCorrector.Correct(input, largeVocabulary);
        Assert.Contains("CanonicalTerm4222", warmup.Text, StringComparison.Ordinal);
        Assert.Contains("BarBaz", warmup.Text, StringComparison.Ordinal);
        Assert.Contains("EnviousWispr", warmup.Text, StringComparison.Ordinal);

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++)
        {
            _ = CustomWordCorrector.Correct(input, largeVocabulary);
        }

        sw.Stop();
        var averageMs = sw.Elapsed.TotalMilliseconds / 20.0;
        Assert.True(averageMs < 50.0, $"Expected sub-50 ms per dictation at 5,000 words, measured {averageMs:F2} ms.");
    }

    [Fact]
    public void SnippetExpanderHandlesOneThousandSnippetsWithZeroRegression()
    {
        var snippets = Enumerable.Range(0, 1_000)
            .Select(i => new SnippetEntry($"trigger {i}", $"expanded value {i}"))
            .Append(new SnippetEntry("my email", "me@example.com"))
            .ToArray();
        var vocabulary = new SnippetVocabulary(snippets, "backslash");
        var values = new SnippetDynamicValues(
            new DateTimeOffset(2026, 11, 27, 9, 0, 0, TimeSpan.Zero),
            System.Globalization.CultureInfo.InvariantCulture,
            TimeZoneInfo.Utc,
            string.Empty);
        var expander = new SnippetExpander();

        var noSnippet = expander.Expand("Just an ordinary sentence without any keyword trigger.", vocabulary, values);
        Assert.False(noSnippet.DidFire);
        Assert.Equal("Just an ordinary sentence without any keyword trigger.", noSnippet.Text);

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 50; i++)
        {
            var outcome = expander.Expand("Please send backslash trigger 888 and backslash my email now.", vocabulary, values);
            Assert.Equal(2, outcome.Records.Count);
        }

        sw.Stop();
        Assert.True(sw.Elapsed.TotalMilliseconds < 1_000, $"Expected fast 1,000-snippet expansion, took {sw.Elapsed.TotalMilliseconds:F1} ms.");
    }

    private static XElement FindNamed(XContainer container, string name) =>
        container.Descendants().Single(element =>
            string.Equals((string?)element.Attribute(XamlNamespace + "Name"), name, StringComparison.Ordinal));

    private static string FindRepositoryRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "EnviousWispr.Windows.slnx")))
        {
            directory = Directory.GetParent(directory)?.FullName;
        }

        return directory ?? throw new InvalidOperationException("Repository root not found.");
    }
}
