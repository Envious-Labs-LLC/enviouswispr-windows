using System.Xml.Linq;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;
using EnviousWispr.Presentation;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EnviousWispr.Architecture.Tests;

public sealed class Wave4WordsSnippetsFileTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public void StrictnessFormattingUsesPlainEnglishLabelsInOrder()
    {
        Assert.Equal("Balanced (recommended)", MatchStrictnessFormatting.Label(MatchStrictness.Default));
        Assert.Equal("Loose (match similar-sounding words)", MatchStrictnessFormatting.Label(MatchStrictness.Loose));
        Assert.Equal("Strict (exact match only)", MatchStrictnessFormatting.Label(MatchStrictness.Strict));

        Assert.Equal(
            [
                (MatchStrictness.Default, "Balanced (recommended)"),
                (MatchStrictness.Loose, "Loose (match similar-sounding words)"),
                (MatchStrictness.Strict, "Strict (exact match only)"),
            ],
            MatchStrictnessFormatting.Options);
    }

    [Fact]
    public async Task UpdatingAWordReplacesEditedRowPreservesPacksAndKeepsListSorted()
    {
        var packWord = new CustomWordEntry("kubernetes", "Kubernetes");
        var edited = new CustomWordEntry("envy wisper", "EnviousWispr", MatchStrictness.Default);
        var other = new CustomWordEntry("zeta", "Zeta");
        var (presenter, store) = Build(words: [edited, packWord, other]);

        var result = await presenter.UpdateWordAsync(
            edited,
            "  envious wisper  ",
            "  EnviousWispr Pro  ",
            MatchStrictness.Strict).WaitAsync(Patience);

        Assert.True(result.Saved);
        Assert.Null(result.Value.Refusal);
        var words = store.Saved!.UserData.CustomWords;
        Assert.Equal(["envious wisper", "kubernetes", "zeta"], words.Select(w => w.SpokenForm));
        Assert.Equal("EnviousWispr Pro", words[0].Replacement);
        Assert.Equal(MatchStrictness.Strict, words[0].Strictness);
        Assert.Same(packWord, words[1]);
        Assert.Same(other, words[2]);
    }

    [Fact]
    public async Task UpdatingAWordRefusesCollisionAndMissingTargetWithoutSaving()
    {
        var edited = new CustomWordEntry("envy", "Envy");
        var other = new CustomWordEntry("zeta", "Zeta");
        var missing = new CustomWordEntry("gone", "Gone");
        var (presenter, store) = Build(words: [edited, other]);

        var collision = await presenter.UpdateWordAsync(edited, "ZETA", "Clash", MatchStrictness.Default).WaitAsync(Patience);
        Assert.True(collision.Saved);
        Assert.Equal(WordEditRefusal.DuplicateSpokenForm, collision.Value.Refusal);
        Assert.Same(other, collision.Value.Clash);
        Assert.Null(store.Saved);

        var stale = await presenter.UpdateWordAsync(missing, "new", "New", MatchStrictness.Default).WaitAsync(Patience);
        Assert.True(stale.Saved);
        Assert.Equal(WordEditRefusal.TargetMissing, stale.Value.Refusal);
        Assert.Null(store.Saved);
    }

    [Fact]
    public async Task UpdatingAMissingSnippetDoesNotResurrectIt()
    {
        var existing = new SnippetEntry("alpha", "First");
        var missing = new SnippetEntry("deleted", "Gone");
        var (presenter, store) = Build(snippets: [existing]);

        var stale = await presenter.UpdateSnippetAsync(missing, "deleted", "Back").WaitAsync(Patience);
        Assert.True(stale.Saved);
        Assert.True(stale.Value.TargetMissing);
        Assert.Null(store.Saved);
    }

    [Fact]
    public void ReplaceWordRemovesOnlyTargetedTwinWhenDuplicateSpokenFormsExist()
    {
        var first = new CustomWordEntry("alpha", "Alpha1");
        var second = new CustomWordEntry("alpha", "Alpha2");
        var keep = new CustomWordEntry("beta", "Beta");
        var updated = new CustomWordEntry("gamma", "Gamma", MatchStrictness.Loose);

        var result = VocabularyPresenter.ReplaceWord([first, second, keep], first, updated);

        Assert.Equal(["alpha", "beta", "gamma"], result.Select(w => w.SpokenForm));
        Assert.Same(second, result[0]);
        Assert.Same(keep, result[1]);
        Assert.Equal(MatchStrictness.Loose, result[2].Strictness);

        // Editing one duplicate without changing its spoken form preserves the unedited twin.
        var sameSpoken = new CustomWordEntry("alpha", "AlphaEdited", MatchStrictness.Strict);
        var keptTwin = VocabularyPresenter.ReplaceWord([first, second, keep], first, sameSpoken);
        Assert.Equal(["alpha", "alpha", "beta"], keptTwin.Select(w => w.SpokenForm));
        Assert.Contains(keptTwin, w => ReferenceEquals(w, second));
        Assert.Contains(keptTwin, w => ReferenceEquals(w, sameSpoken));
    }

    [Fact]
    public async Task UpdatingASnippetPreservesListOrderAndIgnoresSelfDuringDuplicateCheck()
    {
        var first = new SnippetEntry("alpha", "First body");
        var middle = new SnippetEntry("my email", "old@example.com");
        var last = new SnippetEntry("zeta", "Last body");
        var (presenter, store) = Build(snippets: [first, middle, last]);

        // Editing the same trigger (different punctuation/casing) succeeds and stays at index 1.
        var selfEdit = await presenter.UpdateSnippetAsync(middle, "My Email!", "new@example.com").WaitAsync(Patience);
        Assert.True(selfEdit.Saved);
        Assert.Null(selfEdit.Value.Refusal);
        var saved = store.Saved!.UserData.Snippets;
        Assert.Equal(["alpha", "My Email!", "zeta"], saved.Select(s => s.Name));
        Assert.Equal("new@example.com", saved[1].Body);

        // Renaming to collide with another snippet is refused and keeps existing data intact.
        var editedMiddle = saved[1];
        var duplicate = await presenter.UpdateSnippetAsync(editedMiddle, "Alpha", "clash").WaitAsync(Patience);
        Assert.True(duplicate.Saved);
        Assert.Equal(SnippetRefusal.DuplicateTrigger, duplicate.Value.Refusal);
        Assert.Equal(first, duplicate.Value.Clash);
        Assert.Equal(["alpha", "My Email!", "zeta"], store.Saved!.UserData.Snippets.Select(s => s.Name));
    }

    [Fact]
    public void ValidateSnippetEditRejectsEmptyTriggerAndExpansion()
    {
        var target = new SnippetEntry("sig", "Regards");
        SnippetEntry[] existing = [target, new SnippetEntry("address", "123 Main St")];

        Assert.Equal(
            SnippetRefusal.TriggerEmpty,
            VocabularyPresenter.ValidateSnippetEdit(target, "!!!", "Valid", existing, out _));
        Assert.Equal(
            SnippetRefusal.ExpansionEmpty,
            VocabularyPresenter.ValidateSnippetEdit(target, "sig", "   ", existing, out _));
        Assert.Equal(
            SnippetRefusal.DuplicateTrigger,
            VocabularyPresenter.ValidateSnippetEdit(target, "address", "Other", existing, out var clash));
        Assert.Equal("address", clash?.Name);
    }

    [Fact]
    public void FileTranscriptionUsesHonestThreeStepTransitions()
    {
        Assert.Equal(1, FileTranscriptionStepState.ChooseFileStep);
        Assert.Equal(2, FileTranscriptionStepState.TranscribingStep);
        Assert.Equal(3, FileTranscriptionStepState.DoneStep);
        Assert.Equal(2, FileTranscriptionStepState.StartStep);

        Assert.Equal(2, FileTranscriptionStepState.StepForProgress(new FileTranscriptionProgress(0, TimeSpan.Zero, TimeSpan.FromMinutes(2))));
        Assert.Equal(2, FileTranscriptionStepState.StepForProgress(new FileTranscriptionProgress(5, TimeSpan.FromSeconds(110), TimeSpan.FromMinutes(2))));

        Assert.Equal(3, FileTranscriptionStepState.StepForOutcome(FileTranscriptionOutcome.Completed));
        Assert.Equal(1, FileTranscriptionStepState.StepForOutcome(FileTranscriptionOutcome.Cancelled));
        Assert.Equal(1, FileTranscriptionStepState.StepForOutcome(FileTranscriptionOutcome.Refused));
        Assert.Equal(1, FileTranscriptionStepState.StepForOutcome(FileTranscriptionOutcome.Failed));
        Assert.Equal(1, FileTranscriptionStepState.StepForOutcome(FileTranscriptionOutcome.Empty));

        Assert.Equal(1, FileTranscriptionStepState.ClampStep(0));
        Assert.Equal(3, FileTranscriptionStepState.ClampStep(6));
    }

    [Fact]
    public void MainWindowMarkupReflectsWave4WordsSnippetsAndThreeStepFileStrip()
    {
        var root = FindRepositoryRoot();
        var xamlPath = Path.Combine(root, "src", "Production", "EnviousWispr.App", "MainWindow.xaml");
        var document = XDocument.Load(xamlPath);
        var xaml = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");

        XElement? FindByName(string name) =>
            document.Descendants().SingleOrDefault(e => (string?)e.Attribute(xaml + "Name") == name);

        // 1. What's New release notes replace Founder build placeholder.
        var whatsNew = FindByName("WhatsNewPage");
        Assert.NotNull(whatsNew);
        Assert.NotNull(FindByName("WhatsNewBuildInfoText"));
        Assert.DoesNotContain("Founder build", whatsNew!.ToString(), StringComparison.Ordinal);
        Assert.Contains("Version 0.19.0", whatsNew.ToString(), StringComparison.Ordinal);

        // 2. Your Words header Add button, top tab bar & Quick Add 3-step cards + shortcut badge.
        Assert.NotNull(FindByName("OpenAddWordDialogButton"));
        Assert.NotNull(FindByName("DictionaryTabStrip"));
        Assert.NotNull(FindByName("WordFilterAutoLearnedChip"));
        Assert.NotNull(FindByName("QuickAddCurrentShortcutText"));
        Assert.NotNull(FindByName("QuickAddStep1Card"));
        Assert.NotNull(FindByName("QuickAddStep2Card"));
        Assert.NotNull(FindByName("QuickAddStep3Card"));
        var quickAdd = FindByName("DictPanelQuickAdd");
        Assert.NotNull(quickAdd);
        Assert.Contains("Quick Add from anywhere", quickAdd!.ToString(), StringComparison.Ordinal);
        Assert.Contains("GoToQuickAddKeybindButton_Click", quickAdd.ToString(), StringComparison.Ordinal);

        // 3. Dictionary & Snippet row Edit buttons + reimagined Snippets card & modal editor (no inline form / bottom remove).
        var dictList = FindByName("DictionaryList");
        Assert.Contains("EditWordRowButton_Click", dictList!.ToString(), StringComparison.Ordinal);
        var snippetList = FindByName("SnippetList");
        Assert.Contains("EditSnippetRowButton_Click", snippetList!.ToString(), StringComparison.Ordinal);
        Assert.NotNull(FindByName("AddSnippetToolbarButton"));
        Assert.NotNull(FindByName("LoadExampleSnippetsButton"));
        Assert.Null(FindByName("SnippetNameBox"));
        Assert.Null(FindByName("SnippetBodyBox"));
        Assert.Null(FindByName("RemoveSnippetButton"));
        Assert.Null(FindByName("HelpNavItem"));
        Assert.Null(FindByName("HelpPage"));

        // 4. Transcribe a File 3-step strip + AAC format pill.
        Assert.Equal("Choose file", (string?)FindByName("TranscribeStep1Label")?.Attribute("Text"));
        Assert.Equal("Transcribing", (string?)FindByName("TranscribeStep2Label")?.Attribute("Text"));
        Assert.Equal("Done", (string?)FindByName("TranscribeStep3Label")?.Attribute("Text"));
        Assert.Null(FindByName("TranscribeStep4"));
        Assert.Null(FindByName("TranscribeStep5"));
        Assert.Null(FindByName("TranscribeStep6"));

        var transcribeSection = FindByName("TranscribeFileSection")!;
        foreach (var fmt in new[] { "WAV", "MP3", "M4A", "AAC", "WMA", "FLAC" })
        {
            Assert.Contains(
                transcribeSection.Descendants(),
                e => e.Name.LocalName == "TextBlock" && (string?)e.Attribute("Text") == fmt);
        }
    }

    [Fact]
    public void ProductNavigationSelectionChangedIgnoresClearedSelectionBeforeHistoryFallback()
    {
        var root = FindRepositoryRoot();
        var method = Directory.EnumerateFiles(
                Path.Combine(root, "src", "Production", "EnviousWispr.App"),
                "MainWindow*.cs")
            .Order()
            .SelectMany(codePath => CSharpSyntaxTree.ParseText(File.ReadAllText(codePath), path: codePath)
                .GetRoot()
                .DescendantNodes()
                .OfType<MethodDeclarationSyntax>())
            .Single(m => m.Identifier.ValueText == "ProductNavigation_SelectionChanged");

        var statements = method.Body!.Statements;
        Assert.True(statements.Count >= 2);
        Assert.IsType<IfStatementSyntax>(statements[0]);
        var guardText = statements[0].ToString();
        Assert.Contains("args.SelectedItem is null && args.SelectedItemContainer is null", guardText, StringComparison.Ordinal);
        Assert.Contains("return;", guardText, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "EnviousWispr.Windows.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }

    private static (VocabularyPresenter Presenter, RecordingStore Store) Build(
        IReadOnlyList<CustomWordEntry>? words = null,
        IReadOnlyList<SnippetEntry>? snippets = null)
    {
        var store = new RecordingStore();
        var settings = AppSettings.Default with { UserData = new ReusableUserData(words ?? [], snippets ?? []) };
        return (new VocabularyPresenter(new SettingsPresenter(store, settings)), store);
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
}
