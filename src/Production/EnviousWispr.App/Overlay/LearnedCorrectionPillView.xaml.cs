using EnviousWispr.Core.Learning;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace EnviousWispr.App.Overlay;

/// <summary>
/// Load-on-demand pill view for auto-learned vocabulary corrections with undo and keep controls.
/// </summary>
public sealed partial class LearnedCorrectionPillView : UserControl
{
    private bool _detached;

    public LearnedCorrectionPillView()
    {
        InitializeComponent();
    }

    public event Action? UndoClicked;

    public event Action? CloseClicked;

    public (bool ShowRail, int TargetWidth) Render(
        LearnedCorrectionPillModel model,
        Style titleStyle,
        bool announceNow)
    {
        ArgumentNullException.ThrowIfNull(model);
        var titleText = model.Phase switch
        {
            LearnedCorrectionPillPhase.Undone => model.Kind == LearnedCorrectionPillKind.Added
                ? $"\u201C{model.Canonical}\u201D removed"
                : "Removed from dictionary",
            LearnedCorrectionPillPhase.UndoPending => model.Canonical,
            LearnedCorrectionPillPhase.UndoError => "Couldn\u2019t undo",
            _ => model.Canonical,
        };
        var subtitleText = model.Phase switch
        {
            LearnedCorrectionPillPhase.UndoPending => "Undoing...",
            LearnedCorrectionPillPhase.Learned => model.Kind == LearnedCorrectionPillKind.Added
                ? "Added to dictionary"
                : "Updated in dictionary",
            _ => string.Empty,
        };

        LearnedPillTitle.Text = titleText;
        LearnedPillTitle.Style = titleStyle;
        LearnedPillSubtitle.Text = subtitleText;
        LearnedPillSubtitle.Visibility = string.IsNullOrEmpty(subtitleText)
            ? Visibility.Collapsed
            : Visibility.Visible;

        var controlsVisible = model.Phase == LearnedCorrectionPillPhase.Learned;
        LearnedPillControls.Visibility = controlsVisible ? Visibility.Visible : Visibility.Collapsed;
        LearnedPillUndoButton.IsEnabled = controlsVisible;
        LearnedPillCloseButton.IsEnabled = controlsVisible;

        var showRail = model.Phase is LearnedCorrectionPillPhase.Learned or LearnedCorrectionPillPhase.UndoPending;

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            LearnedPillTitle,
            string.IsNullOrEmpty(subtitleText) ? titleText : $"{titleText}. {subtitleText}. Undo available.");
        if (announceNow)
        {
            AnnounceTitle();
        }

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            LearnedPillCloseButton,
            $"Close and keep \u201C{model.Canonical}\u201D");

        LearnedPillPanel.InvalidateMeasure();
        LearnedPillPanel.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var desiredW = LearnedPillPanel.DesiredSize.Width > 0 ? LearnedPillPanel.DesiredSize.Width : 300;
        var width = Math.Clamp((int)Math.Ceiling(2 + desiredW + 24), 240, 520);
        return (showRail, width);
    }

    public void AnnounceTitle()
    {
        var peer = FrameworkElementAutomationPeer.FromElement(LearnedPillTitle)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(LearnedPillTitle);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    public void Detach()
    {
        if (_detached)
        {
            return;
        }

        _detached = true;
        LearnedPillUndoButton.Click -= UndoButton_Click;
        LearnedPillCloseButton.Click -= CloseButton_Click;
        UndoClicked = null;
        CloseClicked = null;
    }

    private void UndoButton_Click(object sender, RoutedEventArgs e) => UndoClicked?.Invoke();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => CloseClicked?.Invoke();
}
