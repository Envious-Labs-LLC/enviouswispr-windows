using EnviousWispr.Core.Settings;
using EnviousWispr.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace EnviousWispr.App;

/// <summary>One row of the dictionary list carrying its underlying entry plus clean 2-line display fields.</summary>
public sealed class DictionaryWordRow
{
    public DictionaryWordRow(CustomWordEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Entry = entry;
        CategoryLabel = WordCategoryFormatting.Label(entry.Category);
        AliasesText = entry.Aliases.Count > 0
            ? string.Join(", ", entry.Aliases)
            : "No extra mishearings (matches canonical word)";
        StrictnessLabel = entry.Strictness switch
        {
            MatchStrictness.Loose => "Loose",
            MatchStrictness.Strict => "Strict",
            _ => "Balanced",
        };
    }

    public CustomWordEntry Entry { get; }

    public string Replacement => Entry.Replacement;

    public string CategoryLabel { get; }

    public string AliasesText { get; }

    public string StrictnessLabel { get; }

    public Visibility NonDefaultStrictnessVisibility =>
        Entry.Strictness == MatchStrictness.Default ? Visibility.Collapsed : Visibility.Visible;

    public Visibility AutoLearnedVisibility =>
        Entry.IsAutoLearned ? Visibility.Visible : Visibility.Collapsed;

    public override string ToString() => Entry.ToString();
}

/// <summary>In-place add/edit dialogs for Your Words, pack inspection, and Quick Add shortcut guidance.</summary>
public sealed partial class MainWindow
{
    internal async void OpenAddWordDialogButton_Click(object sender, RoutedEventArgs e) =>
        await OpenAddWordDialogAsync().ConfigureAwait(true);

    private async Task OpenAddWordDialogAsync() =>
        await ShowWordEditorDialogAsync(existing: null).ConfigureAwait(true);

    internal async void EditWordRowButton_Click(object sender, RoutedEventArgs e)
    {
        var existing = sender switch
        {
            Button { Tag: CustomWordEntry direct } => direct,
            Button { Tag: DictionaryWordRow row } => row.Entry,
            _ => null,
        };
        if (existing is null)
        {
            return;
        }

        await ShowWordEditorDialogAsync(existing).ConfigureAwait(true);
    }

    private async Task ShowWordEditorDialogAsync(CustomWordEntry? existing)
    {
        var headerTemplate = (DataTemplate)Application.Current.Resources["BrandControlHeaderTemplate"];
        var replacementBox = new TextBox
        {
            Header = "Word",
            HeaderTemplate = headerTemplate,
            MaxLength = 256,
            PlaceholderText = "e.g., EnviousWispr, Kubernetes",
            Text = existing?.Replacement ?? string.Empty,
        };

        var categoryBox = new ComboBox
        {
            Header = "Category",
            HeaderTemplate = headerTemplate,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedValuePath = "Tag",
        };
        foreach (var cat in WordCategoryFormatting.All)
        {
            categoryBox.Items.Add(new ComboBoxItem { Content = WordCategoryFormatting.Label(cat), Tag = cat });
        }

        var categoryManuallyChosen = existing is not null;
        var initialCategory = existing?.Category ?? WordCategoryFormatting.Classify(replacementBox.Text);
        categoryBox.SelectedItem = categoryBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag is WordCategory c && c == initialCategory)
            ?? categoryBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
        categoryBox.SelectionChanged += (_, _) =>
        {
            if (categoryBox.FocusState != FocusState.Unfocused)
            {
                categoryManuallyChosen = true;
            }
        };

        // 3-way segmented strictness picker (Balanced, Loose, Strict) matching macOS CustomWordEditSheet
        var selectedStrictness = existing?.Strictness ?? MatchStrictness.Default;
        var strictnessCaption = new TextBlock
        {
            Style = (Style)Application.Current.Resources["BrandHelperStyle"],
            TextWrapping = TextWrapping.Wrap,
        };
        var strictnessGrid = new Grid { ColumnSpacing = 4 };
        strictnessGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        strictnessGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        strictnessGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var strictnessChoices = new (MatchStrictness Value, string Label, string Caption)[]
        {
            (MatchStrictness.Default, "Balanced", "Matches exact aliases and close phonetic variants."),
            (MatchStrictness.Loose, "Loose", "Catches broader misspellings from noisy dictation."),
            (MatchStrictness.Strict, "Strict", "Only fixes when the speech closely matches your alias."),
        };
        var strictnessButtons = new List<(MatchStrictness Value, Button Button, string Caption)>(3);

        void SyncStrictnessButtons()
        {
            var activeStyle = (Style)Application.Current.Resources["BrandSegmentedActiveButtonStyle"];
            var normalStyle = (Style)Application.Current.Resources["BrandSegmentedButtonStyle"];
            foreach (var (value, button, caption) in strictnessButtons)
            {
                var isSelected = value == selectedStrictness;
                button.Style = isSelected ? activeStyle : normalStyle;
                if (isSelected)
                {
                    strictnessCaption.Text = caption;
                }
            }
        }

        for (var i = 0; i < strictnessChoices.Length; i++)
        {
            var choice = strictnessChoices[i];
            var btn = new Button
            {
                Content = choice.Label,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(btn, $"Match strictness: {choice.Label}");
            btn.Click += (_, _) =>
            {
                selectedStrictness = choice.Value;
                SyncStrictnessButtons();
            };
            Grid.SetColumn(btn, i);
            strictnessGrid.Children.Add(btn);
            strictnessButtons.Add((choice.Value, btn, choice.Caption));
        }

        SyncStrictnessButtons();

        var strictnessSection = Stack(
            Text("Match strictness", "BrandSubsectionEyebrowStyle"),
            strictnessGrid,
            strictnessCaption);

        // Multi-alias chips + inline input + offline & optional AI-polish suggestions
        var aliases = new List<string>(existing?.Aliases ?? Array.Empty<string>());
        var aiSuggestions = new List<string>();
        var aliasInputBox = new TextBox
        {
            MaxLength = 256,
            PlaceholderText = "+ Add mishearing (e.g., envy wisper)",
        };
        var addAliasButton = new Button
        {
            Content = "Add",
            Style = (Style)Application.Current.Resources["BrandQuietButtonStyle"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        var aliasChipsHost = new StackPanel { Spacing = 6 };
        var suggestionChipsHost = new StackPanel { Spacing = 6 };
        var suggestionsHeader = Text("Suggestions", "BrandHelperStyle");
        var suggestMoreButton = new Button
        {
            Content = "Ask AI polish for more",
            Style = (Style)Application.Current.Resources["BrandQuietButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var polishSuggestionStatus = new TextBlock
        {
            Style = (Style)Application.Current.Resources["BrandHelperStyle"],
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        void RebuildAliasChips()
        {
            aliasChipsHost.Children.Clear();
            if (aliases.Count == 0)
            {
                aliasChipsHost.Children.Add(Text(
                    "No extra mishearings added yet. The canonical word itself is still protected.",
                    "BrandHelperStyle"));
                return;
            }

            foreach (var row in WrapChips(aliases, alias =>
            {
                var chip = new Button
                {
                    Content = $"{alias}  \u2715",
                    Style = (Style)Application.Current.Resources["BrandQuietButtonStyle"],
                    Padding = new Thickness(10, 4, 10, 4),
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(chip, $"Remove mishearing {alias}");
                var captured = alias;
                chip.Click += (_, _) =>
                {
                    aliases.RemoveAll(item => string.Equals(item, captured, StringComparison.OrdinalIgnoreCase));
                    RebuildAliasChips();
                    RebuildSuggestionChips();
                };
                return chip;
            }))
            {
                aliasChipsHost.Children.Add(row);
            }
        }

        void RebuildSuggestionChips()
        {
            suggestionChipsHost.Children.Clear();
            var offline = OfflineAliasSuggester.Suggest(replacementBox.Text, aliases, maxCount: 6);
            var combined = offline
                .Concat(aiSuggestions)
                .Where(s => !string.IsNullOrWhiteSpace(s) &&
                            !aliases.Contains(s.Trim(), StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToArray();

            suggestionsHeader.Visibility = combined.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            suggestionChipsHost.Visibility = combined.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var row in WrapChips(combined, suggestion =>
            {
                var chip = new Button
                {
                    Content = $"+ {suggestion}",
                    Style = (Style)Application.Current.Resources["BrandQuietButtonStyle"],
                    Padding = new Thickness(10, 4, 10, 4),
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(chip, $"Add suggested mishearing {suggestion}");
                var captured = suggestion;
                chip.Click += (_, _) =>
                {
                    if (!aliases.Contains(captured, StringComparer.OrdinalIgnoreCase))
                    {
                        aliases.Add(captured);
                        RebuildAliasChips();
                        RebuildSuggestionChips();
                    }
                };
                return chip;
            }))
            {
                suggestionChipsHost.Children.Add(row);
            }
        }

        void CommitTypedAlias()
        {
            var typed = aliasInputBox.Text.Trim();
            if (typed.Length == 0)
            {
                aliasInputBox.Focus(FocusState.Programmatic);
                return;
            }

            if (!aliases.Contains(typed, StringComparer.OrdinalIgnoreCase))
            {
                aliases.Add(typed);
            }

            aliasInputBox.Text = string.Empty;
            RebuildAliasChips();
            RebuildSuggestionChips();
            aliasInputBox.Focus(FocusState.Programmatic);
        }

        addAliasButton.Click += (_, _) => CommitTypedAlias();
        aliasInputBox.KeyDown += (_, keyArgs) =>
        {
            if (keyArgs.Key == VirtualKey.Enter)
            {
                keyArgs.Handled = true;
                CommitTypedAlias();
            }
        };

        var aliasEntryGrid = new Grid { ColumnSpacing = 8 };
        aliasEntryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        aliasEntryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        aliasEntryGrid.Children.Add(aliasInputBox);
        Grid.SetColumn(addAliasButton, 1);
        aliasEntryGrid.Children.Add(addAliasButton);

        CancellationTokenSource? activeSuggestionCts = null;
        var suggestionGeneration = 0;
        var isDialogOpen = true;

        void EndSuggestionSession()
        {
            isDialogOpen = false;
            suggestionGeneration++;
            var previous = Interlocked.Exchange(ref activeSuggestionCts, null);
            if (previous is not null)
            {
                try
                {
                    previous.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        replacementBox.TextChanged += (_, _) =>
        {
            suggestionGeneration++;
            var previous = Interlocked.Exchange(ref activeSuggestionCts, null);
            if (previous is not null)
            {
                try
                {
                    previous.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            if (!categoryManuallyChosen)
            {
                var inferred = WordCategoryFormatting.Classify(replacementBox.Text);
                categoryBox.SelectedItem = categoryBox.Items
                    .OfType<ComboBoxItem>()
                    .FirstOrDefault(item => item.Tag is WordCategory c && c == inferred)
                    ?? categoryBox.SelectedItem;
            }

            suggestMoreButton.IsEnabled = true;
            polishSuggestionStatus.Text = string.Empty;
            polishSuggestionStatus.Visibility = Visibility.Collapsed;
            aiSuggestions.Clear();
            RebuildSuggestionChips();
        };

        var polishReady = _settings?.Preferences.Polish.Provider is { } provider && provider != PolishProvider.None;
        suggestMoreButton.Visibility = polishReady
            ? Visibility.Visible
            : Visibility.Collapsed;
        suggestMoreButton.Click += async (_, _) =>
        {
            var canonical = replacementBox.Text.Trim();
            if (canonical.Length == 0)
            {
                polishSuggestionStatus.Text = "Type the canonical word first.";
                polishSuggestionStatus.Visibility = Visibility.Visible;
                return;
            }

            var requestGeneration = ++suggestionGeneration;
            var cts = new CancellationTokenSource();
            var previous = Interlocked.Exchange(ref activeSuggestionCts, cts);
            if (previous is not null)
            {
                try
                {
                    previous.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            suggestMoreButton.IsEnabled = false;
            polishSuggestionStatus.Text = "Asking AI polish for variants...";
            polishSuggestionStatus.Visibility = Visibility.Visible;

            try
            {
                MishearingAdvice advice;
                try
                {
                    advice = await _mishearingAdvisor.SuggestAsync(canonical, aliases.ToArray(), cts.Token)
                        .ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    if (isDialogOpen && requestGeneration == suggestionGeneration)
                    {
                        suggestMoreButton.IsEnabled = true;
                        polishSuggestionStatus.Text = string.Empty;
                        polishSuggestionStatus.Visibility = Visibility.Collapsed;
                    }
                    return;
                }
                catch (Exception)
                {
                    advice = MishearingAdvice.None(MishearingAdviceStatus.Failed);
                }

                if (!isDialogOpen
                    || requestGeneration != suggestionGeneration
                    || cts.IsCancellationRequested
                    || !string.Equals(canonical, replacementBox.Text.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    if (isDialogOpen && requestGeneration == suggestionGeneration)
                    {
                        suggestMoreButton.IsEnabled = true;
                    }
                    return;
                }

                suggestMoreButton.IsEnabled = true;
                if (advice.Status == MishearingAdviceStatus.Suggested && advice.Suggestions.Count > 0)
                {
                    aiSuggestions.Clear();
                    aiSuggestions.AddRange(advice.Suggestions);
                    polishSuggestionStatus.Visibility = Visibility.Collapsed;
                    RebuildSuggestionChips();
                }
                else
                {
                    polishSuggestionStatus.Text = advice.Status == MishearingAdviceStatus.NotSupported
                        ? "Your current polish option cannot suggest variants. Offline suggestions remain available."
                        : "No extra AI polish variants returned. Offline suggestions remain available.";
                    polishSuggestionStatus.Visibility = Visibility.Visible;
                }
            }
            finally
            {
                Interlocked.CompareExchange(ref activeSuggestionCts, null, cts);
                cts.Dispose();
            }
        };

        RebuildAliasChips();
        RebuildSuggestionChips();

        var errorText = new TextBlock
        {
            Style = (Style)Application.Current.Resources["BrandErrorHelperStyle"],
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        var body = Stack(
            replacementBox,
            categoryBox,
            strictnessSection,
            Text("When you say (mishearings)", "BrandSubsectionEyebrowStyle"),
            aliasChipsHost,
            aliasEntryGrid,
            suggestionsHeader,
            suggestionChipsHost,
            suggestMoreButton,
            polishSuggestionStatus,
            errorText);

        var scroll = new ScrollViewer
        {
            Content = body,
            MaxHeight = 520,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var dialog = new ContentDialog
        {
            XamlRoot = WindowRoot.XamlRoot ?? Content.XamlRoot,
            RequestedTheme = WindowRoot.ActualTheme,
            Title = existing is null ? "Add word" : "Edit word",
            PrimaryButtonText = existing is null ? "Add word" : "Save",
            SecondaryButtonText = existing is null ? string.Empty : "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = scroll,
        };

        var isSaving = false;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            if (isSaving)
            {
                args.Cancel = true;
                return;
            }

            if (!string.IsNullOrWhiteSpace(aliasInputBox.Text) &&
                !aliases.Contains(aliasInputBox.Text.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                aliases.Add(aliasInputBox.Text.Trim());
                aliasInputBox.Text = string.Empty;
                RebuildAliasChips();
            }

            if (string.IsNullOrWhiteSpace(replacementBox.Text))
            {
                errorText.Text = "Enter the canonical word you want written.";
                errorText.Visibility = Visibility.Visible;
                args.Cancel = true;
                return;
            }

            var deferral = args.GetDeferral();
            isSaving = true;
            dialog.IsPrimaryButtonEnabled = false;
            try
            {
                var replacement = replacementBox.Text.Trim();
                var category = categoryBox.SelectedValue as WordCategory? ?? WordCategory.General;

                var saveResult = existing is null
                    ? await _session.Vocabulary.AddMultiAliasWordAsync(aliases, replacement, selectedStrictness, category).ConfigureAwait(true)
                    : await _session.Vocabulary.UpdateMultiAliasWordAsync(existing, aliases, replacement, selectedStrictness, category).ConfigureAwait(true);

                if (!saveResult.Saved)
                {
                    if (saveResult.Refusal != SettingsSaveRefusal.Closing)
                    {
                        errorText.Text = SettingsRefusalDialogMessage(saveResult.Refusal);
                        errorText.Visibility = Visibility.Visible;
                        args.Cancel = true;
                    }

                    return;
                }

                if (saveResult.Value.Refusal is { } wordRefusal)
                {
                    errorText.Text = wordRefusal switch
                    {
                        WordEditRefusal.DuplicateSpokenForm =>
                            $"You already have a word using \u201C{saveResult.Value.Clash?.SpokenForm}\u201D (\u2192 {saveResult.Value.Clash?.Replacement}). Remove the duplicate alias or edit that entry.",
                        WordEditRefusal.DuplicateReplacement =>
                            $"You already have an entry for \u201C{saveResult.Value.Clash?.Replacement}\u201D. Edit that entry to combine its mishearings.",
                        _ => "That word changed or was removed while you were editing it. Cancel and try again.",
                    };
                    errorText.Visibility = Visibility.Visible;
                    args.Cancel = true;
                    return;
                }

                PublishSettings();
                if (existing is { IsAutoLearned: true, LearnedAliases.Count: > 0 })
                {
                    var remainingAliases = aliases.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var removedLearned = existing.LearnedAliases
                        .Where(alias => !remainingAliases.Contains(alias) ||
                                        !string.Equals(existing.Replacement, replacement, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    if (removedLearned.Length > 0)
                    {
                        LearnedAliasesRemovedFromDictionary?.Invoke(existing.Replacement, removedLearned);
                        RefreshLearnMemoryUi();
                    }
                }
                ApplyUserDataRefresh(UserDataRefreshScope.Dictionary);
                ShowMessage("Dictionary saved", "The change was saved locally.", InfoBarSeverity.Success);
            }
            finally
            {
                isSaving = false;
                dialog.IsPrimaryButtonEnabled = true;
                deferral.Complete();
            }
        };

        dialog.Closed += (_, _) => EndSuggestionSession();
        ContentDialogResult result;
        try
        {
            result = await dialog.ShowAsync().AsTask().ConfigureAwait(true);
        }
        finally
        {
            EndSuggestionSession();
        }

        if (result == ContentDialogResult.Secondary && existing is not null)
        {
            if (await ConfirmAsync(
                    $"Delete \u201C{existing.Replacement}\u201D?",
                    "This removes the word and all its mishearings from Your Words.",
                    "Delete").ConfigureAwait(true))
            {
                var removed = await CommitVocabularyAsync(_session.Vocabulary.RemoveWordsAndReportAsync(new[] { existing }))
                    .ConfigureAwait(true);
                if (removed.Saved && removed.Value.Count > 0)
                {
                    WordsRemovedFromDictionary?.Invoke(removed.Value);
                    RefreshLearnMemoryUi();
                    ShowMessage("Word deleted", $"\u201C{existing.Replacement}\u201D was removed.", InfoBarSeverity.Informational);
                }
                else if (removed.Saved)
                {
                    ShowMessage("Nothing was removed", "That word changed or was removed while you were editing it.", InfoBarSeverity.Warning);
                }
            }
        }
    }

    private static List<StackPanel> WrapChips<T>(IEnumerable<T> items, Func<T, Button> buildChip)
    {
        var rows = new List<StackPanel>();
        StackPanel? current = null;
        var countInRow = 0;

        foreach (var item in items)
        {
            if (current is null || countInRow >= 3)
            {
                current = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                rows.Add(current);
                countInRow = 0;
            }

            current.Children.Add(buildChip(item));
            countInRow++;
        }

        return rows;
    }

    internal async void EditSnippetRowButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SnippetRow { Entry: var existing } })
        {
            return;
        }

        await ShowSnippetEditorDialogAsync(existing).ConfigureAwait(true);
    }

    private async Task ShowSnippetEditorDialogAsync(SnippetEntry? existing)
    {
        var isNew = existing is null;
        var keyword = _settings?.UserData.SnippetKeyword ?? SnippetVocabulary.DefaultKeyword;
        var headerTemplate = (DataTemplate)Application.Current.Resources["BrandControlHeaderTemplate"];

        var iconTile = new Border
        {
            Width = 40,
            Height = 40,
            Style = (Style)Application.Current.Resources["BrandIconTileStyle"],
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = "{}",
                Style = (Style)Application.Current.Resources["BrandAccentTitleStyle"],
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var dialogHeader = new Grid
        {
            ColumnSpacing = (double)Application.Current.Resources["BrandSpacingM"],
        };
        dialogHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        dialogHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(iconTile, 0);
        dialogHeader.Children.Add(iconTile);

        var headerTextStack = new StackPanel { Spacing = (double)Application.Current.Resources["BrandSpacingXs"] };
        headerTextStack.Children.Add(new TextBlock
        {
            Text = isNew ? "New snippet" : "Edit snippet",
            Style = (Style)Application.Current.Resources["BrandRowTitleStyle"],
        });
        headerTextStack.Children.Add(new TextBlock
        {
            Text = "Say your keyword followed by your trigger to paste saved text anywhere.",
            Style = (Style)Application.Current.Resources["BrandHelperStyle"],
            TextWrapping = TextWrapping.Wrap,
        });
        Grid.SetColumn(headerTextStack, 1);
        dialogHeader.Children.Add(headerTextStack);

        var triggerBox = new TextBox
        {
            Header = "Trigger",
            HeaderTemplate = headerTemplate,
            MaxLength = 128,
            PlaceholderText = "my email",
            Text = existing?.Name ?? string.Empty,
        };

        var liveSayPreviewText = new TextBlock
        {
            Style = (Style)Application.Current.Resources["BrandHelperStyle"],
            TextWrapping = TextWrapping.Wrap,
        };
        var liveSayPreviewCard = new Border
        {
            Style = (Style)Application.Current.Resources["BrandAccentChipStyle"],
            Padding = new Thickness(12, 8, 12, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = liveSayPreviewText,
        };

        void UpdateSayPreview()
        {
            var typedTrigger = triggerBox.Text.Trim();
            var shownTrigger = string.IsNullOrEmpty(typedTrigger) ? "my email" : typedTrigger;
            liveSayPreviewText.Text = $"You\u2019ll say: \u201C{keyword} {shownTrigger}\u201D";
        }

        UpdateSayPreview();
        triggerBox.TextChanged += (_, _) => UpdateSayPreview();

        var bodyBox = new TextBox
        {
            Header = "Expands to",
            HeaderTemplate = headerTemplate,
            AcceptsReturn = true,
            MaxLength = 10000,
            MinHeight = 110,
            PlaceholderText = "Kind regards,",
            TextWrapping = TextWrapping.Wrap,
            Text = existing?.Body ?? string.Empty,
        };

        var fillInRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = (double)Application.Current.Resources["BrandSpacingS"],
        };
        foreach (var (label, placeholder) in new[]
        {
            ("Today's date", SnippetPlaceholder.Date),
            ("Time now", SnippetPlaceholder.Time),
            ("Last copied", SnippetPlaceholder.Clipboard),
        })
        {
            var button = new Button
            {
                Content = label,
                Style = (Style)Application.Current.Resources["BrandQuietButtonStyle"],
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"Insert {label}");
            var captured = placeholder;
            button.Click += (_, _) =>
            {
                bodyBox.Text += SnippetPlaceholders.Token(captured);
                bodyBox.SelectionStart = bodyBox.Text.Length;
                bodyBox.Focus(FocusState.Programmatic);
            };
            fillInRow.Children.Add(button);
        }

        var fillInHint = new TextBlock
        {
            Text = "A fill-in becomes the date, the time, or what you copied. AI Polish never rewrites snippets.",
            Style = (Style)Application.Current.Resources["BrandHelperStyle"],
            TextWrapping = TextWrapping.Wrap,
        };

        var errorText = new TextBlock
        {
            Style = (Style)Application.Current.Resources["BrandErrorHelperStyle"],
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        var dialogBody = new StackPanel
        {
            Spacing = (double)Application.Current.Resources["BrandSpacingM"],
            MinWidth = 440,
            Children =
            {
                dialogHeader,
                triggerBox,
                liveSayPreviewCard,
                bodyBox,
                fillInRow,
                fillInHint,
                errorText,
            },
        };

        var dialog = new ContentDialog
        {
            XamlRoot = WindowRoot.XamlRoot ?? Content.XamlRoot,
            RequestedTheme = WindowRoot.ActualTheme,
            Background = ResolveWindowThemeBrush("BrandCardBgBrush"),
            BorderBrush = ResolveWindowThemeBrush("BrandDividerBrush"),
            PrimaryButtonText = isNew ? "Add snippet" : "Save",
            PrimaryButtonStyle = (Style)Application.Current.Resources["BrandPrimaryButtonStyle"],
            SecondaryButtonText = isNew ? string.Empty : "Delete snippet",
            SecondaryButtonStyle = isNew ? null : (Style)Application.Current.Resources["BrandDestructiveButtonStyle"],
            CloseButtonText = "Cancel",
            CloseButtonStyle = (Style)Application.Current.Resources["BrandQuietButtonStyle"],
            DefaultButton = ContentDialogButton.Primary,
            Content = dialogBody,
        };

        var isSaving = false;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            if (isSaving)
            {
                args.Cancel = true;
                return;
            }

            var trigger = triggerBox.Text.Trim();
            var body = bodyBox.Text;
            var snippets = _settings?.UserData.Snippets ?? Array.Empty<SnippetEntry>();
            var preflightRefusal = existing is null
                ? SnippetRules.Validate(new SnippetEntry(trigger, body), snippets, out var preflightClash)
                : VocabularyPresenter.ValidateSnippetEdit(existing, trigger, body, snippets, out preflightClash);
            if (preflightRefusal is not null)
            {
                errorText.Text = SnippetRefusalMessage(preflightRefusal.Value, preflightClash);
                errorText.Visibility = Visibility.Visible;
                args.Cancel = true;
                return;
            }

            var deferral = args.GetDeferral();
            isSaving = true;
            dialog.IsPrimaryButtonEnabled = false;
            try
            {
                var saved = existing is null
                    ? await _session.Vocabulary.AddSnippetAsync(new SnippetEntry(trigger, body)).ConfigureAwait(true)
                    : await _session.Vocabulary.UpdateSnippetAsync(existing, trigger, body).ConfigureAwait(true);

                if (!saved.Saved)
                {
                    if (saved.Refusal != SettingsSaveRefusal.Closing)
                    {
                        errorText.Text = SettingsRefusalDialogMessage(saved.Refusal);
                        errorText.Visibility = Visibility.Visible;
                        args.Cancel = true;
                    }
                    return;
                }

                if (saved.Value.TargetMissing)
                {
                    errorText.Text = "That snippet changed or was removed while you were editing it. Cancel and try again.";
                    errorText.Visibility = Visibility.Visible;
                    args.Cancel = true;
                    return;
                }

                if (saved.Value.Refusal is { } refusal)
                {
                    errorText.Text = SnippetRefusalMessage(refusal, saved.Value.Clash);
                    errorText.Visibility = Visibility.Visible;
                    args.Cancel = true;
                    return;
                }

                PublishSettings();
                ApplyUserDataRefresh(UserDataRefreshScope.Snippets);
                ShowMessage("Snippet saved", "The change was saved locally.", InfoBarSeverity.Success);
            }
            finally
            {
                isSaving = false;
                dialog.IsPrimaryButtonEnabled = true;
                deferral.Complete();
            }
        };

        var result = await dialog.ShowAsync().AsTask().ConfigureAwait(true);
        if (result == ContentDialogResult.Secondary && existing is not null)
        {
            await CommitVocabularyAsync(
                _session.Vocabulary.RemoveSnippetAsync(existing),
                "Snippet deleted",
                scope: UserDataRefreshScope.Snippets).ConfigureAwait(true);
        }
    }

    private static string SettingsRefusalDialogMessage(SettingsSaveRefusal? refusal) => refusal switch
    {
        SettingsSaveRefusal.InvalidValues =>
            "Settings were not saved. One or more values are invalid or the limit was reached. Your previous settings remain active.",
        SettingsSaveRefusal.StorageBlocked =>
            "Windows blocked settings storage. Your previous settings remain active.",
        _ =>
            "Settings storage is unavailable. Your previous settings remain active.",
    };

    internal void GoToQuickAddKeybindButton_Click(object sender, RoutedEventArgs e)
    {
        OpenPage("settings-keybinds");
        QuickAddHotkeyTextBox?.Focus(FocusState.Programmatic);
    }

    private void UpdateQuickAddShortcutBadge()
    {
        if (QuickAddCurrentShortcutText is null)
        {
            return;
        }

        var gesture = _settings?.Preferences?.Dictation?.QuickAddGesture;
        QuickAddCurrentShortcutText.Text = string.IsNullOrWhiteSpace(gesture)
            ? DictationPreferences.Default.QuickAddGesture
            : gesture;
    }
}
