using System.Xml.Linq;
using EnviousWispr.Core.Settings;
using EnviousWispr.Presentation;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// Appearance has no Save button, so every choice on it must commit when it is made.
/// </summary>
/// <remarks>
/// A CARD ONLY THE SAVE BUTTON READS IS A CARD THAT DOES NOTHING HERE. Appearance is the one settings
/// page with no Save button, deliberately: its choices take effect on screen the moment they are
/// picked, so asking somebody to confirm a change they can already see reads as the app not trusting
/// its own preview. The consequence is a rule about every control that lands on that page, and moving
/// the recording pill cards there is exactly the move that breaks it - somebody picks a design, sees
/// it, walks away, and it is gone.
/// </remarks>
public sealed class AppearanceCommitTests
{
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void EveryChoiceControlOnRecordingPillAndAppearanceCommitsWhenPicked()
    {
        var markup = XDocument.Load(Path.Combine(
            RepositoryRoot(), "src", "Production", "EnviousWispr.App", "MainWindow.xaml"));
        var recordingPill = markup.Descendants().FirstOrDefault(element =>
            (string?)element.Attribute(XName.Get("Name", XamlNamespace)) == "RecordingPillSection");
        var appearance = markup.Descendants().FirstOrDefault(element =>
            (string?)element.Attribute(XName.Get("Name", XamlNamespace)) == "AppearanceSection");
        Assert.True(recordingPill is not null, "There is no RecordingPillSection to check.");
        Assert.True(appearance is not null, "There is no AppearanceSection to check.");

        var silentCards = recordingPill!.Descendants()
            .Concat(appearance!.Descendants())
            .Where(element => element.Name.LocalName == "RadioButton" &&
                (string?)element.Attribute(XName.Get("Name", XamlNamespace)) is not null &&
                element.Attribute("Checked") is null)
            .Select(element => (string?)element.Attribute(XName.Get("Name", XamlNamespace))!)
            .ToArray();

        Assert.True(
            silentCards.Length == 0,
            "These Recording Pill / Appearance cards have no Checked handler: " + string.Join(", ", silentCards));

        foreach (var buttonName in new[]
                 {
                     "TopPositionSegmentButton",
                     "BottomPositionSegmentButton",
                     "SystemThemeSegmentButton",
                     "LightThemeSegmentButton",
                     "DarkThemeSegmentButton",
                 })
        {
            var button = recordingPill.Descendants()
                .Concat(appearance.Descendants())
                .FirstOrDefault(element => (string?)element.Attribute(XName.Get("Name", XamlNamespace)) == buttonName);
            Assert.True(button is not null, $"{buttonName} was not found.");
            Assert.False(
                string.IsNullOrWhiteSpace((string?)button!.Attribute("Click")),
                $"{buttonName} has no Click handler, so choosing it would not commit.");
        }
    }

    [Fact]
    public async Task ThePillDesignIsAmongTheFieldsAppearanceActuallyWrites()
    {
        // A HANDLER THAT WRITES THE WRONG FIELDS IS THE SAME BUG WEARING A CALLBACK. This used to read
        // the window's source for the three assignments; the write is the presenter's now, so the
        // proof is behaviour: what the window hands over is what reaches the store, all three of it.
        var store = new RecordingStore();
        using var presenter = new SettingsPresenter(store, AppSettings.Default);

        var result = await presenter.SaveAppearanceAsync(
            new AppearanceChoices(AppTheme.Light, OverlayPillPosition.Bottom, RecordingPillDesign.LevelRail));

        Assert.True(result.Saved);
        Assert.Equal(AppTheme.Light, store.Saved!.Preferences.Theme);
        Assert.Equal(OverlayPillPosition.Bottom, store.Saved.Preferences.OverlayPosition);
        Assert.Equal(RecordingPillDesign.LevelRail, store.Saved.Preferences.PillDesignWithoutWords);
    }

    /// <summary>Every choice on Recording Pill and Appearance has a persisted path when picked.</summary>
    [Fact]
    public void EveryRecordingPillCardSharesSingleGroupAndHasAnEnabledDefaultPath()
    {
        var markup = XDocument.Load(Path.Combine(
            RepositoryRoot(), "src", "Production", "EnviousWispr.App", "MainWindow.xaml"));
        var recordingPill = markup.Descendants().First(element =>
            (string?)element.Attribute(XName.Get("Name", XamlNamespace)) == "RecordingPillSection");
        var buttons = recordingPill.Descendants()
            .Where(element => element.Name.LocalName == "RadioButton")
            .ToArray();
        Assert.Equal(3, buttons.Length);

        foreach (var button in buttons)
        {
            Assert.Equal("RecordingPillStyle", (string?)button.Attribute("GroupName"));
            Assert.Equal("PillStyleCardChecked", (string?)button.Attribute("Checked"));
        }

        var appearance = markup.Descendants().First(element =>
            (string?)element.Attribute(XName.Get("Name", XamlNamespace)) == "AppearanceSection");
        Assert.DoesNotContain(appearance.Descendants(), element => element.Name.LocalName == "RadioButton");
    }

    /// <summary>The snapshot the window hands over is built from the controls and is what is handed over.</summary>
    [Fact]
    public void TheWindowHandsOverASnapshotBuiltFromItsThreeControls()
    {
        var appDir = Path.Combine(RepositoryRoot(), "src", "Production", "EnviousWispr.App");
        var matches = Directory.EnumerateFiles(appDir, "MainWindow*.cs")
            .Order()
            .SelectMany(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path)
                .GetRoot()
                .DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Where(method => method.Identifier.ValueText == "PersistAppearanceChoicesAsync"))
            .ToArray();
        Assert.True(matches.Length > 0, "PersistAppearanceChoicesAsync is gone.");
        var persist = Assert.Single(matches);

        var creation = Assert.Single(
            persist!.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>(),
            node => node is ObjectCreationExpressionSyntax { Type: var type } && type.ToString() == nameof(AppearanceChoices));
        Assert.Null(creation.Initializer);
        var arguments = creation.ArgumentList?.Arguments.Select(argument => argument.Expression.ToString()).ToArray();
        Assert.NotNull(arguments);
        Assert.Equal(
            ["ThemeFromIndex(_selectedThemeIndex)", "OverlayPositionFromIndex(_selectedOverlayPositionIndex)", "PillDesignWithoutWordsFromControls()"],
            arguments);

        var declarator = Assert.IsType<VariableDeclaratorSyntax>(creation.Parent?.Parent);
        var local = declarator.Identifier.ValueText;
        var declaration = Assert.IsType<LocalDeclarationStatementSyntax>(declarator.Parent?.Parent);
        Assert.Single(declaration.Declaration.Variables);
        var block = Assert.IsType<BlockSyntax>(declaration.Parent);
        Assert.Same(persist.Body, block);
        var index = block.Statements.IndexOf(declaration);
        Assert.True(index >= 0 && index + 1 < block.Statements.Count, "The snapshot is the last statement of its block; nothing hands it over.");

        var next = Assert.IsType<LocalDeclarationStatementSyntax>(block.Statements[index + 1]);
        var result = Assert.Single(next.Declaration.Variables);
        Assert.Equal(
            $"await _session.Settings.SaveAppearanceAsync({local}).ConfigureAwait(true)",
            result.Initializer?.Value.ToString());

        Assert.Single(
            persist.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation => invocation.Expression.ToString().EndsWith("SaveAppearanceAsync", StringComparison.Ordinal));
    }

    private sealed class RecordingStore : ISettingsStore
    {
        public AppSettings? Saved { get; private set; }

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            Saved = settings;
            return Task.CompletedTask;
        }

        public Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SettingsResetResult> ResetAsync(AppSettings replacement, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
            !File.Exists(Path.Combine(directory.FullName, "EnviousWispr.Windows.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
