using EnviousWispr.Core.Settings;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace EnviousWispr.App;

public sealed partial class MainWindow : Window
{
    public MainWindow(AppSettings settings, SettingsLoadStatus settingsLoadStatus)
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new SizeInt32(860, 620));

        SettingsStatusText.Text = settingsLoadStatus switch
        {
            SettingsLoadStatus.Loaded => "Restored",
            SettingsLoadStatus.Missing => "Created safely",
            SettingsLoadStatus.Migrated => "Migrated safely",
            SettingsLoadStatus.Invalid => "Recovered from invalid data",
            SettingsLoadStatus.NewerVersion => "Newer data preserved",
            SettingsLoadStatus.Unavailable => "Using safe defaults",
            _ => "Using safe defaults",
        };
        LaunchCountText.Text = settings.LaunchCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
