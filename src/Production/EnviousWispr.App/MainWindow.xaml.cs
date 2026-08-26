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

    public void SetHotkeyReady(string gesture)
    {
        HotkeyStatusText.Text = $"Hold {gesture}";
        SessionStatusText.Text = "Idle";
    }

    public void SetHotkeyUnavailable(string status)
    {
        HotkeyStatusText.Text = status;
        SessionStatusText.Text = "Unavailable";
    }

    public void SetSessionStatus(string status) => SessionStatusText.Text = status;

    public void SetCloudPolishNotice(string? notice)
    {
        if (string.IsNullOrWhiteSpace(notice))
        {
            return;
        }

        FoundationInfoBar.Title = "Direct BYOK cloud polish enabled";
        FoundationInfoBar.Message = notice;
    }

    public void SetOllamaPolishNotice(string? notice)
    {
        if (string.IsNullOrWhiteSpace(notice))
        {
            return;
        }

        FoundationInfoBar.Title = "Local Ollama polish enabled";
        FoundationInfoBar.Message = notice;
    }

    public void SetLivePreview(string? text)
    {
        LivePreviewText.Text = text ?? string.Empty;
        LivePreviewText.Visibility = string.IsNullOrWhiteSpace(text)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }
}
