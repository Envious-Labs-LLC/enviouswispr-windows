using EnviousWispr.App.Overlay;
using EnviousWispr.Core.Learning;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace EnviousWispr.App;

public sealed partial class DictationOverlayWindow : Window, IDisposable
{
    private const int RecordingCapsuleHeight = 44;
    private const double RecordingCapsuleRadius = 22;
    private const double ReadingWellRadius = 16;
    private const double NoticeCapsuleRadius = 24;
    private const double CardCornerRadius = 18;
    private const double LearnedCapsuleRadius = 32;
    private const double LearnedPillHeight = 64;
    private const double CapsuleHeaderGap = 10;
    private const double HoldLipsSize = 24;
    private const double HandsFreeLipsSize = 36;
    private const double FrameBorderThickness = 1;

    /// <summary>Processing's border, inset, wheel and gap: 14 + 24 + 10 before the label, 14 after it, plus 2 for the frame border.</summary>
    private const double ProcessingChromeWidth = 14 + 24 + 10 + 14 + FrameBorderThickness * 2;

    private const int CapsuleDefaultWidth = 104;
    private const int StandardNoticeMaxWidth = 320;
    private const int LanguageChipMaxWidth = 340;
    private const int AdvisoryNoticeMaxWidth = 360;
    private const int RichErrorCardWidth = 400;
    private const int FloatingCardWidth = 320;

    private readonly OverlayWin32Host _win32Host;
    private readonly OverlayExpiryDriver _expiryDriver;
    private readonly OverlayChromeAnimator _chrome;
    private readonly OverlayRecordingSurface _recordingSurface;
    private readonly DispatcherTimer _elapsedTimer = new();
    private readonly DispatcherTimer _levelTimer = new();
    private readonly System.Diagnostics.Stopwatch _levelClock = System.Diagnostics.Stopwatch.StartNew();
    private readonly RecordingLevelHistory _levelHistory = new();

    private readonly double _noticeColumnSpacing;
    private readonly double _noticeContentSpacing;

    private XamlRoot? _observedXamlRoot;
    private LearnedCorrectionPillView? _learnedPillView;
    private BluetoothAwarenessCardView? _bluetoothCardView;
    private PolishSetupCardView? _polishSetupCardView;

    private LearnedCorrectionPillModel? _learnedModel;
    private DictationOverlayState _state = DictationOverlayState.Hidden;
    private DictationStatus _currentStatus = DictationStatus.Quiet(string.Empty);
    private DateTimeOffset _recordingStartedAt;
    private PillAction? _action;
    private string? _previewText;
    private RecordingNoticeKind _recordingNotice;
    private OverlayPillPosition _position = OverlayPillPosition.Top;
    private bool _livePreviewEnabled;
    private RecordingPillDesign _withoutWordsDesign = RecordingPillDesign.Classic;
    private RecordingPillDesign _withWordsDesign = RecordingPillDesign.ReadingWell;
    private RecordingPillDesign _activeDesign = RecordingPillDesign.Classic;

    private bool _handsFree;
    private bool _autoCapNoticeShown;
    private bool _pointerIsOver;
    private bool _isPreviewSession;
    private bool _previewHoldOpen;
    private RecordingPillDesign? _previewDesignOverride;
    private bool _preWarmed;
    private bool _cleanedUp;
    private bool _closed;
    private float _latestLevel;

    public DictationOverlayWindow()
    {
        InitializeComponent();

        _win32Host = new OverlayWin32Host(this, AppWindow);
        _expiryDriver = new OverlayExpiryDriver(() => _win32Host.AnimationsEnabled);
        _expiryDriver.RailProgressChanged += OnExpiryRailProgressChanged;
        _expiryDriver.Expired += OnExpiryExpired;

        _chrome = new OverlayChromeAnimator(
            OverlayRoot,
            FrameBorder,
            SeverityWash,
            BottomRainbowHairline,
            BottomDistressHairline,
            LipsMark,
            SpectrumWheelTransform,
            () => _win32Host.AnimationsEnabled);

        _recordingSurface = new OverlayRecordingSurface(
            LipsMark,
            LevelBars,
            WellMeter,
            PreviewWell,
            PreviewViewport,
            PreviewText,
            PreviewFade,
            DistressRedProbe,
            LipsSpectrumPalette);

        _noticeColumnSpacing = HeaderRow.ColumnSpacing;
        _noticeContentSpacing = ContentPanel.Spacing;

        var initialWidth = CapsulePillWidth();
        _win32Host.Resize(initialWidth, RecordingCapsuleHeight, RecordingCapsuleRadius);
        _recordingSurface.DrawLips(0f, force: true);
        OverlayRoot.Measure(new Windows.Foundation.Size(initialWidth, RecordingCapsuleHeight));

        OverlayRoot.ActualThemeChanged += OnOverlayRootActualThemeChanged;
        OverlayRoot.Loaded += OnOverlayRootLoaded;
        Closed += OnWindowClosed;

        _elapsedTimer.Interval = TimeSpan.FromMilliseconds(250);
        _elapsedTimer.Tick += OnElapsedTimerTick;

        _levelTimer.Interval = RecordingLevelHistory.SampleInterval / 2;
        _levelTimer.Tick += OnLevelTimerTick;
    }

    public bool IsActiveNonPreview => _state != DictationOverlayState.Hidden && !_isPreviewSession;

    public bool IsPreviewVisible => _state != DictationOverlayState.Hidden && _isPreviewSession;

    public event Action<string>? PreviewActionPressed;

    public event Action<PillActionKind>? ActionInvoked;

    public event Action<Guid>? LearnedUndoRequested;

    public event Action<Guid>? LearnedDismissed;

    public event Action<Guid>? LearnedEnded;

    public void ApplyTheme(ElementTheme theme)
    {
        OverlayRoot.RequestedTheme = theme;
        _recordingSurface.ApplySpectrumSweep();
        _recordingSurface.ApplyLipsColoring(distress: _state == DictationOverlayState.Distress);
    }

    public void PreWarm()
    {
        if (_preWarmed || _state != DictationOverlayState.Hidden || _cleanedUp)
        {
            return;
        }

        _preWarmed = true;
        _win32Host.MoveOffscreenForPreWarm();
        _win32Host.ShowUnactivated();
        OverlayRoot.UpdateLayout();
        _win32Host.Hide();
    }

    /// <summary>Renders an isolated preview entry from the Diagnostics Pill Preview gallery.</summary>
    public void ShowPreviewEntry(PillPreviewEntry entry, bool holdOpen)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _isPreviewSession = true;
        _previewHoldOpen = holdOpen;
        _previewDesignOverride = entry.PreviewDesign;
        _win32Host.ResetDrag();

        if (!string.IsNullOrWhiteSpace(entry.SaveErrorWord))
        {
            ShowLearnedSaveErrorInternal(entry.SaveErrorWord, isPreview: true);
            return;
        }

        if (entry.LearnedModel is { } learned)
        {
            ShowLearnedCorrectionInternal(learned, isPreview: true);
            return;
        }

        var previewStatus = entry.PreviewStatus();
        ShowStateInternal(previewStatus, isPreview: true);
        if (!string.IsNullOrWhiteSpace(entry.PreviewTranscript)
            && previewStatus.State == DictationOverlayState.Recording
            && _activeDesign == RecordingPillDesign.ReadingWell)
        {
            SetPreview(entry.PreviewTranscript);
        }
        else if (previewStatus.State == DictationOverlayState.Recording
            && _activeDesign == RecordingPillDesign.LevelRail)
        {
            _recordingSurface.DrawSampleLevelRailForPreview();
        }
    }

    public void SetPreviewHoldOpen(bool holdOpen)
    {
        _previewHoldOpen = holdOpen;
        if (!_isPreviewSession)
        {
            return;
        }

        _expiryDriver.SetHoldOpen(holdOpen);
        if (holdOpen || _expiryDriver.IsActive)
        {
            return;
        }

        if (_state == DictationOverlayState.EscapeRecovery)
        {
            var seconds = _currentStatus.DwellSeconds ?? OverlayPillCatalog.EscapeRecoveryDwellSeconds;
            StartEscapeCountdown(seconds);
            return;
        }

        if (_state == DictationOverlayState.CorrectionLearned && _learnedModel is { } learned)
        {
            var seconds = OverlayPillCatalog.ResolveLearnedDwellSeconds(learned.Phase);
            if (seconds > 0)
            {
                _expiryDriver.ArmLearnedCountdown(
                    TimeSpan.FromSeconds(seconds),
                    autoExpire: learned.Phase != LearnedCorrectionPillPhase.UndoPending,
                    startPaused: _pointerIsOver && learned.Phase == LearnedCorrectionPillPhase.Learned,
                    holdOpen: false);
            }

            return;
        }

        ArmDwell(_currentStatus);
    }

    public bool DismissPreview()
    {
        if (!_isPreviewSession || _state == DictationOverlayState.Hidden)
        {
            return false;
        }

        HideOverlay();
        return true;
    }

    /// <summary>Puts one live dictation status on screen.</summary>
    public void ShowState(DictationStatus status)
    {
        _isPreviewSession = false;
        _previewHoldOpen = false;
        _previewDesignOverride = null;
        ShowStateInternal(status, isPreview: false);
    }

    public void SetPreview(string? text)
    {
        _previewText = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        if (_state == DictationOverlayState.Recording
            && _activeDesign == RecordingPillDesign.ReadingWell)
        {
            PreviewText.Text = _previewText ?? string.Empty;
            ResizeReadingWell();
        }
    }

    public void SetHandsFree(bool handsFree)
    {
        if (_handsFree == handsFree)
        {
            return;
        }

        _handsFree = handsFree;
        if (_state == DictationOverlayState.Recording)
        {
            ConfigureRecordingDesign();
            AnnounceRecording(RecordingSpokenName());
        }
    }

    public void SetAudioLevel(float rootMeanSquare) =>
        AccumulatePeak(RecordingLevelHistory.Normalize(rootMeanSquare));

    public void ApplyPreferences(
        OverlayPillPosition position,
        bool livePreviewEnabled,
        RecordingPillDesign withoutWordsDesign,
        RecordingPillDesign withWordsDesign)
    {
        _position = position;
        _livePreviewEnabled = livePreviewEnabled;
        _withoutWordsDesign = withoutWordsDesign;
        _withWordsDesign = withWordsDesign;
        if (!livePreviewEnabled && !_isPreviewSession)
        {
            SetPreview(text: null);
        }
    }

    public bool ShowLearnedCorrection(LearnedCorrectionPillModel model)
    {
        _isPreviewSession = false;
        _previewHoldOpen = false;
        _previewDesignOverride = null;
        return ShowLearnedCorrectionInternal(model, isPreview: false);
    }

    public void UpdateLearnedCorrection(LearnedCorrectionPillModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (_state != DictationOverlayState.CorrectionLearned
            || _learnedModel?.PillId != model.PillId)
        {
            return;
        }

        _learnedModel = model;
        RenderLearnedPill(model, announceNow: true);
        _win32Host.PositionOnForegroundMonitor(_position);

        if (_isPreviewSession && _previewHoldOpen)
        {
            _expiryDriver.SetHoldOpen(true);
            return;
        }

        if (model.Phase == LearnedCorrectionPillPhase.Undone)
        {
            _expiryDriver.ArmLearnedCountdown(
                TimeSpan.FromSeconds(OverlayPillCatalog.LearnedUndoneDwellSeconds),
                autoExpire: true,
                startPaused: false,
                holdOpen: false);
        }
        else if (model.Phase == LearnedCorrectionPillPhase.UndoError)
        {
            _expiryDriver.ArmLearnedCountdown(
                TimeSpan.FromSeconds(OverlayPillCatalog.LearnedErrorDwellSeconds),
                autoExpire: true,
                startPaused: false,
                holdOpen: false);
        }
        else if (model.Phase == LearnedCorrectionPillPhase.UndoPending)
        {
            _expiryDriver.EnterUndoPending(TimeSpan.FromSeconds(OverlayPillCatalog.LearnedUndoPendingFloorSeconds));
        }
    }

    public void CloseLearnedCorrection(Guid pillId)
    {
        if (_state != DictationOverlayState.CorrectionLearned
            || _learnedModel?.PillId != pillId)
        {
            return;
        }

        HideOverlay();
    }

    public void ShowLearnedSaveError(string canonical)
    {
        _isPreviewSession = false;
        _previewHoldOpen = false;
        _previewDesignOverride = null;
        ShowLearnedSaveErrorInternal(canonical, isPreview: false);
    }

    public void Shutdown()
    {
        Cleanup();
        if (!_closed)
        {
            _closed = true;
            Close();
        }
    }

    public void Dispose() => Shutdown();

    private void ShowStateInternal(DictationStatus status, bool isPreview)
    {
        if (_cleanedUp)
        {
            return;
        }

        var state = status.State;
        _currentStatus = status;
        _expiryDriver.Cancel();
        FinishLearnedPresentation(notifyEnded: _learnedModel is not null && state != DictationOverlayState.CorrectionLearned);
        DeactivateSurfaces();

        if (state == DictationOverlayState.Hidden)
        {
            _state = state;
            HideOverlay();
            return;
        }

        var previousState = _state;
        _state = state;
        _preWarmed = true;

        var definition = OverlayPillCatalog.Resolve(status);
        StateIcon.Glyph = definition.IconGlyph;

        if (definition.Surface == OverlayPillSurfaceKind.Recording)
        {
            ContentPanel.Visibility = Visibility.Visible;
            if (previousState != DictationOverlayState.Recording || isPreview)
            {
                _recordingStartedAt = DateTimeOffset.UtcNow;
                ElapsedText.Text = RecordingPillGeometry.FormatElapsed(TimeSpan.Zero);
                _previewText = null;
                _handsFree = status.IsHandsFree;
                _autoCapNoticeShown = false;
            }
            else if (status.IsHandsFree)
            {
                _handsFree = true;
            }

            _recordingNotice = status.RecordingNotice;
            _activeDesign = _previewDesignOverride ?? RecordingPillCatalog.Resolve(
                _livePreviewEnabled,
                _withoutWordsDesign,
                _withWordsDesign);

            if (previousState != DictationOverlayState.Recording || isPreview)
            {
                ResetLevelHistory();
            }

            _recordingSurface.ApplyLipsColoring(distress: false);
            ApplyAction(action: null);
            ConfigureRecordingDesign();
            if (!isPreview)
            {
                _elapsedTimer.Start();
                _levelTimer.Start();
            }
            else
            {
                _elapsedTimer.Stop();
                _levelTimer.Stop();
            }
        }
        else if (definition.Surface is OverlayPillSurfaceKind.BluetoothCard or OverlayPillSurfaceKind.PolishSetupCard)
        {
            _elapsedTimer.Stop();
            _levelTimer.Stop();
            _handsFree = false;
            _recordingNotice = RecordingNoticeKind.None;
            ConfigureFloatingCard(status);
        }
        else
        {
            ContentPanel.Visibility = Visibility.Visible;
            _elapsedTimer.Stop();
            _levelTimer.Stop();
            _handsFree = false;
            _recordingNotice = RecordingNoticeKind.None;
            StateTitle.Text = definition.Title;
            ApplyAction(status.Action, status.SecondaryAction);
            ConfigureNotice(status, definition.Title, isSaveError: false);
        }

        _chrome.ApplySeverity(state, _activeDesign, StateIcon);

        var spokenName = OverlayPillCatalog.ComputeSpokenName(
            state,
            status,
            definition.Title,
            state == DictationOverlayState.Recording ? RecordingSpokenName() : null);

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(StateTitle, spokenName);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ProcessingLabel, spokenName);
        _win32Host.PositionOnForegroundMonitor(_position);
        _win32Host.ShowUnactivated();

        if (state == DictationOverlayState.Recording)
        {
            AnnounceRecording(spokenName);
        }
        else if (state == DictationOverlayState.Processing)
        {
            AnnounceStateChange(ProcessingLabel, state);
        }
        else if (definition.Surface is OverlayPillSurfaceKind.BluetoothCard or OverlayPillSurfaceKind.PolishSetupCard)
        {
            LiveAnnouncementText.Text = spokenName;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(LiveAnnouncementText, spokenName);
            AnnounceStateChange(LiveAnnouncementText, state);
        }
        else
        {
            AnnounceStateChange(StateTitle, state);
        }

        if (state == DictationOverlayState.EscapeRecovery && _action is not null)
        {
            var seconds = status.DwellSeconds ?? OverlayPillCatalog.EscapeRecoveryDwellSeconds;
            if (!(_isPreviewSession && _previewHoldOpen))
            {
                StartEscapeCountdown(seconds);
            }
            else
            {
                EscapeCountdownRail.Visibility = Visibility.Visible;
                UpdateEscapeRailScale(0.65);
            }
        }
        else if (definition.AutoDwells)
        {
            ArmDwell(status);
        }
    }

    private bool ShowLearnedCorrectionInternal(LearnedCorrectionPillModel model, bool isPreview)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (_cleanedUp || (!isPreview && _state is DictationOverlayState.Recording or DictationOverlayState.Processing))
        {
            return false;
        }

        _expiryDriver.Cancel();
        _elapsedTimer.Stop();
        _levelTimer.Stop();
        _chrome.StopAll();
        SpectrumWheelIcon.Visibility = Visibility.Collapsed;
        DeactivateSurfaces();
        _chrome.ApplyHairline(visible: false, breathes: false, distress: false);
        _handsFree = false;
        _recordingNotice = RecordingNoticeKind.None;
        _previewText = null;
        _action = null;

        FinishLearnedPresentation(notifyEnded: !isPreview && _learnedModel is not null && _learnedModel.PillId != model.PillId);
        _learnedModel = model;
        _state = DictationOverlayState.CorrectionLearned;
        _preWarmed = true;

        var view = RenderLearnedPill(model, announceNow: false);
        _win32Host.PositionOnForegroundMonitor(_position);
        _win32Host.ShowUnactivated();
        view.AnnounceTitle();

        var seconds = OverlayPillCatalog.ResolveLearnedDwellSeconds(model.Phase);
        if (seconds > 0)
        {
            var holdOpen = isPreview && _previewHoldOpen;
            _expiryDriver.ArmLearnedCountdown(
                TimeSpan.FromSeconds(seconds),
                autoExpire: model.Phase != LearnedCorrectionPillPhase.UndoPending,
                startPaused: _pointerIsOver && model.Phase == LearnedCorrectionPillPhase.Learned,
                holdOpen: holdOpen);
            if (holdOpen && model.Phase is LearnedCorrectionPillPhase.Learned or LearnedCorrectionPillPhase.UndoPending)
            {
                UpdateLearnedRailScale(0.35);
            }
        }

        return true;
    }

    private void ShowLearnedSaveErrorInternal(string canonical, bool isPreview)
    {
        if (_cleanedUp || (!isPreview && _state is DictationOverlayState.Recording or DictationOverlayState.Processing))
        {
            return;
        }

        var word = string.IsNullOrWhiteSpace(canonical) ? "word" : canonical.Trim();
        var message = $"Couldn\u2019t save \u201C{word}\u201D";
        _expiryDriver.Cancel();
        FinishLearnedPresentation(notifyEnded: !isPreview && _learnedModel is not null);
        _elapsedTimer.Stop();
        _levelTimer.Stop();
        DeactivateSurfaces();

        _action = null;
        _handsFree = false;
        _recordingNotice = RecordingNoticeKind.None;
        _state = DictationOverlayState.Error;
        _preWarmed = true;

        ContentPanel.Visibility = Visibility.Visible;
        StateTitle.Text = message;
        ApplyAction(null);
        ConfigureNotice(DictationStatus.Error(message), message, isSaveError: true);
        _chrome.ApplySeverity(DictationOverlayState.Error, _activeDesign, StateIcon);

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(StateTitle, message);
        _win32Host.PositionOnForegroundMonitor(_position);
        _win32Host.ShowUnactivated();
        AnnounceStateChange(StateTitle, DictationOverlayState.Error);

        _expiryDriver.ArmFixedDwell(
            TimeSpan.FromSeconds(OverlayPillCatalog.LearnedErrorDwellSeconds),
            startPaused: _pointerIsOver,
            holdOpen: _isPreviewSession && _previewHoldOpen);
    }

    private void DeactivateSurfaces()
    {
        ContentPanel.Visibility = Visibility.Collapsed;
        DeferredSurfaceHost.Visibility = Visibility.Collapsed;
        if (_learnedPillView is not null)
        {
            _learnedPillView.Visibility = Visibility.Collapsed;
        }

        if (_bluetoothCardView is not null)
        {
            _bluetoothCardView.Visibility = Visibility.Collapsed;
        }

        if (_polishSetupCardView is not null)
        {
            _polishSetupCardView.Visibility = Visibility.Collapsed;
        }

        LearnedCountdownRail.Visibility = Visibility.Collapsed;
        LearnedCountdownScale.ScaleX = 0;
        EscapeCountdownRail.Visibility = Visibility.Collapsed;
        EscapeCountdownScale.ScaleX = 0;
        CardBottomRainbowBar.Visibility = Visibility.Collapsed;
        LeftSpectrumStripe.Visibility = Visibility.Collapsed;
        BottomDistressHairline.Visibility = Visibility.Collapsed;
        InPanelNoticeRow.Visibility = Visibility.Collapsed;
        WarningGlyphBadge.Visibility = Visibility.Collapsed;
        ErrorCircleBadge.Visibility = Visibility.Collapsed;
        ReadyCircleBadge.Visibility = Visibility.Collapsed;
        RichErrorBadge.Visibility = Visibility.Collapsed;
        StateSubtitle.Visibility = Visibility.Collapsed;
        InlineActionRow.Visibility = Visibility.Collapsed;
        InlineActionButton.Visibility = Visibility.Collapsed;
        InlineDismissButton.Visibility = Visibility.Collapsed;
    }

    private void HideOverlay()
    {
        _levelTimer.Stop();
        _elapsedTimer.Stop();
        _expiryDriver.Cancel();
        FinishLearnedPresentation(notifyEnded: !_isPreviewSession);
        DeactivateSurfaces();
        ContentPanel.Visibility = Visibility.Visible;
        _chrome.StopAll();
        SpectrumWheelIcon.Visibility = Visibility.Collapsed;
        _chrome.ApplyHairline(visible: false, breathes: false, distress: false);
        _pointerIsOver = false;
        _handsFree = false;
        _recordingNotice = RecordingNoticeKind.None;
        _previewText = null;
        _action = null;
        _win32Host.ResetDrag();
        _isPreviewSession = false;
        _previewHoldOpen = false;
        _previewDesignOverride = null;
        _state = DictationOverlayState.Hidden;
        _win32Host.Hide();
    }

    private void ConfigureRecordingDesign()
    {
        _chrome.SetSpectrumWheelSpinning(false);
        SpectrumWheelIcon.Visibility = Visibility.Collapsed;
        StateIcon.Visibility = Visibility.Collapsed;
        ProcessingLabel.Visibility = Visibility.Collapsed;
        HeaderLeading.Visibility = Visibility.Visible;
        StateTitle.TextWrapping = TextWrapping.Wrap;
        StateTitle.MaxLines = 0;
        SetLiveVisibility(StateTitle, Visibility.Collapsed);

        var classic = _activeDesign == RecordingPillDesign.Classic;
        var rail = _activeDesign == RecordingPillDesign.LevelRail;
        var readingWell = _activeDesign == RecordingPillDesign.ReadingWell;

        var lipsSize = classic && _handsFree ? HandsFreeLipsSize : HoldLipsSize;
        LipsMark.Visibility = classic ? Visibility.Visible : Visibility.Collapsed;
        LipsMark.Opacity = 1;
        LipsMark.Width = lipsSize;
        LipsMark.Height = lipsSize;
        LipsMarkScale.ScaleX = 1;
        LipsMarkScale.ScaleY = 1;
        if (classic)
        {
            _recordingSurface.DrawLips(_recordingSurface.LastDrawnLevel, force: true);
        }

        ElapsedText.Visibility = classic && _handsFree ? Visibility.Collapsed : Visibility.Visible;
        ElapsedText.Style = OverlayChromeAnimator.GetPillStyle(
            readingWell ? "PillWellTimerTextStyle" : "PillCapsuleTimerTextStyle");
        ElapsedText.Padding = new Thickness(1, 0, 1, 0);
        LevelBars.Visibility = rail ? Visibility.Visible : Visibility.Collapsed;
        RailHandsFreeBadge.Visibility = rail && _handsFree ? Visibility.Visible : Visibility.Collapsed;
        WellMeter.Visibility = readingWell ? Visibility.Visible : Visibility.Collapsed;
        ModeLabel.Visibility = readingWell && !_handsFree ? Visibility.Visible : Visibility.Collapsed;
        WellHandsFreeBadge.Visibility = readingWell && _handsFree ? Visibility.Visible : Visibility.Collapsed;

        HeaderLeading.Spacing = readingWell ? 12 : CapsuleHeaderGap;
        HeaderRow.ColumnSpacing = readingWell ? 8 : 0;
        HeaderRow.HorizontalAlignment = readingWell ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
        HeaderRow.Height = readingWell ? OverlayRecordingSurface.ReadingWellHeaderHeight - FrameBorderThickness * 2 : double.NaN;
        HeaderRow.Padding = readingWell ? new Thickness(16, 0, 16, 0) : new Thickness(0);
        ContentPanel.Margin = readingWell
            ? new Thickness(0)
            : new Thickness(
                OverlayRecordingSurface.CapsuleHorizontalPadding,
                _recordingNotice == RecordingNoticeKind.None ? 0 : 8,
                OverlayRecordingSurface.CapsuleHorizontalPadding,
                _recordingNotice == RecordingNoticeKind.None ? 0 : 6);
        ContentPanel.Spacing = _recordingNotice == RecordingNoticeKind.None ? 0 : 4;
        ContentPanel.VerticalAlignment = readingWell ? VerticalAlignment.Top : VerticalAlignment.Center;

        _chrome.ApplySurface(
            readingWell ? "PillWellSurfaceStyle" : "PillCapsuleSurfaceStyle",
            readingWell ? ReadingWellRadius : RecordingCapsuleRadius);

        _chrome.ApplyHairline(visible: true, breathes: !readingWell, distress: false);
        ApplyInPanelNotice(_recordingNotice, readingWell);
        PreviewText.Text = _previewText ?? string.Empty;

        switch (_activeDesign)
        {
            case RecordingPillDesign.Classic:
                PreviewWell.Visibility = Visibility.Collapsed;
                OverlayRoot.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                if (_recordingNotice == RecordingNoticeKind.None)
                {
                    Resize(CapsulePillWidth(), RecordingCapsuleHeight);
                }
                else
                {
                    Resize(
                        Math.Max(CapsulePillWidth(), InPanelNoticeMinimumWidth()),
                        RecordingCapsuleHeight + InPanelNoticeExtraHeight());
                }

                OverlayRoot.UpdateLayout();
                break;
            case RecordingPillDesign.LevelRail:
                PreviewWell.Visibility = Visibility.Collapsed;
                OverlayRoot.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                if (_recordingNotice == RecordingNoticeKind.None)
                {
                    Resize(LevelRailPillWidth(), RecordingCapsuleHeight);
                }
                else
                {
                    Resize(
                        Math.Max(LevelRailPillWidth(), InPanelNoticeMinimumWidth()),
                        RecordingCapsuleHeight + InPanelNoticeExtraHeight());
                }

                OverlayRoot.UpdateLayout();
                break;
            case RecordingPillDesign.ReadingWell:
                ResizeReadingWell();
                OverlayRoot.UpdateLayout();
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        _recordingSurface.DrawLevelHistory(_activeDesign, _levelHistory);
    }

    private void ConfigureFloatingCard(DictationStatus status)
    {
        _chrome.SetSpectrumWheelSpinning(false);
        _chrome.ApplyHairline(visible: false, breathes: false, distress: false);

        var isBluetooth = status.State == DictationOverlayState.BluetoothCard;
        _action = status.Action;

        DeferredSurfaceHost.Visibility = Visibility.Visible;
        double measuredH;
        if (isBluetooth)
        {
            var card = EnsureBluetoothCardView();
            card.Visibility = Visibility.Visible;
            if (_polishSetupCardView is not null)
            {
                _polishSetupCardView.Visibility = Visibility.Collapsed;
            }

            measuredH = card.MeasureDesiredHeight(FloatingCardWidth);
        }
        else
        {
            var card = EnsurePolishSetupCardView();
            card.Visibility = Visibility.Visible;
            if (_bluetoothCardView is not null)
            {
                _bluetoothCardView.Visibility = Visibility.Collapsed;
            }

            card.Configure(
                string.IsNullOrWhiteSpace(status.Text) ? "Polish needs setup." : status.Text,
                string.IsNullOrWhiteSpace(status.Subtitle) ? "Pasted without AI polish." : status.Subtitle,
                status.Action?.Label ?? "Set up AI polish",
                status.Action?.SpokenLabel ?? "Set up AI polish");
            measuredH = card.MeasureDesiredHeight(FloatingCardWidth);
        }

        CardBottomRainbowBar.Visibility = isBluetooth ? Visibility.Visible : Visibility.Collapsed;

        _chrome.ApplySurface("PillCardSurfaceStyle", CardCornerRadius);
        _chrome.ApplySeverity(status.State, _activeDesign, StateIcon);

        var width = FloatingCardWidth + (int)(FrameBorderThickness * 2);
        var height = (int)Math.Ceiling(measuredH + FrameBorderThickness * 2 + (isBluetooth ? 2 : 0));

        Resize(width, height);
        OverlayRoot.UpdateLayout();
    }

    private void ConfigureNotice(DictationStatus status, string title, bool isSaveError)
    {
        var state = status.State;
        LipsMark.Visibility = Visibility.Collapsed;
        LipsMark.Width = HoldLipsSize;
        LipsMark.Height = HoldLipsSize;
        LipsMarkScale.ScaleX = LipsMarkScale.ScaleY = 1;
        ElapsedText.Visibility = Visibility.Collapsed;
        LevelBars.Visibility = Visibility.Collapsed;
        WellMeter.Visibility = Visibility.Collapsed;
        RailHandsFreeBadge.Visibility = Visibility.Collapsed;
        ModeLabel.Visibility = Visibility.Collapsed;
        WellHandsFreeBadge.Visibility = Visibility.Collapsed;
        PreviewWell.Visibility = Visibility.Collapsed;
        InPanelNoticeRow.Visibility = Visibility.Collapsed;
        HeaderLeading.Spacing = CapsuleHeaderGap;
        HeaderRow.HorizontalAlignment = HorizontalAlignment.Stretch;
        HeaderRow.Height = double.NaN;
        HeaderRow.Padding = new Thickness(0);
        ContentPanel.VerticalAlignment = VerticalAlignment.Center;

        if (state == DictationOverlayState.Processing)
        {
            HeaderLeading.Visibility = Visibility.Visible;
            StateIcon.Visibility = Visibility.Collapsed;
            SpectrumWheelIcon.Visibility = Visibility.Visible;
            SetLiveVisibility(StateTitle, Visibility.Collapsed);
            StateSubtitle.Visibility = Visibility.Collapsed;
            ProcessingLabel.Text = title;
            ProcessingLabel.Visibility = Visibility.Visible;
            HeaderRow.ColumnSpacing = 0;
            HeaderRow.HorizontalAlignment = HorizontalAlignment.Center;
            ContentPanel.Spacing = 0;
            ContentPanel.Margin = new Thickness(
                OverlayRecordingSurface.CapsuleHorizontalPadding,
                0,
                OverlayRecordingSurface.CapsuleHorizontalPadding,
                0);
            _chrome.ApplySurface("PillCapsuleSurfaceStyle", RecordingCapsuleRadius);
            _chrome.ApplyHairline(visible: true, breathes: true, distress: false);
            Resize(ProcessingPillWidth(), RecordingCapsuleHeight);
            _chrome.SetSpectrumWheelSpinning(true);
            return;
        }

        ProcessingLabel.Visibility = Visibility.Collapsed;

        var isRichError = !isSaveError && state == DictationOverlayState.Error && status.ErrorVariant != RichErrorVariant.None;
        var isEscapeRecovery = state == DictationOverlayState.EscapeRecovery;
        var hasSubtitle = !string.IsNullOrWhiteSpace(status.Subtitle) && (isRichError || state == DictationOverlayState.ColdStart);

        var showWheel = state is DictationOverlayState.Success or DictationOverlayState.ColdStart;
        var showDistressLips = state == DictationOverlayState.Distress;
        var showWarningGlyph = !isSaveError && state is DictationOverlayState.Warning or DictationOverlayState.Busy;
        var showErrorCircle = !isSaveError && !isRichError && state == DictationOverlayState.Error;
        var showReadyCircle = state == DictationOverlayState.EngineReady;
        var showFontIcon = !isSaveError && !showWheel && !showDistressLips && !showWarningGlyph && !showErrorCircle && !showReadyCircle && !isRichError && !isEscapeRecovery;

        SpectrumWheelIcon.Visibility = showWheel ? Visibility.Visible : Visibility.Collapsed;
        _chrome.SetSpectrumWheelSpinning(showWheel);

        if (showDistressLips)
        {
            _recordingSurface.ApplyLipsColoring(distress: true);
            _recordingSurface.DrawLips(0.15f, force: true);
            LipsMark.Visibility = Visibility.Visible;
        }
        else
        {
            _recordingSurface.ApplyLipsColoring(distress: false);
        }

        WarningGlyphBadge.Visibility = showWarningGlyph ? Visibility.Visible : Visibility.Collapsed;
        ErrorCircleBadge.Visibility = showErrorCircle ? Visibility.Visible : Visibility.Collapsed;
        ReadyCircleBadge.Visibility = showReadyCircle ? Visibility.Visible : Visibility.Collapsed;
        RichErrorBadge.Visibility = isRichError ? Visibility.Visible : Visibility.Collapsed;
        if (isRichError)
        {
            RichErrorGlyph.Glyph = status.ErrorVariant == RichErrorVariant.PermissionDenied ? "\uF781" : "\uE720";
        }

        StateIcon.Visibility = showFontIcon ? Visibility.Visible : Visibility.Collapsed;
        if (showFontIcon && state == DictationOverlayState.Advisory)
        {
            StateIcon.Style = OverlayChromeAnimator.GetPillStyle("PillMutedIconStyle");
        }

        HeaderLeading.Visibility = (showWheel || showDistressLips || showWarningGlyph || showErrorCircle || showReadyCircle || showFontIcon || isRichError)
            ? Visibility.Visible
            : Visibility.Collapsed;

        StateTitle.Style = isSaveError
            ? OverlayChromeAnimator.GetPillStyle("PillSaveErrorTextStyle")
            : state == DictationOverlayState.EscapeRecovery
                ? OverlayChromeAnimator.GetPillStyle("PillEscapeTextStyle")
                : state == DictationOverlayState.Distress
                    ? OverlayChromeAnimator.GetPillStyle("PillDistressMessageTextStyle")
                    : OverlayChromeAnimator.GetPillStyle("PillCapsuleTimerTextStyle");

        StateTitle.TextWrapping = isEscapeRecovery || isSaveError ? TextWrapping.NoWrap : TextWrapping.Wrap;
        StateTitle.MaxLines = isEscapeRecovery || isSaveError ? 1 : 0;
        SetLiveVisibility(StateTitle, Visibility.Visible);

        if (hasSubtitle)
        {
            StateSubtitle.Text = status.Subtitle!;
            StateSubtitle.Visibility = Visibility.Visible;
        }
        else
        {
            StateSubtitle.Text = string.Empty;
            StateSubtitle.Visibility = Visibility.Collapsed;
        }

        LeftSpectrumStripe.Visibility = isRichError ? Visibility.Visible : Visibility.Collapsed;

        var horizontalPad = isRichError ? 16 : OverlayRecordingSurface.CapsuleHorizontalPadding;
        HeaderRow.ColumnSpacing = 0;
        HeaderLeading.Margin = HeaderLeading.Visibility == Visibility.Visible
            ? new Thickness(0, 0, _noticeColumnSpacing, 0)
            : new Thickness(0);
        InlineActionRow.Margin = _action is not null
            ? new Thickness(_noticeColumnSpacing, 0, 0, 0)
            : new Thickness(0);
        ContentPanel.Spacing = 0;

        var maxOuterWidth = isRichError
            ? RichErrorCardWidth
            : state == DictationOverlayState.Advisory
                ? AdvisoryNoticeMaxWidth
                : state == DictationOverlayState.Suggestion
                    ? LanguageChipMaxWidth
                    : isSaveError
                        ? 440
                        : isEscapeRecovery
                            ? 360
                            : StandardNoticeMaxWidth;

        var leadingReserved = isRichError
            ? 52d
            : HeaderLeading.Visibility == Visibility.Visible
                ? Math.Max(26d, MeasureElementWidth(HeaderLeading, 20d) + _noticeColumnSpacing)
                : 0d;
        var actionReserved = 0d;
        var actionHeight = 0d;
        if (_action is not null)
        {
            InlineActionRow.InvalidateMeasure();
            InlineActionRow.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            actionReserved = Math.Max(70d, InlineActionRow.DesiredSize.Width) + _noticeColumnSpacing;
            actionHeight = InlineActionRow.DesiredSize.Height;
        }

        var maxTextWidth = Math.Max(110d, maxOuterWidth - horizontalPad * 2 - FrameBorderThickness * 2 - leadingReserved - actionReserved);
        StateTitle.MaxWidth = maxTextWidth;
        StateSubtitle.MaxWidth = maxTextWidth;

        StateTitle.InvalidateMeasure();
        StateTitle.Measure(new Windows.Foundation.Size(maxTextWidth, double.PositiveInfinity));
        StateSubtitle.InvalidateMeasure();
        if (hasSubtitle)
        {
            StateSubtitle.Measure(new Windows.Foundation.Size(maxTextWidth, double.PositiveInfinity));
        }

        var textW = Math.Max(StateTitle.DesiredSize.Width, hasSubtitle ? StateSubtitle.DesiredSize.Width : 0);
        var computedWidth = (int)Math.Ceiling(FrameBorderThickness * 2 + horizontalPad * 2 + leadingReserved + textW + actionReserved + (isRichError ? 6 : 8));
        var width = isRichError
            ? RichErrorCardWidth
            : Math.Clamp(computedWidth, isEscapeRecovery ? 240 : 180, maxOuterWidth);

        var finalTextWidth = Math.Max(100d, width - FrameBorderThickness * 2 - horizontalPad * 2 - leadingReserved - actionReserved - (isRichError ? 6 : 2));
        StateTitle.MaxWidth = finalTextWidth;
        StateSubtitle.MaxWidth = finalTextWidth;
        StateTitle.InvalidateMeasure();
        StateTitle.Measure(new Windows.Foundation.Size(finalTextWidth, double.PositiveInfinity));
        if (hasSubtitle)
        {
            StateSubtitle.InvalidateMeasure();
            StateSubtitle.Measure(new Windows.Foundation.Size(finalTextWidth, double.PositiveInfinity));
        }

        var textH = StateTitle.DesiredSize.Height + (hasSubtitle ? StateSubtitle.DesiredSize.Height + 2 : 0);
        var isMultiLine = textH > 26;
        var verticalPad = isEscapeRecovery ? 6 : (isRichError || hasSubtitle || isMultiLine ? 12 : 9);

        ContentPanel.Margin = new Thickness(
            isRichError ? horizontalPad + 2 : horizontalPad,
            verticalPad,
            horizontalPad,
            isEscapeRecovery ? verticalPad + 2 : verticalPad);

        var contentHeight = Math.Max(textH, Math.Max(actionHeight, isRichError ? 40 : 24));
        var height = isEscapeRecovery
            ? Math.Max(46, (int)Math.Ceiling(FrameBorderThickness * 2 + verticalPad * 2 + 2 + contentHeight))
            : Math.Max(RecordingCapsuleHeight, (int)Math.Ceiling(FrameBorderThickness * 2 + verticalPad * 2 + contentHeight));

        var cornerRadius = isRichError || height > 58 ? NoticeCapsuleRadius : height / 2.0;

        _chrome.ApplySurface(
            isEscapeRecovery ? "PillEscapeSurfaceStyle" : "PillCapsuleSurfaceStyle",
            cornerRadius);

        _chrome.ApplyHairline(
            visible: !isEscapeRecovery,
            breathes: state == DictationOverlayState.Success || state == DictationOverlayState.ColdStart,
            distress: state == DictationOverlayState.Distress);

        Resize(width, height);
        if (isEscapeRecovery)
        {
            EscapeCountdownRail.Width = TrackWidthForLogicalWidth(width);
        }

        OverlayRoot.UpdateLayout();
    }

    private LearnedCorrectionPillView RenderLearnedPill(LearnedCorrectionPillModel model, bool announceNow)
    {
        DeactivateSurfaces();
        var view = EnsureLearnedPillView();
        DeferredSurfaceHost.Visibility = Visibility.Visible;
        view.Visibility = Visibility.Visible;

        _chrome.ApplySurface("PillLearnedSurfaceStyle", LearnedCapsuleRadius);
        _chrome.ApplySeverity(DictationOverlayState.CorrectionLearned, _activeDesign, StateIcon);

        var titleStyle = model.Phase == LearnedCorrectionPillPhase.UndoError
            ? OverlayChromeAnimator.GetPillStyle("PillLearnedErrorTitleStyle")
            : OverlayChromeAnimator.GetPillStyle("PillLearnedTitleStyle");

        var (showRail, width) = view.Render(model, titleStyle, announceNow);
        var height = (int)Math.Ceiling(LearnedPillHeight);
        Resize(width, height);

        LearnedCountdownRail.Visibility = showRail ? Visibility.Visible : Visibility.Collapsed;
        LearnedCountdownRail.Width = TrackWidthForLogicalWidth(width);
        UpdateLearnedRailScale(0);
        OverlayRoot.UpdateLayout();
        return view;
    }

    private void ApplyInPanelNotice(RecordingNoticeKind notice, bool readingWell)
    {
        if (notice == RecordingNoticeKind.None)
        {
            InPanelNoticeRow.Visibility = Visibility.Collapsed;
            return;
        }

        InPanelNoticeText.Text = OverlayPillCatalog.FormatRecordingNoticeText(notice);
        InPanelNoticeText.Style = OverlayChromeAnimator.GetPillStyle(
            readingWell ? "PillWellModeTextStyle" : "PillNoticeTextStyle");
        InPanelNoticeRow.Visibility = Visibility.Visible;
    }

    private int InPanelNoticeExtraHeight()
    {
        if (_recordingNotice == RecordingNoticeKind.None)
        {
            return 0;
        }

        InPanelNoticeRow.InvalidateMeasure();
        InPanelNoticeRow.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return (int)Math.Ceiling(Math.Max(28, InPanelNoticeRow.DesiredSize.Height + 12));
    }

    private int InPanelNoticeMinimumWidth()
    {
        if (_recordingNotice == RecordingNoticeKind.None)
        {
            return 0;
        }

        InPanelNoticeRow.InvalidateMeasure();
        InPanelNoticeRow.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var inner = Math.Max(236, InPanelNoticeRow.DesiredSize.Width + 16);
        return (int)Math.Ceiling(FrameBorderThickness * 2 + OverlayRecordingSurface.CapsuleHorizontalPadding * 2 + inner);
    }

    private void ApplyAction(PillAction? action, PillAction? secondaryAction = null)
    {
        _action = action;
        if (action is null)
        {
            InlineActionRow.Visibility = Visibility.Collapsed;
            InlineActionButton.Visibility = Visibility.Collapsed;
            InlineActionButton.Content = null;
            InlineDismissButton.Visibility = Visibility.Collapsed;
            return;
        }

        InlineActionRow.Visibility = Visibility.Visible;
        InlineActionButton.Content = action.Label;
        InlineActionButton.Style = _state switch
        {
            DictationOverlayState.EscapeRecovery => OverlayChromeAnimator.GetPillStyle("PillEscapeUndoButtonStyle"),
            DictationOverlayState.Suggestion when action.Kind == PillActionKind.LockDetectedLanguage =>
                OverlayChromeAnimator.GetPillStyle("PillLanguageLockButtonStyle"),
            _ => OverlayChromeAnimator.GetPillStyle("PillInlineActionButtonStyle"),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(InlineActionButton, action.SpokenLabel);
        InlineActionButton.Visibility = Visibility.Visible;
        InlineActionButton.ApplyTemplate();

        var showDismiss = _state == DictationOverlayState.Suggestion || secondaryAction is not null;
        if (showDismiss)
        {
            var dismissLabel = secondaryAction?.Label ?? "Dismiss";
            InlineDismissButton.Content = dismissLabel;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                InlineDismissButton,
                secondaryAction?.SpokenLabel ?? "Dismiss suggestion");
            InlineDismissButton.Visibility = Visibility.Visible;
            InlineDismissButton.ApplyTemplate();
        }
        else
        {
            InlineDismissButton.Visibility = Visibility.Collapsed;
        }
    }

    private void ResizeReadingWell()
    {
        var (width, height) = _recordingSurface.ComputeReadingWellSize(_previewText, InPanelNoticeExtraHeight());
        Resize(width, height);
        if (_state == DictationOverlayState.Recording)
        {
            _win32Host.PositionOnForegroundMonitor(_position);
        }
    }

    private void Resize(int width, int height) =>
        _win32Host.Resize(width, height, FrameBorder.CornerRadius.TopLeft);

    private static double TrackWidthForLogicalWidth(int logicalWidth) =>
        Math.Max(0, logicalWidth - 40 - FrameBorderThickness * 2);

    private void UpdateLearnedRailScale(double fraction) =>
        LearnedCountdownScale.ScaleX = Math.Clamp(fraction, 0, 1);

    private void UpdateEscapeRailScale(double remainingFraction) =>
        EscapeCountdownScale.ScaleX = Math.Clamp(remainingFraction, 0, 1);

    private void ArmDwell(DictationStatus status)
    {
        var seconds = status.DwellSeconds ?? OverlayPillCatalog.DefaultDwellSeconds(status);
        if (seconds <= 0)
        {
            return;
        }

        _expiryDriver.ArmFixedDwell(
            TimeSpan.FromSeconds(seconds),
            startPaused: _pointerIsOver,
            holdOpen: _isPreviewSession && _previewHoldOpen);
    }

    private void StartEscapeCountdown(double seconds)
    {
        EscapeCountdownRail.Visibility = Visibility.Visible;
        EscapeCountdownRail.Width = TrackWidthForLogicalWidth(_win32Host.LogicalWidth);
        _expiryDriver.ArmEscapeCountdown(
            TimeSpan.FromSeconds(seconds),
            startPaused: _pointerIsOver,
            holdOpen: _isPreviewSession && _previewHoldOpen);
    }

    private void FinishLearnedPresentation(bool notifyEnded)
    {
        var endedModel = _learnedModel;
        _learnedModel = null;
        if (notifyEnded && endedModel is not null)
        {
            LearnedEnded?.Invoke(endedModel.PillId);
        }
    }

    private void AccumulatePeak(float level)
    {
        var current = Volatile.Read(ref _latestLevel);
        while (level > current)
        {
            var seen = Interlocked.CompareExchange(ref _latestLevel, level, current);
            if (seen.Equals(current))
            {
                return;
            }

            current = seen;
        }
    }

    private void ResetLevelHistory()
    {
        _levelHistory.Reset();
        Volatile.Write(ref _latestLevel, 0f);
        _recordingSurface.ResetLevelVisuals(_levelHistory);
    }

    private string RecordingSpokenName() =>
        $"{RecordingPillCatalog.DisplayName(_activeDesign)} recording pill. {(_handsFree ? "Hands-free." : "Listening.")}";

    private void AnnounceRecording(string spokenName)
    {
        if (_activeDesign == RecordingPillDesign.ReadingWell && !_handsFree)
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ModeLabel, spokenName);
            AnnounceStateChange(ModeLabel, DictationOverlayState.Recording);
        }
        else if (_activeDesign == RecordingPillDesign.Classic && _handsFree)
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(LiveAnnouncementText, spokenName);
            AnnounceStateChange(LiveAnnouncementText, DictationOverlayState.Recording);
        }
        else
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ElapsedText, spokenName);
            AnnounceStateChange(ElapsedText, DictationOverlayState.Recording);
        }
    }

    private static double MeasureElementWidth(FrameworkElement element, double fallback)
    {
        element.InvalidateMeasure();
        element.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return element.DesiredSize.Width > 0 ? element.DesiredSize.Width : fallback;
    }

    private double ElapsedTextFallbackWidth() =>
        Math.Max(31.0, (string.IsNullOrEmpty(ElapsedText.Text) ? 4 : ElapsedText.Text.Length) * 8.0);

    private int CapsulePillWidth()
    {
        var markWidth = LipsMark.Width;
        var contentWidth = _handsFree
            ? markWidth
            : markWidth + CapsuleHeaderGap + MeasureElementWidth(ElapsedText, ElapsedTextFallbackWidth()) + 4.0;
        return (int)Math.Ceiling(FrameBorderThickness * 2 + OverlayRecordingSurface.CapsuleHorizontalPadding * 2 + contentWidth);
    }

    private int LevelRailPillWidth()
    {
        var scale = _win32Host.RasterScale > 0 ? _win32Host.RasterScale : 1.0;
        var roundedRailWidth = 24.0 * (Math.Ceiling(3.0 * scale) / scale)
            + 23.0 * (Math.Ceiling(2.0 * scale) / scale);
        var elapsedWidth = Math.Max(
            ElapsedTextFallbackWidth(),
            MeasureElementWidth(ElapsedText, ElapsedTextFallbackWidth()));
        var railWidth = Math.Max(
            Math.Max(OverlayRecordingSurface.LevelRailBarsWidth, roundedRailWidth),
            MeasureElementWidth(LevelBars, OverlayRecordingSurface.LevelRailBarsWidth));
        var computedWidth = elapsedWidth + CapsuleHeaderGap + railWidth;
        if (_handsFree)
        {
            computedWidth += CapsuleHeaderGap + MeasureElementWidth(RailHandsFreeBadge, 76.0);
        }

        HeaderLeading.InvalidateMeasure();
        var leadingWidth = MeasureElementWidth(HeaderLeading, computedWidth);
        var contentWidth = Math.Max(computedWidth, leadingWidth) + 8.0;
        return (int)Math.Ceiling(FrameBorderThickness * 2 + OverlayRecordingSurface.CapsuleHorizontalPadding * 2 + contentWidth);
    }

    private int ProcessingPillWidth()
    {
        var label = MeasureElementWidth(ProcessingLabel, ProcessingLabel.Text.Length * 7.0);
        return (int)Math.Ceiling(ProcessingChromeWidth + label);
    }

    private static void SetLiveVisibility(TextBlock region, Visibility visibility)
    {
        if (region.Visibility == visibility)
        {
            return;
        }

        region.Visibility = visibility;
        var peer = FrameworkElementAutomationPeer.FromElement(region)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(region);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private static void AnnounceStateChange(TextBlock region, DictationOverlayState state)
    {
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(
            region,
            state is DictationOverlayState.Error or DictationOverlayState.Distress
                ? AutomationLiveSetting.Assertive
                : AutomationLiveSetting.Polite);

        var peer = FrameworkElementAutomationPeer.FromElement(region)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(region);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private LearnedCorrectionPillView EnsureLearnedPillView()
    {
        if (_learnedPillView is not null)
        {
            return _learnedPillView;
        }

        var view = new LearnedCorrectionPillView();
        view.UndoClicked += OnLearnedUndoClicked;
        view.CloseClicked += OnLearnedCloseClicked;
        DeferredSurfaceHost.Children.Add(view);
        _learnedPillView = view;
        return view;
    }

    private BluetoothAwarenessCardView EnsureBluetoothCardView()
    {
        if (_bluetoothCardView is not null)
        {
            return _bluetoothCardView;
        }

        var view = new BluetoothAwarenessCardView();
        view.DismissClicked += OnCardDismissClicked;
        view.AdjustSettingsClicked += OnCardActionClicked;
        DeferredSurfaceHost.Children.Add(view);
        _bluetoothCardView = view;
        return view;
    }

    private PolishSetupCardView EnsurePolishSetupCardView()
    {
        if (_polishSetupCardView is not null)
        {
            return _polishSetupCardView;
        }

        var view = new PolishSetupCardView();
        view.DismissClicked += OnCardDismissClicked;
        view.SetupActionClicked += OnCardActionClicked;
        DeferredSurfaceHost.Children.Add(view);
        _polishSetupCardView = view;
        return view;
    }

    private void OnOverlayRootActualThemeChanged(FrameworkElement sender, object args)
    {
        _recordingSurface.ApplySpectrumSweep();
        _recordingSurface.ApplyLipsColoring(distress: _state == DictationOverlayState.Distress);
    }

    private void OnOverlayRootLoaded(object sender, RoutedEventArgs e)
    {
        if (OverlayRoot.XamlRoot is not { } root)
        {
            return;
        }

        if (!ReferenceEquals(_observedXamlRoot, root))
        {
            if (_observedXamlRoot is not null)
            {
                _observedXamlRoot.Changed -= OnXamlRootChanged;
            }

            _observedXamlRoot = root;
            root.Changed += OnXamlRootChanged;
        }

        if (_win32Host.ApplyScale(root.RasterizationScale, FrameBorder.CornerRadius.TopLeft))
        {
            _win32Host.PositionOnForegroundMonitor(_position);
        }
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (_win32Host.ApplyScale(sender.RasterizationScale, FrameBorder.CornerRadius.TopLeft))
        {
            _win32Host.PositionOnForegroundMonitor(_position);
        }
    }

    private void OnElapsedTimerTick(object? sender, object e)
    {
        var elapsed = DateTimeOffset.UtcNow - _recordingStartedAt;
        var formatted = RecordingPillGeometry.FormatElapsed(elapsed);
        var showCapNotice = elapsed >= OverlayPillCatalog.ApproachingCapElapsedThreshold && !_autoCapNoticeShown;
        if (showCapNotice)
        {
            _autoCapNoticeShown = true;
            _recordingNotice = RecordingNoticeKind.ApproachingCap;
            ConfigureRecordingDesign();
            _win32Host.PositionOnForegroundMonitor(_position);
            return;
        }

        if (string.Equals(ElapsedText.Text, formatted, StringComparison.Ordinal))
        {
            return;
        }

        ElapsedText.Text = formatted;
        if (_state == DictationOverlayState.Recording
            && (_activeDesign == RecordingPillDesign.LevelRail
             || (_activeDesign == RecordingPillDesign.Classic && !_handsFree)))
        {
            var width = _activeDesign == RecordingPillDesign.Classic
                ? Math.Max(CapsulePillWidth(), InPanelNoticeMinimumWidth())
                : Math.Max(LevelRailPillWidth(), InPanelNoticeMinimumWidth());
            if (width != _win32Host.LogicalWidth)
            {
                Resize(width, RecordingCapsuleHeight + InPanelNoticeExtraHeight());
                _win32Host.PositionOnForegroundMonitor(_position);
            }
        }
    }

    private void OnLevelTimerTick(object? sender, object e)
    {
        if (_state != DictationOverlayState.Recording || _isPreviewSession)
        {
            return;
        }

        var framePeak = Interlocked.Exchange(ref _latestLevel, 0f);
        if (!_levelHistory.Sample(framePeak, _levelClock.Elapsed))
        {
            AccumulatePeak(framePeak);
            return;
        }

        if (LipsMark.Visibility == Visibility.Visible)
        {
            _recordingSurface.DrawLips(framePeak);
        }

        _recordingSurface.DrawLevelHistory(_activeDesign, _levelHistory);
    }

    private void OnExpiryRailProgressChanged(OverlayExpiryMode mode, double fraction)
    {
        if (mode == OverlayExpiryMode.CountdownResetOnHover)
        {
            UpdateEscapeRailScale(fraction);
        }
        else if (mode == OverlayExpiryMode.CountdownAccumulateWithFloor)
        {
            UpdateLearnedRailScale(fraction);
        }
    }

    private void OnExpiryExpired()
    {
        if (_state == DictationOverlayState.CorrectionLearned
            && _learnedModel?.Phase == LearnedCorrectionPillPhase.UndoPending)
        {
            return;
        }

        HideOverlay();
    }

    private void OnLearnedUndoClicked()
    {
        if (_learnedModel is not { Phase: LearnedCorrectionPillPhase.Learned } model)
        {
            return;
        }

        if (_isPreviewSession)
        {
            PreviewActionPressed?.Invoke("Action pressed: Undo (preview only)");
            return;
        }

        _learnedModel = model with { Phase = LearnedCorrectionPillPhase.UndoPending };
        RenderLearnedPill(_learnedModel, announceNow: true);
        _expiryDriver.EnterUndoPending(TimeSpan.FromSeconds(OverlayPillCatalog.LearnedUndoPendingFloorSeconds));
        LearnedUndoRequested?.Invoke(model.PillId);
    }

    private void OnLearnedCloseClicked()
    {
        if (_learnedModel is not { } model)
        {
            return;
        }

        if (_isPreviewSession)
        {
            PreviewActionPressed?.Invoke("Action pressed: Close learned pill (preview only)");
            return;
        }

        LearnedDismissed?.Invoke(model.PillId);
        HideOverlay();
    }

    private void OnCardDismissClicked(string label)
    {
        if (_isPreviewSession)
        {
            PreviewActionPressed?.Invoke($"Action pressed: {label} (preview only)");
            return;
        }

        HideOverlay();
    }

    private void OnCardActionClicked()
    {
        if (_action is not { } action)
        {
            return;
        }

        if (_isPreviewSession)
        {
            PreviewActionPressed?.Invoke($"Action pressed: {action.Label} (preview only)");
            return;
        }

        _action = null;
        HideOverlay();
        ActionInvoked?.Invoke(action.Kind);
    }

    private void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_action is not { } action)
        {
            return;
        }

        if (_isPreviewSession)
        {
            PreviewActionPressed?.Invoke($"Action pressed: {action.Label} (preview only)");
            return;
        }

        _action = null;
        HideOverlay();
        ActionInvoked?.Invoke(action.Kind);
    }

    private void InlineDismissButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isPreviewSession)
        {
            PreviewActionPressed?.Invoke("Action pressed: Dismiss (preview only)");
            return;
        }

        HideOverlay();
    }

    private void OverlayRoot_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _pointerIsOver = true;
        if (_state == DictationOverlayState.CorrectionLearned
            && _learnedModel?.Phase != LearnedCorrectionPillPhase.Learned)
        {
            return;
        }

        _expiryDriver.PauseForHover();
    }

    private void OverlayRoot_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointerIsOver = false;
        if (_state == DictationOverlayState.CorrectionLearned
            && _learnedModel?.Phase != LearnedCorrectionPillPhase.Learned)
        {
            return;
        }

        _expiryDriver.ResumeFromHover(TimeSpan.FromSeconds(OverlayPillCatalog.LearnedHoverResumeFloorSeconds));
    }

    private void OverlayRoot_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(null);
        if (!point.Properties.IsLeftButtonPressed || !OverlayRoot.CapturePointer(e.Pointer))
        {
            return;
        }

        _win32Host.BeginDrag(e.Pointer.PointerId);
    }

    private void OverlayRoot_PointerMoved(object sender, PointerRoutedEventArgs e) =>
        _win32Host.TryMoveDrag(e.Pointer.PointerId);

    private void OverlayRoot_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_win32Host.EndDrag(e.Pointer.PointerId))
        {
            OverlayRoot.ReleasePointerCapture(e.Pointer);
        }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        Cleanup();
    }

    private void Cleanup()
    {
        if (_cleanedUp)
        {
            return;
        }

        _cleanedUp = true;
        _elapsedTimer.Stop();
        _elapsedTimer.Tick -= OnElapsedTimerTick;
        _levelTimer.Stop();
        _levelTimer.Tick -= OnLevelTimerTick;
        _expiryDriver.RailProgressChanged -= OnExpiryRailProgressChanged;
        _expiryDriver.Expired -= OnExpiryExpired;
        _expiryDriver.Dispose();
        _chrome.StopAll();

        OverlayRoot.ActualThemeChanged -= OnOverlayRootActualThemeChanged;
        OverlayRoot.Loaded -= OnOverlayRootLoaded;
        if (_observedXamlRoot is not null)
        {
            _observedXamlRoot.Changed -= OnXamlRootChanged;
            _observedXamlRoot = null;
        }

        Closed -= OnWindowClosed;

        if (_learnedPillView is not null)
        {
            _learnedPillView.UndoClicked -= OnLearnedUndoClicked;
            _learnedPillView.CloseClicked -= OnLearnedCloseClicked;
            _learnedPillView.Detach();
        }

        if (_bluetoothCardView is not null)
        {
            _bluetoothCardView.DismissClicked -= OnCardDismissClicked;
            _bluetoothCardView.AdjustSettingsClicked -= OnCardActionClicked;
            _bluetoothCardView.Detach();
        }

        if (_polishSetupCardView is not null)
        {
            _polishSetupCardView.DismissClicked -= OnCardDismissClicked;
            _polishSetupCardView.SetupActionClicked -= OnCardActionClicked;
            _polishSetupCardView.Detach();
        }

        _win32Host.Dispose();
    }
}
