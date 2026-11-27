using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Settings;
using EnviousWispr.Services.UserData;
using EnviousWispr.Services.Snippets;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Storage.Pickers;
using Windows.System;

namespace EnviousWispr.App;

/// <summary>One row of the saved-snippets list: the snippet, and whether it is still an untouched example.</summary>
/// <remarks>
/// A VIEW ROW, NOT A STORED FIELD. <see cref="SnippetEntry"/> is written to the settings file as it stands and refuses a
/// computed member, so the "Example" badge is worked out here from <see cref="SnippetStarters.IsUneditedExample"/> and
/// disappears the moment either half is edited.
/// </remarks>
public sealed class SnippetRow
{
    public SnippetRow(SnippetEntry entry)
    {
        Entry = entry;
        IsExample = SnippetStarters.IsUneditedExample(entry);
    }

    public SnippetEntry Entry { get; }

    public string Name => Entry.Name;

    public string Body => Entry.Body;

    public string BodyPreview => Entry.Body
        .Replace("\r\n", " ", StringComparison.Ordinal)
        .Replace('\r', ' ')
        .Replace('\n', ' ')
        .Trim();

    public bool IsExample { get; }

    public Visibility ExampleVisibility => IsExample ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>What a screen reader announces for the row, the badge included (macOS row label).</summary>
    public override string ToString() => IsExample ? $"{Name}, example: {Body}" : Entry.ToString();
}

/// <summary>The Snippets page's list tools: search, export, and import (paste, a file, another app) through one review.</summary>
/// <remarks>
/// THE MAC'S FLOW IN WINDOWS DIALOGS (macOS <c>SnippetImportSheet</c>, #2997). Three ways in feed one review: new rows
/// ticked, "You have this" and "listed twice" rows skip only, and a confirm button that names what it will do. The
/// commit is one write inside the settings gate that refuses when the list moved during the review
/// (<see cref="EnviousWispr.Presentation.SnippetImportController"/>); then the review comes back rebuilt, with every
/// decision reset, under the Mac's notice. The keyword is never imported.
///
/// NOTHING IS READ UNTIL IT IS ASKED FOR. The clipboard is not touched (the person pastes into the box), a file is read
/// only after it is chosen, and another app's store is looked for only when the app list opens and read only when an
/// app is picked.
/// </remarks>
public sealed partial class MainWindow
{
    /// <summary>The folder that holds the settings file; no export may write into it.</summary>
    private readonly string _dataDirectory;

    /// <summary>One snippet import attempt ended: counts and categories only, for the app's diagnostic log.</summary>
    public event Action<DiagnosticSnippetImport>? SnippetImportReported;

    /// <summary>
    /// True, and the person told, when an export was pointed into the app's own data folder. Nothing is written.
    /// </summary>
    /// <remarks>
    /// SHARED BY EVERY LIST EXPORT ON THE WINDOW (snippets and words), because each writes a file the person names,
    /// and a list saved over the settings file erases the settings (macOS refuses its live store the same way).
    /// </remarks>
    private bool RefuseExportIntoDataFolder(string destination)
    {
        if (!ExportDestinationGuard.IsInsideDataDirectory(destination, _dataDirectory))
        {
            return false;
        }

        ShowMessage(
            "Choose another place",
            "That is inside EnviousWispr's own data folder, where your settings are kept. Saving there could erase them, so nothing was saved. Pick somewhere else, such as Documents.",
            InfoBarSeverity.Warning);
        return true;
    }

    private readonly EnviousWispr.App.Collections.BulkObservableCollection<SnippetRow> _snippetRows = new();
    private CancellationTokenSource? _snippetRefreshCts;
    private int _snippetRefreshGeneration;
    private bool _snippetsViewDirty;
    private (IReadOnlyList<SnippetEntry> Source, SnippetRow[] Rows)? _snippetRowCache;

    private const string PasteHint =
        "One per line: the trigger, then =, then the text. A tab, an arrow, or a comma work too. For text on several lines, paste exported JSON or CSV, or type \\n where a line should break.";

    private void FlushPendingSnippetRefresh()
    {
        if (_snippetsViewDirty)
        {
            ScheduleSnippetRefresh(debounce: false);
        }
    }

    private void ScheduleSnippetRefresh(bool debounce)
    {
        _snippetRefreshCts?.Cancel();
        _snippetRefreshCts?.Dispose();
        var cts = new CancellationTokenSource();
        _snippetRefreshCts = cts;
        var generation = ++_snippetRefreshGeneration;
        var snippets = _settings?.UserData.Snippets ?? (IReadOnlyList<SnippetEntry>)Array.Empty<SnippetEntry>();
        var query = SnippetSearchBox?.Text ?? string.Empty;

        if (!debounce && snippets.Count < 128)
        {
            var rows = BuildFilteredSnippetRows(snippets, query);
            ApplyFilteredSnippetRows(rows, snippets.Count, query);
            return;
        }

        _ = RefreshSnippetsAsync(snippets, query, debounce, generation, cts.Token);
    }

    private async Task RefreshSnippetsAsync(
        IReadOnlyList<SnippetEntry> snippets,
        string query,
        bool debounce,
        int generation,
        CancellationToken cancellationToken)
    {
        try
        {
            if (debounce)
            {
                await Task.Delay(150, cancellationToken).ConfigureAwait(true);
            }

            if (cancellationToken.IsCancellationRequested || generation != _snippetRefreshGeneration || _session.Closing)
            {
                return;
            }

            var rows = await Task.Run(
                () => BuildFilteredSnippetRows(snippets, query),
                cancellationToken).ConfigureAwait(true);

            if (cancellationToken.IsCancellationRequested || generation != _snippetRefreshGeneration || _session.Closing)
            {
                return;
            }

            ApplyFilteredSnippetRows(rows, snippets.Count, query);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private SnippetRow[] BuildFilteredSnippetRows(
        IReadOnlyList<SnippetEntry> snippets,
        string query)
    {
        var cached = _snippetRowCache;
        SnippetRow[] allRows;
        if (cached is { Source: var cachedSource } && ReferenceEquals(cachedSource, snippets))
        {
            allRows = cached.Value.Rows;
        }
        else
        {
            allRows = new SnippetRow[snippets.Count];
            for (var i = 0; i < snippets.Count; i++)
            {
                allRows[i] = new SnippetRow(snippets[i]);
            }

            _snippetRowCache = (snippets, allRows);
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return allRows;
        }

        var shown = SnippetSearch.Filter(snippets, query);
        if (shown.Count == 0)
        {
            return [];
        }

        if (shown.Count == allRows.Length)
        {
            return allRows;
        }

        var byEntry = new Dictionary<SnippetEntry, SnippetRow>(allRows.Length, ReferenceEqualityComparer.Instance);
        for (var i = 0; i < allRows.Length; i++)
        {
            byEntry[allRows[i].Entry] = allRows[i];
        }

        var result = new SnippetRow[shown.Count];
        for (var i = 0; i < shown.Count; i++)
        {
            result[i] = byEntry.TryGetValue(shown[i], out var row)
                ? row
                : new SnippetRow(shown[i]);
        }

        return result;
    }

    private void ApplyFilteredSnippetRows(SnippetRow[] rows, int totalCount, string query)
    {
        _snippetsViewDirty = false;
        var searching = !string.IsNullOrWhiteSpace(query);
        _snippetRows.Reconcile(rows, static (left, right) => ReferenceEquals(left, right) || Equals(left.Entry, right.Entry));
        SnippetCountText.Text = totalCount == 0 ? string.Empty : SnippetImportCopy.CountLabel(rows.Length, totalCount, searching);

        if (SnippetSearchClearButton is not null)
        {
            SnippetSearchClearButton.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        }

        // THE SEARCH BOX STAYS WHILE IT HOLDS A QUERY, even if the list emptied under it, so the query can be cleared.
        var showSearch = totalCount > 0 || searching;
        if (SnippetSearchContainer is not null)
        {
            SnippetSearchContainer.Visibility = showSearch ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            SnippetSearchBox.Visibility = showSearch ? Visibility.Visible : Visibility.Collapsed;
        }

        if (SnippetsToolbarGrid is { ActualWidth: > 0 })
        {
            UpdateSnippetsToolbarLayout(SnippetsToolbarGrid.ActualWidth);
        }

        SnippetEmptyState.Visibility = totalCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        var noMatch = totalCount > 0 && rows.Length == 0;
        SnippetNoMatchState.Visibility = noMatch ? Visibility.Visible : Visibility.Collapsed;
        SnippetNoMatchText.Text = noMatch ? $"No snippets match \u201C{query.Trim()}\u201D" : string.Empty;
        SnippetList.Visibility = rows.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelectionDependentButtons();
    }

    private void SnippetSearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
        ScheduleSnippetRefresh(debounce: true);

    private void ClearSnippetSearchButton_Click(object sender, RoutedEventArgs e)
    {
        SnippetSearchBox.Text = string.Empty;
        SnippetSearchBox.Focus(FocusState.Programmatic);
    }

    // ---- Export ------------------------------------------------------------------------------------------

    /// <summary>Writes every snippet and the keyword to the Mac's export file, which both apps import.</summary>
    private async void ExportSnippetsButton_Click(object sender, RoutedEventArgs e)
    {
        var snippets = _settings.UserData.Snippets;
        if (snippets.Count == 0)
        {
            ShowMessage("Nothing to export", "There are no snippets to export yet.", InfoBarSeverity.Informational);
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedFileName = Path.GetFileNameWithoutExtension(SnippetsTransferDocument.DefaultFileName),
            DefaultFileExtension = ".json",
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeChoices.Add("EnviousWispr snippets", [".json"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        if (RefuseExportIntoDataFolder(file.Path))
        {
            return;
        }

        // The list as it is NOW, not as it was when the picker opened.
        var latest = _session.Settings.Current.UserData;
        var text = SnippetsTransferDocument.Write(latest.Snippets, latest.SnippetKeyword, DateTimeOffset.UtcNow, Guid.NewGuid);
        try
        {
            // A NEW FILE PUT AT THE CHOSEN NAME, never a write through it (ExportFileWriter).
            await ExportFileWriter.WriteReplacingAsync(file.Path, text).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowMessage("The export did not finish", "Windows would not save that file. Your snippets are unchanged.", InfoBarSeverity.Error);
            return;
        }

        ShowMessage(
            "Snippets exported",
            latest.Snippets.Count == 1 ? "Exported 1 snippet and your keyword." : $"Exported {latest.Snippets.Count} snippets and your keyword.",
            InfoBarSeverity.Success);
    }

    // ---- The three ways in --------------------------------------------------------------------------------

    private async void PasteSnippetsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 180,
            PlaceholderText = "my email = john.doe@example.com",
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, "Snippets to import");
        var readAs = new ComboBox { Header = "Read as", Items = { "List", "CSV" }, SelectedIndex = 0, Visibility = Visibility.Collapsed };
        var count = Text("Nothing pasted yet.", "BrandHelperStyle");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(count, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        var dialog = new ContentDialog
        {
            XamlRoot = WindowRoot.XamlRoot ?? Content.XamlRoot,
            RequestedTheme = WindowRoot.ActualTheme,
            Title = "Paste snippets",
            PrimaryButtonText = "Continue",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
            Content = Stack(
                Text("Paste your snippets, then check the count before you continue.", "BrandBodyStyle"),
                box,
                Text(PasteHint, "BrandHelperStyle"),
                readAs,
                count),
        };

        // THE COUNT AND CONTINUE SHARE ONE READING (SnippetPasteImport.Preview), taken off the window thread behind
        // a short pause and a generation, so a large paste cannot freeze typing and a late count cannot overwrite a
        // newer one.
        SnippetImportBatch? ready = null;
        var generation = 0;
        async void Recount()
        {
            var mine = ++generation;
            var text = box.Text;
            var choice = readAs.SelectedIndex == 1 ? SnippetPasteFormat.Csv : SnippetPasteFormat.List;
            dialog.IsPrimaryButtonEnabled = false;
            ready = null;
            if (text.Trim().Length == 0)
            {
                count.Text = "Nothing pasted yet.";
                readAs.Visibility = Visibility.Collapsed;
                return;
            }

            count.Text = "Counting\u2026";
            await Task.Delay(150).ConfigureAwait(true);
            if (mine != generation)
            {
                return;
            }

            (SnippetPasteSniff Sniff, SnippetImportBatch Batch)? preview = null;
            string? problem = null;
            try
            {
                preview = await Task.Run(() => SnippetPasteImport.Preview(text, choice)).ConfigureAwait(true);
            }
            catch (SnippetImportException refusal)
            {
                problem = refusal.Message;
            }

            if (mine != generation)
            {
                return;
            }

            if (problem is not null || preview is not { } result)
            {
                count.Text = problem ?? SnippetImportMessages.Unreadable;
                return;
            }

            readAs.Visibility = result.Sniff == SnippetPasteSniff.Ambiguous ? Visibility.Visible : Visibility.Collapsed;
            var skipped = result.Batch.Notices.OfType<SnippetImportNotice.LinesSkipped>().Sum(notice => notice.Count);
            if (result.Batch.Candidates.Count == 0)
            {
                count.Text = "No snippets found. Each line needs a trigger and some text.";
                return;
            }

            count.Text = SnippetImportCopy.PasteSummary(result.Batch.Candidates.Count, skipped);
            ready = result.Batch;
            dialog.IsPrimaryButtonEnabled = true;
        }

        box.TextChanged += (_, _) => Recount();
        readAs.SelectionChanged += (_, _) => Recount();

        if (await dialog.ShowAsync().AsTask().ConfigureAwait(true) != ContentDialogResult.Primary || ready is null)
        {
            return;
        }

        await ReviewSnippetImportAsync(ready).ConfigureAwait(true);
    }

    private async void OpenSnippetsFileMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        foreach (var extension in SnippetFileImport.Extensions)
        {
            picker.FileTypeFilter.Add(extension);
        }

        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        var path = file.Path;
        await LoadAndReviewAsync(DiagnosticSnippetImport.SourceFor(Path.GetExtension(path)), () =>
        {
            var extension = Path.GetExtension(path);
            var ceiling = SnippetFileImport.MaximumBytes(extension);
            byte[] bytes;
            try
            {
                // BOUNDED BY WHAT IS READ, not by a size asked for first: a file that grows during the read is still stopped.
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int read;
                while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                {
                    if (buffer.Length + read > ceiling)
                    {
                        throw new SnippetImportException(SnippetImportFailure.TooLarge, SnippetImportMessages.TooLarge);
                    }

                    buffer.Write(chunk, 0, read);
                }

                bytes = buffer.ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new SnippetImportException(SnippetImportFailure.Unreadable, SnippetImportMessages.Unreadable);
            }

            return SnippetFileImport.Read(extension, bytes);
        }).ConfigureAwait(true);
    }

    private async void SnippetsFromAnotherAppMenuItem_Click(object sender, RoutedEventArgs e)
    {
        // Looked for now, because the person asked to see the list - never at launch, never in the background.
        var installed = await Task.Run(() => SnippetImportApps.All.Where(app => app.IsInstalled).ToArray()).ConfigureAwait(true);
        ISnippetImportApp? chosen = null;
        var panel = Stack(Text("Snippets you already saved in another dictation app, found on this PC.", "BrandBodyStyle"));
        var dialog = new ContentDialog
        {
            XamlRoot = WindowRoot.XamlRoot ?? Content.XamlRoot,
            RequestedTheme = WindowRoot.ActualTheme,
            Title = "From another app",
            CloseButtonText = "Cancel",
            Content = panel,
        };
        if (installed.Length == 0)
        {
            panel.Children.Add(Text(
                $"No supported dictation apps found on this PC. EnviousWispr can read snippets from {SnippetImportApps.SupportedNames}.",
                "BrandHelperStyle"));
        }

        foreach (var app in installed)
        {
            var card = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Style = (Style)Application.Current.Resources["BrandQuietButtonStyle"],
                Content = Stack(Text(app.DisplayName, "BrandRowLabelStyle"), Text($"Read your snippets from {app.DisplayName}.", "BrandHelperStyle")),
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(card, app.DisplayName);
            card.Click += (_, _) =>
            {
                chosen = app;
                dialog.Hide();
            };
            panel.Children.Add(card);
        }

        panel.Children.Add(Text("Snippets you already have are left as they are.", "BrandHelperStyle"));
        await dialog.ShowAsync().AsTask().ConfigureAwait(true);
        if (chosen is { } source)
        {
            await LoadAndReviewAsync(DiagnosticSnippetImport.SourceFor(source.Id), () => source.Load().Validated()).ConfigureAwait(true);
        }
    }

    // ---- Load, review, commit -----------------------------------------------------------------------------

    /// <summary>Loads a source off the window thread; a refusal becomes the Mac's failure result, an empty source its own.</summary>
    private async Task LoadAndReviewAsync(DiagnosticSnippetImportSource source, Func<SnippetImportBatch> load)
    {
        SnippetImportBatch batch;
        try
        {
            batch = await Task.Run(load).ConfigureAwait(true);
        }
        catch (SnippetImportException refusal)
        {
            SnippetImportReported?.Invoke(new DiagnosticSnippetImport(source, DiagnosticSnippetImportOutcome.Failed, refusal.Failure));
            ShowMessage("Import didn't finish", refusal.Message, InfoBarSeverity.Error);
            return;
        }

        await ReviewSnippetImportAsync(batch).ConfigureAwait(true);
    }

    /// <summary>The review, the one write, and the result - again, rebuilt, when the list moved during the review.</summary>
    private async Task ReviewSnippetImportAsync(SnippetImportBatch batch)
    {
        var source = DiagnosticSnippetImport.SourceFor(batch.SourceId);
        if (batch.Candidates.Count == 0)
        {
            var excluded = batch.ExcludedCount;
            SnippetImportReported?.Invoke(new DiagnosticSnippetImport(
                source,
                excluded > 0 ? DiagnosticSnippetImportOutcome.NothingCompatible : DiagnosticSnippetImportOutcome.NothingFound,
                Excluded: excluded));
            ShowMessage(
                excluded > 0 ? "Nothing compatible" : "Nothing to import",
                excluded > 0 ? SnippetImportCopy.NothingCompatible(excluded) : SnippetImportCopy.NothingFound,
                InfoBarSeverity.Informational);
            return;
        }

        var baseline = _session.Settings.Current.UserData.Snippets;
        string? staleNotice = null;
        while (true)
        {
            var rows = await Task.Run(() => SnippetImportReview.BuildRows(batch.Candidates, baseline).ToArray()).ConfigureAwait(true);
            var approved = await ShowSnippetReviewAsync(rows, batch.Notices, staleNotice).ConfigureAwait(true);
            if (approved is null)
            {
                return;
            }

            var result = await CommitVocabularyAsync(
                _session.SnippetImport.CommitAsync(baseline, approved),
                UserDataRefreshScope.Snippets).ConfigureAwait(true);
            if (!result.Saved)
            {
                SnippetImportReported?.Invoke(DiagnosticSnippetImport.ForReview(
                    source, DiagnosticSnippetImportOutcome.Failed, batch.ExcludedCount, rows, added: 0, SnippetImportFailure.WriteFailed));
                return;
            }

            var outcome = result.Value;
            SnippetImportReported?.Invoke(DiagnosticSnippetImport.ForReview(
                source,
                outcome.Kind switch
                {
                    SnippetImportCommitKind.Committed => DiagnosticSnippetImportOutcome.Completed,
                    SnippetImportCommitKind.NothingApproved => DiagnosticSnippetImportOutcome.NothingApproved,
                    SnippetImportCommitKind.Stale => DiagnosticSnippetImportOutcome.Stale,
                    _ => DiagnosticSnippetImportOutcome.Failed,
                },
                batch.ExcludedCount,
                rows,
                outcome.Added,
                outcome.Failure));
            switch (outcome.Kind)
            {
                case SnippetImportCommitKind.Committed:
                    ShowMessage("Import complete", SnippetImportCopy.Completed(outcome.Added), InfoBarSeverity.Success);
                    return;
                case SnippetImportCommitKind.NothingApproved:
                    ShowMessage("Nothing added", SnippetImportCopy.NothingApproved, InfoBarSeverity.Informational);
                    return;
                case SnippetImportCommitKind.Stale:
                    baseline = outcome.Current;
                    staleNotice = outcome.Message;
                    continue;
                default:
                    ShowMessage("Import didn't finish", outcome.Message ?? SnippetImportMessages.Unreadable, InfoBarSeverity.Error);
                    return;
            }
        }
    }

    /// <summary>The review screen. Null when cancelled; otherwise the ticked rows' snippets, in review order.</summary>
    private async Task<IReadOnlyList<SnippetImportCandidate>?> ShowSnippetReviewAsync(
        SnippetImportReviewRow[] rows,
        IReadOnlyList<SnippetImportNotice> notices,
        string? staleNotice)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Review",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        void UpdateConfirm() => dialog.PrimaryButtonText = SnippetImportCopy.ConfirmTitle(rows.Count(row => row.Add));

        var list = new StackPanel { Spacing = 8 };
        for (var index = 0; index < rows.Length; index++)
        {
            list.Children.Add(ReviewRow(rows, index, UpdateConfirm));
        }

        var content = Stack();
        if (staleNotice is not null)
        {
            content.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Message = staleNotice });
        }

        foreach (var notice in notices)
        {
            content.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational, Message = SnippetImportCopy.Notice(notice) });
        }

        content.Children.Add(Text(
            SnippetImportCopy.ReviewSummary(
                rows.Count(row => row.Status == SnippetImportRowStatus.New),
                rows.Count(row => row.Status == SnippetImportRowStatus.Existing),
                rows.Count(row => row.Status == SnippetImportRowStatus.DuplicateInBatch)),
            "BrandBodyStyle"));
        content.Children.Add(new ScrollViewer { MaxHeight = 320, Content = list });
        dialog.Content = content;
        UpdateConfirm();

        if (await dialog.ShowAsync().AsTask().ConfigureAwait(true) != ContentDialogResult.Primary)
        {
            return null;
        }

        return rows.Where(row => row.Add).Select(row => row.Candidate).ToArray();
    }

    /// <summary>One review row: trigger, its text on one line, the "you have this" note, then a tick or the skip label.</summary>
    private static Border ReviewRow(SnippetImportReviewRow[] rows, int index, Action changed)
    {
        var row = rows[index];
        var body = Stack(
            Text(row.Candidate.Trigger, "BrandRowLabelStyle"),
            new TextBlock
            {
                Text = string.Join(' ', row.Candidate.Expansion.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)),
                Style = (Style)Application.Current.Resources["BrandHelperStyle"],
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
            });
        if (row.StatusNote is { } note)
        {
            body.Children.Add(Text(note, "BrandHelperStyle"));
        }

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(body);
        FrameworkElement trailing;
        if (row.IsAddable)
        {
            var tick = new CheckBox { IsChecked = row.Add, MinWidth = 0, VerticalAlignment = VerticalAlignment.Top };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(tick, $"Add {row.Candidate.Trigger}");
            tick.Checked += (_, _) => { rows[index] = rows[index].WithAdd(true); changed(); };
            tick.Unchecked += (_, _) => { rows[index] = rows[index].WithAdd(false); changed(); };
            trailing = tick;
        }
        else
        {
            trailing = Text(row.SkipLabel ?? string.Empty, "BrandHelperStyle");
        }

        Grid.SetColumn(trailing, 1);
        grid.Children.Add(trailing);
        return new Border
        {
            Padding = new Thickness(14, 10, 14, 10),
            Style = (Style)Application.Current.Resources["BrandSectionCardStyle"],
            Child = grid,
        };
    }

    private static TextBlock Text(string text, string style) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Style = (Style)Application.Current.Resources[style],
    };

    private static StackPanel Stack(params UIElement[] children)
    {
        var panel = new StackPanel { Spacing = 10 };
        foreach (var child in children)
        {
            panel.Children.Add(child);
        }

        return panel;
    }

    private async void AddSnippetToolbarButton_Click(object sender, RoutedEventArgs e) =>
        await ShowSnippetEditorDialogAsync(existing: null).ConfigureAwait(true);

    private async void OpenNewSnippetDialogButton_Click(object sender, RoutedEventArgs e) =>
        await ShowSnippetEditorDialogAsync(existing: null).ConfigureAwait(true);

    private async void LoadExampleSnippetsButton_Click(object sender, RoutedEventArgs e)
    {
        var saved = await CommitVocabularyAsync(
            _session.Vocabulary.RestoreStarterSnippetsAsync(),
            UserDataRefreshScope.Snippets).ConfigureAwait(true);
        if (!saved.Saved)
        {
            return;
        }

        if (saved.Value > 0)
        {
            ShowMessage(
                "Example snippets loaded",
                saved.Value == 1 ? "Added 1 example snippet." : $"Added {saved.Value} example snippets.",
                InfoBarSeverity.Success);
        }
    }

    private async void SnippetList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SnippetRow { Entry: var existing })
        {
            await ShowSnippetEditorDialogAsync(existing).ConfigureAwait(true);
        }
    }

    private void SnippetKeywordBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            CommitSnippetKeyword();
        }
    }

    private void SnippetKeywordBox_LostFocus(object sender, RoutedEventArgs e) => CommitSnippetKeyword();

    /// <summary>Stores the keyword in the box when it changed, then shows what was stored.</summary>
    /// <remarks>
    /// READ BACK RATHER THAN KEEPING WHAT WAS TYPED: a cleared box stores the default, so the box must
    /// show what was saved, not what was on screen. A refused keyword puts the stored one back.
    /// </remarks>
    private async void CommitSnippetKeyword()
    {
        var typed = SnippetKeywordBox.Text;
        if (_isApplyingSettings || string.Equals(typed, _settings.UserData.SnippetKeyword, StringComparison.Ordinal))
        {
            return;
        }

        var saved = await CommitVocabularyAsync(
            _session.Vocabulary.SetSnippetKeywordAsync(typed),
            UserDataRefreshScope.Snippets).ConfigureAwait(true);
        if (saved.Saved && saved.Value.Refusal is { } refusal)
        {
            ShowMessage("Keyword not saved", SnippetRefusalMessage(refusal, clash: null), InfoBarSeverity.Warning);
        }

        RefreshSnippetKeywordViews(force: true);
    }

    /// <summary>One sentence per refusal, written for the person who typed the thing; the macOS copy.</summary>
    private static string SnippetRefusalMessage(SnippetRefusal refusal, SnippetEntry? clash) => refusal switch
    {
        SnippetRefusal.TriggerEmpty =>
            "Give the snippet something to say. A trigger of only punctuation can never match.",
        SnippetRefusal.ExpansionEmpty =>
            "Add the text this snippet should paste. An empty snippet would delete the words you said.",
        SnippetRefusal.DuplicateTrigger =>
            $"You already have a snippet for those words: \u201C{clash?.Name}\u201D. Change one of them.",
        SnippetRefusal.KeywordNotOneWord =>
            "Your keyword has to be a single word. Pick one you would not say by accident.",
        _ => throw new ArgumentOutOfRangeException(nameof(refusal), refusal, "Not a snippet refusal."),
    };

    /// <summary>The keyword box and the visual spoken-command example built from what the person actually has.</summary>
    private void RefreshSnippetKeywordViews(bool force)
    {
        var keyword = _settings.UserData.SnippetKeyword;
        if (force || SnippetKeywordBox.FocusState == FocusState.Unfocused)
        {
            SnippetKeywordBox.Text = keyword;
        }

        var snippets = _settings.UserData.Snippets;
        var hasSnippet = snippets.Count > 0;
        var exampleTrigger = hasSnippet ? snippets[0].Name : "my email";
        var exampleExpansion = hasSnippet
            ? new SnippetRow(snippets[0]).BodyPreview
            : "john.doe@example.com";

        if (SnippetKeywordExample is not null)
        {
            SnippetKeywordExample.Text = hasSnippet
                ? $"For example, say \u201C{keyword} {exampleTrigger}\u201D and that snippet is pasted."
                : "Say your keyword, then the words you saved a snippet under, and that snippet is pasted.";
        }

        if (SnippetFlowKeywordChipText is not null)
        {
            SnippetFlowKeywordChipText.Text = keyword;
        }

        if (SnippetFlowTriggerChipText is not null)
        {
            SnippetFlowTriggerChipText.Text = exampleTrigger;
        }

        if (SnippetFlowExpansionText is not null)
        {
            SnippetFlowExpansionText.Text = exampleExpansion;
        }
    }
}
