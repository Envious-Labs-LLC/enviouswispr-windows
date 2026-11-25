using System.Globalization;
using System.Reflection;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Distribution;
using EnviousWispr.Core.Feedback;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Settings;
using EnviousWispr.Services.Diagnostics;
using EnviousWispr.Services.Distribution;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace EnviousWispr.App;

/// <summary>Speed check, diagnostics export, Microsoft Store updates, startup registration, feedback dialog, and portable profile transfer.</summary>
public sealed partial class MainWindow
{
    // Whether the last Store answer offered an update. A busy refusal leaves it as it was, so the
    // person can install once the dictation or the file has finished without checking again.
    private bool _updateOffered;

    // True between asking for a speed check and being handed the answer. Without it, any status
    // change arriving mid-run hands the button back and lets a second check start over the first.
    private bool _speedCheckRunning;

    public event Action<bool, int>? DiagnosticsExportCompleted;

    public event Action? UpdateCheckRequested;

    public event Action? UpdateApplyRequested;

    public event Action<bool>? StartupToggleRequested;

    public Func<Task<StartupRegistrationResult>>? LoadStartupRegistration { get; set; }

    public Func<FeedbackSubmission, CancellationToken, Task<FeedbackSendResult>>? SubmitFeedback { get; set; }

    public Func<FeedbackDiagnosticsSnapshot>? BuildFeedbackDiagnosticsSnapshot { get; set; }

    public FeedbackDraftStore? FeedbackDraftStore { get; set; }

    public string? FeedbackDataDirectory { get; set; }

    public Func<CancellationToken, Task>? FlushFeedbackOutbox { get; set; }

    public Func<CancellationToken, Task<bool>>? CheckUndeliverableFeedback { get; set; }

    private bool _isFeedbackDialogOpen;

    private bool _isUpdatingStartupRegistration;
    private bool _startupOperationInProgress;
    private int _startupOperationGeneration;

    /// <summary>Raised when the user asks for a speed check.</summary>
    public event Action? SpeedCheckRequested;

    private async Task LoadStartupRegistrationAsync()
    {
        if (_startupOperationInProgress || LoadStartupRegistration is not { } loadStartup)
        {
            return;
        }

        var generation = _startupOperationGeneration;
        var startup = await loadStartup().ConfigureAwait(true);
        if (_session.Closing || _startupOperationInProgress || generation != _startupOperationGeneration)
        {
            return;
        }

        SetStartupRegistration(startup);
    }

    private const string NotFromStoreSentence =
        "This copy of EnviousWispr was not installed from the Microsoft Store, so it cannot check for updates.";

    public void SetUpdateCheckInProgress()
    {
        CheckForUpdatesButton.IsEnabled = false;
        ApplyUpdateButton.IsEnabled = false;
        SetLiveText(UpdateStatusText, "Checking the Microsoft Store for updates…");
    }

    public void SetUpdateInstallInProgress()
    {
        CheckForUpdatesButton.IsEnabled = false;
        ApplyUpdateButton.IsEnabled = false;
        SetLiveText(UpdateStatusText, "Asking the Microsoft Store to install the update…");
    }

    public void SetUpdateStatus(UpdateOperationResult result)
    {
        // A BUSY REFUSAL CHANGES NOTHING ABOUT WHAT THE STORE OFFERED. Every other answer is the Store's
        // latest word, and only an available update can be installed.
        if (result.Status is not (UpdateOperationStatus.BusyDictating or UpdateOperationStatus.BusyTranscribingFile))
        {
            _updateOffered = result.CanApply;
        }

        // WINDOWS IS ABOUT TO CLOSE THE APP ONCE THE STORE IS INSTALLING, so nothing is offered after it.
        var installing = result.Status == UpdateOperationStatus.Installing;
        CheckForUpdatesButton.IsEnabled = _storeInstalled && !installing;
        ApplyUpdateButton.IsEnabled = _storeInstalled && _updateOffered && !installing;
        SetLiveText(
            UpdateStatusText,
            result.Status switch
            {
                UpdateOperationStatus.NotPackaged => NotFromStoreSentence,
                UpdateOperationStatus.BusyDictating =>
                    "Finish or cancel your dictation, then choose Install update.",
                UpdateOperationStatus.BusyTranscribingFile =>
                    "A file is being transcribed. Let it finish or stop it, then choose Install update.",
                UpdateOperationStatus.NoUpdate =>
                    $"You have the latest version, {result.Version}.",
                UpdateOperationStatus.UpdateAvailable =>
                    $"Version {result.Version} is available. Choose Install update when you are not dictating.",
                UpdateOperationStatus.Installing =>
                    "The Microsoft Store installed the update. EnviousWispr closes now; open it again from the Start menu.",
                UpdateOperationStatus.Cancelled =>
                    "The update was not installed. Check for updates again when you are ready.",
                _ => "The Microsoft Store could not finish that. Your current version is unchanged. Try again later.",
            });
    }

    private async void FeedbackToolbarButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isFeedbackDialogOpen || WindowRoot.XamlRoot is null)
        {
            return;
        }

        _isFeedbackDialogOpen = true;
        try
        {
            if (FlushFeedbackOutbox is { } flushOutbox)
            {
                _ = FlushFeedbackOutboxBestEffortAsync(flushOutbox);
            }

            var draftStore = ResolveFeedbackDraftStore();
            var snapshotBuilder = BuildFeedbackDiagnosticsSnapshot ?? BuildFallbackFeedbackDiagnosticsSnapshot;
            var submitter = SubmitFeedback ?? ((submission, _) => Task.FromResult(new FeedbackSendResult(
                FeedbackSendOutcome.Unavailable,
                submission.SubmissionId,
                "Feedback delivery is not configured.")));

            var defaultIncludeDiagnostics = _settings.Observability?.ShareAnonymousTelemetry ?? true;
            using var dialog = new FeedbackDialog(
                draftStore,
                snapshotBuilder,
                submitter,
                defaultIncludeDiagnostics,
                CheckUndeliverableFeedback)
            {
                XamlRoot = WindowRoot.XamlRoot,
                RequestedTheme = WindowRoot.RequestedTheme,
            };
            await dialog.ShowAsync();
        }
        finally
        {
            _isFeedbackDialogOpen = false;
        }
    }

    private FeedbackDraftStore? ResolveFeedbackDraftStore()
    {
        if (FeedbackDraftStore is { } existing)
        {
            return existing;
        }

        var directory = !string.IsNullOrWhiteSpace(FeedbackDataDirectory)
            ? FeedbackDataDirectory
            : _dataDirectory;
        return string.IsNullOrWhiteSpace(directory)
            ? null
            : new FeedbackDraftStore(directory);
    }

    private FeedbackDiagnosticsSnapshot BuildFallbackFeedbackDiagnosticsSnapshot()
    {
        var appVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.19.0";
        var osVersion = $"Windows {Environment.OSVersion.Version}";
        return new FeedbackDiagnosticsSnapshot(
            appVersion: appVersion,
            osVersion: osVersion,
            finalAsrEngine: _settings.Preferences.Dictation.FinalEngine.ToString(),
            polishProvider: _settings.Preferences.Polish.Provider.ToString(),
            livePreviewEnabled: _settings.Preferences.LivePreviewEnabled,
            livePreviewEngine: _settings.Preferences.LivePreviewEngine.ToString(),
            recordingMode: _settings.Preferences.Dictation.RecordingMode.ToString(),
            autoStopEnabled: _settings.Preferences.Dictation.AutoStopEnabled,
            hardwareClass: DiagnosticHardwareClass.Unknown.ToString(),
            recentEventCodes: []);
    }

    private static async Task FlushFeedbackOutboxBestEffortAsync(Func<CancellationToken, Task> flushOutbox)
    {
        try
        {
            await flushOutbox(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Background outbox flush is best-effort when opening Feedback.
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

        var result = await _session.Profiles.ExportAsync(_settings.ToPortableProfile(), file.Path).ConfigureAwait(true);
        ShowMessage(
            result.Succeeded ? "Profile exported" : "Profile export failed safely",
            result.Succeeded ? "Settings, dictionary entries, and snippets were written without private machine data." : "No existing destination data was intentionally replaced with an invalid profile.",
            result.Succeeded ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    /// <summary>
    /// Asks for a repeatable measurement of the text cleanup, and shows the answer.
    /// </summary>
    /// <remarks>
    /// The window does not own the pipeline, so it asks rather than measures. Building a second
    /// pipeline here to avoid the round trip would measure a DIFFERENT object from the one every
    /// dictation uses, which is the one thing a speed check must not do.
    /// </remarks>
    private void RunSpeedCheckButton_Click(object sender, RoutedEventArgs e)
    {
        _speedCheckRunning = true;
        RunSpeedCheckButton.IsEnabled = false;
        SetLiveRegion(SpeedCheckResultText, "Running...", Visibility.Visible);
        SpeedCheckRequested?.Invoke();
    }

    /// <summary>
    /// Greys the speed check out while a dictation is running, and says why underneath it.
    /// </summary>
    /// <remarks>
    /// A LIVE BUTTON THAT ANSWERS "NO" IS THE WRONG FEEL ON WINDOWS. The check genuinely cannot run
    /// during a dictation - it would be measuring a machine that is busy doing the thing being
    /// measured - so the honest presentation is a control that is visibly unavailable, not one that
    /// accepts the click and then declines.
    ///
    /// THE REASON GETS ITS OWN LINE RATHER THAN REPLACING THE RESULT. A greyed control with no
    /// explanation is a different bad, and overwriting the last measurement to explain the greying
    /// would destroy the number the user came back to read.
    /// </remarks>
    private void SetSpeedCheckAvailability(bool available)
    {
        RunSpeedCheckButton.IsEnabled = available && !_speedCheckRunning;
        SetLiveVisibility(SpeedCheckUnavailableText, available ? Visibility.Collapsed : Visibility.Visible);
    }

    /// <summary>Shows a finished speed check, or says why it did not run.</summary>
    /// <remarks>
    /// The refusal says WHY, for the same reason Quick Add's does: a check that silently produces
    /// nothing is indistinguishable from a button that does not work.
    /// </remarks>

    public void SetSpeedCheckResult(LatencySummary? summary)
    {
        // Not an unconditional re-enable. A dictation can begin between the click and the answer,
        // and handing the button back then would undo the greying the moment it was needed.
        _speedCheckRunning = false;
        SetSpeedCheckAvailability(_currentOverlayState != DictationOverlayState.Recording);
        SetLiveRegion(
            SpeedCheckResultText,
    summary is null || summary.Count == 0
                ? "The speed check did not run. It is skipped while a dictation is in progress."
                : $"{summary.Count} runs. Typical {summary.MedianMilliseconds:0.0}ms, "
                    + $"fastest {summary.MinMilliseconds:0.0}ms, slowest {summary.MaxMilliseconds:0.0}ms. "
                    + (summary.Percentile95IsJustTheMaximum
                        ? "Too few runs to report a slow tail separately."
                        : $"The slowest 5% took {summary.Percentile95Milliseconds:0.0}ms or more."),
            Visibility.Visible);
    }

    private async void ExportDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker
        {
            SuggestedFileName = "EnviousWispr-diagnostics",
            DefaultFileExtension = ".jsonl",
        };
        picker.FileTypeChoices.Add("Privacy-safe diagnostics", [".jsonl"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        var retentionDays = _settings.Observability?.DiagnosticRetentionDays ??
            ObservabilityPreferences.Default.DiagnosticRetentionDays;
        var result = await _session.Diagnostics.ExportAsync(
            file.Path,
            retentionDays,
            DateTimeOffset.UtcNow).ConfigureAwait(true);
        DiagnosticsExportCompleted?.Invoke(result.Succeeded, result.ExportedRecordCount);
        ShowMessage(
            result.Succeeded ? "Diagnostics exported" : "Diagnostics export failed safely",
            result.Succeeded
                ? $"{result.ExportedRecordCount.ToString(CultureInfo.CurrentCulture)} content-free operational record{(result.ExportedRecordCount == 1 ? string.Empty : "s")} exported. No dictated text, audio, keys, clipboard, surrounding context, or stable device identifier is included."
                : "The destination was left untouched or replaced only with a valid content-free export.",
            result.Succeeded ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    private void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e) =>
        UpdateCheckRequested?.Invoke();

    private void ApplyUpdateButton_Click(object sender, RoutedEventArgs e) =>
        UpdateApplyRequested?.Invoke();

    public void SetStartupRegistrationInProgress()
    {
        _startupOperationGeneration++;
        _startupOperationInProgress = true;
        StartAtLoginToggle.IsEnabled = false;
        SetLiveText(StartAtLoginStatusText, "Updating Windows startup settings…");
    }

    public void SetStartupRegistration(StartupRegistrationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _startupOperationGeneration++;
        _startupOperationInProgress = false;
        _isUpdatingStartupRegistration = true;
        try
        {
            StartAtLoginToggle.IsOn = result.IsEnabled;
        }
        finally
        {
            _isUpdatingStartupRegistration = false;
        }

        StartAtLoginToggle.IsEnabled = result.CanToggle;
        OpenStartupSettingsButton.Visibility = result.NeedsWindowsStartupSettings
            ? Visibility.Visible
            : Visibility.Collapsed;
        SetLiveText(
            StartAtLoginStatusText,
            result.Status switch
            {
                StartupRegistrationStatus.Enabled =>
                    "EnviousWispr starts when you sign in to Windows.",
                StartupRegistrationStatus.Disabled =>
                    "EnviousWispr starts only when you open it.",
                StartupRegistrationStatus.DisabledByUser =>
                    "Turned off in Windows Settings or Task Manager. Turn EnviousWispr on under Startup Apps in Windows Settings to start when you sign in.",
                StartupRegistrationStatus.DisabledByPolicy =>
                    "Turned off by a Windows policy on this PC.",
                StartupRegistrationStatus.EnabledByPolicy =>
                    "Turned on by a Windows policy on this PC.",
                _ => "Windows could not change the startup setting. Try again, or check Startup Apps in Windows Settings.",
            });
    }

    private void StartAtLoginToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isApplyingSettings || _isUpdatingStartupRegistration)
        {
            return;
        }

        StartupToggleRequested?.Invoke(StartAtLoginToggle.IsOn);
    }

    private async void OpenStartupSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var opened = await Windows.System.Launcher.LaunchUriAsync(
                new Uri("ms-settings:startupapps"));
            if (!opened)
            {
                ShowMessage(
                    "Windows startup settings did not open",
                    "Open Windows Settings, then choose Apps > Startup.",
                    InfoBarSeverity.Warning);
            }
        }
        catch
        {
            ShowMessage(
                "Windows startup settings did not open",
                "Open Windows Settings, then choose Apps > Startup.",
                InfoBarSeverity.Warning);
        }
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

        var imported = await _session.Profiles.ImportAsync(file.Path).ConfigureAwait(true);
        if (imported.Status != PortableProfileImportStatus.Imported || imported.Profile is null)
        {
            ShowMessage("Profile not imported", ImportFailureMessage(imported.Status), InfoBarSeverity.Error);
            return;
        }

        if (await TrySaveAsync(
                current => current.Apply(imported.Profile),
                "Profile imported",
                "Settings, dictionary entries, and snippets are ready. Machine-local choices and history were preserved.").ConfigureAwait(true))
        {
            ApplySettingsToControls();
        }
    }

    private void UpdateDiagnosticsStatusText(bool storedSendCrashReports)
    {
        SetLiveText(
            DiagnosticsStatusText,
            storedSendCrashReports != _launchedCrashReports
                ? "Takes effect when EnviousWispr restarts"
                : "We never collect your audio, dictated or transcribed text, history, snippets, dictionary words, API keys, or any text on your screen.");
    }

    private static string ImportFailureMessage(PortableProfileImportStatus status) => status switch
    {
        PortableProfileImportStatus.NewerVersion => "This profile was created by a newer EnviousWispr version. The file was left untouched.",
        PortableProfileImportStatus.Invalid => "This is not a valid EnviousWispr portable profile. The file and current settings were left untouched.",
        _ => "Windows could not read this profile. The file and current settings were left untouched.",
    };
}
