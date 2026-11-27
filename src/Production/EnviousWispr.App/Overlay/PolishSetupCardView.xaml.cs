using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EnviousWispr.App.Overlay;

/// <summary>Load-on-demand floating card prompting AI polish model or API setup.</summary>
public sealed partial class PolishSetupCardView : UserControl
{
    private bool _detached;

    public PolishSetupCardView()
    {
        InitializeComponent();
    }

    public event Action<string>? DismissClicked;

    public event Action? SetupActionClicked;

    public void Configure(string title, string subtitle, string actionLabel, string spokenActionLabel)
    {
        PolishSetupTitle.Text = title;
        PolishSetupSubtitle.Text = subtitle;
        PolishSetupActionButton.Content = actionLabel;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            PolishSetupActionButton,
            spokenActionLabel);
    }

    public double MeasureDesiredHeight(double cardWidth)
    {
        PolishSetupCardPanel.InvalidateMeasure();
        PolishSetupCardPanel.Measure(new Windows.Foundation.Size(cardWidth, double.PositiveInfinity));
        return PolishSetupCardPanel.DesiredSize.Height > 0
            ? PolishSetupCardPanel.DesiredSize.Height
            : 184;
    }

    public void Detach()
    {
        if (_detached)
        {
            return;
        }

        _detached = true;
        PolishSetupActionButton.Click -= SetupActionButton_Click;
        PolishSetupNotNowButton.Click -= NotNowButton_Click;
        DismissClicked = null;
        SetupActionClicked = null;
    }

    private void SetupActionButton_Click(object sender, RoutedEventArgs e) => SetupActionClicked?.Invoke();

    private void NotNowButton_Click(object sender, RoutedEventArgs e)
    {
        var label = sender is Button { Content: string text } && !string.IsNullOrWhiteSpace(text)
            ? text
            : "Not now";
        DismissClicked?.Invoke(label);
    }
}
