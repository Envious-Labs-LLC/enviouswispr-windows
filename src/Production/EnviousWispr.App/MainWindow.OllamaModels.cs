using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using EnviousWispr.Core.Polish;
using EnviousWispr.Core.Settings;
using EnviousWispr.Presentation;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EnviousWispr.App;

/// <summary>The AI Polish page's Ollama models block: setup, live awareness, search, local downloads, hosted additions, and removals. Ref: #71, #213.</summary>
public sealed partial class MainWindow
{
    private static readonly TimeSpan OllamaLivePollInterval = TimeSpan.FromSeconds(12);

    private readonly ObservableCollection<OllamaRowItem> _ollamaRows = [];
    private OllamaSetupAction _ollamaSetupAction;
    private DispatcherQueueTimer? _ollamaLivePollTimer;
    private bool _isWindowActive;

    private OllamaModelsPresenter? OllamaModels => _session.Ollama;

    /// <summary>Shows the block for Ollama and looks at Ollama; for any other provider hides it, stops the timer, and stops a download.</summary>
    private void ShowOllamaModels(PolishProvider provider)
    {
        var visible = provider == PolishProvider.Ollama && OllamaModels is not null;
        OllamaModelsRow.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        UpdateOllamaLivePollTimer();
        if (!visible)
        {
            // AS macOS DOES: leaving Ollama stops the download or startup poll it was running.
            OllamaModels?.Stop();
            return;
        }

        OllamaModelList.ItemsSource ??= _ollamaRows;
        _ = RefreshOllamaModelsAsync();
    }

    private void OnWindowActivationChangedForOllama(bool active)
    {
        _isWindowActive = active;
        UpdateOllamaLivePollTimer();
        if (active &&
            _initialFocusAssigned &&
            !_session.Closing &&
            PolishProviderFromIndex(SelectedIndexOf(PolishProviderChoices)) == PolishProvider.Ollama)
        {
            _ = PollOllamaModelsSilentlyAsync();
        }
    }

    private void UpdateOllamaLivePollTimer()
    {
        var shouldRun = !_session.Closing &&
            _isWindowActive &&
            OllamaModels is not null &&
            SettingsPage is { Visibility: Visibility.Visible } &&
            AiPolishSection is { Visibility: Visibility.Visible } &&
            OllamaModelsRow is { Visibility: Visibility.Visible } &&
            PolishProviderFromIndex(SelectedIndexOf(PolishProviderChoices)) == PolishProvider.Ollama;

        if (!shouldRun)
        {
            _ollamaLivePollTimer?.Stop();
            return;
        }

        if (_ollamaLivePollTimer is null && DispatcherQueue is { } queue)
        {
            _ollamaLivePollTimer = queue.CreateTimer();
            _ollamaLivePollTimer.Interval = OllamaLivePollInterval;
            _ollamaLivePollTimer.IsRepeating = true;
            _ollamaLivePollTimer.Tick += OnOllamaLivePollTimerTick;
        }

        if (_ollamaLivePollTimer is { IsRunning: false })
        {
            _ollamaLivePollTimer.Start();
        }
    }

    private void StopOllamaLivePollTimer()
    {
        if (_ollamaLivePollTimer is not null)
        {
            _ollamaLivePollTimer.Stop();
            _ollamaLivePollTimer.Tick -= OnOllamaLivePollTimerTick;
            _ollamaLivePollTimer = null;
        }
    }

    private void OnOllamaLivePollTimerTick(DispatcherQueueTimer sender, object args)
    {
        _ = PollOllamaModelsSilentlyAsync();
    }

    private async Task PollOllamaModelsSilentlyAsync()
    {
        if (_session.Closing ||
            OllamaModels is not { } models ||
            PolishProviderFromIndex(SelectedIndexOf(PolishProviderChoices)) != PolishProvider.Ollama)
        {
            return;
        }

        var endpoint = NullIfBlank(OllamaEndpointTextBox.Text);
        var previousVersion = models.Context?.Version;
        var previousBadge = OllamaServerStatusBadgeText?.Text;
        var polled = await models.PollAsync(endpoint).ConfigureAwait(true);
        if (polled is null ||
            _session.Closing ||
            PolishProviderFromIndex(SelectedIndexOf(PolishProviderChoices)) != PolishProvider.Ollama)
        {
            return;
        }

        RenderOllamaModels(models.View);
        if (models.Context?.Version != previousVersion ||
            !string.Equals(previousBadge, models.View.Setup.StatusBadge, StringComparison.Ordinal))
        {
            await RefreshPolishModelChoicesAsync(
                PolishProvider.Ollama,
                chooseDefault: false,
                silentPoll: true).ConfigureAwait(true);
        }
    }

    private async Task RefreshOllamaModelsAsync()
    {
        if (OllamaModels is not { } models)
        {
            return;
        }

        RenderOllamaModels(models.View);
        var endpoint = NullIfBlank(OllamaEndpointTextBox.Text);
        // THE BLOCK AS IT IS WHEN DRAWN, never the answer this call was handed: a newer look may have finished first.
        if (await models.RefreshAsync(endpoint).ConfigureAwait(true) is not null &&
            !_session.Closing)
        {
            RenderOllamaModels(models.View);
            if (!string.IsNullOrWhiteSpace(_selectedPolishModelId))
            {
                _ = WarmUpSelectedOllamaModelAsync(_selectedPolishModelId);
            }
        }
    }

    private async Task WarmUpSelectedOllamaModelAsync(string? modelId)
    {
        if (_session.Closing ||
            OllamaModels is not { } models ||
            string.IsNullOrWhiteSpace(modelId) ||
            PolishProviderFromIndex(SelectedIndexOf(PolishProviderChoices)) != PolishProvider.Ollama)
        {
            return;
        }

        _ = await models.WarmUpAsync(NullIfBlank(OllamaEndpointTextBox.Text), modelId).ConfigureAwait(true);
    }

    private void RenderOllamaModels(OllamaModelsView view)
    {
        if (OllamaServerStatusBadgeText is not null && OllamaServerStatusBadgeText.Text != view.Setup.StatusBadge)
        {
            OllamaServerStatusBadgeText.Text = view.Setup.StatusBadge;
        }

        if (ActivePolishProviderBadgeText is not null &&
            PolishProviderFromIndex(SelectedIndexOf(PolishProviderChoices)) == PolishProvider.Ollama)
        {
            ApplyActivePolishProviderBadge(EnginePolishPresentation.DescribeActivePolishProvider(
                PolishProvider.Ollama,
                selectedEgOneState: null,
                egOneServerExists: false,
                isOllamaHosted: IsSelectedOllamaModelHosted()));
        }

        if (OllamaSetupText.Text != view.Setup.Sentence)
        {
            SetLiveText(OllamaSetupText, view.Setup.Sentence);
        }

        _ollamaSetupAction = view.Setup.Action;
        OllamaSetupActionButton.Visibility = view.Setup.Action == OllamaSetupAction.None ? Visibility.Collapsed : Visibility.Visible;
        OllamaSetupActionButton.Content = view.Setup.ActionLabel;
        OllamaSetupActionButton.IsEnabled = !(OllamaModels?.Changing ?? false) && view.Rows.All(row => !row.Downloading);
        var showsModels = view.Setup.ShowsModels;
        OllamaCaveatText.Visibility = showsModels ? Visibility.Visible : Visibility.Collapsed;
        if (OllamaModelSearchBox is not null)
        {
            OllamaModelSearchBox.Visibility = showsModels ? Visibility.Visible : Visibility.Collapsed;
        }

        _lastOllamaModelsNotice = string.IsNullOrWhiteSpace(view.Notice) ? null : view.Notice.Trim();
        var notice = HistoryPlaybackPresentation.ResolveOllamaNoticeDisplay(
            _ollamaPolishNotice,
            _lastOllamaModelsNotice) ?? string.Empty;
        if (OllamaNoticeText.Text != notice)
        {
            SetLiveRegion(OllamaNoticeText, notice, notice.Length == 0 ? Visibility.Collapsed : Visibility.Visible);
        }

        var headings = ComputeGroupHeadings(view.Rows);
        var sameRows = _ollamaRows.Count == view.Rows.Count &&
            _ollamaRows.Select(row => (row.Id, row.IsRemote)).SequenceEqual(view.Rows.Select(row => (row.Id, row.IsRemote)));
        if (!sameRows)
        {
            _ollamaRows.Clear();
            for (var i = 0; i < view.Rows.Count; i++)
            {
                _ollamaRows.Add(new OllamaRowItem(view.Rows[i], headings[i]));
            }

            return;
        }

        for (var i = 0; i < view.Rows.Count; i++)
        {
            _ollamaRows[i].Update(view.Rows[i], headings[i]);
        }
    }

    private static string[] ComputeGroupHeadings(IReadOnlyList<OllamaModelRow> rows)
    {
        var headings = new string[rows.Count];
        string? previousGroup = null;
        for (var i = 0; i < rows.Count; i++)
        {
            var group = rows[i].GroupTitle;
            if (!string.Equals(group, previousGroup, StringComparison.Ordinal))
            {
                headings[i] = group;
                previousGroup = group;
            }
            else
            {
                headings[i] = string.Empty;
            }
        }

        return headings;
    }

    private void OllamaModelSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (OllamaModels is not { } models)
        {
            return;
        }

        RenderOllamaModels(models.SetSearchQuery(OllamaModelSearchBox.Text));
    }

    private async void OllamaCheckAgainButton_Click(object sender, RoutedEventArgs e)
    {
        // ONE REFRESH, FOR THE PROVIDER SELECTED NOW: it shows the block and looks at Ollama itself. Refreshing the block and
        // then asking for Ollama's choices after that await brought the Ollama controls back over a provider chosen meanwhile.
        await RefreshPolishModelChoicesAsync(
            PolishProviderFromIndex(SelectedIndexOf(PolishProviderChoices)),
            chooseDefault: false).ConfigureAwait(true);
    }

    private async void OllamaSetupActionButton_Click(object sender, RoutedEventArgs e)
    {
        switch (_ollamaSetupAction)
        {
            case OllamaSetupAction.OpenOllama:
                await OpenOllamaFromSetupAsync().ConfigureAwait(true);
                break;
            case OllamaSetupAction.DownloadOllama:
                _ = await Windows.System.Launcher.LaunchUriAsync(new Uri("https://ollama.com/download"));
                break;
            case OllamaSetupAction.DownloadRecommended when OllamaModels?.Context is { } context:
                await DownloadOllamaModelAsync(OllamaModelCatalog.RecommendedModelId, context).ConfigureAwait(true);
                break;
            default:
                break;
        }
    }

    private async Task OpenOllamaFromSetupAsync()
    {
        if (OllamaModels is not { } models)
        {
            return;
        }

        var endpoint = NullIfBlank(OllamaEndpointTextBox.Text);
        var started = await models.OpenOllamaAsync(
                endpoint,
                new Progress<OllamaModelsView>(_ =>
                {
                    if (!_session.Closing)
                    {
                        RenderOllamaModels(models.View);
                    }
                }))
            .ConfigureAwait(true);
        if (started is null || _session.Closing)
        {
            return;
        }

        RenderOllamaModels(models.View);
        if (PolishProviderFromIndex(SelectedIndexOf(PolishProviderChoices)) == PolishProvider.Ollama &&
            string.Equals(endpoint, NullIfBlank(OllamaEndpointTextBox.Text), StringComparison.Ordinal))
        {
            await RefreshPolishModelChoicesAsync(
                PolishProvider.Ollama,
                chooseDefault: true,
                silentPoll: true).ConfigureAwait(true);
        }
    }

    private async void OllamaModelAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: OllamaRowItem row } || OllamaModels is not { } models)
        {
            return;
        }

        // THE LIST THE PERSON CLICKED IN, taken before any dialog: the action is refused if the page moved meanwhile.
        if (models.Context is not { } context)
        {
            return;
        }

        switch (row.Action)
        {
            case OllamaRowAction.Download:
                await DownloadOllamaModelAsync(row.Id, context).ConfigureAwait(true);
                break;
            case OllamaRowAction.Add:
                await AddHostedOllamaModelAsync(row.Id, context).ConfigureAwait(true);
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

                if (await models.RemoveAsync(row.Id, context).ConfigureAwait(true) is { } removed &&
                    !_session.Closing)
                {
                    await ApplyOllamaChangeAsync(removed).ConfigureAwait(true);
                }

                break;
            default:
                break;
        }
    }

    private async Task DownloadOllamaModelAsync(string modelId, OllamaListContext context)
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
                modelId,
                context,
                // A PROGRESS VIEW QUEUED BEFORE THE DOWNLOAD ENDED IS NOT DRAWN AFTER IT: the final view is.
                new Progress<OllamaModelsView>(_ =>
                {
                    if (models.Changing)
                    {
                        RenderOllamaModels(models.View);
                    }
                }))
            .ConfigureAwait(true);
        if (downloaded is null || _session.Closing)
        {
            return;
        }

        await ApplyOllamaChangeAsync(downloaded).ConfigureAwait(true);
    }

    private async Task AddHostedOllamaModelAsync(string advertisedId, OllamaListContext context)
    {
        if (OllamaModels is not { } models)
        {
            return;
        }

        var added = await models.AddHostedAsync(
                advertisedId,
                context,
                new Progress<OllamaModelsView>(_ =>
                {
                    if (models.Changing)
                    {
                        RenderOllamaModels(models.View);
                    }
                }))
            .ConfigureAwait(true);
        if (added is null || _session.Closing)
        {
            return;
        }

        await ApplyOllamaChangeAsync(added).ConfigureAwait(true);
    }

    /// <summary>
    /// Draws the block after a download or removal and repairs the picker - only while Ollama is still the provider and
    /// the endpoint is still the one the change was made against. A download stopped by switching to a cloud provider
    /// still ends here, and must not bring the Ollama controls back; one made at an endpoint since edited must not
    /// repair the field against another server's list.
    /// </summary>
    private async Task ApplyOllamaChangeAsync(OllamaModelChange change)
    {
        if (OllamaModels is { } models)
        {
            RenderOllamaModels(models.View);
        }

        if (PolishProviderFromIndex(SelectedIndexOf(PolishProviderChoices)) == PolishProvider.Ollama &&
            string.Equals(change.Endpoint, NullIfBlank(OllamaEndpointTextBox.Text), StringComparison.Ordinal))
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
        private string _groupHeading;

        public OllamaRowItem(OllamaModelRow row, string groupHeading = "")
        {
            _row = row;
            _groupHeading = groupHeading;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Id => _row.Id;

        public bool IsRemote => _row.IsRemote;

        public string GroupHeading => _groupHeading;

        public Visibility GroupHeadingVisibility => Shown(_groupHeading.Length > 0);

        /// <summary>Installed models carry a check, the rest the download/cloud mark (Segoe Fluent E73E, E753, E896).</summary>
        public string Glyph => ((char)(_row.Installed ? 0xE73E : (_row.IsRemote ? 0xE753 : 0xE896))).ToString();

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

        public Visibility RecommendedVisibility => Shown(!_row.IsRemote && _row.Verdict == OllamaModelVerdict.Recommended);

        public Visibility MixedVisibility => Shown(!_row.IsRemote && _row.Verdict == OllamaModelVerdict.Mixed);

        public Visibility CautionVisibility => Shown(!_row.IsRemote && _row.Verdict is OllamaModelVerdict.Unreliable or OllamaModelVerdict.NotRecommended);

        public Visibility UntestedVisibility => Shown(_row.IsRemote || _row.Verdict == OllamaModelVerdict.NotTested);

        public Visibility ProgressVisibility => Shown(_row.Downloading);

        public bool Indeterminate => _row.Downloading && _row.Progress is null;

        public double Percent => (_row.Progress ?? 0) * 100;

        public string ProgressText => _row.ProgressText ?? string.Empty;

        public void Update(OllamaModelRow row, string groupHeading = "")
        {
            if (row == _row && string.Equals(groupHeading, _groupHeading, StringComparison.Ordinal))
            {
                return;
            }

            _row = row;
            _groupHeading = groupHeading;
            Changed(string.Empty);
        }

        public override string ToString() => Name;

        private static Visibility Shown(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;

        private void Changed([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
