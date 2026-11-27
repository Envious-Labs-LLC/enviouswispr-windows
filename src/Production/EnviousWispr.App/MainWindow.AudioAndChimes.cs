using EnviousWispr.Audio;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Settings;
using EnviousWispr.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace EnviousWispr.App;

/// <summary>Recording chime cards and previews, microphone enumeration, readiness, and input level test meter.</summary>
public sealed partial class MainWindow
{
    private readonly RecordingSoundCuePlayer _recordingSoundPlayer;
    private readonly RecordingSoundCueCoordinator _recordingSoundCoordinator;
    private long _recordingSoundAttempt;

    private MicrophoneChoice[] _microphones = [];
    private IReadOnlyList<AudioDeviceInfo> _activeMicrophones = [];
    private bool _isRefreshingMicrophones;
    /// <summary>The session's catalogue, once the microphone list has asked for it; the window subscribes and unsubscribes, the session owns and disposes.</summary>
    private IAudioDeviceCatalog? _deviceCatalog;

    private CancellationTokenSource? _soundPreviewCancellation;

    private RecordingSoundPairing _selectedRecordingSoundPairing = RecordingSoundPairing.WhisperTick;
    private RecordingSoundPairing? _focusedChimePairing;
    private RecordingSoundPairing? _hoveredChimePairing;
    private readonly Dictionary<RecordingSoundPairing, Border> _chimeCards = new();
    private readonly Dictionary<RecordingSoundPairing, Button> _chimeSelectButtons = new();
    private readonly Dictionary<RecordingSoundPairing, Border> _chimeBadges = new();
    private readonly Dictionary<RecordingSoundPairing, List<Border>> _chimeWaveformBars = new();
    private readonly Dictionary<RecordingSoundPairing, Button> _chimePlayButtons = new();

    public event Action<RecordingSoundPairing, RecordingSoundMoment>? RecordingSoundPlaybackFailed;

    public event Action<AudioDeviceChange>? AudioDevicesChanged;

    public void CancelRecordingSoundCue()
    {
        Interlocked.Increment(ref _recordingSoundAttempt);
        _recordingSoundPlayer.CancelPendingStart();
        _recordingSoundCoordinator.CancelActive();
    }

    private void HandleRecordingSoundTransition(DictationOverlayState nextState)
    {
        if (nextState != DictationOverlayState.Recording)
        {
            Interlocked.Increment(ref _recordingSoundAttempt);
        }

        _recordingSoundCoordinator.Handle(
            isRecording: nextState == DictationOverlayState.Recording,
            _settings.Preferences.PlayRecordingSounds,
            _settings.Preferences.RecordingSoundPairing);
    }

    private bool PlayRecordingSoundCue(RecordingSoundPairing pairing, RecordingSoundMoment moment)
    {
        if (moment == RecordingSoundMoment.Start)
        {
            var attempt = Interlocked.Increment(ref _recordingSoundAttempt);
            return _recordingSoundPlayer.Play(
                pairing,
                moment,
                () =>
                {
                    if (Volatile.Read(ref _recordingSoundAttempt) == attempt)
                    {
                        _recordingSoundCoordinator.CancelActive();
                    }
                });
        }

        return _recordingSoundPlayer.Play(pairing, moment);
    }

    private void OnRecordingSoundCueFailed(
        RecordingSoundPairing pairing,
        RecordingSoundMoment moment,
        Exception exception)
    {
        RecordingSoundPlaybackFailed?.Invoke(pairing, moment);
    }

    private void PreloadRecordingSoundsInBackground(RecordingSoundPairing pairing)
    {
        if (_shuttingDownProductWindows)
        {
            return;
        }

        _ = Task.Run(() => _recordingSoundPlayer.Preload(pairing));
    }

    private void InitializeChimeCards()
    {
        if (ChimesGrid is null)
        {
            return;
        }

        ChimesGrid.Children.Clear();
        _chimeCards.Clear();
        _chimeSelectButtons.Clear();
        _chimeBadges.Clear();
        _chimeWaveformBars.Clear();
        _chimePlayButtons.Clear();
        _focusedChimePairing = null;
        _hoveredChimePairing = null;

        var defaultCardStyle = (Style)Application.Current.Resources["BrandChimeCardStyle"];
        var rowLabelStyle = (Style)Application.Current.Resources["BrandRowLabelStyle"];
        var helperStyle = (Style)Application.Current.Resources["BrandHelperStyle"];
        var accentChipStyle = (Style)Application.Current.Resources["BrandAccentChipStyle"];
        var accentChipTextStyle = (Style)Application.Current.Resources["BrandAccentChipTextStyle"];
        var waveInactiveStyle = (Style)Application.Current.Resources["BrandChimeWaveInactiveStyle"];
        var selectButtonStyle = (Style)Application.Current.Resources["BrandChimeSelectButtonStyle"];
        var quietButtonStyle = (Style)Application.Current.Resources["BrandQuietButtonStyle"];

        var choices = RecordingSoundCatalog.Choices;
        for (var index = 0; index < choices.Count; index++)
        {
            var choice = choices[index];
            var col = index % 2;
            var row = index / 2;

            var card = new Border
            {
                Style = defaultCardStyle,
                Tag = choice.Pairing,
                IsTapEnabled = true,
            };
            Grid.SetColumn(card, col);
            Grid.SetRow(card, row);

            card.Tapped += (s, e) =>
            {
                if (s is Border b && b.Tag is RecordingSoundPairing pairing)
                {
                    SelectChimePairing(pairing, triggerSave: true);
                }
            };
            card.PointerEntered += (s, _) =>
            {
                if (s is Border b && b.Tag is RecordingSoundPairing pairing)
                {
                    _hoveredChimePairing = pairing;
                    ApplyChimeCardStyle(pairing);
                }
            };
            card.PointerExited += (s, _) =>
            {
                if (s is Border b && b.Tag is RecordingSoundPairing pairing)
                {
                    if (_hoveredChimePairing == pairing)
                    {
                        _hoveredChimePairing = null;
                    }

                    ApplyChimeCardStyle(pairing);
                }
            };

            var cardGrid = new Grid
            {
                ColumnSpacing = 8,
            };
            cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var contentStack = new StackPanel { Spacing = 4 };

            var headerPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var titleText = new TextBlock
            {
                Text = choice.DisplayName,
                Style = rowLabelStyle,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            };

            var badge = new Border
            {
                CornerRadius = new CornerRadius(4),
                Style = accentChipStyle,
                Padding = new Thickness(6, 1, 6, 1),
                Visibility = Visibility.Collapsed,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "IN USE",
                    FontSize = 10,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Style = accentChipTextStyle,
                },
            };

            headerPanel.Children.Add(titleText);
            headerPanel.Children.Add(badge);
            contentStack.Children.Add(headerPanel);

            var descText = new TextBlock
            {
                Text = choice.Description,
                Style = helperStyle,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
            };
            contentStack.Children.Add(descText);

            var waveformPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                Height = 20,
                Margin = new Thickness(0, 4, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var barHeights = Audio.RecordingChimeWaveform.GetBarHeights(choice.Pairing);
            var bars = new List<Border>();
            foreach (var h in barHeights)
            {
                var barHeight = Math.Max(2.0, h * 18.0);
                var bar = new Border
                {
                    Width = 2,
                    Height = barHeight,
                    Style = waveInactiveStyle,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                bars.Add(bar);
                waveformPanel.Children.Add(bar);
            }
            contentStack.Children.Add(waveformPanel);

            var selectButton = new Button
            {
                Style = selectButtonStyle,
                Content = contentStack,
                Tag = choice.Pairing,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(selectButton, choice.DisplayName);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(selectButton, choice.Description);
            selectButton.Click += (s, e) =>
            {
                if (s is Button btn && btn.Tag is RecordingSoundPairing pairing)
                {
                    SelectChimePairing(pairing, triggerSave: true);
                }
            };
            selectButton.GotFocus += (s, e) =>
            {
                if (s is Button btn && btn.Tag is RecordingSoundPairing pairing)
                {
                    _focusedChimePairing = pairing;
                    ApplyChimeCardStyle(pairing);
                }
            };
            selectButton.LostFocus += (s, e) =>
            {
                if (s is Button btn && btn.Tag is RecordingSoundPairing pairing)
                {
                    if (_focusedChimePairing == pairing)
                    {
                        _focusedChimePairing = null;
                    }

                    ApplyChimeCardStyle(pairing);
                }
            };
            Grid.SetColumn(selectButton, 0);
            cardGrid.Children.Add(selectButton);

            var playButton = new Button
            {
                Width = 32,
                Height = 32,
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Style = quietButtonStyle,
                Content = new FontIcon
                {
                    Glyph = "\uE768",
                    FontSize = 12,
                },
                Tag = choice.Pairing,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(playButton, $"Preview {choice.DisplayName} sound");
            ToolTipService.SetToolTip(playButton, $"Play {choice.DisplayName} preview");
            playButton.Click += (s, e) =>
            {
                if (s is Button btn && btn.Tag is RecordingSoundPairing pairing)
                {
                    _ = PlayChimePreviewAsync(pairing, btn);
                }
            };
            Grid.SetColumn(playButton, 1);
            cardGrid.Children.Add(playButton);

            card.Child = cardGrid;
            ChimesGrid.Children.Add(card);

            _chimeCards[choice.Pairing] = card;
            _chimeSelectButtons[choice.Pairing] = selectButton;
            _chimeBadges[choice.Pairing] = badge;
            _chimeWaveformBars[choice.Pairing] = bars;
            _chimePlayButtons[choice.Pairing] = playButton;
        }

        UpdateChimeCardsVisuals();
    }

    private void SelectChimePairing(RecordingSoundPairing pairing, bool triggerSave = true)
    {
        _selectedRecordingSoundPairing = pairing;
        PreloadRecordingSoundsInBackground(pairing);
        UpdateChimeCardsVisuals();
        if (triggerSave && !_isApplyingSettings && _settings is not null && _chimeSelectButtons.TryGetValue(pairing, out var selectBtn))
        {
            var choice = RecordingSoundCatalog.Choices.FirstOrDefault(c => c.Pairing == pairing);
            var name = choice?.DisplayName ?? pairing.ToString();
            var peer = FrameworkElementAutomationPeer.FromElement(selectBtn)
                ?? FrameworkElementAutomationPeer.CreatePeerForElement(selectBtn);
            peer?.RaiseNotificationEvent(
                AutomationNotificationKind.Other,
                AutomationNotificationProcessing.CurrentThenMostRecent,
                $"{name} selected",
                "EnviousWisprChimeSelection");
        }
        if (triggerSave && !_isApplyingSettings && _settings is not null)
        {
            _ = TriggerAutoSaveAsync();
        }
    }

    private void ApplyChimeCardStyle(RecordingSoundPairing pairing)
    {
        if (!_chimeCards.TryGetValue(pairing, out var card))
        {
            return;
        }

        var styleKey = pairing == _selectedRecordingSoundPairing
            ? "BrandChimeCardSelectedStyle"
            : _focusedChimePairing == pairing
                ? "BrandChimeCardFocusedStyle"
                : _hoveredChimePairing == pairing
                    ? "BrandChimeCardHoverStyle"
                    : "BrandChimeCardStyle";
        card.Style = (Style)Application.Current.Resources[styleKey];
    }

    private void UpdateChimeCardsVisuals()
    {
        var waveActiveStyle = (Style)Application.Current.Resources["BrandChimeWaveActiveStyle"];
        var waveInactiveStyle = (Style)Application.Current.Resources["BrandChimeWaveInactiveStyle"];

        foreach (var (pairing, _) in _chimeCards)
        {
            var isSelected = pairing == _selectedRecordingSoundPairing;
            ApplyChimeCardStyle(pairing);

            if (_chimeSelectButtons.TryGetValue(pairing, out var selectButton))
            {
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(
                    selectButton,
                    isSelected ? "Selected" : "Not selected");
            }

            if (_chimeBadges.TryGetValue(pairing, out var badge))
            {
                badge.Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed;
            }

            if (_chimeWaveformBars.TryGetValue(pairing, out var bars))
            {
                var barStyle = isSelected ? waveActiveStyle : waveInactiveStyle;
                foreach (var bar in bars)
                {
                    bar.Style = barStyle;
                }
            }
        }
    }

    private async Task PlayChimePreviewAsync(RecordingSoundPairing pairing, Button? button = null)
    {
        if (_currentOverlayState == DictationOverlayState.Recording)
        {
            return;
        }

        _soundPreviewCancellation?.Cancel();
        _soundPreviewCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _soundPreviewCancellation = cancellation;
        if (button is not null)
        {
            button.IsEnabled = false;
        }

        try
        {
            if (!_recordingSoundPlayer.Play(pairing, RecordingSoundMoment.Start))
            {
                ShowMessage(
                    "Sound preview unavailable",
                    "Windows could not open the current audio output device.",
                    InfoBarSeverity.Warning);
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(550), cancellation.Token).ConfigureAwait(true);
            if (_currentOverlayState != DictationOverlayState.Recording)
            {
                _recordingSoundPlayer.Play(pairing, RecordingSoundMoment.Stop);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_soundPreviewCancellation, cancellation))
            {
                _soundPreviewCancellation.Dispose();
                _soundPreviewCancellation = null;
                if (button is not null)
                {
                    button.IsEnabled = _currentOverlayState != DictationOverlayState.Recording;
                }
            }
        }
    }

    /// <summary>The microphone test's lifecycle, without the page: who may open the device, for how long, and what the counts mean.</summary>
    /// <remarks>
    /// THE PAGE KEEPS THE METER, THE WORDS AND THE PRIVACY LINK. The rules - a dictation outranks a
    /// test, one test at a time, the token goes all the way into the capture, the stop is never
    /// cancelled, a frame from a finished test is not drawn - are MicrophoneTestControllerTests.
    /// </remarks>

    /// <summary>Opens the microphone for a moment and shows what actually arrives.</summary>
    /// <remarks>
    /// IT SAYS WHICH KIND OF NOTHING. A device that could not be opened, a device that opened and
    /// delivered packets Windows marked as deliberately silent, and a device that delivered real
    /// packets of zeroes are three different faults with three different answers, and a bare "no
    /// sound" sends somebody to look in the wrong place for all three. The verdict is the
    /// controller's; the sentences for the ways a test does not reach one are here.
    /// </remarks>
    private async void MicrophoneTestButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session.MicrophoneTest.IsRunning)
        {
            return;
        }

        // THE DEVICE IS READ HERE, ON THE UI THREAD, and the test is told whether a recording holds
        // the microphone - the pair that, with the cancel from the other side, keeps a test and a
        // dictation from both opening it.
        var selectedId = (MicrophoneComboBox.SelectedItem as MicrophoneChoice)?.Id;
        AudioDeviceId? device = _activeMicrophones.Count > 0
            ? InputDeviceResolver.Resolve(_activeMicrophones, selectedId).SelectedDeviceId
            : selectedId is null
                ? null
                : new AudioDeviceId(selectedId);
        var recording = _currentOverlayState == DictationOverlayState.Recording;
        if (recording)
        {
            SetLiveText(
                MicrophoneTestResultText,
                "A recording is running. Finish it, then test the microphone.");
            return;
        }

        MicrophoneTestButton.IsEnabled = false;
        SetLiveText(MicrophoneTestResultText, "Listening. Say a few words.");
        _session.MicrophoneTest.Frame += OnMicrophoneTestFrame;
        try
        {
            var result = await _session.MicrophoneTest.RunAsync(device, recording).ConfigureAwait(true);
            if (_session.Closing)
            {
                // The exit stopped the test, or began as it finished; the page is not written to.
                return;
            }

            switch (result.Outcome)
            {
                case MicrophoneTestOutcome.Completed:
                    SetLiveText(MicrophoneTestResultText, result.Verdict!);
                    break;
                case MicrophoneTestOutcome.DeviceWouldNotOpen:
                    SetLiveText(
                        MicrophoneTestResultText,
                        "Windows would not open that microphone. Check it is plugged in and that "
                            + "microphone privacy allows desktop apps.");
                    break;
                case MicrophoneTestOutcome.StoppedPartWay:
                    SetLiveText(
                        MicrophoneTestResultText,
                        "The microphone stopped part way through the test. It may have been unplugged, "
                            + "or taken by another app.");
                    break;
                case MicrophoneTestOutcome.Cancelled:
                    SetLiveText(MicrophoneTestResultText, "Microphone test stopped.");
                    break;
                default:
                    // Already running: the controller refused, and the page has nothing new to say.
                    // (The recording case was answered above; the controller's own refusal of it is
                    // a contract for callers that do not ask first.)
                    break;
            }
        }
        finally
        {
            _session.MicrophoneTest.Frame -= OnMicrophoneTestFrame;
            MicrophoneTestButton.IsEnabled = true;
            DrawMicrophoneTestLevel(0f);
        }
    }

    /// <summary>Stops a microphone test, because something with a better claim wants the device.</summary>
    private void CancelMicrophoneTest() => _session.MicrophoneTest.Cancel();

    /// <summary>One meter frame, posted to the UI thread and refused there if its test has ended.</summary>
    /// <remarks>
    /// AN UPDATE ALREADY ON THE QUEUE OUTLIVES THE UNSUBSCRIBE. Removing the handler stops new ones
    /// being posted and does nothing about the ones already waiting, so a late update could relight
    /// the meter after the test had finished and cleared it, or during the next one. The frame
    /// carries its test, and the controller says whether that test is still the one running.
    /// </remarks>
    private void OnMicrophoneTestFrame(object? sender, MicrophoneTestFrame frame) =>
        MicrophoneTestBars.DispatcherQueue.TryEnqueue(() =>
        {
            if (!_session.MicrophoneTest.IsCurrent(frame.TestId))
            {
                return;
            }

            DrawMicrophoneTestLevel(frame.Level);
        });

    private void DrawMicrophoneTestLevel(float level)
    {
        if (MicrophoneTestBars is null)
        {
            return;
        }

        var lit = (int)Math.Round(level * MicrophoneTestBars.Children.Count);
        for (var index = 0; index < MicrophoneTestBars.Children.Count; index++)
        {
            if (MicrophoneTestBars.Children[index] is not Border bar)
            {
                continue;
            }

            var on = index < lit;
            bar.Height = on ? 22 : 6;
            bar.Opacity = on ? 1 : 0.25;

            // COLOUR AS WELL AS HEIGHT, so the row reads at a glance rather than only under
            // comparison. A lit segment differs from an unlit one in three ways at once - taller,
            // fully opaque, and accent rather than grey - which is what makes a single lit bar
            // legible on a quiet microphone.
            bar.Background = on
                ? ResolveWindowThemeBrush("BrandAccentSolidBrush")
                : ResolveWindowThemeBrush("BrandTextSecondaryBrush");
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

    private async Task LoadMicrophonesAsync()
    {
        // DECLARED OUTSIDE THE TRY SO THE ANSWER SURVIVES A FAILURE. A blocked switch is one of the
        // reasons enumeration throws, and the catch is exactly where knowing that matters most.
        var consent = MicrophoneConsent.Unknown;
        try
        {
            if (_deviceCatalog is null)
            {
                _deviceCatalog = _session.DeviceCatalog();
                _deviceCatalog.DevicesChanged += OnAudioDevicesChanged;
            }

            // READ BEFORE THE ENUMERATION, because the enumeration is one of the things a blocked
            // switch breaks. Reading it afterwards meant the catch threw the answer away and
            // reported the symptom instead of the cause.
            consent = WindowsMicrophoneConsent.Read();
            var devices = await _deviceCatalog.GetCaptureDevicesAsync().ConfigureAwait(true);
            _activeMicrophones = devices;
            var autoResolution = InputDeviceResolver.Resolve(devices, preferredDeviceId: null);
            var effectiveResolution = InputDeviceResolver.Resolve(devices, _settings.PreferredMicrophoneId);
            var choices = new List<MicrophoneChoice>
            {
                new(null, InputDeviceResolver.FormatAutoSelectionLabel(autoResolution)),
            };
            choices.AddRange(devices.Select(device =>
                new MicrophoneChoice(device.Id.Value, InputDeviceResolver.FormatDeviceDisplayName(device))));

            var preferredId = _settings.PreferredMicrophoneId;
            MicrophoneChoice? selected = null;
            if (!string.IsNullOrWhiteSpace(preferredId))
            {
                selected = choices.FirstOrDefault(choice =>
                    string.Equals(choice.Id, preferredId, StringComparison.Ordinal));
                if (selected is null)
                {
                    var fallbackSuffix = effectiveResolution.SelectedDevice is { } fallbackDevice
                        ? $"Auto using {fallbackDevice.DisplayName}"
                        : "no active microphone";
                    selected = new MicrophoneChoice(
                        preferredId,
                        $"Saved microphone (unavailable · {fallbackSuffix})");
                    choices.Add(selected);
                }
            }

            _isRefreshingMicrophones = true;
            try
            {
                _microphones = choices.ToArray();
                _selectedMicrophoneChoice = selected ?? _microphones[0];
                if (MicrophoneComboBox is not null)
                {
                    MicrophoneComboBox.ItemsSource = _microphones;
                    MicrophoneComboBox.SelectedItem = _selectedMicrophoneChoice;
                }
            }
            finally
            {
                _isRefreshingMicrophones = false;
            }

            ApplyMicrophoneReadiness(
                MicrophoneReadinessReport.For(
                    consent,
                    effectiveResolution.SelectedDevice?.DisplayName),
                consent);
        }
        catch
        {
            _activeMicrophones = [];
            _isRefreshingMicrophones = true;
            try
            {
                _microphones = (MicrophoneChoice[])[new MicrophoneChoice(null, "Auto")];
                _selectedMicrophoneChoice = _microphones[0];
                if (MicrophoneComboBox is not null)
                {
                    MicrophoneComboBox.ItemsSource = _microphones;
                    MicrophoneComboBox.SelectedIndex = 0;
                }
            }
            finally
            {
                _isRefreshingMicrophones = false;
            }

            ApplyMicrophoneReadiness(
                MicrophoneReadinessReport.For(
                    consent,
                    defaultDeviceName: null,
                    enumerationFailed: true),
                consent);
        }
    }

    private void ApplyMicrophoneReadiness(MicrophoneReadiness readiness, MicrophoneConsent consent)
    {
        _latestMicrophoneReadiness = readiness;
        _latestMicrophoneConsent = consent;
        ApplyMicrophoneReadinessToControls(readiness, consent);
        ObserveMicrophoneForOnboarding(readiness, consent);
    }

    private void ApplyMicrophoneReadinessToControls(MicrophoneReadiness readiness, MicrophoneConsent consent)
    {
        if (PermissionsMicrophoneText is null
            || PermissionsMicrophoneAllowedBadge is null
            || PermissionsMicrophoneAttentionBadge is null)
        {
            return;
        }

        SetLiveText(PermissionsMicrophoneText, readiness.Sentence);
        var allowed = consent == MicrophoneConsent.Allowed;
        PermissionsMicrophoneAllowedBadge.Visibility = allowed ? Visibility.Visible : Visibility.Collapsed;
        PermissionsMicrophoneAttentionBadge.Visibility = allowed ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnAudioDevicesChanged(object? sender, AudioDeviceChange change)
    {
        if (!change.AffectsCapture)
        {
            return;
        }

        AudioDevicesChanged?.Invoke(change);
        // An async callback handed to TryEnqueue is an async void: a throw inside it is
        // UNOBSERVED and takes the process down. This one fires on a device change - a headset
        // plugged in, a dock disconnected - which is an ordinary thing to do while the app is
        // sitting in the tray, and both halves can fail (device enumeration, and touching UI on a
        // window that may be closing). Same shape as the navigation crash: a callback that runs
        // later, against a world that has moved on.
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                await LoadMicrophonesAsync().ConfigureAwait(true);
                var resolution = InputDeviceResolver.Resolve(_activeMicrophones, _settings.PreferredMicrophoneId);
                var noticeDetail = resolution.FallbackFromPinned && resolution.SelectedDevice is { } fallback
                    ? $"Your pinned microphone is unavailable; falling back to {fallback.DisplayName}."
                    : resolution.BypassedDefaultDevice is not null && resolution.SelectedDevice is { } physical
                        ? $"EnviousWispr refreshed active microphones and selected {physical.DisplayName} instead of virtual default."
                        : "EnviousWispr refreshed the active recording-device list. A missing preferred microphone falls back to the resolved device.";
                ShowMessage(
                    "Microphone devices updated",
                    noticeDetail,
                    InfoBarSeverity.Informational);
            }
            catch (Exception)
            {
                // Failing to REFRESH a device list, or to announce that it changed, is a missed
                // convenience. Letting it escape is a dead app and a lost dictation. The user's
                // next recording re-reads the device list anyway, so there is nothing here worth
                // the process.
            }
        });
    }

    private RecordingSoundPairing SelectedRecordingSoundPairing() => _selectedRecordingSoundPairing;

    private sealed record MicrophoneChoice(string? Id, string DisplayName);
}
