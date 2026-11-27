using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EnviousWispr.App.Pages;

/// <summary>Standalone load-on-demand page for <see cref="WhatsNewPage"/>.</summary>
public sealed partial class WhatsNewPageView : UserControl
{
    private long _visibilityCallbackToken;

    public WhatsNewPageView(MainWindow host)
    {
        ArgumentNullException.ThrowIfNull(host);
        Host = host;
        InitializeComponent();
        Visibility = WhatsNewPage.Visibility;
        _visibilityCallbackToken = WhatsNewPage.RegisterPropertyChangedCallback(
            VisibilityProperty,
            (_, _) => Visibility = WhatsNewPage.Visibility);
    }

    internal MainWindow Host { get; }

    internal void Detach()
    {
        if (_visibilityCallbackToken != 0)
        {
            WhatsNewPage.UnregisterPropertyChangedCallback(VisibilityProperty, _visibilityCallbackToken);
            _visibilityCallbackToken = 0;
        }
    }
}
