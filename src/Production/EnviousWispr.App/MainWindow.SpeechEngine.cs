using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Settings;
using EnviousWispr.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace EnviousWispr.App;

/// <summary>Speech recognition engine cards, model/graphics delivery, Live Preview controls, and language-lock offers.</summary>
public sealed partial class MainWindow
{
    /// <summary>The language the pill is currently offering to pin, or null when it offers none.</summary>
    private WhisperLanguagePreference? _offeredLanguage;

    /// <summary>Notices a language being spoken over and over, and offers to pin it.</summary>
    /// <remarks>
    /// IT LIVES HERE BECAUSE THE ANSWER HAS TO BE WRITTEN DOWN, and this is where the settings and
    /// the one save path are. Owning it from the app meant the count of offers already made could be
    /// read at launch and never updated, so the promise to stop after three lasted only until the
    /// next relaunch.
    /// </remarks>
    private readonly LanguageLockSuggester _languageSuggestions;

    /// <summary>True while a pin started from the pill is still being written.</summary>
    private bool _lockingLanguage;

    private FinalAsrEngine? _lastActiveEngine;

    /// <summary>The person asked for the speech model this build pins to be downloaded.</summary>
    public event Action? ModelDownloadRequested;

    /// <summary>The person asked for the running download to stop.</summary>
    public event Action? ModelDownloadCancelRequested;

    /// <summary>The person asked to download the Parakeet or Whisper final speech model.</summary>
    public event Action<FinalAsrEngine>? SpeechEngineDownloadRequested;

    /// <summary>The person confirmed removing the Parakeet or Whisper final speech model.</summary>
    public event Action<FinalAsrEngine>? SpeechEngineRemoveRequested;

    /// <summary>Returns whether dictation or file transcription currently holds the engine.</summary>
    public Func<bool>? IsSpeechEngineBusy { get; set; }

    private SpeechEngineModelState _parakeetModelState = SpeechEngineModelState.Unknown(EnginePolishPresentation.ParakeetBundleSizeLabel);
    private SpeechEngineModelState _whisperModelState = SpeechEngineModelState.Unknown(EnginePolishPresentation.WhisperBundleSizeLabel);
    private FinalAsrEngine? _activeSpeechDownloadEngine;

    /// <summary>The person asked for the 190 MB Whisper Live Preview model to be downloaded (#127).</summary>
    public event Action? PreviewModelDownloadRequested;

    /// <summary>The person asked for the downloaded Whisper Live Preview model to be removed (#127).</summary>
    public event Action? PreviewModelRemoveRequested;

    private bool _livePreviewParakeetInstalled = true;
    private bool _livePreviewWhisperInstalled;
    private bool _livePreviewCanRemoveWhisper;
    private SpeechModelDelivery _livePreviewWhisperDeliveryStage = SpeechModelDelivery.Unknown;
    private double? _livePreviewWhisperDownloadPercent;
    private string? _livePreviewWhisperDeliveryDetail;

    /// <summary>The person asked for the NVIDIA graphics runtime to be downloaded.</summary>
    public event Action? GraphicsRuntimeDownloadRequested;

    /// <summary>The person asked for the running graphics runtime download to stop.</summary>
    public event Action? GraphicsRuntimeDownloadCancelRequested;

    /// <summary>Shows where the words went, and asks about the language if this is the moment to.</summary>
    /// <remarks>
    /// ONE ENTRY POINT SO THE PILL CANNOT BE SET TWICE. The offer takes the pill INSTEAD of the
    /// delivery sentence rather than after it, and it carries that sentence along when the sentence
    /// had a pill of its own, so somebody who asked for the clipboard is still told the text went to
    /// the clipboard. Onboarding and the offer read where the words went from the delivery, never
    /// from the pill: a landed paste raises no pill at all.
    /// </remarks>
    public void ReportDeliveryAndMaybeOfferLanguage(DeliveryReport delivered, string? detectedLanguage)
    {
        ObserveDeliveryForOnboarding(delivered.Landing);
        var offer = delivered.InvitesLanguageOffer
            ? _languageSuggestions.Observe(
                detectedLanguage,
                _settings.Preferences.Dictation.WhisperLanguage)
            : null;
        if (offer is null)
        {
            SetSessionStatus(delivered.Status());
            return;
        }

        ShowLanguageLockOffer(offer, delivered);

        // WRITTEN DOWN THE MOMENT IT IS SHOWN, not when it is answered. Most offers are never
        // answered at all - the pill lapses - and that is exactly the case the count exists to
        // limit, so waiting for an answer would record only the offers that did not need recording.
        _ = RememberLanguageOffersAsync();
    }

    private async Task RememberLanguageOffersAsync()
    {
        var history = _languageSuggestions.OfferHistory;
        if (string.Equals(_settings.LanguageOfferHistory, history, StringComparison.Ordinal))
        {
            return;
        }

        // NO NOTICE ON FAILURE, DELIBERATELY. Nobody asked for this write and nobody is waiting on
        // it; the worst a lost one costs is being asked about a language once more than intended,
        // which is quieter than an error about bookkeeping over somebody's work.
        await UpdateSettingsAsync(current => current with { LanguageOfferHistory = history })
            .ConfigureAwait(true);
    }

    /// <summary>Offers to pin the language the app keeps hearing.</summary>
    /// <remarks>
    /// THE OFFERED LANGUAGE IS REMEMBERED HERE RATHER THAN CARRIED ON THE BUTTON, because the pill's
    /// vocabulary names intents and a language code in it would tie the overlay to the list of
    /// languages the settings page happens to hold.
    /// </remarks>
    private void ShowLanguageLockOffer(LanguageLockOffer offer, DeliveryReport delivered)
    {
        ArgumentNullException.ThrowIfNull(offer);
        _offeredLanguage = offer.Language;
        var (question, action) = offer.Kind == LanguageOfferKind.AskToLock
            ? ($"You keep speaking {offer.DisplayName}. Pin it so recognition stops guessing?",
                new PillAction(
                    $"Use {offer.DisplayName}",
                    PillActionKind.LockDetectedLanguage,
                    $"Always use {offer.DisplayName} for speech recognition"))
            : ($"You keep speaking {offer.DisplayName}. Pin it under Transcription whenever you like.",
                new PillAction(
                    "Open settings",
                    PillActionKind.OpenTranscriptionSettings,
                    "Open transcription settings"));

        // THE OFFER TAKES THE PILL, SO IT HAS TO CARRY WHAT THE PILL WOULD HAVE SAID. Where the
        // words went is not always the same sentence: somebody using the clipboard setting was told
        // "Copied to your clipboard", and replacing that with a question about languages leaves them
        // believing the text was inserted into the window in front of them. A landed paste had no
        // pill, so the question stands alone.
        SetSessionStatus(DictationStatus.Suggestion(delivered.SuggestionText(question), action));
    }

    /// <summary>Saves the language the pill offered, and tells the app it was taken.</summary>
    /// <remarks>
    /// THE OFFER IS HELD UNTIL THE SAVE SUCCEEDS. Clearing it first meant a save that failed left
    /// nothing: the pill was already gone, the setting was unchanged, and the only report was an
    /// InfoBar inside a window that is usually not on screen when somebody dictates. A guard rather
    /// than the cleared field is what stops a second press starting a second save.
    /// </remarks>
    private async void LockOfferedLanguage()
    {
        var language = _offeredLanguage;
        if (language is null || _lockingLanguage)
        {
            return;
        }

        _lockingLanguage = true;
        try
        {
            await LockLanguageAsync(language.Value).ConfigureAwait(true);
        }
        finally
        {
            _lockingLanguage = false;
        }
    }

    private async Task LockLanguageAsync(WhisperLanguagePreference language)
    {
        if (await TrySaveAsync(
            current => current with
            {
                Preferences = current.Preferences with
                {
                    Dictation = current.Preferences.Dictation with { WhisperLanguage = language },
                },
            },
            "Language pinned",
            $"Recognition will use {LanguageLockSuggester.DisplayName(language)}.").ConfigureAwait(true))
        {
            _offeredLanguage = null;
            _languageSuggestions.Accepted(language);
            await RememberLanguageOffersAsync().ConfigureAwait(true);
            return;
        }

        // THE PILL SAYS SO, BECAUSE THE PILL IS WHERE THEY WERE LOOKING. A settings page notice is
        // the right place for a save somebody started on the settings page; this one started on an
        // overlay over their work, and a button that appears to do nothing is worse than no button.
        SetSessionStatus(DictationStatus.Warning(
            $"{LanguageLockSuggester.DisplayName(language)} could not be pinned. Open Transcription "
                + "settings to set it there."));
    }

    private void LivePreviewToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_isApplyingSettings)
        {
            UpdatePillDesignControls();
            RefreshLivePreviewSectionPresentation();
        }
    }

    private async void LivePreviewEngineCard_Checked(object sender, RoutedEventArgs e)
    {
        if (_isApplyingSettings)
        {
            return;
        }

        RefreshLivePreviewSectionPresentation();
        await TriggerAutoSaveAsync().ConfigureAwait(true);
    }

    private void DownloadLivePreviewWhisperButton_Click(object sender, RoutedEventArgs e) =>
        PreviewModelDownloadRequested?.Invoke();

    private void CancelLivePreviewWhisperDownloadButton_Click(object sender, RoutedEventArgs e) =>
        ModelDownloadCancelRequested?.Invoke();

    private async void RemoveLivePreviewWhisperButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session.Closing)
        {
            return;
        }

        if (IsSpeechEngineBusy?.Invoke() == true)
        {
            ShowMessage(
                "Finish dictation first",
                EnginePolishPresentation.RemovalBlockedBusyNotice,
                InfoBarSeverity.Warning);
            return;
        }

        if (Content?.XamlRoot is not { } xamlRoot)
        {
            return;
        }

        var confirmation = LivePreviewSettingsPresentation.DescribeWhisperRemovalDialog(
            whisperSelected: LivePreviewWhisperEngineCard?.IsChecked == true,
            livePreviewEnabled: LivePreviewToggle?.IsOn == true,
            parakeetInstalled: _livePreviewParakeetInstalled);
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            RequestedTheme = (Content as FrameworkElement)?.RequestedTheme ?? ElementTheme.Default,
            Title = confirmation.Title,
            Content = new TextBlock
            {
                Text = confirmation.Message,
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = confirmation.ConfirmButtonText,
            PrimaryButtonStyle = Application.Current.Resources["BrandDestructiveButtonStyle"] as Style,
            CloseButtonText = confirmation.CancelButtonText,
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary || _session.Closing)
        {
            return;
        }

        if (IsSpeechEngineBusy?.Invoke() == true)
        {
            ShowMessage(
                "Finish dictation first",
                EnginePolishPresentation.RemovalBlockedBusyNotice,
                InfoBarSeverity.Warning);
            return;
        }

        PreviewModelRemoveRequested?.Invoke();
    }

    private void LivePreviewParakeetCardContainer_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (IsTapFromNestedActionButton(e.OriginalSource) || _settings is null)
        {
            return;
        }

        if (LivePreviewParakeetEngineCard is not null && LivePreviewParakeetEngineCard.IsChecked != true)
        {
            LivePreviewParakeetEngineCard.IsChecked = true;
        }
    }

    private void LivePreviewWhisperCardContainer_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (IsTapFromNestedActionButton(e.OriginalSource) || _settings is null)
        {
            return;
        }

        if (LivePreviewWhisperEngineCard is not null && LivePreviewWhisperEngineCard.IsChecked != true)
        {
            LivePreviewWhisperEngineCard.IsChecked = true;
        }
    }

    private void LivePreviewEngineCardsPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (LivePreviewEngineCardsPanel is null || LivePreviewParakeetCardContainer is null || LivePreviewWhisperCardContainer is null ||
            LivePreviewParakeetCardColumn is null || LivePreviewWhisperCardColumn is null)
        {
            return;
        }

        var stacked = e.NewSize.Width < 540;
        if (stacked)
        {
            LivePreviewWhisperCardColumn.Width = new GridLength(0);
            Grid.SetRow(LivePreviewWhisperCardContainer, 1);
            Grid.SetColumn(LivePreviewWhisperCardContainer, 0);
        }
        else
        {
            LivePreviewWhisperCardColumn.Width = new GridLength(1, GridUnitType.Star);
            Grid.SetRow(LivePreviewWhisperCardContainer, 0);
            Grid.SetColumn(LivePreviewWhisperCardContainer, 1);
        }
    }

    private int SelectedLivePreviewEngineIndex() =>
        LivePreviewWhisperEngineCard?.IsChecked == true
            ? 1
            : LivePreviewParakeetEngineCard?.IsChecked == true
                ? 0
                : -1;

    public void SetLivePreviewModelInventory(
        bool parakeetInstalled,
        bool whisperPreviewInstalled,
        bool canRemoveWhisperModel)
    {
        _livePreviewParakeetInstalled = parakeetInstalled;
        _livePreviewWhisperInstalled = whisperPreviewInstalled;
        _livePreviewCanRemoveWhisper = canRemoveWhisperModel;
        if (whisperPreviewInstalled)
        {
            _livePreviewWhisperDeliveryStage = SpeechModelDelivery.Installed;
            _livePreviewWhisperDownloadPercent = null;
            _livePreviewWhisperDeliveryDetail = null;
        }
        else if (_livePreviewWhisperDeliveryStage is SpeechModelDelivery.Installed or SpeechModelDelivery.Activating)
        {
            _livePreviewWhisperDeliveryStage = SpeechModelDelivery.Missing;
            _livePreviewWhisperDownloadPercent = null;
            _livePreviewWhisperDeliveryDetail = null;
        }

        RefreshLivePreviewSectionPresentation();
    }

    public void SetLivePreviewWhisperDelivery(ModelDeliveryPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        _livePreviewWhisperDeliveryStage = presentation.Stage;
        _livePreviewWhisperDownloadPercent = presentation.Percent;
        _livePreviewWhisperDeliveryDetail = presentation.Text;
        RefreshLivePreviewSectionPresentation();
    }

    private void RefreshLivePreviewSectionPresentation()
    {
        if (LivePreviewToggle is null || LivePreviewParakeetEngineCard is null || LivePreviewWhisperEngineCard is null)
        {
            return;
        }

        var selectedEngine = LivePreviewWhisperEngineCard.IsChecked == true
            ? LivePreviewEngineChoice.Whisper
            : LivePreviewEngineChoice.Parakeet;
        var presentation = LivePreviewSettingsPresentation.Present(
            LivePreviewToggle.IsOn,
            selectedEngine,
            _livePreviewParakeetInstalled,
            _livePreviewWhisperInstalled,
            _livePreviewWhisperDeliveryStage,
            _livePreviewWhisperDownloadPercent,
            _livePreviewWhisperDeliveryDetail,
            _livePreviewCanRemoveWhisper);

        LivePreviewReadyBadgeText.Text = presentation.StatusBadgeText;
        LivePreviewWarningBadgeText.Text = presentation.StatusBadgeText;
        LivePreviewErrorBadgeText.Text = presentation.StatusBadgeText;
        LivePreviewOffBadgeText.Text = presentation.StatusBadgeText;

        LivePreviewReadyBadge.Visibility = presentation.StatusTone == LivePreviewStatusTone.Ready ? Visibility.Visible : Visibility.Collapsed;
        LivePreviewWarningBadge.Visibility = presentation.StatusTone == LivePreviewStatusTone.Warning ? Visibility.Visible : Visibility.Collapsed;
        LivePreviewErrorBadge.Visibility = presentation.StatusTone == LivePreviewStatusTone.Error ? Visibility.Visible : Visibility.Collapsed;
        LivePreviewOffBadge.Visibility = presentation.StatusTone == LivePreviewStatusTone.Off ? Visibility.Visible : Visibility.Collapsed;

        var accentBrush = Application.Current.Resources["BrandAccentBrush"] as Brush;
        var dividerBrush = Application.Current.Resources["BrandDividerBrush"] as Brush;
        if (LivePreviewParakeetCardContainer is not null)
        {
            LivePreviewParakeetCardContainer.BorderBrush = presentation.ParakeetCard.IsSelected ? accentBrush : dividerBrush;
            LivePreviewParakeetCardContainer.BorderThickness = new Thickness(presentation.ParakeetCard.IsSelected ? 2 : 1);
        }

        if (LivePreviewWhisperCardContainer is not null)
        {
            LivePreviewWhisperCardContainer.BorderBrush = presentation.WhisperCard.IsSelected ? accentBrush : dividerBrush;
            LivePreviewWhisperCardContainer.BorderThickness = new Thickness(presentation.WhisperCard.IsSelected ? 2 : 1);
        }

        if (LivePreviewParakeetHeadlineText is not null)
        {
            SetLiveText(LivePreviewParakeetHeadlineText, presentation.ParakeetCard.StatusHeadline);
        }

        if (LivePreviewWhisperHeadlineText is not null)
        {
            SetLiveText(LivePreviewWhisperHeadlineText, presentation.WhisperCard.StatusHeadline);
        }

        SetLiveText(LivePreviewStatusDetailText, presentation.StatusDetailText);
        LivePreviewPrivacyText.Text = presentation.PrivacyReassuranceText;
        LivePreviewParakeetStatusText.Text = presentation.ParakeetCard.StatusText;
        LivePreviewWhisperStatusText.Text = presentation.WhisperCard.StatusText;

        LivePreviewWhisperDownloadProgress.Visibility = presentation.WhisperCard.ProgressPercent is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        LivePreviewWhisperDownloadProgress.Value = presentation.WhisperCard.ProgressPercent ?? 0;

        DownloadLivePreviewWhisperButton.Content = presentation.WhisperCard.DownloadButtonText;
        DownloadLivePreviewWhisperButton.Visibility = presentation.WhisperCard.CanDownload
            ? Visibility.Visible
            : Visibility.Collapsed;
        CancelLivePreviewWhisperDownloadButton.Visibility = presentation.WhisperCard.CanCancel
            ? Visibility.Visible
            : Visibility.Collapsed;
        RemoveLivePreviewWhisperButton.Visibility = presentation.WhisperCard.CanRemove
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private int SelectedFinalEngineRawIndex() =>
        ParakeetEngineCard?.IsChecked == true
            ? 0
            : WhisperEngineCard?.IsChecked == true
                ? 1
                : -1;

    private FinalAsrEngine SelectedFinalEngine() =>
        WhisperEngineCard?.IsChecked == true
            ? FinalAsrEngine.Whisper
            : FinalAsrEngine.Parakeet;

    private void SetSelectedFinalEngineCards(FinalAsrEngine selectedEngine)
    {
        if (ParakeetEngineCard is not null)
        {
            ParakeetEngineCard.IsChecked = selectedEngine == FinalAsrEngine.Parakeet;
        }

        if (WhisperEngineCard is not null)
        {
            WhisperEngineCard.IsChecked = selectedEngine == FinalAsrEngine.Whisper;
        }

        RenderSpeechEngineCards();
    }

    private FinalAsrEngine ResolveEngineFromCard(object sender) =>
        sender is RadioButton radio && ReferenceEquals(radio, WhisperEngineCard)
            ? FinalAsrEngine.Whisper
            : ReferenceEquals(sender, ParakeetEngineCard)
                ? FinalAsrEngine.Parakeet
                : SelectedFinalEngine();

    private void ApplySelectedEngineCard(FinalAsrEngine selectedEngine)
    {
        _lastActiveEngine = selectedEngine;
        SetSelectedFinalEngineCards(selectedEngine);
        UpdateActiveEngineDisplay(selectedEngine);
        UpdateHistoryStatusCards();
        ShowSpeechEngineNotice(string.Empty);
        _ = TriggerAutoSaveAsync();
    }

    private void EngineCardChecked(object sender, RoutedEventArgs e)
    {
        if (_isApplyingSettings || _settings is null)
        {
            return;
        }

        var selectedEngine = ResolveEngineFromCard(sender);
        if (_lastActiveEngine == selectedEngine)
        {
            RenderSpeechEngineCards();
            return;
        }

        ApplySelectedEngineCard(selectedEngine);
    }

    private void EngineCardClicked(object sender, RoutedEventArgs e)
    {
        if (_isApplyingSettings || _settings is null)
        {
            return;
        }

        var selectedEngine = ResolveEngineFromCard(sender);
        if (_lastActiveEngine == selectedEngine)
        {
            return;
        }

        ApplySelectedEngineCard(selectedEngine);
    }

    private static bool IsTapFromNestedActionButton(object? originalSource)
    {
        var current = originalSource as DependencyObject;
        while (current is not null)
        {
            if (current is Button)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void ParakeetEngineCardContainer_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (IsTapFromNestedActionButton(e.OriginalSource) || _settings is null)
        {
            return;
        }

        if (ParakeetEngineCard is not null && ParakeetEngineCard.IsChecked != true)
        {
            ParakeetEngineCard.IsChecked = true;
        }
    }

    private void WhisperEngineCardContainer_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (IsTapFromNestedActionButton(e.OriginalSource) || _settings is null)
        {
            return;
        }

        if (WhisperEngineCard is not null && WhisperEngineCard.IsChecked != true)
        {
            WhisperEngineCard.IsChecked = true;
        }
    }

    private void EngineCardsGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (EngineCardsGrid is null || ParakeetEngineCardContainer is null || WhisperEngineCardContainer is null ||
            ParakeetCardColumn is null || WhisperCardColumn is null)
        {
            return;
        }

        var stacked = e.NewSize.Width < 540;
        if (stacked)
        {
            WhisperCardColumn.Width = new GridLength(0);
            Grid.SetRow(WhisperEngineCardContainer, 1);
            Grid.SetColumn(WhisperEngineCardContainer, 0);
        }
        else
        {
            WhisperCardColumn.Width = new GridLength(1, GridUnitType.Star);
            Grid.SetRow(WhisperEngineCardContainer, 0);
            Grid.SetColumn(WhisperEngineCardContainer, 1);
        }
    }

    private void ParakeetDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSpeechEngineNotice(string.Empty);
        SpeechEngineDownloadRequested?.Invoke(FinalAsrEngine.Parakeet);
    }

    private void WhisperDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSpeechEngineNotice(string.Empty);
        SpeechEngineDownloadRequested?.Invoke(FinalAsrEngine.Whisper);
    }

    private async void ParakeetRemoveButton_Click(object sender, RoutedEventArgs e) =>
        await HandleSpeechEngineRemoveClickAsync(FinalAsrEngine.Parakeet).ConfigureAwait(true);

    private async void WhisperRemoveButton_Click(object sender, RoutedEventArgs e) =>
        await HandleSpeechEngineRemoveClickAsync(FinalAsrEngine.Whisper).ConfigureAwait(true);

    private async Task HandleSpeechEngineRemoveClickAsync(FinalAsrEngine engine)
    {
        if (_session.Closing)
        {
            return;
        }

        ShowSpeechEngineNotice(string.Empty);
        if (IsSpeechEngineBusy?.Invoke() == true)
        {
            ShowSpeechEngineNotice(EnginePolishPresentation.RemovalBlockedBusyNotice);
            return;
        }

        var activeEngine = _settings?.Preferences.Dictation.FinalEngine ?? SelectedFinalEngine();
        var isSelectedFinalEngine = engine == activeEngine;
        var livePreviewUsesParakeet = _settings?.Preferences.LivePreviewEnabled == true &&
            (!_livePreviewWhisperInstalled || _settings.Preferences.LivePreviewEngine == LivePreviewEngineChoice.Parakeet);
        var requiresConfirm = isSelectedFinalEngine ||
            (engine == FinalAsrEngine.Parakeet && livePreviewUsesParakeet);
        if (requiresConfirm)
        {
            if (Content?.XamlRoot is not { } xamlRoot)
            {
                return;
            }

            var confirmation = EnginePolishPresentation.DescribeActiveEngineRemovalDialog(
                engine,
                livePreviewUsesParakeet,
                isSelectedFinalEngine);
            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                RequestedTheme = (Content as FrameworkElement)?.RequestedTheme ?? ElementTheme.Default,
                Title = confirmation.Title,
                Content = new TextBlock
                {
                    Text = confirmation.Message,
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = confirmation.ConfirmButtonText,
                CloseButtonText = confirmation.CancelButtonText,
                DefaultButton = ContentDialogButton.Close,
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary || _session.Closing)
            {
                return;
            }
        }

        if (IsSpeechEngineBusy?.Invoke() == true)
        {
            ShowSpeechEngineNotice(EnginePolishPresentation.RemovalBlockedBusyNotice);
            return;
        }

        SpeechEngineRemoveRequested?.Invoke(engine);
    }

    public void SetSpeechEngineModels(
        SpeechEngineModelState parakeet,
        SpeechEngineModelState whisper,
        FinalAsrEngine? downloadingEngine)
    {
        ArgumentNullException.ThrowIfNull(parakeet);
        ArgumentNullException.ThrowIfNull(whisper);
        _parakeetModelState = parakeet;
        _whisperModelState = whisper;
        _activeSpeechDownloadEngine = downloadingEngine;
        RenderSpeechEngineCards();
        UpdateActiveEngineDisplay();
    }

    public void SetSpeechEngineDelivery(FinalAsrEngine engine, ModelDeliveryPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        if (engine == FinalAsrEngine.Whisper)
        {
            _whisperModelState = _whisperModelState with
            {
                DeliveryStage = presentation.Stage,
                DownloadPercent = presentation.Percent,
                StatusDetail = presentation.Text,
            };
        }
        else
        {
            _parakeetModelState = _parakeetModelState with
            {
                DeliveryStage = presentation.Stage,
                DownloadPercent = presentation.Percent,
                StatusDetail = presentation.Text,
            };
        }

        _activeSpeechDownloadEngine = presentation.Stage == SpeechModelDelivery.Downloading
            ? engine
            : null;
        RenderSpeechEngineCards();
        UpdateActiveEngineDisplay();
    }

    public void ShowSpeechEngineNotice(string message)
    {
        if (SpeechEngineNoticeText is null || SpeechEngineNoticeBanner is null)
        {
            return;
        }

        var normalized = message ?? string.Empty;
        SetLiveText(SpeechEngineNoticeText, normalized);
        SpeechEngineNoticeBanner.Visibility = string.IsNullOrWhiteSpace(normalized)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void RenderSpeechEngineCards()
    {
        var selectedEngine = SelectedFinalEngine();
        var parakeetOtherInFlight = _activeSpeechDownloadEngine is not null && _activeSpeechDownloadEngine != FinalAsrEngine.Parakeet;
        var whisperOtherInFlight = _activeSpeechDownloadEngine is not null && _activeSpeechDownloadEngine != FinalAsrEngine.Whisper;
        var parakeetCard = EnginePolishPresentation.DescribeEngineCard(
            FinalAsrEngine.Parakeet,
            selectedEngine,
            _parakeetModelState,
            parakeetOtherInFlight);
        var whisperCard = EnginePolishPresentation.DescribeEngineCard(
            FinalAsrEngine.Whisper,
            selectedEngine,
            _whisperModelState,
            whisperOtherInFlight);

        if (ParakeetCardStatusText is not null)
        {
            SetLiveText(ParakeetCardStatusText, parakeetCard.StatusText);
        }

        if (WhisperCardStatusText is not null)
        {
            SetLiveText(WhisperCardStatusText, whisperCard.StatusText);
        }

        ApplySpeechEngineCardPresentation(
            parakeetCard,
            ParakeetEngineCardContainer,
            ParakeetCardSizeText,
            ParakeetCardDetailText,
            ParakeetCardProgress,
            ParakeetDownloadButton,
            ParakeetCancelButton,
            ParakeetRemoveButton);
        ApplySpeechEngineCardPresentation(
            whisperCard,
            WhisperEngineCardContainer,
            WhisperCardSizeText,
            WhisperCardDetailText,
            WhisperCardProgress,
            WhisperDownloadButton,
            WhisperCancelButton,
            WhisperRemoveButton);
    }

    private static void ApplySpeechEngineCardPresentation(
        SpeechEngineCardPresentation card,
        Border? container,
        TextBlock? sizeText,
        TextBlock? detailText,
        ProgressBar? progressBar,
        Button? downloadButton,
        Button? cancelButton,
        Button? removeButton)
    {
        if (container is not null)
        {
            var accentBrush = Application.Current.Resources["BrandAccentBrush"] as Brush;
            var dividerBrush = Application.Current.Resources["BrandDividerBrush"] as Brush;
            container.BorderBrush = card.IsSelected ? accentBrush : dividerBrush;
            container.BorderThickness = new Thickness(card.IsSelected ? 2 : 1);
        }

        if (sizeText is not null)
        {
            sizeText.Text = card.BundleSizeText;
        }

        if (detailText is not null)
        {
            detailText.Text = card.StatusDetail ?? string.Empty;
            detailText.Visibility = string.IsNullOrWhiteSpace(card.StatusDetail)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        if (progressBar is not null)
        {
            progressBar.Visibility = card.ShowProgress ? Visibility.Visible : Visibility.Collapsed;
            progressBar.Value = card.DownloadPercent;
        }

        if (downloadButton is not null)
        {
            downloadButton.Content = card.DownloadButtonText;
            downloadButton.IsEnabled = card.DownloadButtonEnabled;
            downloadButton.Visibility = card.ShowDownloadButton ? Visibility.Visible : Visibility.Collapsed;
        }

        if (cancelButton is not null)
        {
            cancelButton.Visibility = card.ShowCancelButton ? Visibility.Visible : Visibility.Collapsed;
        }

        if (removeButton is not null)
        {
            removeButton.IsEnabled = card.RemoveButtonEnabled;
            removeButton.Visibility = card.ShowRemoveButton ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void UpdateActiveEngineDisplay(FinalAsrEngine? engineOverride = null)
    {
        var engine = engineOverride ?? SelectedFinalEngine();
        var summary = EnginePolishPresentation.DescribeEngine(engine);

        if (ActiveEngineNameText is not null)
        {
            ActiveEngineNameText.Text = summary.Name;
        }

        if (ActiveEngineBadgeText is not null)
        {
            ActiveEngineBadgeText.Text = summary.BadgeText;
        }

        if (ActiveEngineDescriptionText is not null)
        {
            ActiveEngineDescriptionText.Text = summary.Description;
        }

        if (ActiveEngineIcon is not null)
        {
            ActiveEngineIcon.Glyph = summary.IconGlyph;
        }

        if (ActiveEngineStatusText is not null)
        {
            var activeState = engine == FinalAsrEngine.Whisper ? _whisperModelState : _parakeetModelState;
            var activeCard = EnginePolishPresentation.DescribeEngineCard(
                engine,
                engine,
                activeState,
                otherDownloadInFlight: false);
            ActiveEngineStatusText.Text = activeState.DeliveryStage == SpeechModelDelivery.Unknown
                ? "Checking..."
                : activeCard.StatusText;
        }

        if (WhisperLanguageRow is not null)
        {
            WhisperLanguageRow.Visibility = summary.ShowWhisperLanguage
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        UpdateSpeechEngineOptionsCardVisibility();
    }

    private void CancelModelDownloadButton_Click(object sender, RoutedEventArgs e) =>
        ModelDownloadCancelRequested?.Invoke();

    private void DownloadGraphicsRuntimeButton_Click(object sender, RoutedEventArgs e) =>
        GraphicsRuntimeDownloadRequested?.Invoke();

    private void CancelGraphicsRuntimeDownloadButton_Click(object sender, RoutedEventArgs e) =>
        GraphicsRuntimeDownloadCancelRequested?.Invoke();

    /// <summary>
    /// Shows the NVIDIA graphics runtime row on the Transcription card, or hides it when there is nothing to say.
    /// </summary>
    /// <remarks>
    /// HIDDEN UNLESS IT APPLIES. The app decides whether this PC's card would be used; a PC without an NVIDIA
    /// card never sees the row at all, so null is the common case and it collapses the whole row.
    /// </remarks>
    public void SetGraphicsRuntimeDelivery(ModelDeliveryPresentation? presentation)
    {
        if (presentation is null)
        {
            GraphicsRuntimeRow.Visibility = Visibility.Collapsed;
            UpdateSpeechEngineOptionsCardVisibility();
            return;
        }

        GraphicsRuntimeRow.Visibility = Visibility.Visible;
        SetLiveText(GraphicsRuntimeStatusText, presentation.Text);
        GraphicsRuntimeProgress.Visibility = presentation.Percent is null ? Visibility.Collapsed : Visibility.Visible;
        GraphicsRuntimeProgress.Value = presentation.Percent ?? 0;
        DownloadGraphicsRuntimeButton.Visibility = presentation.CanDownload ? Visibility.Visible : Visibility.Collapsed;
        CancelGraphicsRuntimeDownloadButton.Visibility = presentation.CanCancel ? Visibility.Visible : Visibility.Collapsed;
        UpdateSpeechEngineOptionsCardVisibility();
    }

    private void UpdateSpeechEngineOptionsCardVisibility()
    {
        if (SpeechEngineOptionsCard is null)
        {
            return;
        }

        var showWhisperLanguage = WhisperLanguageRow?.Visibility == Visibility.Visible;
        var showGraphicsRuntime = GraphicsRuntimeRow?.Visibility == Visibility.Visible;
        SpeechEngineOptionsCard.Visibility = showWhisperLanguage || showGraphicsRuntime
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>
    /// Forwards active speech-model delivery state to the onboarding flow (#92, #357).
    /// </summary>
    /// <remarks>
    /// Per-engine download progress and actions live inside each Engine card in EngineCardsGrid;
    /// forwarding the active presentation here keeps first-run onboarding tracking in sync.
    /// </remarks>
    public void SetModelDelivery(ModelDeliveryPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        ObserveModelForOnboarding(presentation);
    }

    private void SyncAppliedWhisperLanguageStatus(WhisperLanguagePreference language)
    {
        var index = (int)language;
        var appliedLabel = index >= 0 && index < WhisperLanguageComboBox.Items.Count &&
            WhisperLanguageComboBox.Items[index] is string label
                ? label
                : language.ToString();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(WhisperLanguageComboBox, appliedLabel);
    }
}
