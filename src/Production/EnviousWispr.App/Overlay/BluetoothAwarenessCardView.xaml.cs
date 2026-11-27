using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EnviousWispr.App.Overlay;

/// <summary>Load-on-demand Bluetooth microphone cold-start awareness card.</summary>
public sealed partial class BluetoothAwarenessCardView : UserControl
{
    private bool _detached;

    public BluetoothAwarenessCardView()
    {
        InitializeComponent();
    }

    public event Action<string>? DismissClicked;

    public event Action? AdjustSettingsClicked;

    public double MeasureDesiredHeight(double cardWidth)
    {
        BluetoothAwarenessCardPanel.InvalidateMeasure();
        BluetoothAwarenessCardPanel.Measure(new Windows.Foundation.Size(cardWidth, double.PositiveInfinity));
        return BluetoothAwarenessCardPanel.DesiredSize.Height > 0
            ? BluetoothAwarenessCardPanel.DesiredSize.Height
            : 344;
    }

    public void Detach()
    {
        if (_detached)
        {
            return;
        }

        _detached = true;
        BluetoothCloseButton.Click -= DismissButton_Click;
        BluetoothGotItButton.Click -= DismissButton_Click;
        BluetoothAdjustButton.Click -= AdjustButton_Click;
        DismissClicked = null;
        AdjustSettingsClicked = null;
    }

    private void DismissButton_Click(object sender, RoutedEventArgs e)
    {
        var label = sender is Button { Content: string text } && !string.IsNullOrWhiteSpace(text)
            ? text
            : "Close";
        DismissClicked?.Invoke(label);
    }

    private void AdjustButton_Click(object sender, RoutedEventArgs e) => AdjustSettingsClicked?.Invoke();
}
