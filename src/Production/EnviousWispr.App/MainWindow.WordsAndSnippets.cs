using EnviousWispr.Core.Settings;
using EnviousWispr.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EnviousWispr.App;

/// <summary>In-place add/edit dialogs for Your Words and Snippets, plus Quick Add shortcut guidance.</summary>
public sealed partial class MainWindow
{
    private async void OpenAddWordDialogButton_Click(object sender, RoutedEventArgs e) =>
        await OpenAddWordDialogAsync().ConfigureAwait(true);

    private async Task OpenAddWordDialogAsync() =>
        await ShowWordEditorDialogAsync(existing: null).ConfigureAwait(true);

    private async void EditWordRowButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CustomWordEntry existing })
        {
            return;
        }

        await ShowWordEditorDialogAsync(existing).ConfigureAwait(true);
    }

    private async Task ShowWordEditorDialogAsync(CustomWordEntry? existing)
    {
        var headerTemplate = (DataTemplate)Application.Current.Resources["BrandControlHeaderTemplate"];
        var spokenBox = new TextBox
        {
            Header = "When I say",
            HeaderTemplate = headerTemplate,
            MaxLength = 256,
            PlaceholderText = "envy wisper",
            Text = existing?.SpokenForm ?? string.Empty,
        };
        var replacementBox = new TextBox
        {
            Header = "Write",
            HeaderTemplate = headerTemplate,
            MaxLength = 256,
            PlaceholderText = "EnviousWispr",
            Text = existing?.Replacement ?? string.Empty,
        };
        var strictnessBox = new ComboBox
        {
            Header = "Match strictness",
            HeaderTemplate = headerTemplate,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedValuePath = "Tag",
        };
        foreach (var (value, label) in MatchStrictnessFormatting.Options)
        {
            strictnessBox.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        }

        var targetStrictness = existing?.Strictness ?? MatchStrictness.Default;
        strictnessBox.SelectedItem = strictnessBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag is MatchStrictness s && s == targetStrictness)
            ?? strictnessBox.Items.OfType<ComboBoxItem>().FirstOrDefault();

        var errorText = new TextBlock
        {
            Style = (Style)Application.Current.Resources["BrandHelperStyle"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["BrandErrorBrush"],
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        var dialog = new ContentDialog
        {
            XamlRoot = WindowRoot.XamlRoot ?? Content.XamlRoot,
            RequestedTheme = WindowRoot.RequestedTheme,
            Title = existing is null ? "Add word" : "Edit word",
            PrimaryButtonText = existing is null ? "Add word" : "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = Stack(spokenBox, replacementBox, strictnessBox, errorText),
        };

        var isSaving = false;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            if (isSaving)
            {
                args.Cancel = true;
                return;
            }

            if (string.IsNullOrWhiteSpace(spokenBox.Text) || string.IsNullOrWhiteSpace(replacementBox.Text))
            {
                errorText.Text = "Both fields are required. Enter the spoken form and the exact replacement.";
                errorText.Visibility = Visibility.Visible;
                args.Cancel = true;
                return;
            }

            var deferral = args.GetDeferral();
            isSaving = true;
            dialog.IsPrimaryButtonEnabled = false;
            try
            {
                var spoken = spokenBox.Text.Trim();
                var replacement = replacementBox.Text.Trim();
                var strictness = strictnessBox.SelectedValue as MatchStrictness? ?? MatchStrictness.Default;

                if (existing is null)
                {
                    var addResult = await _session.Vocabulary.AddWordAsync(spoken, replacement, strictness)
                        .ConfigureAwait(true);
                    if (!addResult.Saved)
                    {
                        if (addResult.Refusal != SettingsSaveRefusal.Closing)
                        {
                            errorText.Text = SettingsRefusalDialogMessage(addResult.Refusal);
                            errorText.Visibility = Visibility.Visible;
                            args.Cancel = true;
                        }
                        return;
                    }
                }
                else
                {
                    var editResult = await _session.Vocabulary.UpdateWordAsync(existing, spoken, replacement, strictness)
                        .ConfigureAwait(true);
                    if (!editResult.Saved)
                    {
                        if (editResult.Refusal != SettingsSaveRefusal.Closing)
                        {
                            errorText.Text = SettingsRefusalDialogMessage(editResult.Refusal);
                            errorText.Visibility = Visibility.Visible;
                            args.Cancel = true;
                        }
                        return;
                    }

                    if (editResult.Value.Refusal is { } wordRefusal)
                    {
                        errorText.Text = wordRefusal == WordEditRefusal.DuplicateSpokenForm
                            ? $"You already have a word for \u201C{editResult.Value.Clash?.SpokenForm}\u201D. Change one of them."
                            : "That word changed or was removed while you were editing it. Cancel and try again.";
                        errorText.Visibility = Visibility.Visible;
                        args.Cancel = true;
                        return;
                    }
                }

                PublishSettings();
                ApplySettingsToControls();
                ShowMessage("Dictionary saved", "The change was saved locally.", InfoBarSeverity.Success);
                RefreshReusableUserDataViews();
            }
            finally
            {
                isSaving = false;
                dialog.IsPrimaryButtonEnabled = true;
                deferral.Complete();
            }
        };

        await dialog.ShowAsync().AsTask().ConfigureAwait(true);
    }

    private async void EditSnippetRowButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SnippetRow { Entry: var existing } })
        {
            return;
        }

        var headerTemplate = (DataTemplate)Application.Current.Resources["BrandControlHeaderTemplate"];
        var triggerBox = new TextBox
        {
            Header = "Trigger",
            HeaderTemplate = headerTemplate,
            MaxLength = 128,
            PlaceholderText = "my email address",
            Text = existing.Name,
        };
        var bodyBox = new TextBox
        {
            Header = "Expands to",
            HeaderTemplate = headerTemplate,
            AcceptsReturn = true,
            MaxLength = 10000,
            MinHeight = 100,
            PlaceholderText = "Kind regards,",
            TextWrapping = TextWrapping.Wrap,
            Text = existing.Body,
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

        var errorText = new TextBlock
        {
            Style = (Style)Application.Current.Resources["BrandHelperStyle"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["BrandErrorBrush"],
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        var dialog = new ContentDialog
        {
            XamlRoot = WindowRoot.XamlRoot ?? Content.XamlRoot,
            RequestedTheme = WindowRoot.RequestedTheme,
            Title = "Edit snippet",
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = Stack(triggerBox, bodyBox, fillInRow, errorText),
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
            var preflightRefusal = VocabularyPresenter.ValidateSnippetEdit(
                existing,
                trigger,
                body,
                _settings.UserData.Snippets,
                out var preflightClash);
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
                var saved = await _session.Vocabulary.UpdateSnippetAsync(existing, trigger, body)
                    .ConfigureAwait(true);
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
                ApplySettingsToControls();
                ShowMessage("Snippet saved", "The change was saved locally.", InfoBarSeverity.Success);
                RefreshReusableUserDataViews();
            }
            finally
            {
                isSaving = false;
                dialog.IsPrimaryButtonEnabled = true;
                deferral.Complete();
            }
        };

        await dialog.ShowAsync().AsTask().ConfigureAwait(true);
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

    private void GoToQuickAddKeybindButton_Click(object sender, RoutedEventArgs e)
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
