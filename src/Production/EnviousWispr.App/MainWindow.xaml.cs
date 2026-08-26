using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Security;
using EnviousWispr.Audio;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Credentials;
using EnviousWispr.Core.History;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage.Pickers;

namespace EnviousWispr.App;

public sealed partial class MainWindow : Window, IDisposable
{
    private readonly ISettingsStore _settingsStore;
    private readonly IPortableProfileService _profileService;
    private readonly IHistoryStore _historyStore;
    private readonly IApiKeyStore _apiKeyStore;
    private readonly IRecoveryTextStore _recoveryTextStore;
    private readonly DictationOverlayWindow _overlayWindow;
    private readonly List<HistoryItemViewModel> _history = [];
    private IReadOnlyList<MicrophoneChoice> _microphones = [];
    private WasapiDeviceCatalog? _deviceCatalog;
    private AppSettings _settings;
    private bool _isApplyingSettings;
    private bool _initialFocusAssigned;

    public MainWindow(
        AppSettings settings,
        SettingsLoadStatus settingsLoadStatus,
        ISettingsStore settingsStore,
        IPortableProfileService profileService,
        IHistoryStore historyStore,
        IApiKeyStore apiKeyStore,
        IRecoveryTextStore recoveryTextStore)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(profileService);
        ArgumentNullException.ThrowIfNull(historyStore);
        ArgumentNullException.ThrowIfNull(apiKeyStore);
        ArgumentNullException.ThrowIfNull(recoveryTextStore);

        _settings = settings;
        _settingsStore = settingsStore;
        _profileService = profileService;
        _historyStore = historyStore;
        _apiKeyStore = apiKeyStore;
        _recoveryTextStore = recoveryTextStore;

        InitializeComponent();
        _overlayWindow = new DictationOverlayWindow();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new SizeInt32(1120, 760));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "EnviousWispr.App.exe"));
        Activated += OnWindowActivated;

        ApplyTheme(settings.Preferences.Theme);
        ApplySettingsToControls();
        ShowOnboarding(!settings.HasCompletedOnboarding);
        ProductNavigation.SelectedItem = HomeNavItem;
        BuildInfoText.Text = $"EnviousWispr {Assembly.GetExecutingAssembly().GetName().Version} · Windows 11 x64 development build";
        if (settingsLoadStatus is SettingsLoadStatus.Invalid or SettingsLoadStatus.Migrated)
        {
            FoundationInfoBar.Message += " Previous settings were recovered safely.";
        }
    }

    public event Action<AppSettings>? SettingsChanged;

    public event Action<string>? SessionStatusChanged;

    public event Action<AudioDeviceChange>? AudioDevicesChanged;

    public event Action? RecoveryCleared;

    public AppSettings CurrentSettings => _settings;

    public void FocusInitialControl()
    {
        if (_settings.HasCompletedOnboarding)
        {
            HomeNavItem.Focus(FocusState.Programmatic);
        }
        else
        {
            FinishOnboardingButton.Focus(FocusState.Programmatic);
        }
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_initialFocusAssigned || args.WindowActivationState == WindowActivationState.Deactivated)
        {
            return;
        }

        _initialFocusAssigned = true;
        DispatcherQueue.TryEnqueue(FocusInitialControl);
    }

    public async Task InitializeProductDataAsync()
    {
        await LoadMicrophonesAsync().ConfigureAwait(true);
        await ReloadHistoryAsync().ConfigureAwait(true);
    }

    public void SetHotkeyReady(string gesture)
    {
        HotkeyStatusText.Text = $"Hold {gesture}";
        OnboardingHotkeyText.Text = $"Hold {gesture} while speaking; release to finish.";
        SessionStatusText.Text = "Idle";
        SessionStatusChanged?.Invoke($"ready · {gesture}");
    }

    public void SetHotkeyUnavailable(string status)
    {
        HotkeyStatusText.Text = status;
        OnboardingHotkeyText.Text = status;
        SessionStatusText.Text = "Unavailable";
        SessionStatusChanged?.Invoke("shortcut unavailable");
    }

    public void SetSessionStatus(string status)
    {
        SessionStatusText.Text = status;
        SessionStatusChanged?.Invoke(status);
        _overlayWindow.ShowState(OverlayStateFor(status), status);
        if (status.Contains("ready", StringComparison.OrdinalIgnoreCase))
        {
            EngineReadinessText.Text = status;
            OnboardingModelText.Text = status;
        }
        else if (status.Contains("model is not installed", StringComparison.OrdinalIgnoreCase) ||
                 status.Contains("transcription is unavailable", StringComparison.OrdinalIgnoreCase) ||
                 status.Contains("worker could not start", StringComparison.OrdinalIgnoreCase))
        {
            EngineReadinessText.Text = status;
            OnboardingModelText.Text = status;
        }
    }

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
        _overlayWindow.SetPreview(text);
    }

    public async Task NotifyHistoryChangedAsync() => await ReloadHistoryAsync().ConfigureAwait(true);

    public void OpenSettings()
    {
        ShowOnboarding(show: false);
        ProductNavigation.SelectedItem = SettingsNavItem;
        ShowPage("settings");
    }

    public void ShutdownProductWindows()
    {
        if (_deviceCatalog is not null)
        {
            _deviceCatalog.DevicesChanged -= OnAudioDevicesChanged;
            _deviceCatalog.Dispose();
            _deviceCatalog = null;
        }

        _overlayWindow.Shutdown();
    }

    public void Dispose() => ShutdownProductWindows();

    public void SetRecoveredText(RecoveryTextLoadResult result)
    {
        if (result.Status == RecoveryTextLoadStatus.Found && result.Record is not null)
        {
            RecoveryTextBox.Text = result.Record.Text;
            RecoveryCard.Visibility = Visibility.Visible;
            FoundationInfoBar.Title = "Interrupted dictation recovered";
            FoundationInfoBar.Message = "Review or copy the private recovery text on Home. It was not pasted automatically.";
            FoundationInfoBar.Severity = InfoBarSeverity.Warning;
            SetOnboardingReliabilityNotice(
                "Interrupted dictation recovered",
                "Select Get started to review or copy the private recovery text. It will not be pasted automatically.");
            return;
        }

        ClearRecoveredText();
        if (result.Status == RecoveryTextLoadStatus.Invalid)
        {
            SetOnboardingReliabilityNotice(
                "Recovery data needs attention",
                "The encrypted recovery file is invalid. It was preserved and no text was exposed.");
            ShowMessage(
                "Recovery data needs attention",
                "The encrypted recovery file is invalid. It was preserved and no text was exposed.",
                InfoBarSeverity.Warning);
        }
        else if (result.Status == RecoveryTextLoadStatus.Unavailable)
        {
            SetOnboardingReliabilityNotice(
                "Recovery storage is unavailable",
                "Windows could not open the encrypted recovery file. Dictation remains available.");
            ShowMessage(
                "Recovery storage is unavailable",
                "Windows could not open the encrypted recovery file. Dictation remains available.",
                InfoBarSeverity.Warning);
        }
    }

    public void SetRunRecoveryNotice(int consecutiveInterruptedRuns)
    {
        FoundationInfoBar.Title = "Recovered after an interrupted run";
        FoundationInfoBar.Message = consecutiveInterruptedRuns > 1
            ? $"EnviousWispr detected {consecutiveInterruptedRuns.ToString(CultureInfo.CurrentCulture)} interrupted starts in a row. Global input and owned runtimes were reinitialized; unfinished text is never pasted automatically."
            : "Global input and owned runtimes were reinitialized. Unfinished text is never pasted automatically.";
        FoundationInfoBar.Severity = InfoBarSeverity.Warning;
        SetOnboardingReliabilityNotice(
            "Recovered after an interrupted run",
            FoundationInfoBar.Message);
    }

    private void SetOnboardingReliabilityNotice(string title, string message)
    {
        OnboardingReliabilityInfoBar.Title = title;
        OnboardingReliabilityInfoBar.Message = message;
        OnboardingReliabilityInfoBar.IsOpen = true;
    }

    public void SetReliabilityNotice(string title, string message, bool isError = false) =>
        ShowMessage(
            title,
            message,
            isError ? InfoBarSeverity.Error : InfoBarSeverity.Warning);

    public void ClearRecoveredText()
    {
        RecoveryTextBox.Text = string.Empty;
        RecoveryCard.Visibility = Visibility.Collapsed;
    }

    private async void FinishOnboardingButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.HasCompletedOnboarding)
        {
            ShowOnboarding(show: false);
            return;
        }

        var next = _settings with { HasCompletedOnboarding = true };
        if (await TrySaveAsync(next, "Setup complete", "Your choices were saved on this PC.").ConfigureAwait(true))
        {
            ShowOnboarding(show: false);
            ProductNavigation.SelectedItem = HomeNavItem;
            HomeNavItem.Focus(FocusState.Programmatic);
        }
    }

    private void ProductNavigation_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItemContainer as NavigationViewItem)?.Tag?.ToString() ?? "home";
        ShowPage(tag);
    }

    private void OpenHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        var historyItem = ProductNavigation.MenuItems
            .OfType<NavigationViewItem>()
            .First(item => string.Equals(item.Tag?.ToString(), "history", StringComparison.Ordinal));
        ProductNavigation.SelectedItem = historyItem;
    }

    private async void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var parsedHotkey = HotkeyGestureParser.Parse(HotkeyTextBox.Text);
        if (!parsedHotkey.Succeeded)
        {
            ShowMessage("Shortcut needs attention", "Use a supported key such as F8 or Ctrl+F8.", InfoBarSeverity.Error);
            HotkeyTextBox.Focus(FocusState.Programmatic);
            return;
        }

        var dictation = new DictationPreferences(
            (FinalAsrEngine)Math.Clamp(EngineComboBox.SelectedIndex, 0, 2),
            parsedHotkey.Gesture!.Value.ToString(),
            WordCorrectionToggle.IsOn,
            FillerRemovalToggle.IsOn,
            EmojiFormatterToggle.IsOn,
            SpokenPunctuationToggle.IsOn,
            (WhisperLanguagePreference)Math.Clamp(WhisperLanguageComboBox.SelectedIndex, 0, 4));
        var polish = new PolishPreferences(
            PolishProviderFromIndex(PolishProviderComboBox.SelectedIndex),
            NullIfBlank(PolishModelTextBox.Text),
            NullIfBlank(OllamaEndpointTextBox.Text));
        var history = new HistoryPreferences(
            HistoryEnabledToggle.IsOn,
            (int)Math.Clamp(double.IsNaN(RetentionDaysBox.Value) ? 30 : RetentionDaysBox.Value, 0, 3650));
        var theme = ThemeFromIndex(ThemeComboBox.SelectedIndex);
        var microphoneId = (MicrophoneComboBox.SelectedItem as MicrophoneChoice)?.Id;
        var next = _settings with
        {
            PreferredMicrophoneId = microphoneId,
            Preferences = new UserPreferences(dictation, polish, history, theme),
        };

        if (await TrySaveAsync(
                next,
                "Settings saved",
                "Theme and local data choices apply now. Engine, microphone, shortcut, and polish changes apply safely on the next launch.")
            .ConfigureAwait(true))
        {
            ApplyTheme(theme);
        }
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isApplyingSettings)
        {
            ApplyTheme(ThemeFromIndex(ThemeComboBox.SelectedIndex));
        }
    }

    private void PolishProviderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingSettings)
        {
            return;
        }

        ApiKeyPasswordBox.Password = string.Empty;
        RefreshApiKeyStatus();
    }

    private void SaveApiKeyButton_Click(object sender, RoutedEventArgs e)
    {
        var provider = PolishProviderFromIndex(PolishProviderComboBox.SelectedIndex);
        if (!IsCloudProvider(provider))
        {
            ShowMessage(
                "No cloud key needed",
                "Choose OpenAI, Anthropic, or Gemini before storing a provider key.",
                InfoBarSeverity.Informational);
            return;
        }

        var value = ApiKeyPasswordBox.Password.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            ShowMessage("Enter an API key", "The key field is empty.", InfoBarSeverity.Warning);
            ApiKeyPasswordBox.Focus(FocusState.Programmatic);
            return;
        }

        try
        {
            _apiKeyStore.Store(provider, value);
            ApiKeyPasswordBox.Password = string.Empty;
            RefreshApiKeyStatus();
            ShowMessage(
                $"{ProviderDisplayName(provider)} key saved",
                "The key is stored in Windows Credential Manager. It is not part of settings, profiles, history, or diagnostics.",
                InfoBarSeverity.Success);
        }
        catch (Exception exception) when (IsCredentialStorageFailure(exception))
        {
            ApiKeyPasswordBox.Password = string.Empty;
            RefreshApiKeyStatus();
            ShowMessage(
                "API key was not saved",
                "Windows Credential Manager is unavailable. No key was written to settings or exports.",
                InfoBarSeverity.Error);
        }
    }

    private async void RemoveApiKeyButton_Click(object sender, RoutedEventArgs e)
    {
        var provider = PolishProviderFromIndex(PolishProviderComboBox.SelectedIndex);
        if (!IsCloudProvider(provider))
        {
            return;
        }

        if (_apiKeyStore.GetStatus(provider) == ApiKeyReadStatus.Missing)
        {
            RefreshApiKeyStatus();
            ShowMessage(
                "No stored key to remove",
                $"No {ProviderDisplayName(provider)} key is stored for EnviousWispr in Windows Credential Manager.",
                InfoBarSeverity.Informational);
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = WindowRoot.XamlRoot,
            Title = $"Remove the stored {ProviderDisplayName(provider)} key?",
            Content = "This deletes the EnviousWispr credential from Windows Credential Manager. Settings, history, dictionary entries, and snippets are not affected.",
            PrimaryButtonText = "Remove key",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            _apiKeyStore.Delete(provider);
            ApiKeyPasswordBox.Password = string.Empty;
            RefreshApiKeyStatus();
            ShowMessage(
                $"{ProviderDisplayName(provider)} key removed",
                "The stored credential was removed from Windows Credential Manager.",
                InfoBarSeverity.Success);
        }
        catch (Exception exception) when (IsCredentialStorageFailure(exception))
        {
            RefreshApiKeyStatus();
            ShowMessage(
                "API key could not be removed",
                "Windows Credential Manager is unavailable. Existing settings were not changed.",
                InfoBarSeverity.Error);
        }
    }

    private async void OpenMicrophonePrivacyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var opened = await Windows.System.Launcher.LaunchUriAsync(
                new Uri("ms-settings:privacy-microphone"));
            if (!opened)
            {
                ShowMessage(
                    "Microphone privacy settings did not open",
                    "Open Windows Settings, then choose Privacy & security > Microphone.",
                    InfoBarSeverity.Warning);
            }
        }
        catch
        {
            ShowMessage(
                "Microphone privacy settings did not open",
                "Open Windows Settings, then choose Privacy & security > Microphone.",
                InfoBarSeverity.Warning);
        }
    }

    private async void AddWordButton_Click(object sender, RoutedEventArgs e)
    {
        var spoken = SpokenFormBox.Text.Trim();
        var replacement = ReplacementBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(spoken) || string.IsNullOrWhiteSpace(replacement))
        {
            ShowMessage("Both fields are required", "Enter the spoken form and the exact replacement.", InfoBarSeverity.Warning);
            return;
        }

        var words = _settings.UserData.CustomWords
            .Where(entry => !string.Equals(entry.SpokenForm, spoken, StringComparison.OrdinalIgnoreCase))
            .Append(new CustomWordEntry(spoken, replacement))
            .OrderBy(entry => entry.SpokenForm, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        await SaveUserDataAsync(new ReusableUserData(words, _settings.UserData.Snippets), "Dictionary saved").ConfigureAwait(true);
        SpokenFormBox.Text = string.Empty;
        ReplacementBox.Text = string.Empty;
    }

    private async void RemoveWordButton_Click(object sender, RoutedEventArgs e)
    {
        if (DictionaryList.SelectedItem is not CustomWordEntry selected)
        {
            ShowMessage("Select a word first", "Choose the dictionary row you want to remove.", InfoBarSeverity.Informational);
            return;
        }

        var words = _settings.UserData.CustomWords.Where(entry => entry != selected).ToArray();
        await SaveUserDataAsync(new ReusableUserData(words, _settings.UserData.Snippets), "Dictionary entry removed").ConfigureAwait(true);
    }

    private async void AddSnippetButton_Click(object sender, RoutedEventArgs e)
    {
        var name = SnippetNameBox.Text.Trim();
        var body = SnippetBodyBox.Text;
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(body))
        {
            ShowMessage("Name and text are required", "Give this snippet a name and some reusable text.", InfoBarSeverity.Warning);
            return;
        }

        var snippets = _settings.UserData.Snippets
            .Where(entry => !string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
            .Append(new SnippetEntry(name, body))
            .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        await SaveUserDataAsync(new ReusableUserData(_settings.UserData.CustomWords, snippets), "Snippet saved").ConfigureAwait(true);
        SnippetNameBox.Text = string.Empty;
        SnippetBodyBox.Text = string.Empty;
    }

    private async void RemoveSnippetButton_Click(object sender, RoutedEventArgs e)
    {
        if (SnippetList.SelectedItem is not SnippetEntry selected)
        {
            ShowMessage("Select a snippet first", "Choose the snippet you want to remove.", InfoBarSeverity.Informational);
            return;
        }

        var snippets = _settings.UserData.Snippets.Where(entry => entry != selected).ToArray();
        await SaveUserDataAsync(new ReusableUserData(_settings.UserData.CustomWords, snippets), "Snippet removed").ConfigureAwait(true);
    }

    private void HistorySearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshHistoryView();

    private void CopyHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryItemViewModel selected)
        {
            ShowMessage("Select a dictation first", "Choose the history entry you want to copy.", InfoBarSeverity.Informational);
            return;
        }

        var package = new DataPackage();
        package.SetText(selected.Text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
        ShowMessage("Copied", "The selected dictation is on your clipboard.", InfoBarSeverity.Success);
    }

    private void CopyRecoveryButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(RecoveryTextBox.Text))
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(RecoveryTextBox.Text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
        ShowMessage(
            "Recovered text copied",
            "The private recovery text is on your clipboard. Delete the recovery copy when you no longer need it.",
            InfoBarSeverity.Success);
    }

    private async void DeleteRecoveryButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(RecoveryTextBox.Text))
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = WindowRoot.XamlRoot,
            Title = "Delete the recovered dictation?",
            Content = "This permanently removes the encrypted recovery copy from this PC. It does not change history or clipboard contents.",
            PrimaryButtonText = "Delete recovery copy",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        if (await _recoveryTextStore.ClearAsync().ConfigureAwait(true))
        {
            ClearRecoveredText();
            FoundationInfoBar.Title = "No recovered dictation is pending";
            FoundationInfoBar.Message = "The encrypted recovery copy was removed. EnviousWispr will not paste or retain that text.";
            FoundationInfoBar.Severity = InfoBarSeverity.Success;
            OnboardingReliabilityInfoBar.IsOpen = false;
            RecoveryCleared?.Invoke();
            ShowMessage(
                "Recovery copy deleted",
                "The encrypted local recovery copy was removed.",
                InfoBarSeverity.Success);
        }
        else
        {
            ShowMessage(
                "Recovery copy could not be deleted",
                "Windows left the encrypted recovery file untouched.",
                InfoBarSeverity.Error);
        }
    }

    private async void DeleteHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryItemViewModel selected)
        {
            ShowMessage("Select a dictation first", "Choose the history entry you want to delete.", InfoBarSeverity.Informational);
            return;
        }

        var result = await _historyStore.DeleteAsync(selected.Id).ConfigureAwait(true);
        if (result.Succeeded)
        {
            await ReloadHistoryAsync().ConfigureAwait(true);
            ShowMessage("History entry deleted", "The local copy was removed.", InfoBarSeverity.Success);
        }
        else
        {
            ShowMessage("History could not be changed", "The private history file was left untouched.", InfoBarSeverity.Error);
        }
    }

    private async void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = WindowRoot.XamlRoot,
            Title = "Delete all dictation history?",
            Content = "This permanently removes every locally saved transcript. Settings, dictionary entries, and snippets are not affected.",
            PrimaryButtonText = "Delete all",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var result = await _historyStore.ClearAsync().ConfigureAwait(true);
        if (result.Succeeded)
        {
            await ReloadHistoryAsync().ConfigureAwait(true);
            ShowMessage("History cleared", "All locally saved dictations were removed.", InfoBarSeverity.Success);
        }
        else
        {
            ShowMessage("History could not be cleared", "The private history file was left untouched.", InfoBarSeverity.Error);
        }
    }

    private async void ExportProfileButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker
        {
            SuggestedFileName = "EnviousWispr-profile",
            DefaultFileExtension = ".json",
        };
        picker.FileTypeChoices.Add("EnviousWispr profile", [".json"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        var result = await _profileService.ExportAsync(_settings.ToPortableProfile(), file.Path).ConfigureAwait(true);
        ShowMessage(
            result.Succeeded ? "Profile exported" : "Profile export failed safely",
            result.Succeeded ? "Settings, dictionary entries, and snippets were written without private machine data." : "No existing destination data was intentionally replaced with an invalid profile.",
            result.Succeeded ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    private async void ImportProfileButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".json");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        var imported = await _profileService.ImportAsync(file.Path).ConfigureAwait(true);
        if (imported.Status != PortableProfileImportStatus.Imported || imported.Profile is null)
        {
            ShowMessage("Profile not imported", ImportFailureMessage(imported.Status), InfoBarSeverity.Error);
            return;
        }

        var next = _settings.Apply(imported.Profile);
        if (await TrySaveAsync(next, "Profile imported", "Settings, dictionary entries, and snippets are ready. Machine-local choices and history were preserved.").ConfigureAwait(true))
        {
            ApplySettingsToControls();
        }
    }

    private async Task LoadMicrophonesAsync()
    {
        try
        {
            if (_deviceCatalog is null)
            {
                _deviceCatalog = new WasapiDeviceCatalog();
                _deviceCatalog.DevicesChanged += OnAudioDevicesChanged;
            }

            var devices = await _deviceCatalog.GetCaptureDevicesAsync().ConfigureAwait(true);
            _microphones = [
                new MicrophoneChoice(null, "Use the Windows default microphone"),
                .. devices.Select(device => new MicrophoneChoice(
                    device.Id.Value,
                    device.IsDefault ? $"{device.DisplayName} (Windows default)" : device.DisplayName)),
            ];
            MicrophoneComboBox.ItemsSource = _microphones;
            var selected = _microphones.FirstOrDefault(choice =>
                string.Equals(choice.Id, _settings.PreferredMicrophoneId, StringComparison.Ordinal)) ?? _microphones[0];
            MicrophoneComboBox.SelectedItem = selected;
            var defaultDevice = devices.FirstOrDefault(device => device.IsDefault) ??
                (devices.Count == 0 ? null : devices[0]);
            var status = defaultDevice is null
                ? "No active recording device found. Windows microphone privacy or device settings may need attention."
                : $"{defaultDevice.DisplayName} is available.";
            MicrophoneReadinessText.Text = status;
            OnboardingMicrophoneText.Text = status;
        }
        catch
        {
            _microphones = [new MicrophoneChoice(null, "Use the Windows default microphone")];
            MicrophoneComboBox.ItemsSource = _microphones;
            MicrophoneComboBox.SelectedIndex = 0;
            const string status = "Windows could not enumerate microphones. Settings remain available and dictation will fail safely.";
            MicrophoneReadinessText.Text = status;
            OnboardingMicrophoneText.Text = status;
        }
    }

    private void OnAudioDevicesChanged(object? sender, AudioDeviceChange change)
    {
        if (!change.AffectsCapture)
        {
            return;
        }

        AudioDevicesChanged?.Invoke(change);
        DispatcherQueue.TryEnqueue(async () =>
        {
            await LoadMicrophonesAsync().ConfigureAwait(true);
            ShowMessage(
                "Microphone devices updated",
                "EnviousWispr refreshed the active recording-device list. A missing preferred microphone falls back to the Windows default.",
                InfoBarSeverity.Informational);
        });
    }

    private async Task ReloadHistoryAsync()
    {
        var result = await _historyStore.LoadAsync(
            _settings.Preferences.History.RetentionDays,
            DateTimeOffset.UtcNow).ConfigureAwait(true);
        _history.Clear();
        _history.AddRange(result.Entries.Select(entry => new HistoryItemViewModel(entry)));
        RefreshHistoryView();
        HistorySummaryText.Text = result.Status switch
        {
            HistoryLoadStatus.Invalid => "History is unavailable because its local file is invalid; the source was preserved for recovery.",
            HistoryLoadStatus.Unavailable => "Windows could not open the private history file.",
            _ when !_settings.Preferences.History.IsEnabled => "History is off. New dictations will not be saved.",
            _ when _history.Count == 0 => "No dictations saved yet.",
            _ => $"{_history.Count.ToString(CultureInfo.CurrentCulture)} local dictation{(_history.Count == 1 ? string.Empty : "s")} saved.",
        };
    }

    private void RefreshHistoryView()
    {
        var query = HistorySearchBox.Text.Trim();
        HistoryList.ItemsSource = string.IsNullOrWhiteSpace(query)
            ? _history.ToArray()
            : _history.Where(item => item.Text.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
    }

    private async Task SaveUserDataAsync(ReusableUserData userData, string title)
    {
        if (await TrySaveAsync(_settings with { UserData = userData }, title, "The change was saved locally.").ConfigureAwait(true))
        {
            DictionaryList.ItemsSource = _settings.UserData.CustomWords;
            SnippetList.ItemsSource = _settings.UserData.Snippets;
        }
    }

    private async Task<bool> TrySaveAsync(AppSettings next, string title, string message)
    {
        try
        {
            await _settingsStore.SaveAsync(next).ConfigureAwait(true);
            _settings = next;
            SettingsChanged?.Invoke(next);
            ApplySettingsToControls();
            ShowMessage(title, message, InfoBarSeverity.Success);
            return true;
        }
        catch (ArgumentException)
        {
            ShowMessage("Settings were not saved", "One or more values are invalid. Your previous settings remain active.", InfoBarSeverity.Error);
        }
        catch (IOException)
        {
            ShowMessage("Settings storage is unavailable", "Your previous settings remain active.", InfoBarSeverity.Error);
        }
        catch (UnauthorizedAccessException)
        {
            ShowMessage("Windows blocked settings storage", "Your previous settings remain active.", InfoBarSeverity.Error);
        }

        return false;
    }

    private void ApplySettingsToControls()
    {
        _isApplyingSettings = true;
        try
        {
            var preferences = _settings.Preferences;
            EngineComboBox.SelectedIndex = (int)preferences.Dictation.FinalEngine;
            WhisperLanguageComboBox.SelectedIndex = (int)preferences.Dictation.WhisperLanguage;
            HotkeyTextBox.Text = preferences.Dictation.PushToTalkGesture;
            WordCorrectionToggle.IsOn = preferences.Dictation.WordCorrectionEnabled;
            FillerRemovalToggle.IsOn = preferences.Dictation.FillerRemovalEnabled;
            EmojiFormatterToggle.IsOn = preferences.Dictation.EmojiFormatterEnabled;
            SpokenPunctuationToggle.IsOn = preferences.Dictation.SpokenPunctuationEnabled;
            PolishProviderComboBox.SelectedIndex = PolishProviderIndex(preferences.Polish.Provider);
            PolishModelTextBox.Text = preferences.Polish.ModelId ?? string.Empty;
            OllamaEndpointTextBox.Text = preferences.Polish.OllamaEndpoint ?? string.Empty;
            HistoryEnabledToggle.IsOn = preferences.History.IsEnabled;
            RetentionDaysBox.Value = preferences.History.RetentionDays;
            ThemeComboBox.SelectedIndex = ThemeIndex(preferences.Theme);
            DictionaryList.ItemsSource = _settings.UserData.CustomWords;
            SnippetList.ItemsSource = _settings.UserData.Snippets;
        }
        finally
        {
            _isApplyingSettings = false;
        }

        ApiKeyPasswordBox.Password = string.Empty;
        RefreshApiKeyStatus();
    }

    private void RefreshApiKeyStatus()
    {
        var provider = PolishProviderFromIndex(PolishProviderComboBox.SelectedIndex);
        var isCloudProvider = IsCloudProvider(provider);
        ApiKeyPasswordBox.IsEnabled = isCloudProvider;
        SaveApiKeyButton.IsEnabled = isCloudProvider;
        RemoveApiKeyButton.IsEnabled = isCloudProvider;
        ApiKeyPasswordBox.PlaceholderText = isCloudProvider
            ? $"Enter {ProviderDisplayName(provider)} API key"
            : "Choose a direct cloud provider to manage its key";

        if (!isCloudProvider)
        {
            ApiKeyStatusText.Text = provider switch
            {
                PolishProvider.Ollama => "Ollama runs on this PC and does not use a cloud API key.",
                PolishProvider.EgOne => "EG-1 runs on this PC and does not use a cloud API key.",
                _ => "AI polish is off; no provider key is used.",
            };
            return;
        }

        ApiKeyStatusText.Text = _apiKeyStore.GetStatus(provider) switch
        {
            ApiKeyReadStatus.Found =>
                $"{CredentialArticle(provider)} {ProviderDisplayName(provider)} key is stored in Windows Credential Manager.",
            ApiKeyReadStatus.Missing =>
                $"No {ProviderDisplayName(provider)} key is stored on this PC.",
            _ => "Windows Credential Manager status is unavailable. No key value was revealed.",
        };
    }

    private void ShowOnboarding(bool show)
    {
        OnboardingView.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ProductNavigation.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        if (show)
        {
            FinishOnboardingButton.Focus(FocusState.Programmatic);
        }
    }

    private void ShowPage(string tag)
    {
        HomePage.Visibility = tag == "home" ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = tag == "history" ? Visibility.Visible : Visibility.Collapsed;
        DictionaryPage.Visibility = tag == "dictionary" ? Visibility.Visible : Visibility.Collapsed;
        SnippetsPage.Visibility = tag == "snippets" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
        HelpPage.Visibility = tag == "help" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowMessage(string title, string message, InfoBarSeverity severity)
    {
        OperationInfoBar.Title = title;
        OperationInfoBar.Message = message;
        OperationInfoBar.Severity = severity;
        OperationInfoBar.IsOpen = true;
    }

    private void ApplyTheme(AppTheme theme) => WindowRoot.RequestedTheme = theme switch
    {
        AppTheme.Light => ElementTheme.Light,
        AppTheme.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    private static AppTheme ThemeFromIndex(int index) => index switch
    {
        1 => AppTheme.Light,
        2 => AppTheme.Dark,
        _ => AppTheme.System,
    };

    private static int ThemeIndex(AppTheme theme) => theme switch
    {
        AppTheme.Light => 1,
        AppTheme.Dark => 2,
        _ => 0,
    };

    private static PolishProvider PolishProviderFromIndex(int index) => index switch
    {
        1 => PolishProvider.EgOne,
        2 => PolishProvider.Ollama,
        3 => PolishProvider.OpenAI,
        4 => PolishProvider.Anthropic,
        5 => PolishProvider.Gemini,
        _ => PolishProvider.None,
    };

    private static bool IsCloudProvider(PolishProvider provider) =>
        provider is PolishProvider.OpenAI or PolishProvider.Anthropic or PolishProvider.Gemini;

    private static string ProviderDisplayName(PolishProvider provider) => provider switch
    {
        PolishProvider.OpenAI => "OpenAI",
        PolishProvider.Anthropic => "Anthropic",
        PolishProvider.Gemini => "Gemini",
        _ => provider.ToString(),
    };

    private static string CredentialArticle(PolishProvider provider) => provider is
        PolishProvider.OpenAI or PolishProvider.Anthropic
            ? "An"
            : "A";

    private static bool IsCredentialStorageFailure(Exception exception) => exception is
        Win32Exception or
        UnauthorizedAccessException or
        SecurityException or
        ArgumentException;

    private static int PolishProviderIndex(PolishProvider provider) => provider switch
    {
        PolishProvider.EgOne => 1,
        PolishProvider.Ollama => 2,
        PolishProvider.OpenAI => 3,
        PolishProvider.Anthropic => 4,
        PolishProvider.Gemini => 5,
        _ => 0,
    };

    private static string? NullIfBlank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string ImportFailureMessage(PortableProfileImportStatus status) => status switch
    {
        PortableProfileImportStatus.NewerVersion => "This profile was created by a newer EnviousWispr version. The file was left untouched.",
        PortableProfileImportStatus.Invalid => "This is not a valid EnviousWispr portable profile. The file and current settings were left untouched.",
        _ => "Windows could not read this profile. The file and current settings were left untouched.",
    };

    private static DictationOverlayState OverlayStateFor(string status)
    {
        if (status.StartsWith("Recording", StringComparison.OrdinalIgnoreCase))
        {
            return DictationOverlayState.Recording;
        }

        if (status.StartsWith("Transcribing", StringComparison.OrdinalIgnoreCase) ||
            status.StartsWith("Delivering", StringComparison.OrdinalIgnoreCase))
        {
            return DictationOverlayState.Processing;
        }

        if (status.Contains("copied only", StringComparison.OrdinalIgnoreCase) ||
            status.StartsWith("Copied", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("held safely", StringComparison.OrdinalIgnoreCase))
        {
            return DictationOverlayState.Warning;
        }

        if (status.StartsWith("Local transcription failed", StringComparison.OrdinalIgnoreCase) ||
            status.StartsWith("Session failed", StringComparison.OrdinalIgnoreCase) ||
            status.StartsWith("Text delivery stopped", StringComparison.OrdinalIgnoreCase))
        {
            return DictationOverlayState.Error;
        }

        if (status.StartsWith("Inserted", StringComparison.OrdinalIgnoreCase) ||
            status.StartsWith("Pasted", StringComparison.OrdinalIgnoreCase) ||
            status.StartsWith("Transcribed", StringComparison.OrdinalIgnoreCase) ||
            status.StartsWith("Cleaned", StringComparison.OrdinalIgnoreCase))
        {
            return DictationOverlayState.Success;
        }

        return DictationOverlayState.Hidden;
    }

    private sealed record MicrophoneChoice(string? Id, string DisplayName);

    private sealed class HistoryItemViewModel
    {
        public HistoryItemViewModel(DictationHistoryEntry entry)
        {
            Id = entry.Id;
            Text = entry.Text;
            CreatedDisplay = entry.CreatedAt.ToLocalTime().ToString("f", CultureInfo.CurrentCulture);
            Details = $"{entry.EngineId} · {(entry.WasPolished ? "AI polished" : "deterministic cleanup")} · {(entry.WasDelivered ? "delivered" : "held safely")}";
        }

        public Guid Id { get; }

        public string Text { get; }

        public string CreatedDisplay { get; }

        public string Details { get; }
    }
}
