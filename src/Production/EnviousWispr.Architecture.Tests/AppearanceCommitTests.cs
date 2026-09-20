using System.Xml.Linq;
using EnviousWispr.Core.Settings;
using EnviousWispr.Presentation;

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
    /// wired to the handler is still lost if the snapshot has no field for it. A group with one
    /// card is not a choice - Live Preview's pill has one design today - and is the only kind of
    /// group allowed to have no field.
    /// </remarks>
    [Fact]
    public void EveryAppearanceChoiceHasAFieldInTheSnapshotTheWindowHandsOver()
    {
        var markup = XDocument.Load(Path.Combine(
            RepositoryRoot(), "src", "Production", "EnviousWispr.App", "MainWindow.xaml"));
        var appearance = markup.Descendants().First(element =>
            (string?)element.Attribute(XName.Get("Name", XamlNamespace)) == "AppearanceSection");
        var groups = appearance.Descendants()
            .Where(element => element.Name.LocalName == "RadioButton")
            .GroupBy(element => (string?)element.Attribute("GroupName") ?? "(no group)")
            .ToDictionary(group => group.Key, group => group.Count());

        var fieldByGroup = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PillTheme"] = nameof(AppearanceChoices.Theme),
            ["PillOverlayPosition"] = nameof(AppearanceChoices.OverlayPosition),
            ["PillWithoutWords"] = nameof(AppearanceChoices.PillDesignWithoutWords),
        };
        var fields = typeof(AppearanceChoices).GetConstructors().Single().GetParameters()
            .Select(parameter => parameter.Name!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (group, cards) in groups)
        {
            if (fieldByGroup.TryGetValue(group, out var field))
            {
                Assert.True(fields.Contains(field), $"The {group} choice maps to {field}, which the snapshot no longer carries.");
            }
            else
            {
                Assert.True(
                    cards == 1,
                    $"The {group} group offers {cards} cards on Appearance and has no field in the snapshot, so its choice is lost.");
            }
        }

        Assert.Equal(fieldByGroup.Values.Order(), fields.Order());
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
