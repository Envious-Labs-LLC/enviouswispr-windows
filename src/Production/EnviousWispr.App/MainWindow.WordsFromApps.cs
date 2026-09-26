using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Settings;
using EnviousWispr.Services.Words;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EnviousWispr.App;

/// <summary>Your Words' "From another app": pick an app, review what it holds, add it in one write.</summary>
/// <remarks>
/// THE SNIPPETS PAGE'S FLOW FOR WORDS (macOS <c>CustomWordsImportSheet</c>, From another app). The app list is looked for
/// only when it opens; the chosen app's store is read off the window thread, read only; a review names every outcome
/// and the words it would add; the commit is one write inside the settings gate that refuses when the list moved
/// during the review, and the review then comes back rebuilt under the Mac's notice. Nothing is written on Cancel.
/// Words the person already corrects differently are left alone and offered afterwards, as for a pasted list.
/// </remarks>
public sealed partial class MainWindow
{
    /// <summary>How many new pairs the review lists by name; the count above it is always the whole count.</summary>
    private const int ReviewPreviewLimit = 200;

    /// <summary>One word import from another app ended: counts and categories only, for the app's diagnostic log.</summary>
    public event Action<DiagnosticWordImport>? WordImportReported;

    private async void WordsFromAnotherAppButton_Click(object sender, RoutedEventArgs e)
    {
        // Looked for now, because the person asked to see the list - never at launch, never in the background.
        var installed = await Task.Run(() => WordImportApps.All.Where(app => app.IsInstalled).ToArray()).ConfigureAwait(true);
        IWordImportApp? chosen = null;
        var panel = Stack(Text("Bring the words across from another dictation app you use.", "BrandBodyStyle"));
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "From another app",
            CloseButtonText = "Cancel",
            Content = panel,
        };
        if (installed.Length == 0)
        {
            panel.Children.Add(Text(WordImportMessages.NoAppsFound(WordImportApps.SupportedNames), "BrandHelperStyle"));
        }

        foreach (var app in installed)
        {
            var card = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Style = (Style)Application.Current.Resources["BrandQuietButtonStyle"],
                Content = Stack(Text(app.DisplayName, "BrandRowLabelStyle"), Text($"Read your words from {app.DisplayName}.", "BrandHelperStyle")),
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(card, app.DisplayName);
            card.Click += (_, _) =>
            {
                chosen = app;
                dialog.Hide();
            };
            panel.Children.Add(card);
        }

        panel.Children.Add(Text(WordImportMessages.CarriesSpellings, "BrandHelperStyle"));
        await dialog.ShowAsync().AsTask().ConfigureAwait(true);
        if (chosen is { } source)
        {
            await LoadAndReviewWordsAsync(source).ConfigureAwait(true);
        }
    }

    /// <summary>Reads the app off the window thread; a refusal is said and logged, an empty source gets its own sentence.</summary>
    private async Task LoadAndReviewWordsAsync(IWordImportApp app)
    {
        var source = DiagnosticWordImport.SourceFor(app.Id);
        CustomWordAppBatch batch;
        try
        {
            batch = await Task.Run(app.Load).ConfigureAwait(true);
        }
        catch (WordImportException refusal)
        {
            WordImportReported?.Invoke(new DiagnosticWordImport(source, DiagnosticWordImportOutcome.Failed, refusal.Failure));
            ShowMessage("Import didn't finish", refusal.Message, InfoBarSeverity.Error);
            return;
        }

        if (batch.Entries.Count == 0)
        {
            var compatible = batch.Excluded > 0;
            WordImportReported?.Invoke(new DiagnosticWordImport(
                source,
                compatible ? DiagnosticWordImportOutcome.NothingCompatible : DiagnosticWordImportOutcome.NothingFound,
                Excluded: batch.Excluded));
            ShowMessage(
                compatible ? "Nothing compatible" : "Nothing to import",
                compatible ? WordImportMessages.NothingCompatible(batch.Excluded) : WordImportMessages.NothingFound,
                InfoBarSeverity.Informational);
            return;
        }

        var baseline = _session.Settings.Current.UserData.CustomWords;
        var plan = CustomWordImport.Plan(batch.Entries, baseline);
        string? staleNotice = null;
        while (true)
        {
            if (!await ShowWordReviewAsync(batch, plan, staleNotice).ConfigureAwait(true))
            {
                WordImportReported?.Invoke(DiagnosticWordImport.ForPlan(
                    source, DiagnosticWordImportOutcome.Cancelled, batch.Excluded, plan, added: 0));
                return;
            }

            var result = await CommitVocabularyAsync(_session.VocabularyImport.CommitFromAppAsync(baseline, batch.Entries)).ConfigureAwait(true);
            if (!result.Saved)
            {
                WordImportReported?.Invoke(DiagnosticWordImport.ForPlan(
                    source, DiagnosticWordImportOutcome.Failed, batch.Excluded, plan, added: 0, WordImportFailure.WriteFailed));
                return;
            }

            RefreshReusableUserDataViews();
            var outcome = result.Value;
            switch (outcome.Kind)
            {
                case WordImportCommitKind.Stale:
                    WordImportReported?.Invoke(DiagnosticWordImport.ForPlan(
                        source, DiagnosticWordImportOutcome.Stale, batch.Excluded, plan, added: 0));
                    baseline = outcome.Current;
                    plan = outcome.Plan;
                    staleNotice = WordImportMessages.StaleNotice;
                    continue;
                case WordImportCommitKind.WouldExceedStore:
                    WordImportReported?.Invoke(DiagnosticWordImport.ForPlan(
                        source, DiagnosticWordImportOutcome.Failed, batch.Excluded, outcome.Plan, added: 0, WordImportFailure.WouldExceedStore));
                    ShowMessage(
                        "Import didn't finish",
                        WordImportMessages.WouldExceedStore(AppSettingsValidator.MaximumCustomWords),
                        InfoBarSeverity.Error);
                    return;
                case WordImportCommitKind.NothingNew:
                    WordImportReported?.Invoke(DiagnosticWordImport.ForPlan(
                        source, DiagnosticWordImportOutcome.NothingNew, batch.Excluded, outcome.Plan, added: 0));
                    ShowMessage("No new words to add", WordImportMessages.Result(outcome.Plan), InfoBarSeverity.Informational, ReplaceConflictsAction(outcome.Plan));
                    return;
                default:
                    WordImportReported?.Invoke(DiagnosticWordImport.ForPlan(
                        source, DiagnosticWordImportOutcome.Completed, batch.Excluded, outcome.Plan, outcome.Plan.Additions.Count));
                    ShowMessage("Words imported", WordImportMessages.Result(outcome.Plan), InfoBarSeverity.Success, ReplaceConflictsAction(outcome.Plan));
                    return;
            }
        }
    }

    /// <summary>The review: what the source left out, one line naming every outcome, and the words it would add.</summary>
    private async Task<bool> ShowWordReviewAsync(CustomWordAppBatch batch, CustomWordImportPlan plan, string? staleNotice)
    {
        var content = Stack();
        if (staleNotice is not null)
        {
            content.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Message = staleNotice });
        }

        if (batch.Excluded > 0)
        {
            content.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational, Message = WordImportMessages.LeftOut(batch.Excluded) });
        }

        content.Children.Add(Text($"From {batch.SourceDisplayName}: {WordImportMessages.ReviewSummary(plan)}", "BrandBodyStyle"));
        if (plan.ConflictCount > 0)
        {
            content.Children.Add(Text(
                "Words you already correct differently are left as they are. You can replace them after the import.",
                "BrandHelperStyle"));
        }

        var additions = plan.Additions;
        if (additions.Count > 0)
        {
            var list = new StackPanel { Spacing = 4 };
            foreach (var entry in additions.Take(ReviewPreviewLimit))
            {
                list.Children.Add(Text(
                    string.Equals(entry.SpokenForm, entry.Replacement, StringComparison.Ordinal)
                        ? entry.Replacement
                        : $"{entry.SpokenForm} → {entry.Replacement}",
                    "BrandHelperStyle"));
            }

            if (additions.Count > ReviewPreviewLimit)
            {
                list.Children.Add(Text($"and {additions.Count - ReviewPreviewLimit} more", "BrandHelperStyle"));
            }

            content.Children.Add(new ScrollViewer { MaxHeight = 320, Content = list });
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Review",
            PrimaryButtonText = additions.Count > 0 ? WordImportMessages.ConfirmTitle(additions.Count) : "Continue",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = content,
        };
        return await dialog.ShowAsync().AsTask().ConfigureAwait(true) == ContentDialogResult.Primary;
    }
}
