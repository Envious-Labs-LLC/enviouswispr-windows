using EnviousWispr.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace EnviousWispr.App.Controls;

/// <summary>Per-word editor card inside the full-pane Vocabulary Pack editor.</summary>
public sealed partial class PackWordRowView : UserControl
{
    public static readonly DependencyProperty RowProperty = DependencyProperty.Register(
        nameof(Row),
        typeof(PackWordRowItem),
        typeof(PackWordRowView),
        new PropertyMetadata(null, OnRowChanged));

    private PackWordRowItem? _subscribedRow;
    private IReadOnlyList<string>? _renderedAliases;
    private bool _isSyncing;
    private bool _isMutating;
    private int _toggleGeneration;

    public PackWordRowView()
    {
        InitializeComponent();
        Loaded += PackWordRowView_Loaded;
        Unloaded += PackWordRowView_Unloaded;
    }

    public PackWordRowItem? Row
    {
        get => (PackWordRowItem?)GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    private static void OnRowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PackWordRowView view)
        {
            return;
        }

        view.AttachRow(e.NewValue as PackWordRowItem);
    }

    private void PackWordRowView_Loaded(object sender, RoutedEventArgs e)
    {
        if (Row is not null && !ReferenceEquals(_subscribedRow, Row))
        {
            AttachRow(Row);
        }
    }

    private void PackWordRowView_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_subscribedRow is not null)
        {
            _subscribedRow.StateUpdated -= OnRowStateUpdated;
            _subscribedRow = null;
        }
    }

    private void AttachRow(PackWordRowItem? nextRow)
    {
        if (_subscribedRow is not null)
        {
            _subscribedRow.StateUpdated -= OnRowStateUpdated;
            _subscribedRow = null;
        }

        if (nextRow is not null)
        {
            _subscribedRow = nextRow;
            _subscribedRow.StateUpdated += OnRowStateUpdated;
        }

        _toggleGeneration++;
        _renderedAliases = null;
        SyncFromRow(resetValidation: true);
    }

    private void OnRowStateUpdated()
    {
        SyncFromRow(resetValidation: false);
    }

    private void SyncFromRow(bool resetValidation)
    {
        var row = Row;
        if (row is null)
        {
            return;
        }

        var state = row.State;
        _isSyncing = true;
        try
        {
            CanonicalText.Text = state.Canonical;
            CanonicalText.Opacity = state.IsEnabled ? 1.0 : 0.55;
            WordDetailsPanel.Opacity = state.IsEnabled ? 1.0 : 0.55;

            var editedVisibility = state.IsEdited ? Visibility.Visible : Visibility.Collapsed;
            EditedBadge.Visibility = editedVisibility;
            RestoreButton.Visibility = editedVisibility;
            AutomationProperties.SetName(RestoreButton, $"Restore {state.Canonical} to pack defaults");

            WordToggle.IsOn = state.IsEnabled;
            AutomationProperties.SetName(WordToggle, $"Enable {state.Canonical}");
            AutomationProperties.SetName(NewAliasBox, $"Add a mishearing for {state.Canonical}");
            AutomationProperties.SetName(AddAliasButton, $"Add mishearing for {state.Canonical}");

            if (_renderedAliases is null
                || !_renderedAliases.SequenceEqual(state.DisplayAliases, StringComparer.Ordinal))
            {
                RebuildAliasChips(state);
                _renderedAliases = state.DisplayAliases;
            }

            var draft = row.Callbacks.GetDraft(state.Canonical);
            if (!string.Equals(NewAliasBox.Text, draft, StringComparison.Ordinal))
            {
                NewAliasBox.Text = draft;
            }

            AddAliasButton.IsEnabled = !_isMutating && !string.IsNullOrWhiteSpace(NewAliasBox.Text);

            if (resetValidation)
            {
                RowValidationText.Text = string.Empty;
                RowValidationText.Visibility = Visibility.Collapsed;
            }
        }
        finally
        {
            _isSyncing = false;
        }
    }

    private void RebuildAliasChips(VocabularyPackWordState state)
    {
        AliasesWrapPanel.Children.Clear();
        if (state.DisplayAliases.Count == 0)
        {
            AliasesWrapPanel.Visibility = Visibility.Collapsed;
            NoAliasesText.Visibility = Visibility.Visible;
            return;
        }

        AliasesWrapPanel.Visibility = Visibility.Visible;
        NoAliasesText.Visibility = Visibility.Collapsed;

        var chipStyle = (Style)Application.Current.Resources["BrandAccentChipStyle"];
        var bodyStyle = (Style)Application.Current.Resources["BrandBodyStyle"];
        var removeButtonStyle = (Style)Application.Current.Resources["BrandHelpButtonStyle"];

        foreach (var alias in state.DisplayAliases)
        {
            var capturedAlias = alias;
            var aliasText = new TextBlock
            {
                Text = capturedAlias,
                Style = bodyStyle,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTipService.SetToolTip(aliasText, capturedAlias);
            var removeButton = new Button
            {
                Style = removeButtonStyle,
                Padding = new Thickness(4, 2, 4, 2),
                MinWidth = 22,
                MinHeight = 22,
                VerticalAlignment = VerticalAlignment.Center,
                Content = new FontIcon
                {
                    Glyph = "\uE711",
                    FontSize = 11,
                },
            };
            AutomationProperties.SetName(removeButton, $"Remove mishearing {capturedAlias} from {state.Canonical}");
            ToolTipService.SetToolTip(removeButton, "Remove mishearing");
            removeButton.Click += async (_, _) => await RemoveAliasAsync(capturedAlias).ConfigureAwait(true);

            var contentGrid = new Grid { ColumnSpacing = 4 };
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            contentGrid.Children.Add(aliasText);
            Grid.SetColumn(removeButton, 1);
            contentGrid.Children.Add(removeButton);

            var chip = new Border
            {
                Style = chipStyle,
                Padding = new Thickness(10, 3, 4, 3),
                Child = contentGrid,
            };
            AliasesWrapPanel.Children.Add(chip);
        }
    }

    private async void WordToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isSyncing || Row is not { } row)
        {
            return;
        }

        var canonical = row.State.Canonical;
        var targetEnabled = WordToggle.IsOn;
        if (targetEnabled == row.State.IsEnabled)
        {
            return;
        }

        var generation = ++_toggleGeneration;
        _isMutating = true;
        try
        {
            var saved = await row.Callbacks.SetWordEnabledAsync(canonical, targetEnabled).ConfigureAwait(true);
            if (!saved
                && generation == _toggleGeneration
                && string.Equals(Row?.State.Canonical, canonical, StringComparison.Ordinal))
            {
                _isSyncing = true;
                try
                {
                    WordToggle.IsOn = !targetEnabled;
                }
                finally
                {
                    _isSyncing = false;
                }
            }
        }
        finally
        {
            _isMutating = false;
            AddAliasButton.IsEnabled = !string.IsNullOrWhiteSpace(NewAliasBox.Text);
        }
    }

    private void NewAliasBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isSyncing || Row is not { } row)
        {
            return;
        }

        row.Callbacks.SetDraft(row.State.Canonical, NewAliasBox.Text);
        AddAliasButton.IsEnabled = !_isMutating && !string.IsNullOrWhiteSpace(NewAliasBox.Text);
        if (RowValidationText.Visibility == Visibility.Visible)
        {
            RowValidationText.Text = string.Empty;
            RowValidationText.Visibility = Visibility.Collapsed;
        }
    }

    private void NewAliasBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            _ = CommitNewAliasAsync();
        }
    }

    private void AddAliasButton_Click(object sender, RoutedEventArgs e) =>
        _ = CommitNewAliasAsync();

    private async Task CommitNewAliasAsync()
    {
        if (_isMutating || Row is not { } row)
        {
            return;
        }

        var canonical = row.State.Canonical;
        var trimmed = NewAliasBox.Text.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        var currentAliases = row.State.Aliases;
        if (currentAliases.Any(existing => string.Equals(existing, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            row.Callbacks.SetDraft(canonical, string.Empty);
            if (string.Equals(Row?.State.Canonical, canonical, StringComparison.Ordinal))
            {
                _isSyncing = true;
                try
                {
                    NewAliasBox.Text = string.Empty;
                    AddAliasButton.IsEnabled = false;
                }
                finally
                {
                    _isSyncing = false;
                }
            }

            return;
        }

        if (currentAliases.Count >= CustomWordEntry.MaximumAliasesPerEntry)
        {
            RowValidationText.Text = $"You can add up to {CustomWordEntry.MaximumAliasesPerEntry} mishearings per word.";
            RowValidationText.Visibility = Visibility.Visible;
            return;
        }

        RowValidationText.Text = string.Empty;
        RowValidationText.Visibility = Visibility.Collapsed;

        var nextAliases = new List<string>(currentAliases.Count + 1);
        nextAliases.AddRange(currentAliases);
        nextAliases.Add(trimmed);

        _isMutating = true;
        AddAliasButton.IsEnabled = false;
        try
        {
            var saved = await row.Callbacks.SetWordAliasesAsync(canonical, nextAliases).ConfigureAwait(true);
            if (saved)
            {
                row.Callbacks.SetDraft(canonical, string.Empty);
                if (string.Equals(Row?.State.Canonical, canonical, StringComparison.Ordinal))
                {
                    _isSyncing = true;
                    try
                    {
                        NewAliasBox.Text = string.Empty;
                    }
                    finally
                    {
                        _isSyncing = false;
                    }

                    NewAliasBox.Focus(FocusState.Programmatic);
                }
            }
        }
        finally
        {
            _isMutating = false;
            AddAliasButton.IsEnabled = !string.IsNullOrWhiteSpace(NewAliasBox.Text);
        }
    }

    private async Task RemoveAliasAsync(string alias)
    {
        if (_isMutating || Row is not { } row)
        {
            return;
        }

        var canonical = row.State.Canonical;
        var nextAliases = row.State.Aliases
            .Where(existing => !string.Equals(existing, alias, StringComparison.Ordinal))
            .ToArray();

        _isMutating = true;
        try
        {
            await row.Callbacks.SetWordAliasesAsync(canonical, nextAliases).ConfigureAwait(true);
        }
        finally
        {
            _isMutating = false;
            AddAliasButton.IsEnabled = !string.IsNullOrWhiteSpace(NewAliasBox.Text);
        }
    }

    private async void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isMutating || Row is not { } row)
        {
            return;
        }

        var canonical = row.State.Canonical;
        _isMutating = true;
        try
        {
            var saved = await row.Callbacks.RestoreWordAsync(canonical).ConfigureAwait(true);
            if (saved)
            {
                row.Callbacks.SetDraft(canonical, string.Empty);
                if (string.Equals(Row?.State.Canonical, canonical, StringComparison.Ordinal))
                {
                    _isSyncing = true;
                    try
                    {
                        NewAliasBox.Text = string.Empty;
                    }
                    finally
                    {
                        _isSyncing = false;
                    }
                }
            }
        }
        finally
        {
            _isMutating = false;
            AddAliasButton.IsEnabled = !string.IsNullOrWhiteSpace(NewAliasBox.Text);
        }
    }
}
