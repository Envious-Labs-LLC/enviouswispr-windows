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
    public void EveryChoiceCardOnAppearanceCommitsWhenItIsPicked()
    {
        var markup = XDocument.Load(Path.Combine(
            RepositoryRoot(), "src", "Production", "EnviousWispr.App", "MainWindow.xaml"));
        var appearance = markup.Descendants().FirstOrDefault(element =>
            (string?)element.Attribute(XName.Get("Name", XamlNamespace)) == "AppearanceSection");
        Assert.True(appearance is not null, "There is no AppearanceSection to check.");

        var silent = appearance!.Descendants()
            .Where(element => element.Name.LocalName == "RadioButton" &&
                (string?)element.Attribute(XName.Get("Name", XamlNamespace)) is not null &&
                element.Attribute("Checked") is null)
            .Select(element => (string?)element.Attribute(XName.Get("Name", XamlNamespace))!)
            .ToArray();

        Assert.True(
            silent.Length == 0,
            "These Appearance cards have no Checked handler, and Appearance has no Save button, so "
                + "picking one changes the screen and nothing else: " + string.Join(", ", silent));
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

    /// <summary>Every choice on Appearance has a field in the snapshot the window hands the presenter.</summary>
    /// <remarks>
    /// THE WINDOW BUILDS THE SNAPSHOT AND THE PRESENTER WRITES IT, so a card added to Appearance and
    /// wired to the handler is still lost if the snapshot has no field for it. Every radio group on
    /// the page must be mapped to a field, with one named exception: the Live Preview pill has one
    /// design today, declared as one plain card rather than through a template - ONE DECLARATION IN
    /// A TEMPLATE IS ANY NUMBER OF CARDS, which is how the theme and position groups are built, so
    /// "one RadioButton in the markup" is not an exemption anything else can claim.
    /// </remarks>
    [Fact]
    public void EveryAppearanceChoiceHasAFieldInTheSnapshotTheWindowHandsOver()
    {
        var markup = XDocument.Load(Path.Combine(
            RepositoryRoot(), "src", "Production", "EnviousWispr.App", "MainWindow.xaml"));
        var appearance = markup.Descendants().First(element =>
            (string?)element.Attribute(XName.Get("Name", XamlNamespace)) == "AppearanceSection");
        var buttons = appearance.Descendants()
            .Where(element => element.Name.LocalName == "RadioButton")
            .ToArray();
        var groups = buttons
            .GroupBy(element => (string?)element.Attribute("GroupName") ?? "(no group)")
            .ToDictionary(group => group.Key, group => group.ToArray());

        var fieldByGroup = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PillTheme"] = nameof(AppearanceChoices.Theme),
            ["PillOverlayPosition"] = nameof(AppearanceChoices.OverlayPosition),
            ["PillWithoutWords"] = nameof(AppearanceChoices.PillDesignWithoutWords),
        };
        const string fixedGroup = "PillWithWords";
        const string fixedCard = "ReadingWellPillButton";
        var fields = typeof(AppearanceChoices).GetConstructors().Single().GetParameters()
            .Select(parameter => parameter.Name!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (group, cards) in groups)
        {
            if (group == fixedGroup)
            {
                var card = Assert.Single(cards);
                Assert.Equal(fixedCard, (string?)card.Attribute(XName.Get("Name", XamlNamespace)));
                Assert.False(
                    card.Ancestors().Any(ancestor => ancestor.Name.LocalName == "DataTemplate"),
                    $"{fixedCard} is declared through a template now, so it may be any number of cards and needs a field.");
                continue;
            }

            Assert.True(
                fieldByGroup.TryGetValue(group, out var field),
                $"The {group} group on Appearance has no field in the snapshot the window hands over, so its choice is lost.");
            Assert.True(fields.Contains(field!), $"The {group} choice maps to {field}, which the snapshot no longer carries.");
        }

        Assert.Equal(fieldByGroup.Values.Order(), fields.Order());
        Assert.True(groups.ContainsKey(fixedGroup), $"The {fixedGroup} group is gone; drop its exemption here.");
    }

    /// <summary>The snapshot the window hands over is built from the controls and is what is handed over.</summary>
    /// <remarks>
    /// READING THE CONTROLS IS NOT ENOUGH; THE VALUES READ HAVE TO BE THE ONES SUBMITTED. Three reads
    /// into unused locals and a snapshot of defaults would reach the presenter and be written
    /// faithfully. So the snapshot's constructor arguments are inspected one by one, and the call
    /// that hands it over must be given that very snapshot.
    /// </remarks>
    [Fact]
    public void TheWindowHandsOverASnapshotBuiltFromItsThreeControls()
    {
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Production", "EnviousWispr.App", "MainWindow.xaml.cs")));
        var persist = tree.GetRoot().DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(method => method.Identifier.ValueText == "PersistAppearanceChoicesAsync");
        Assert.True(persist is not null, "PersistAppearanceChoicesAsync is gone.");

        var creation = Assert.Single(
            persist!.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>(),
            node => node is ObjectCreationExpressionSyntax { Type: var type } && type.ToString() == nameof(AppearanceChoices));
        var arguments = creation.ArgumentList?.Arguments.Select(argument => argument.Expression.ToString()).ToArray();
        Assert.NotNull(arguments);
        Assert.Equal(3, arguments.Length);
        Assert.Contains("ThemeChoices", arguments[0], StringComparison.Ordinal);
        Assert.Contains("OverlayPositionChoices", arguments[1], StringComparison.Ordinal);
        Assert.Contains("PillDesignWithoutWordsFromControls", arguments[2], StringComparison.Ordinal);

        // The snapshot is a named local, and that local - not another - is what SaveAppearanceAsync gets.
        var declarator = Assert.IsType<VariableDeclaratorSyntax>(creation.Parent?.Parent);
        var handOver = Assert.Single(
            persist.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation => invocation.Expression.ToString().EndsWith("SaveAppearanceAsync", StringComparison.Ordinal));
        var handed = Assert.Single(handOver.ArgumentList.Arguments);
        Assert.Equal(declarator.Identifier.ValueText, handed.Expression.ToString());
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
