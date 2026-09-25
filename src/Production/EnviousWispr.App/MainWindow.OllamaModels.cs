using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using EnviousWispr.Core.Polish;
using EnviousWispr.Core.Settings;
using EnviousWispr.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EnviousWispr.App;

/// <summary>The AI Polish page's Ollama models block: setup, the catalogue, downloads and removals. Ref: #213, macOS ProviderSetup.</summary>
/// <remarks>
/// THE PRESENTER DECIDES, THIS DRAWS. Every sentence, order and button state comes from <see cref="OllamaModelsPresenter"/>;
/// the window turns a view into controls and a click into a call. THE ROWS ARE UPDATED IN PLACE while a download reports
/// progress, so the Stop button a keyboard user is on keeps its focus instead of being rebuilt under them twenty times a
/// second. Progress itself is not announced - only the outcome is - so a screen reader is not read a percentage stream.
/// </remarks>
public sealed partial class MainWindow
{
    private readonly ObservableCollection<OllamaRowItem> _ollamaRows = [];
    private OllamaSetupAction _ollamaSetupAction;

    private OllamaModelsPresenter? OllamaModels => _session.Ollama;

    /// <summary>Shows the block for Ollama and looks at Ollama; for any other provider hides it and stops a download.</summary>
    private void ShowOllamaModels(PolishProvider provider)
    {
        var visible = provider == PolishProvider.Ollama && OllamaModels is not null;
        OllamaModelsRow.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible)
        {
            // AS macOS DOES: leaving Ollama stops the download it was running.
            OllamaModels?.Stop();
            return;
        }

        OllamaModelList.ItemsSource ??= _ollamaRows;
        _ = RefreshOllamaModelsAsync();
    }

    private async Task RefreshOllamaModelsAsync()
    {
        if (OllamaModels is not { } models)
        {
            return;
        }

        RenderOllamaModels(models.View with { Notice = models.View.Notice });
        if (await models.RefreshAsync(NullIfBlank(OllamaEndpointTextBox.Text)).ConfigureAwait(true) is { } view &&
            !_session.Closing)
        {
            RenderOllamaModels(view);
        }
    }

    private void RenderOllamaModels(OllamaModelsView view)
    {
        if (OllamaSetupText.Text != view.Setup.Sentence)
        {
            SetLiveText(OllamaSetupText, view.Setup.Sentence);
        }

        _ollamaSetupAction = view.Setup.Action;
        OllamaSetupActionButton.Visibility = view.Setup.Action == OllamaSetupAction.None ? Visibility.Collapsed : Visibility.Visible;
        OllamaSetupActionButton.Content = view.Setup.ActionLabel;
        OllamaSetupActionButton.IsEnabled = view.Rows.All(row => !row.Downloading);
        OllamaCaveatText.Visibility = view.Setup.ShowsModels ? Visibility.Visible : Visibility.Collapsed;
        var notice = view.Notice ?? string.Empty;
        if (OllamaNoticeText.Text != notice)
        {
            SetLiveRegion(OllamaNoticeText, notice, notice.Length == 0 ? Visibility.Collapsed : Visibility.Visible);
        }

        var sameRows = _ollamaRows.Count == view.Rows.Count &&
            _ollamaRows.Select(row => row.Id).SequenceEqual(view.Rows.Select(row => row.Id), StringComparer.Ordinal);
        if (!sameRows)
        {
            _ollamaRows.Clear();
            foreach (var row in view.Rows)
            {
                _ollamaRows.Add(new OllamaRowItem(row));
            }

            return;
        }

        for (var i = 0; i < view.Rows.Count; i++)
        {
            _ollamaRows[i].Update(view.Rows[i]);
        }
    }

    private async void OllamaCheckAgainButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshOllamaModelsAsync().ConfigureAwait(true);
        await RefreshPolishModelChoicesAsync(PolishProvider.Ollama, chooseDefault: false).ConfigureAwait(true);
    }

    private async void OllamaSetupActionButton_Click(object sender, RoutedEventArgs e)
    {
        switch (_ollamaSetupAction)
        {
            case OllamaSetupAction.DownloadOllama:
                _ = await Windows.System.Launcher.LaunchUriAsync(new Uri("https://ollama.com/download"));
                break;
            case OllamaSetupAction.DownloadRecommended:
                await DownloadOllamaModelAsync(OllamaModelCatalog.RecommendedModelId).ConfigureAwait(true);
                break;
            default:
                break;
        }
    }

    private async void OllamaModelAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: OllamaRowItem row } || OllamaModels is not { } models)
        {
            return;
        }

        switch (row.Action)
        {
            case OllamaRowAction.Download:
                await DownloadOllamaModelAsync(row.Id).ConfigureAwait(true);
                break;
            case OllamaRowAction.Stop:
                models.Stop();
                break;
            case OllamaRowAction.Remove when models.CheckRemove(row.Id) == OllamaCheck.Confirm:
                var (title, message, proceed) = OllamaModelsPresenter.RemoveQuestion(row.Id);
                if (!await ConfirmOllamaAsync(title, message, proceed).ConfigureAwait(true))
                {
                    return;
                }

                if (await models.RemoveAsync(NullIfBlank(OllamaEndpointTextBox.Text), row.Id).ConfigureAwait(true) is { } removed &&
                    !_session.Closing)
                {
                    await ApplyOllamaChangeAsync(removed).ConfigureAwait(true);
                }

                break;
            default:
                break;
        }
    }

    private async Task DownloadOllamaModelAsync(string modelId)
    {
        if (OllamaModels is not { } models)
        {
            return;
        }

        switch (models.CheckDownload(modelId))
        {
            case OllamaCheck.Confirm:
                var (title, message, proceed) = OllamaModelsPresenter.NotRecommendedQuestion(modelId);
                if (!await ConfirmOllamaAsync(title, message, proceed).ConfigureAwait(true))
                {
                    return;
                }

                break;
            case OllamaCheck.Proceed:
                break;
            default:
                return;
        }

        var downloaded = await models.DownloadAsync(
                NullIfBlank(OllamaEndpointTextBox.Text),
                modelId,
                // A PROGRESS VIEW QUEUED BEFORE THE DOWNLOAD ENDED IS NOT DRAWN AFTER IT: the final view is.
                new Progress<OllamaModelsView>(view =>
                {
                    if (models.Changing)
                    {
                        RenderOllamaModels(view);
                    }
                }))
            .ConfigureAwait(true);
        if (downloaded is null || _session.Closing)
        {
            return;
        }

        await ApplyOllamaChangeAsync(downloaded).ConfigureAwait(true);
    }

    /// <summary>
    /// Draws the block after a download or removal and repairs the picker - only while Ollama is still the provider. A
    /// download stopped by switching to a cloud provider still ends here, and must not bring the Ollama controls back.
    /// </summary>
    private async Task ApplyOllamaChangeAsync(OllamaModelChange change)
    {
        RenderOllamaModels(change.View);
        if (PolishProviderFromIndex(SelectedIndexOf(PolishProviderChoices)) == PolishProvider.Ollama)
        {
            await RefreshPolishModelChoicesAsync(PolishProvider.Ollama, chooseDefault: false, change).ConfigureAwait(true);
        }
    }

    private async Task<bool> ConfirmOllamaAsync(string title, string message, string proceed)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = WindowRoot.XamlRoot,
            Title = title,
            Content = message,
            PrimaryButtonText = proceed,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>One row as the template binds it; updated in place so the controls it drew survive a progress tick.</summary>
    internal sealed class OllamaRowItem : INotifyPropertyChanged
    {
        private OllamaModelRow _row;

        public OllamaRowItem(OllamaModelRow row) => _row = row;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Id => _row.Id;

        /// <summary>Installed models carry a check, the rest the download mark (Segoe Fluent E73E and E896).</summary>
        public string Glyph => ((char)(_row.Installed ? 0xE73E : 0xE896)).ToString();

        public string Name => _row.Name;

        public string VerdictLabel => _row.VerdictLabel;

        public string Note => _row.Note;

        public string Detail => _row.Detail;

        public OllamaRowAction Action => _row.Action;

        public string? ActionLabel => _row.ActionLabel;

        public string? ActionName => _row.ActionName;

        public bool ActionEnabled => _row.ActionEnabled;

        public Visibility ActionVisibility => Shown(_row.Action != OllamaRowAction.None);

        public Visibility NoteVisibility => Shown(_row.Note.Length > 0);

        public Visibility RecommendedVisibility => Shown(_row.Verdict == OllamaModelVerdict.Recommended);

        public Visibility MixedVisibility => Shown(_row.Verdict == OllamaModelVerdict.Mixed);

        public Visibility CautionVisibility => Shown(_row.Verdict is OllamaModelVerdict.Unreliable or OllamaModelVerdict.NotRecommended);

        public Visibility UntestedVisibility => Shown(_row.Verdict == OllamaModelVerdict.NotTested);

        public Visibility ProgressVisibility => Shown(_row.Downloading);

        public bool Indeterminate => _row.Downloading && _row.Progress is null;

        public double Percent => (_row.Progress ?? 0) * 100;

        public string ProgressText => _row.ProgressText ?? string.Empty;

        public void Update(OllamaModelRow row)
        {
            if (row == _row)
            {
                return;
            }

            _row = row;
            Changed(string.Empty);
        }

        private static Visibility Shown(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;

        private void Changed([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
