using System.Runtime.InteropServices;
using EnviousWispr.Core.Polish;
using EnviousWispr.Core.Settings;
using EnviousWispr.Presentation;
using EnviousWispr.Services.UserData;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace EnviousWispr.App;

/// <summary>Dictionary word filtering, mass edit, alias suggestions, vocabulary packs, and word file/clipboard import and export.</summary>
public sealed partial class MainWindow
{
    /// <summary>Asks the app to find out what a word might be misheard as.</summary>
    /// <remarks>
    /// The window does not own the polish provider, so it asks rather than calls, exactly as the
    /// speed check does. The answer comes back through <see cref="SetAliasSuggestions"/>.
    /// </remarks>
    public event Action<string, IReadOnlyList<string>>? MishearingSuggestionsRequested;

    /// <summary>
    /// Asks what speech recognition is likely to hear instead of the word being taught.
    /// </summary>
    /// <remarks>
    /// THE WORD IT ASKS ABOUT IS THE ONE ON THE RIGHT, NOT THE LEFT. The left field is what the
    /// recogniser produces and the right one is what should be written instead, so the thing the
    /// model is being asked about is the CORRECT spelling, and every answer is a candidate for the
    /// left field. Getting this the wrong way round would ask a model for mishearings of a
    /// mishearing, which is both useless and completely plausible on screen.
    ///
    /// The aliases already pointing at this same word are sent along, so the model does not spend
    /// its five answers on ones the user already has.
    /// </remarks>
    private void SuggestAliasesButton_Click(object sender, RoutedEventArgs e)
    {
        var term = ReplacementBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(term))
        {
            ShowMessage(
                "Type the word first",
                "Put the correct spelling in the Write field, and this will suggest what speech "
                    + "recognition might hear instead.",
                InfoBarSeverity.Informational);
            return;
        }

        var existing = _settings.UserData.CustomWords
            .Where(entry => string.Equals(entry.Replacement, term, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.SpokenForm)
            .ToArray();

        SuggestAliasesButton.IsEnabled = false;
        SuggestedAliasesPanel.Children.Clear();
        SuggestedAliasesScroller.Visibility = Visibility.Collapsed;
        SetLiveRegion(SuggestAliasesStatusText, "Asking...", Visibility.Visible);
        MishearingSuggestionsRequested?.Invoke(term, existing);
    }

    /// <summary>Shows what came back, or says why nothing did.</summary>
    /// <remarks>
    /// EVERY OUTCOME GETS ITS OWN SENTENCE, because the user's next move differs in each. "The
    /// model had no ideas" means try a different word; "it did not answer" means check the
    /// connection and press again; "this option cannot do this" means change the polish choice.
    /// Collapsing them into one empty list is what makes a feature feel broken.
    ///
    /// A SUGGESTION IS A BUTTON, NOT A ROW THAT HAS ALREADY BEEN ADDED. Nothing reaches the user's
    /// word list until they click one. A wrong alias added quietly is a correction that fires on
    /// words they never said, and nothing on screen would connect it back to a suggestion.
    /// </remarks>
    public void SetAliasSuggestions(string term, MishearingAdvice advice)
    {
        ArgumentNullException.ThrowIfNull(advice);
        _activeWordDialogSuggestionSink?.Invoke(term, advice);
        SuggestAliasesButton.IsEnabled = true;
        SuggestedAliasesPanel.Children.Clear();
        // Shown by the SetLiveRegion call below, which sets text and visibility together.

        if (advice.Status != MishearingAdviceStatus.Suggested)
        {
            SuggestedAliasesScroller.Visibility = Visibility.Collapsed;
            SetLiveText(
                SuggestAliasesStatusText,
    advice.Status switch
                {
                    MishearingAdviceStatus.NothingUsable =>
                        "No likely mishearings came back for that word. Add one yourself when you hear it.",
                    MishearingAdviceStatus.NotSupported =>
                        "The polish option you have chosen cannot answer this. Pick the built-in model, "
                            + "Ollama, or a cloud provider to use suggestions.",
                    _ => "The suggestion did not come back. Check your polish settings and try again.",
            
                });
            return;
        }

        SetLiveText(SuggestAliasesStatusText, "Click one to add it. Nothing is saved until you do.");
        foreach (var suggestion in advice.Suggestions)
        {
            var candidate = suggestion;
            var button = new Button
            {
                Content = candidate,
                Style = (Style)Application.Current.Resources["BrandQuietButtonStyle"],
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                button, $"Add {candidate} as a mishearing of {term}");
            button.Click += async (_, _) => await AddSuggestedAliasAsync(candidate, term)
                .ConfigureAwait(true);
            SuggestedAliasesPanel.Children.Add(button);
        }

        SuggestedAliasesScroller.Visibility = Visibility.Visible;
    }

    /// <summary>Saves one accepted suggestion, and takes it off the screen.</summary>
    /// <remarks>
    /// The chip is removed once it is saved, so the panel shows only what is still on offer. Leaving
    /// an accepted one in place invites a second click that would do nothing visible, which reads as
    /// the button being broken.
    /// </remarks>
    private async Task AddSuggestedAliasAsync(string spokenForm, string replacement)
    {
        if (!await SaveCustomWordFromPickerAsync(spokenForm, replacement).ConfigureAwait(true))
        {
            // THE CHIP STAYS WHEN THE SAVE DID NOT. Removing it says the suggestion was taken, and
            // the panel then shows a shorter list of offers than the user actually still has - so
            // the one thing they could do about the failure disappears along with the failure.
            return;
        }

        ResetMatchStrictness();

        var accepted = SuggestedAliasesPanel.Children
            .OfType<Button>()
            .FirstOrDefault(button => Equals(button.Content, spokenForm));
        if (accepted is not null)
        {
            SuggestedAliasesPanel.Children.Remove(accepted);
        }

        if (SuggestedAliasesPanel.Children.Count == 0)
        {
            SuggestedAliasesScroller.Visibility = Visibility.Collapsed;
            SetLiveText(SuggestAliasesStatusText, "All of them added.");
        }
    }

    private async void AddWordButton_Click(object sender, RoutedEventArgs e)
    {
        var spoken = SpokenFormBox.Text.Trim();
        var replacement = ReplacementBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(spoken) || string.IsNullOrWhiteSpace(replacement))
        {
            ShowMessage("Both fields are required", "Enter the spoken form and the exact replacement.", InfoBarSeverity.Warning);
            return;
        }

        // CLEARED ONLY IF IT WAS SAVED. Emptying the boxes after a refused save throws away what
        // the person typed and leaves them looking at an error with nothing to retry.
        if (!await SaveCustomWordFromPickerAsync(spoken, replacement).ConfigureAwait(true))
        {
            return;
        }

        SpokenFormBox.Text = string.Empty;
        ReplacementBox.Text = string.Empty;
        ResetMatchStrictness();
    }

    /// <summary>Saves one word under whatever the picker currently says, replacing any twin.</summary>
    /// <remarks>
    /// ONE DOOR, BECAUSE THERE ARE TWO WAYS TO ADD A WORD. Suggested mishearings are accepted by
    /// their own button, and building the entry separately there meant a person could set Loose,
    /// press a suggestion, and get a word under the ordinary rule with the picker still saying Loose
    /// in front of them. Reading the picker inside the one method that saves is what makes the
    /// second path unable to forget - and it is what a gate can check, because it is the only place
    /// in the app allowed to build one of these.
    /// </remarks>
    private Task<bool> SaveCustomWordFromPickerAsync(string spokenForm, string replacement)
    {
        // THE PICKER CARRIES THE ANSWER ITSELF, not a position that has to be looked up. A mapping
        // from index to meaning is a contract between this file and the ORDER of three strings in
        // the markup, and reordering those strings changes what people choose while every test
        // stays green. Each choice now holds its own value, so there is nothing left to keep in step.
        //
        // READ HERE, ON THE UI THREAD, BEFORE THE SAVE. The transform below runs inside the writer's
        // gate, after a wait of unknown length on another save, on whatever thread that wait ends
        // on; a control read in there is a control read off the UI thread. The word is built now
        // and the transform only places it.
        var word = new CustomWordEntry(
            spokenForm,
            replacement,
            WordStrictnessComboBox.SelectedValue as MatchStrictness? ?? MatchStrictness.Default);
        return CommitVocabularyAsync(_session.Vocabulary.AddWordAsync(word), "Dictionary saved");
    }

    /// <summary>Puts the picker back to the ordinary rule after a word is saved.</summary>
    /// <remarks>
    /// A picker that keeps its last answer means the second word somebody adds is silently strict
    /// because the first one was, and the only sign of it is a column they have no reason to be
    /// watching.
    /// </remarks>
    private void ResetMatchStrictness() =>
        WordStrictnessComboBox.SelectedItem = WordStrictnessComboBox.Items
            .OfType<ComboBoxItem>()
            .First(item => item.Tag is MatchStrictness.Default or (int)MatchStrictness.Default);

    /// <summary>Selects every word, or clears the selection when they are all already chosen.</summary>
    /// <remarks>
    /// ONE PRESS FOR THE WHOLE LIST, AND THE SAME PRESS TO UNDO IT. Selecting twenty words by hand
    /// to remove them is the work this button exists to remove; a separate "deselect all" would be a
    /// second control for the same idea, and macOS uses the one toggle for both.
    /// </remarks>
    private void SelectAllWordsButton_Click(object sender, RoutedEventArgs e)
    {
        var shownCount = DictionaryList.Items.Count;
        if (DictionaryList.SelectionMode != ListViewSelectionMode.Multiple)
        {
            if (!_isDictionaryMassEdit)
            {
                MassEditWordsToggle_Click(sender, e);
            }
            else
            {
                DictionaryList.SelectionMode = ListViewSelectionMode.Multiple;
            }
        }

        if (DictionaryList.SelectionMode != ListViewSelectionMode.Multiple)
        {
            return;
        }

        if (DictionaryList.SelectedItems.Count == shownCount && shownCount > 0)
        {
            DictionaryList.SelectedItems.Clear();
            return;
        }

        DictionaryList.SelectAll();
    }

    /// <summary>Removes every selected word, asking first when it is more than one.</summary>
    /// <remarks>
    /// ONE AT A TIME WAS THE WHOLE FEATURE, AND IT IS NOT ONE macOS HAS. Clearing a list of twenty
    /// imported words meant twenty selections and twenty presses, and the only thing stopping a
    /// person doing it in one go was the list refusing to hold more than one selection.
    ///
    /// THE CONFIRMATION IS FOR THE PLURAL CASE ONLY. Asking before removing a single word turns the
    /// ordinary action into two steps for no gain; asking before removing fifteen is the difference
    /// between a mistake somebody notices and one they have to rebuild by hand.
    /// </remarks>
    private async void RemoveWordButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = DictionaryList.SelectionMode == ListViewSelectionMode.None
            ? Array.Empty<CustomWordEntry>()
            : DictionaryList.SelectedItems
                .Select(item => item is DictionaryWordRow row ? row.Entry : item as CustomWordEntry)
                .OfType<CustomWordEntry>()
                .ToArray();
        if (selected.Length == 0)
        {
            ShowMessage("Select a word first", "Choose the dictionary rows you want to remove.", InfoBarSeverity.Informational);
            return;
        }

        if (selected.Length > 1 && !await ConfirmAsync(
            $"Remove {selected.Length} words?",
            "They are deleted from this PC. Nothing else changes, and you can add them again.",
            "Remove them").ConfigureAwait(true))
        {
            return;
        }

        // THE COUNT IS WHAT WAS REALLY REMOVED, NOT WHAT WAS SELECTED. Removal matches by identity,
        // so a row that another change replaced while this was waiting is no longer the row that was
        // chosen - it is left alone, correctly, and saying "removed" over the top of that tells
        // somebody a word is gone when it is still there.
        var removal = await CommitVocabularyAsync(_session.Vocabulary.RemoveWordsAsync(selected)).ConfigureAwait(true);
        if (!removal.Saved)
        {
            return;
        }

        var removedCount = removal.Value;

        if (removedCount == 0)
        {
            ShowMessage(
                "Nothing was removed",
                "Those words changed while this was saving. Select them again to remove them.",
                InfoBarSeverity.Warning);
            return;
        }

        ShowMessage(
            removedCount == 1 ? "Dictionary entry removed" : $"{removedCount} dictionary entries removed",
            removedCount < selected.Length
                ? $"{selected.Length - removedCount} of them changed while this was saving and were left alone."
                : "The change was saved locally.",
            InfoBarSeverity.Success);
    }

    private async void FocusAddWordButton_Click(object sender, RoutedEventArgs e) =>
        await OpenAddWordDialogAsync().ConfigureAwait(true);

    /// <summary>
    /// Reads a word list and reports what it found, without changing anything the user did not see.
    /// </summary>
    /// <remarks>
    /// The message names every outcome that occurred, including the ones that are NOT failures.
    /// A count of additions alone would let a file that was half unreadable report a clean import,
    /// which is the failure this whole feature is shaped to avoid.
    ///
    /// A conflict adds nothing. Overwriting a correction someone tuned by hand, silently, because
    /// a file they downloaded happened to disagree, is worse than importing nothing at all - so
    /// conflicts are reported and skipped rather than resolved on the user's behalf.
    /// </remarks>
    /// <summary>
    /// Offers the shipped word lists, one menu item each.
    /// </summary>
    /// <remarks>
    /// Built from the catalogue rather than declared in markup, so a pack added later appears
    /// without anyone remembering to add a row - the failure mode of a hand-written menu is that
    /// it is correct until it silently is not.
    ///
    /// Each item carries the pack's DESCRIPTION as its help text rather than its name twice, so a
    /// screen reader user hears what the list is for rather than a label they already heard.
    /// </remarks>
    private bool _syncingVocabularyPacks;

    private void RefreshVocabularyPacksPanel()
    {
        if (VocabularyPacksList is null || _settings is null)
        {
            return;
        }

        var enabledIds = new HashSet<string>(
            _settings.UserData.EnabledVocabularyPackIds,
            StringComparer.OrdinalIgnoreCase);

        if (VocabularyPacksList.Children.Count != VocabularyPacks.All.Count)
        {
            VocabularyPacksList.Children.Clear();
            foreach (var pack in VocabularyPacks.All)
            {
                VocabularyPacksList.Children.Add(BuildVocabularyPackCard(pack));
            }
        }

        _syncingVocabularyPacks = true;
        try
        {
            foreach (var child in VocabularyPacksList.Children.OfType<Border>())
            {
                if (child.Tag is ToggleSwitch toggle && toggle.Tag is string packId)
                {
                    toggle.IsOn = enabledIds.Contains(packId);
                }
            }
        }
        finally
        {
            _syncingVocabularyPacks = false;
        }
    }

    private Border BuildVocabularyPackCard(VocabularyPack pack)
    {
        var icon = new FontIcon
        {
            Glyph = pack.IconGlyph,
            FontSize = 16,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["BrandAccentBrush"],
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var iconTile = new Border
        {
            Width = 36,
            Height = 36,
            Style = (Style)Application.Current.Resources["BrandIconTileStyle"],
            VerticalAlignment = VerticalAlignment.Center,
            Child = icon,
        };

        var titleText = new TextBlock
        {
            Text = pack.Name,
            Style = (Style)Application.Current.Resources["BrandRowLabelStyle"],
        };
        var descText = new TextBlock
        {
            Text = pack.Description,
            Style = (Style)Application.Current.Resources["BrandHelperStyle"],
            TextWrapping = TextWrapping.Wrap,
        };
        var summaryText = new TextBlock
        {
            Text = $"{pack.FixCount} fixes \u00B7 e.g. {string.Join(", ", pack.ExampleTerms)}",
            Style = (Style)Application.Current.Resources["BrandHelperStyle"],
            TextWrapping = TextWrapping.Wrap,
        };
        var infoStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 2,
            Children = { titleText, descText, summaryText },
        };

        var seeAllButton = new Button
        {
            Content = "See all",
            Style = (Style)Application.Current.Resources["BrandQuietButtonStyle"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(seeAllButton, $"See all words in {pack.Name}");
        seeAllButton.Click += async (_, _) => await ShowPackDetailDialogAsync(pack).ConfigureAwait(true);

        var toggle = new ToggleSwitch
        {
            Style = (Style)Application.Current.Resources["BrandToggleSwitchStyle"],
            MinWidth = 0,
            Tag = pack.Id,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLabeledBy(toggle, titleText);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(toggle, pack.Description);
        toggle.Toggled += async (_, _) =>
        {
            if (_syncingVocabularyPacks || _settings is null)
            {
                return;
            }

            var enabled = toggle.IsOn;
            var saveResult = await _session.Vocabulary.SetVocabularyPackEnabledAsync(pack.Id, enabled)
                .ConfigureAwait(true);
            if (!saveResult.Saved)
            {
                _syncingVocabularyPacks = true;
                toggle.IsOn = !enabled;
                _syncingVocabularyPacks = false;
                if (saveResult.Refusal is { } refusal)
                {
                    ShowSettingsRefusal(refusal);
                }

                return;
            }

            PublishSettings();
            ApplySettingsToControls();
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { seeAllButton, toggle },
        };

        var rowGrid = new Grid { ColumnSpacing = 12 };
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rowGrid.Children.Add(iconTile);
        Grid.SetColumn(infoStack, 1);
        rowGrid.Children.Add(infoStack);
        Grid.SetColumn(actions, 2);
        rowGrid.Children.Add(actions);

        return new Border
        {
            Padding = new Thickness(14, 12, 14, 12),
            Style = (Style)Application.Current.Resources["BrandSettingsCardStyle"],
            Child = rowGrid,
            Tag = toggle,
        };
    }

    private Button BuildOfferButton(ImportConflictOffer offer)
    {
        var button = new Button { Content = offer.Label };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, offer.Label);
        button.Click += async (_, _) =>
        {
            if (await ReplaceConflictsAsync(offer.Replacements).ConfigureAwait(true))
            {
                return;
            }

            // THE OFFER COMES BACK WHEN THE SAVE DID NOT TAKE. The failure message clears the bar's
            // button along with the old sentence, so without this a refused replacement leaves the
            // user reading an error with the one thing that would retry it gone. An earlier draft
            // cleared the button BEFORE saving, which had the same end state and no way back.
            OperationInfoBar.ActionButton = BuildOfferButton(offer);
        };
        return button;
    }

    private async void ImportWordsButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".csv");
        picker.FileTypeFilter.Add(".txt");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        string text;
        try
        {
            text = await File.ReadAllTextAsync(file.Path).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowMessage(
                "That file could not be read",
                "Windows would not open it. Nothing was changed.",
                InfoBarSeverity.Error);
            return;
        }

        await ApplyWordListAsync(text).ConfigureAwait(true);
    }

    /// <summary>Reads one word list, adds what is new, and offers what it could not decide.</summary>
    /// <remarks>
    /// ONE PATH FOR EVERY WORD LIST, HOWEVER IT ARRIVED. A file and a paste are the same list with
    /// two ways in, and two copies of this would be two places for the conflict offer to be
    /// forgotten from.
    ///
    /// A CONFLICT IS OFFERED RATHER THAN DECIDED, AND THAT IS THE ROW THIS CLOSES. Until now a word
    /// the list corrected differently from the user's own was counted and left alone, which tells
    /// somebody their curated list was ignored and gives them nothing to do about it but retype
    /// words they already have written down. Silently overwriting a correction tuned by hand is
    /// worse, so neither is chosen for them: the message carries a button and they pick.
    /// </remarks>
    private async Task ApplyWordListAsync(string text)
    {
        // THE PLAN IS THE CONTROLLER'S, COMPUTED INSIDE THE GATE, and the message is chosen from
        // what came back.
        //
        // THE ITEMISED DESCRIPTION IS USED ON BOTH OUTCOMES. An earlier version fell back to the
        // generic save message the moment ONE word imported, so a hundred-line file with sixty good
        // rows and forty unreadable ones said "the change was saved locally" and the forty were
        // never mentioned.
        var outcome = await CommitVocabularyAsync(_session.VocabularyImport.ImportAsync(text)).ConfigureAwait(true);
        if (!outcome.Saved)
        {
            // The save refused and has already said why. Speaking again here would paint over that
            // with a success, and offering to replace corrections nothing imported would be worse.
            return;
        }

        var committed = outcome.Value;
        ShowMessage(
            committed.Additions.Count == 0 ? "No new words to add" : "Words imported",
            DescribeImport(committed),
            committed.Additions.Count == 0 ? InfoBarSeverity.Informational : InfoBarSeverity.Success,
            ReplaceConflictsAction(committed));
    }

    /// <summary>Pastes a word list straight out of the clipboard.</summary>
    /// <remarks>
    /// THE SHORTEST ROUTE FROM SOMEBODY ELSE'S LIST INTO THIS ONE. macOS has the same path, and
    /// without it a person with a list in a spreadsheet or an email has to save a file first for no
    /// reason the app can explain.
    ///
    /// It reads the clipboard and does not touch it: the clipboard is somewhere a dictation product
    /// has to be a good citizen, and this app already guards it carefully on the delivery path.
    /// </remarks>
    private async void PasteWordsButton_Click(object sender, RoutedEventArgs e)
    {
        string text;
        try
        {
            var clipboard = Clipboard.GetContent();
            text = clipboard.Contains(StandardDataFormats.Text)
                ? await clipboard.GetTextAsync()
                : string.Empty;
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            // Another app can hold the clipboard open, and Windows says so by failing the read.
            // UnauthorizedAccessException is the one that mattered: .NET maps E_ACCESSDENIED to it
            // rather than to COMException, so protected clipboard content escaped this handler -
            // and an async void handler that throws takes the process down with it.
            ShowMessage(
                "The clipboard could not be read",
                "Another app may be using it. Copy the list again and retry. Nothing was changed.",
                InfoBarSeverity.Error);
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            ShowMessage(
                "There is no text on the clipboard",
                "Copy a list of word pairs first. Each line needs a spoken form and a replacement, "
                    + "separated by a comma, a tab, or an equals sign.",
                InfoBarSeverity.Informational);
            return;
        }

        await ApplyWordListAsync(text).ConfigureAwait(true);
    }

    /// <summary>The offer to take the list's version of the words the user corrects differently.</summary>
    private static ImportConflictOffer? ReplaceConflictsAction(CustomWordImportPlan plan)
    {
        if (plan.Conflicts.Count == 0)
        {
            return null;
        }

        var word = plan.Conflicts.Count == 1 ? "correction" : "corrections";
        return new ImportConflictOffer($"Replace my {plan.Conflicts.Count} {word}", plan.Conflicts);
    }

    private Task<bool> ReplaceConflictsAsync(IReadOnlyList<CustomWordEntry> replacements)
    {
        var word = replacements.Count == 1 ? "correction" : "corrections";
        return CommitVocabularyAsync(
            _session.VocabularyImport.ReplaceConflictsAsync(replacements),
            "Corrections replaced",
            $"{replacements.Count} {word} now {(replacements.Count == 1 ? "matches" : "match")} the list you imported.");
    }

    /// <summary>A button a message can carry, and what pressing it applies.</summary>
    private sealed record ImportConflictOffer(
        string Label, IReadOnlyList<CustomWordEntry> Replacements);

    private async void ExportWordsButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker
        {
            SuggestedFileName = "EnviousWispr-words",
            DefaultFileExtension = ".csv",
        };
        picker.FileTypeChoices.Add("Word list", [".csv"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null || RefuseExportIntoDataFolder(file.Path))
        {
            return;
        }

        try
        {
            // A NEW FILE PUT AT THE CHOSEN NAME, never a write through it (ExportFileWriter).
            await EnviousWispr.Services.UserData.ExportFileWriter.WriteReplacingAsync(
                file.Path,
                CustomWordImport.Write(_settings.UserData.CustomWords)).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowMessage(
                "That file could not be written",
                "Windows would not save it. Your words are unchanged.",
                InfoBarSeverity.Error);
            return;
        }

        ShowMessage(
            "Words exported",
            "Your word list was saved. You can edit it and import it back.",
            InfoBarSeverity.Success);
    }

    /// <summary>
    /// Says what happened to EVERY line, not only the ones that worked.
    /// </summary>
    /// <remarks>
    /// Built by naming each outcome that occurred rather than by reporting a total. A file that is
    /// half unreadable and half duplicates would otherwise produce "0 words added", which reads as
    /// nothing to do rather than as a file that needs fixing.
    /// </remarks>
    private static string DescribeImport(CustomWordImportPlan plan)
    {
        var parts = new List<string>();
        var already = plan.Lines.Count(line => line.Outcome == ImportedWordOutcome.AlreadyPresent);
        if (plan.Additions.Count > 0)
        {
            parts.Add($"{plan.Additions.Count} added");
        }

        if (already > 0)
        {
            parts.Add($"{already} you already had");
        }

        if (plan.ConflictCount > 0)
        {
            parts.Add($"{plan.ConflictCount} left alone because you already correct them differently");
        }

        if (plan.UnreadableCount > 0)
        {
            // No trailing full stop: the join below adds one, and the first version produced
            // "separated by a comma..". And the list of separators is the parser's rather than a
            // remembered one - it accepts a tab and an equals sign too, and a user with a
            // tab-separated file was being told the wrong thing about a file it would have taken.
            // "1 lines could not be read" - the only clause in this message with a noun that has
            // to agree with its count. The others are correct at any number: "1 added", "1 you
            // already had", "1 left alone because you already correct them differently". Checked
            // rather than assumed, so this is one line rather than a pattern to sweep for.
            var lineWord = plan.UnreadableCount == 1 ? "line" : "lines";
            parts.Add(
                $"{plan.UnreadableCount} {lineWord} could not be read. Each needs a spoken form "
                + "and a replacement, separated by a comma, a tab, or an equals sign");
        }

        return parts.Count == 0
            ? "That file had no word pairs in it."
            : string.Join(". ", parts) + ".";
    }

    /// <summary>Saves the user's words and snippets, and says whether it worked.</summary>
    /// <remarks>
    /// IT RETURNS THE OUTCOME BECAUSE A CALLER THAT SPEAKS AFTER IT NEEDS TO KNOW. TrySaveAsync
    /// already shows an error of its own when the save is refused - the word ceiling, for one - and
    /// a caller that then showed its own success message would paint over that error with the
    /// opposite of the truth. Measured on the import path, where a refused save reported "Words
    /// imported" and offered to replace corrections that had not been imported.
    /// </remarks>
    /// <summary>Saves user data and returns what the change worked out while it held the gate.</summary>
    /// <remarks>
    /// AN IMPORT DECIDES WHAT TO ADD BY LOOKING AT WHAT IS ALREADY THERE, so that decision is a
    /// question about the CURRENT words and has to be answered inside the gate. Answered outside, the
    /// plan describes a list that may have changed - and saving its result then overwrites whatever
    /// changed it. The value comes back so the message describes what was actually stored.
    /// </remarks>
    /// <summary>Takes a vocabulary change's answer onto the page: the refusal said, or the settings published and the controls refreshed.</summary>
    private async Task<SettingsSaveResult<T>> CommitVocabularyAsync<T>(Task<SettingsSaveResult<T>> change)
    {
        var result = await change.ConfigureAwait(true);
        if (!result.Saved)
        {
            ShowSettingsRefusal(result.Refusal!.Value);
            return result;
        }

        PublishSettings();
        ApplySettingsToControls();
        return result;
    }

    /// <summary>The same, for a change with nothing to say beyond whether it worked, with the message a person sees.</summary>
    private async Task<bool> CommitVocabularyAsync(
        Task<SettingsSaveResult> change,
        string title,
        string message = "The change was saved locally.")
    {
        var result = await change.ConfigureAwait(true);
        if (!result.Saved)
        {
            ShowSettingsRefusal(result.Refusal!.Value);
            return false;
        }

        PublishSettings();
        ApplySettingsToControls();
        ShowMessage(title, message, InfoBarSeverity.Success);
        RefreshReusableUserDataViews();
        return true;
    }

    private void DictionaryTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            SelectDictionaryTab(tag);
        }
    }

    private void SelectDictionaryTab(string tabKey)
    {
        DictTabYourWords.Style = (Style)Application.Current.Resources[tabKey == "your-words" ? "BrandTabStripActiveButtonStyle" : "BrandTabStripButtonStyle"];
        DictTabPacks.Style = (Style)Application.Current.Resources[tabKey == "packs" ? "BrandTabStripActiveButtonStyle" : "BrandTabStripButtonStyle"];
        DictTabLearnFrom.Style = (Style)Application.Current.Resources[tabKey == "learn-from" ? "BrandTabStripActiveButtonStyle" : "BrandTabStripButtonStyle"];
        DictTabQuickAdd.Style = (Style)Application.Current.Resources[tabKey == "quick-add" ? "BrandTabStripActiveButtonStyle" : "BrandTabStripButtonStyle"];

        DictPanelYourWords.Visibility = tabKey == "your-words" ? Visibility.Visible : Visibility.Collapsed;
        DictPanelPacks.Visibility = tabKey == "packs" ? Visibility.Visible : Visibility.Collapsed;
        DictPanelLearnFrom.Visibility = tabKey == "learn-from" ? Visibility.Visible : Visibility.Collapsed;
        DictPanelQuickAdd.Visibility = tabKey == "quick-add" ? Visibility.Visible : Visibility.Collapsed;
    }

    private string _wordFilterCategory = "all";
    private bool _isDictionaryMassEdit;

    private void WordFilterChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            _wordFilterCategory = tag;
            var active = (Style)Application.Current.Resources["BrandTabStripActiveButtonStyle"];
            var normal = (Style)Application.Current.Resources["BrandTabStripButtonStyle"];
            WordFilterAllChip.Style = tag == "all" ? active : normal;
            WordFilterGeneralChip.Style = tag == "general" ? active : normal;
            WordFilterPersonChip.Style = tag == "person" ? active : normal;
            WordFilterBrandChip.Style = tag == "brand" ? active : normal;
            WordFilterAcronymChip.Style = tag == "acronym" ? active : normal;
            WordFilterDomainChip.Style = tag == "domain" ? active : normal;
            RefreshDictionaryView();
        }
    }

    private void MassEditWordsToggle_Click(object sender, RoutedEventArgs e)
    {
        _isDictionaryMassEdit = !_isDictionaryMassEdit;
        if (DictionaryList.SelectionMode != ListViewSelectionMode.None)
        {
            DictionaryList.SelectedItems.Clear();
        }

        DictionaryList.SelectionMode = _isDictionaryMassEdit
            ? ListViewSelectionMode.Multiple
            : ListViewSelectionMode.None;
        DictionaryMassEditBar.Visibility = _isDictionaryMassEdit ? Visibility.Visible : Visibility.Collapsed;
        MassEditWordsToggle.Style = (Style)Application.Current.Resources[
            _isDictionaryMassEdit ? "BrandQuietActiveButtonStyle" : "BrandQuietButtonStyle"];
        UpdateSelectionDependentButtons();
    }

    private void DictionarySearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
        RefreshDictionaryView();

    private void RefreshDictionaryView()
    {
        var allWords = _settings?.UserData?.CustomWords ?? (IReadOnlyList<CustomWordEntry>)Array.Empty<CustomWordEntry>();
        var query = DictionarySearchBox?.Text?.Trim() ?? string.Empty;
        var list = VocabularyPresenter.FilterWords(allWords, _wordFilterCategory, query)
            .Select(entry => new DictionaryWordRow(entry))
            .ToArray();

        if (DictionaryList is not null)
        {
            DictionaryList.ItemsSource = list;
            UpdateListAndEmptyStateVisibility(DictionaryList, DictionaryEmptyState, list.Length);
        }
        if (WordCountText is not null)
        {
            var total = allWords.Count;
            var wordUnit = total == 1 ? "WORD" : "WORDS";
            WordCountText.Text = list.Length == total
                ? $"{total} {wordUnit}"
                : $"Showing {list.Length} of {total} {wordUnit}";
        }
        UpdateSelectionDependentButtons();
    }

    private async void DictionaryEnabledToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isApplyingSettings || _settings is null || WordCorrectionToggle is null)
        {
            return;
        }

        var isEnabled = DictionaryEnabledToggle.IsOn;
        WordCorrectionToggle.IsOn = isEnabled;
        await TriggerAutoSaveAsync().ConfigureAwait(true);
    }
}
