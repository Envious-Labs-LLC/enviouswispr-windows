using EnviousWispr.Core.Feedback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace EnviousWispr.App;

public sealed partial class FeedbackDialog : ContentDialog, IDisposable
{
    public const string GitHubIssuesUrl = "https://github.com/Envious-Labs-LLC/enviouswispr-windows/issues";

    private readonly FeedbackDraftStore? _draftStore;
    private readonly Func<FeedbackDiagnosticsSnapshot> _buildDiagnosticsSnapshot;
    private readonly Func<FeedbackSubmission, CancellationToken, Task<FeedbackSendResult>> _submitFeedback;
    private readonly Func<CancellationToken, Task<bool>>? _checkUndeliverableFeedback;
    private readonly FeedbackDiagnosticsSnapshot _diagnosticsSnapshot;

    private CancellationTokenSource? _submitCts;
    private FeedbackKind _selectedKind = FeedbackKind.Bug;
    private bool _isSubmitting;
    private bool _isRestoringDraft;

    public FeedbackDialog(
        FeedbackDraftStore? draftStore,
        Func<FeedbackDiagnosticsSnapshot> buildDiagnosticsSnapshot,
        Func<FeedbackSubmission, CancellationToken, Task<FeedbackSendResult>> submitFeedback,
        bool defaultIncludeDiagnostics = true,
        Func<CancellationToken, Task<bool>>? checkUndeliverableFeedback = null)
    {
        ArgumentNullException.ThrowIfNull(buildDiagnosticsSnapshot);
        ArgumentNullException.ThrowIfNull(submitFeedback);

        _draftStore = draftStore;
        _buildDiagnosticsSnapshot = buildDiagnosticsSnapshot;
        _submitFeedback = submitFeedback;
        _checkUndeliverableFeedback = checkUndeliverableFeedback;

        InitializeComponent();

        _diagnosticsSnapshot = _buildDiagnosticsSnapshot();
        RestoreDraft(defaultIncludeDiagnostics);
        UpdateCategoryVisuals(announceSelection: false);
        UpdateDiagnosticsPreview();
        UpdateCharacterCount();
        UpdateValidationAndSubmitState();
        Opened += FeedbackDialog_Opened;
    }

    private void RestoreDraft(bool defaultIncludeDiagnostics)
    {
        _isRestoringDraft = true;
        try
        {
            IncludeDiagnosticsToggle.IsOn = defaultIncludeDiagnostics;
            var draft = _draftStore?.Load();
            if (draft is null)
            {
                return;
            }

            _selectedKind = draft.Kind;
            MessageTextBox.Text = draft.Message;
            EmailTextBox.Text = draft.ContactEmail ?? string.Empty;
        }
        finally
        {
            _isRestoringDraft = false;
        }
    }

    private async void FeedbackDialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        if (_checkUndeliverableFeedback is null)
        {
            return;
        }

        try
        {
            var hasUndeliverable = await _checkUndeliverableFeedback(CancellationToken.None).ConfigureAwait(true);
            if (hasUndeliverable &&
                !_isSubmitting &&
                FeedbackStatusBorder.Visibility != Visibility.Visible)
            {
                ShowStatusBanner(
                    "Some previously saved feedback could not be sent and remains on this PC.",
                    isProgress: false,
                    backgroundKey: "BrandWarningSoftBrush",
                    borderKey: "BrandWarningBrush");
            }
        }
        catch
        {
            // Best-effort status check.
        }
    }

    private void PersistDraft()
    {
        if (_isRestoringDraft || _isSubmitting || _draftStore is null)
        {
            return;
        }

        try
        {
            _draftStore.Save(new FeedbackDraft(
                Kind: _selectedKind,
                Message: MessageTextBox.Text ?? string.Empty,
                ContactEmail: string.IsNullOrWhiteSpace(EmailTextBox.Text) ? null : EmailTextBox.Text,
                IncludeDiagnostics: IncludeDiagnosticsToggle.IsOn));
        }
        catch
        {
            // Draft persistence is best-effort while typing.
        }
    }

    private void CategoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isSubmitting)
        {
            return;
        }

        _selectedKind = ReferenceEquals(sender, FeatureCategoryButton)
            ? FeedbackKind.Feature
            : ReferenceEquals(sender, GeneralCategoryButton)
                ? FeedbackKind.General
                : FeedbackKind.Bug;

        UpdateCategoryVisuals(announceSelection: true);
        PersistDraft();
    }

    private void UpdateCategoryVisuals(bool announceSelection)
    {
        var activeStyle = ResolveStyle("BrandSegmentedActiveButtonStyle");
        var inactiveStyle = ResolveStyle("BrandSegmentedButtonStyle");

        ApplyCategoryButtonState(BugCategoryButton, _selectedKind == FeedbackKind.Bug, activeStyle, inactiveStyle);
        ApplyCategoryButtonState(FeatureCategoryButton, _selectedKind == FeedbackKind.Feature, activeStyle, inactiveStyle);
        ApplyCategoryButtonState(GeneralCategoryButton, _selectedKind == FeedbackKind.General, activeStyle, inactiveStyle);

        if (announceSelection)
        {
            var selectedButton = _selectedKind switch
            {
                FeedbackKind.Feature => FeatureCategoryButton,
                FeedbackKind.General => GeneralCategoryButton,
                _ => BugCategoryButton,
            };
            var peer = FrameworkElementAutomationPeer.FromElement(selectedButton);
            peer?.RaiseNotificationEvent(
                AutomationNotificationKind.Other,
                AutomationNotificationProcessing.CurrentThenMostRecent,
                $"{FeedbackKinds.ToDisplayName(_selectedKind)} selected",
                "FeedbackCategorySelected");
        }
    }

    private static void ApplyCategoryButtonState(
        Button button,
        bool isSelected,
        Style? activeStyle,
        Style? inactiveStyle)
    {
        var style = isSelected ? activeStyle : inactiveStyle;
        if (style is not null)
        {
            button.Style = style;
        }

        AutomationProperties.SetItemStatus(button, isSelected ? "Selected" : "Not selected");
    }

    private void MessageTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateCharacterCount();
        UpdateValidationAndSubmitState();
        PersistDraft();
    }

    private void EmailTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateValidationAndSubmitState();
        PersistDraft();
    }

    private void IncludeDiagnosticsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (DiagnosticsPreviewTextBlock is null)
        {
            return;
        }

        UpdateDiagnosticsPreview();
        PersistDraft();
    }

    private void UpdateDiagnosticsPreview()
    {
        DiagnosticsPreviewTextBlock.Text = IncludeDiagnosticsToggle.IsOn
            ? _diagnosticsSnapshot.FormatPreviewText()
            : "Diagnostics will not be included with this feedback.";
    }

    private void UpdateCharacterCount()
    {
        var length = MessageTextBox.Text?.Length ?? 0;
        CharacterCountText.Text = $"{length} / {FeedbackSubmission.MaxMessageLength}";
    }

    private void UpdateValidationAndSubmitState()
    {
        var emailValid = FeedbackSubmission.IsValidOptionalEmail(EmailTextBox.Text, out var emailError);
        if (!emailValid && !string.IsNullOrWhiteSpace(emailError))
        {
            EmailValidationText.Text = emailError;
            EmailValidationText.Visibility = Visibility.Visible;
        }
        else
        {
            EmailValidationText.Text = string.Empty;
            EmailValidationText.Visibility = Visibility.Collapsed;
        }

        var messageTrimmedLength = MessageTextBox.Text?.Trim().Length ?? 0;
        var messageValid = messageTrimmedLength > 0 && messageTrimmedLength <= FeedbackSubmission.MaxMessageLength;
        IsPrimaryButtonEnabled = !_isSubmitting && messageValid && emailValid;
    }

    private async void FeedbackDialog_PrimaryButtonClick(
        ContentDialog sender,
        ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (_isSubmitting)
        {
            return;
        }

        if (!FeedbackSubmission.TryCreate(
                _selectedKind,
                MessageTextBox.Text,
                EmailTextBox.Text,
                IncludeDiagnosticsToggle.IsOn,
                IncludeDiagnosticsToggle.IsOn ? _diagnosticsSnapshot : null,
                out var submission,
                out var validationError) ||
            submission is null)
        {
            ShowStatusBanner(
                validationError ?? "Please check your feedback message and try again.",
                isProgress: false,
                backgroundKey: "BrandErrorSoftBrush",
                borderKey: "BrandErrorBrush");
            UpdateValidationAndSubmitState();
            return;
        }

        await SubmitCoreAsync(submission).ConfigureAwait(true);
    }

    private async Task SubmitCoreAsync(FeedbackSubmission submission)
    {
        _isSubmitting = true;
        SetInputsEnabled(false);
        UpdateValidationAndSubmitState();
        ShowStatusBanner(
            "Sending feedback...",
            isProgress: true,
            backgroundKey: "BrandAccentLightBrush",
            borderKey: "BrandDividerBrush");

        _submitCts?.Cancel();
        _submitCts?.Dispose();
        _submitCts = new CancellationTokenSource();

        try
        {
            var result = await _submitFeedback(submission, _submitCts.Token).ConfigureAwait(true);
            HandleSubmitResult(result);
        }
        catch (OperationCanceledException)
        {
            ShowStatusBanner(
                "Sending cancelled. Your draft is still saved.",
                isProgress: false,
                backgroundKey: "BrandWarningSoftBrush",
                borderKey: "BrandWarningBrush");
        }
        catch
        {
            ShowStatusBanner(
                "Could not send feedback right now. Your draft is still saved.",
                isProgress: false,
                backgroundKey: "BrandErrorSoftBrush",
                borderKey: "BrandErrorBrush");
        }
        finally
        {
            _isSubmitting = false;
            SetInputsEnabled(true);
            UpdateValidationAndSubmitState();
        }
    }

    private void HandleSubmitResult(FeedbackSendResult result)
    {
        switch (result.Outcome)
        {
            case FeedbackSendOutcome.Sent:
                ClearFormAfterAccepted();
                ShowStatusBanner(
                    "Feedback sent. Thank you!",
                    isProgress: false,
                    backgroundKey: "BrandSuccessSoftBrush",
                    borderKey: "BrandSuccessBrush");
                break;

            case FeedbackSendOutcome.QueuedOffline:
                ClearFormAfterAccepted();
                ShowStatusBanner(
                    "Saved offline and will send automatically once connected.",
                    isProgress: false,
                    backgroundKey: "BrandAccentLightBrush",
                    borderKey: "BrandAccentBrush");
                break;

            case FeedbackSendOutcome.Unavailable:
                ShowStatusBanner(
                    "Feedback delivery isn't configured right now. You can open GitHub Issues below to share this report.",
                    isProgress: false,
                    backgroundKey: "BrandWarningSoftBrush",
                    borderKey: "BrandWarningBrush");
                break;

            case FeedbackSendOutcome.OutboxFull:
                ShowStatusBanner(
                    "Too many feedback reports are waiting offline. Please try again later or open GitHub Issues below.",
                    isProgress: false,
                    backgroundKey: "BrandErrorSoftBrush",
                    borderKey: "BrandErrorBrush");
                break;

            case FeedbackSendOutcome.Rejected:
            default:
                ShowStatusBanner(
                    "Feedback couldn't be accepted right now. Your draft is still here, or you can open GitHub Issues below.",
                    isProgress: false,
                    backgroundKey: "BrandErrorSoftBrush",
                    borderKey: "BrandErrorBrush");
                break;
        }
    }

    private void ClearFormAfterAccepted()
    {
        _isRestoringDraft = true;
        try
        {
            MessageTextBox.Text = string.Empty;
            EmailTextBox.Text = string.Empty;
            UpdateCharacterCount();
        }
        finally
        {
            _isRestoringDraft = false;
        }
    }

    private void SetInputsEnabled(bool enabled)
    {
        BugCategoryButton.IsEnabled = enabled;
        FeatureCategoryButton.IsEnabled = enabled;
        GeneralCategoryButton.IsEnabled = enabled;
        MessageTextBox.IsEnabled = enabled;
        EmailTextBox.IsEnabled = enabled;
        IncludeDiagnosticsToggle.IsEnabled = enabled;
    }

    private void ShowStatusBanner(
        string message,
        bool isProgress,
        string backgroundKey,
        string borderKey)
    {
        FeedbackStatusBorder.Visibility = Visibility.Visible;
        FeedbackStatusBorder.Background = ResolveBrush(backgroundKey);
        FeedbackStatusBorder.BorderBrush = ResolveBrush(borderKey);
        SubmittingProgressRing.IsActive = isProgress;
        SubmittingProgressRing.Visibility = isProgress ? Visibility.Visible : Visibility.Collapsed;
        SetLiveText(FeedbackStatusText, message);
    }

    private static void SetLiveText(TextBlock target, string text)
    {
        target.Text = text;
        FrameworkElementAutomationPeer.FromElement(target)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void FeedbackDialog_CloseButtonClick(
        ContentDialog sender,
        ContentDialogButtonClickEventArgs args)
    {
        CancelAndDisposeSubmitCts();
    }

    public void Dispose()
    {
        CancelAndDisposeSubmitCts();
    }

    private void CancelAndDisposeSubmitCts()
    {
        var cts = _submitCts;
        _submitCts = null;
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        cts.Dispose();
    }

    private async void GitHubIssuesLink_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri(GitHubIssuesUrl));
        }
        catch
        {
            // Launcher best-effort.
        }
    }

    private Style? ResolveStyle(string key) =>
        ResolveThemeResource<Style>(key);

    private Brush? ResolveBrush(string key) =>
        ResolveThemeResource<Brush>(key);

    private T? ResolveThemeResource<T>(string key) where T : class =>
        ThemeResourceLookup.Resolve<T>(this, key);
}
