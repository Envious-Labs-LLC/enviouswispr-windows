using System.Runtime.InteropServices;
using EnviousWispr.Core.Learning;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Settings;
using EnviousWispr.Services.Lifecycle;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;

namespace EnviousWispr.App;

public sealed partial class DictationOverlayWindow : Window
{
    private const uint MonitorDefaultToNearest = 2;

    /// <summary>Classic, Level Rail, Processing and single-line notices: 44 tall capsules.</summary>
    private const int RecordingCapsuleHeight = 44;

    private const double RecordingCapsuleRadius = 22;
    private const double ReadingWellRadius = 16;
    private const double NoticeCapsuleRadius = 24;
    private const double CardCornerRadius = 18;
    private const int CapsuleHorizontalPadding = 14;
    private const int CapsuleDefaultWidth = 104;
    private const double CapsuleHeaderGap = 10;
    private const double HoldLipsSize = 24;
    private const double HandsFreeLipsSize = 36;
    private const double LevelRailBarsWidth = 118;
    private const int ReadingWellWidth = 400;

    /// <summary>The Reading Well's header, and the whole pill while it waits for words.</summary>
    private const int ReadingWellHeaderHeight = 36;

    /// <summary>The 1px edge thickness of <c>FrameBorder</c>, subtracted where inner heights or widths are exact.</summary>
    private const double FrameBorderThickness = 1;

    /// <summary>Bleed margin around <c>FrameBorder</c> so <c>SetWindowRgn</c> clips only the outer edge, not the border or hairline.</summary>
    private const int WindowBleed = 2;

    /// <summary>The well's own inset around the transcript: 16 either side, 12 above, 15 below.</summary>
    private static readonly Thickness ReadingWellInsets = new(16, 12, 16, 15);

    /// <summary>One transcript line: the 14pt face plus macOS's 4pt line spacing.</summary>
    private const double PreviewLineHeight = 21;

    /// <summary>Five lines, then the well holds its size and the oldest words leave at the top.</summary>
    private const int PreviewMaxLines = 5;

    /// <summary>How much of a full well the top fade covers. RecordingOverlayView.previewFadeFraction.</summary>
    private const double PreviewFadeFraction = 0.22;

    /// <summary>Processing's border, inset, wheel and gap: 14 + 24 + 10 before the label, 14 after it, plus 2 for the frame border.</summary>
    private const double ProcessingChromeWidth = 14 + 24 + 10 + 14 + FrameBorderThickness * 2;

    private const int StandardNoticeMaxWidth = 320;
    private const int LanguageChipMaxWidth = 340;
    private const int AdvisoryNoticeMaxWidth = 360;
    private const int RichErrorCardWidth = 400;
    private const int FloatingCardWidth = 320;
    private const int WorkAreaMargin = 28;

    private static readonly TimeSpan ApproachingCapElapsedThreshold = TimeSpan.FromSeconds(280);

    private readonly DispatcherTimer _hideTimer = new();
    private readonly DispatcherTimer _elapsedTimer = new();
    private readonly DispatcherTimer _learnedTimer = new();
    private readonly DispatcherTimer _escapeTimer = new();
    private readonly System.Diagnostics.Stopwatch _learnedWatch = new();
    private readonly System.Diagnostics.Stopwatch _escapeWatch = new();
    private LearnedCorrectionPillModel? _learnedModel;
    private TimeSpan _learnedDuration = TimeSpan.FromSeconds(12);
    private double _learnedElapsedBaseMs;
    private TimeSpan _escapeDuration = TimeSpan.FromSeconds(3);
    private const double LearnedCapsuleRadius = 32;
    private const double LearnedPillHeight = 64;
    private const double LearnedDwellSeconds = 12.0;
    private const double LearnedHoverResumeFloorSeconds = 2.0;
    private const double LearnedUndoPendingFloorSeconds = 3.0;
    private const double LearnedUndoneDwellSeconds = 1.5;
    private const double LearnedErrorDwellSeconds = 3.0;
    private const double EscapeRecoveryDwellSeconds = 3.0;
    private readonly Storyboard _distressPulse = new();
    private readonly Storyboard _spectrumWheelPulse = new();
    private readonly Storyboard _hairlineBreath = new();

    private bool _handsFree;
    private RecordingNoticeKind _recordingNotice;
    private bool _autoCapNoticeShown;
    private readonly double _noticeColumnSpacing;
    private readonly double _noticeContentSpacing;
    private TimeSpan _dwell = TimeSpan.Zero;
    private bool _pointerIsOver;
    private OverlayPillPosition _position = OverlayPillPosition.Top;
    private bool _livePreviewEnabled;
    private RecordingPillDesign _withoutWordsDesign = RecordingPillDesign.Classic;
    private RecordingPillDesign _withWordsDesign = RecordingPillDesign.ReadingWell;
    private RecordingPillDesign _activeDesign = RecordingPillDesign.Classic;

    private bool _isPreviewSession;
    private bool _previewHoldOpen;
    private RecordingPillDesign? _previewDesignOverride;

    private readonly RecordingLevelHistory _levelHistory = new();
    private float _latestLevel;
    private float _lastDrawnLevel;

    private readonly DispatcherTimer _levelTimer = new();
    private readonly System.Diagnostics.Stopwatch _levelClock = System.Diagnostics.Stopwatch.StartNew();
    private DictationOverlayState _state = DictationOverlayState.Hidden;
    private DictationStatus _currentStatus = DictationStatus.Quiet(string.Empty);
    private DateTimeOffset _recordingStartedAt;
    private PillAction? _action;
    private string? _previewText;
    private int _overlayWidth = CapsuleDefaultWidth;
    private int _overlayHeight = RecordingCapsuleHeight;
    private int _logicalWidth = CapsuleDefaultWidth;
    private int _logicalHeight = RecordingCapsuleHeight;

    private uint? _dragPointer;
    private PointInt32 _dragFromWindow;
    private NativePoint _dragFromCursor;
    private bool _draggedThisPresentation;
    private bool _preWarmed;
    private double _rasterScale;

    private const uint WmGetMinMaxInfo = 0x0024;
    private const uint WmNcDestroy = 0x0082;
    private const nuint OverlaySubclassId = 0x45573335;

    private delegate nint SubclassProc(
        nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData);

    private SubclassProc? _subclassProc;
    private nint _subclassedHandle;

    private const uint SpiGetClientAreaAnimation = 0x1042;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref bool pvParam, uint fWinIni);

    private static bool AnimationsEnabled
    {
        get
        {
            try
            {
                var enabled = true;
                if (SystemParametersInfo(SpiGetClientAreaAnimation, 0, ref enabled, 0))
                {
                    return enabled;
                }
            }
            catch
            {
            }

            return true;
        }
    }

    public DictationOverlayWindow()
    {
        InitializeComponent();
        var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = GetDpiForWindow(windowHandle);
        _rasterScale = dpi > 0 ? dpi / 96d : 1d;
        _noticeColumnSpacing = HeaderRow.ColumnSpacing;
        _noticeContentSpacing = ContentPanel.Spacing;
        var initialWidth = CapsulePillWidth();
        Resize(initialWidth, RecordingCapsuleHeight);
        DrawLips(0f);
        ApplySpectrumSweep();
        OverlayRoot.Measure(new Windows.Foundation.Size(initialWidth, RecordingCapsuleHeight));
        OverlayRoot.ActualThemeChanged += (_, _) =>
        {
            ApplySpectrumSweep();
            ApplyLipsColoring(distress: _state == DictationOverlayState.Distress);
        };
        OverlayRoot.Loaded += (_, _) =>
        {
            if (OverlayRoot.XamlRoot is not { } root)
            {
                return;
            }

            root.Changed += OnXamlRootChanged;
            ApplyScale(root.RasterizationScale);
        };

        _ = WindowsToolWindowStyle.HideFromTaskSwitchers(
            () => AppWindow.IsShownInSwitchers = false,
            windowHandle);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        }

        var borderColour = DwmColorNone;
        _ = DwmSetWindowAttribute(windowHandle, DwmwaBorderColor, ref borderColour, sizeof(uint));
        InstallMinTrackOverride(windowHandle);

        _hideTimer.Tick += (_, _) => HideOverlay();
        _elapsedTimer.Interval = TimeSpan.FromMilliseconds(250);
        _elapsedTimer.Tick += (_, _) => UpdateElapsed();
        _learnedTimer.Interval = TimeSpan.FromMilliseconds(33);
        _learnedTimer.Tick += (_, _) => TickLearnedCountdown();
        _escapeTimer.Interval = TimeSpan.FromMilliseconds(33);
        _escapeTimer.Tick += (_, _) => TickEscapeCountdown();

        _levelTimer.Interval = RecordingLevelHistory.SampleInterval / 2;
        _levelTimer.Tick += (_, _) => OnLevelTick();

        // Distress pulse (0.3 <-> 0.6 over 0.4s) matching OverlayCapsuleBackgrounds.swift.
        var distressHairlinePulse = new DoubleAnimation
        {
            From = 0.6,
            To = 0.3,
            Duration = new Duration(TimeSpan.FromMilliseconds(400)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(distressHairlinePulse, BottomDistressHairline);
        Storyboard.SetTargetProperty(distressHairlinePulse, "Opacity");
        _distressPulse.Children.Add(distressHairlinePulse);

        var distressLipsPulse = new DoubleAnimation
        {
            From = 1.0,
            To = 0.45,
            Duration = new Duration(TimeSpan.FromMilliseconds(400)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(distressLipsPulse, LipsMark);
        Storyboard.SetTargetProperty(distressLipsPulse, "Opacity");
        _distressPulse.Children.Add(distressLipsPulse);

        var spin = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = new Duration(TimeSpan.FromSeconds(8)),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Storyboard.SetTarget(spin, SpectrumWheelTransform);
        Storyboard.SetTargetProperty(spin, "Rotation");
        _spectrumWheelPulse.Children.Add(spin);

        var glow = new DoubleAnimation
        {
            From = RecordingPillGeometry.HairlineDimOpacity,
            To = RecordingPillGeometry.HairlineBrightOpacity,
            Duration = new Duration(TimeSpan.FromSeconds(2)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(glow, BottomRainbowHairline);
        Storyboard.SetTargetProperty(glow, "Opacity");
        _hairlineBreath.Children.Add(glow);
    }

    public bool IsActiveNonPreview => _state != DictationOverlayState.Hidden && !_isPreviewSession;

    public bool IsPreviewVisible => _state != DictationOverlayState.Hidden && _isPreviewSession;

    public event Action<string>? PreviewActionPressed;

    public void ApplyTheme(ElementTheme theme)
    {
        OverlayRoot.RequestedTheme = theme;
        ApplySpectrumSweep();
        ApplyLipsColoring(distress: _state == DictationOverlayState.Distress);
    }

    public void PreWarm()
    {
        if (_preWarmed || _state != DictationOverlayState.Hidden)
        {
            return;
        }

        _preWarmed = true;
        AppWindow.Move(new PointInt32(-32000, -32000));
        AppWindow.Show(activateWindow: false);
        OverlayRoot.UpdateLayout();
        AppWindow.Hide();
    }

    /// <summary>Renders an isolated preview entry from the Diagnostics Pill Preview gallery.</summary>
    public void ShowPreviewEntry(PillPreviewEntry entry, bool holdOpen)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _isPreviewSession = true;
        _previewHoldOpen = holdOpen;
        _previewDesignOverride = entry.PreviewDesign;
        _draggedThisPresentation = false;

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
        if (!string.IsNullOrWhiteSpace(entry.PreviewTranscript) &&
            previewStatus.State == DictationOverlayState.Recording &&
            _activeDesign == RecordingPillDesign.ReadingWell)
        {
            SetPreview(entry.PreviewTranscript);
        }
        else if (previewStatus.State == DictationOverlayState.Recording &&
            _activeDesign == RecordingPillDesign.LevelRail)
        {
            DrawSampleLevelRailForPreview();
        }
    }

    public void SetPreviewHoldOpen(bool holdOpen)
    {
        _previewHoldOpen = holdOpen;
        if (!_isPreviewSession)
        {
            return;
        }

        if (holdOpen)
        {
            _hideTimer.Stop();
            _escapeTimer.Stop();
            _learnedTimer.Stop();
            return;
        }

        if (_state == DictationOverlayState.EscapeRecovery)
        {
            StartEscapeCountdown();
        }
        else if (_state == DictationOverlayState.CorrectionLearned &&
            _learnedModel?.Phase == LearnedCorrectionPillPhase.Learned)
        {
            _learnedWatch.Restart();
            _learnedTimer.Start();
        }
        else
        {
            ArmDwell();
        }
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

    private void ShowStateInternal(DictationStatus status, bool isPreview)
    {
        var state = status.State;
        var detail = status.Text;
        _currentStatus = status;
        _hideTimer.Stop();
        StopEscapeCountdown();
        StopLearnedCountdown(notifyEnded: _learnedModel is not null && state != DictationOverlayState.CorrectionLearned);
        ResetExtraPanels();

        if (state == DictationOverlayState.Hidden)
        {
            _state = state;
            HideOverlay();
            return;
        }

        var previousState = _state;
        _state = state;
        _preWarmed = true;

        var presentation = ResolvePresentation(status, detail);
        StateIcon.Glyph = presentation.IconGlyph;

        if (state == DictationOverlayState.Recording)
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

            ApplyLipsColoring(distress: false);
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
        else if (state is DictationOverlayState.BluetoothCard or DictationOverlayState.PolishSetupCard)
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
            StateTitle.Text = presentation.Title;
            ApplyAction(status.Action, status.SecondaryAction);
            ConfigureNotice(status, presentation.Title, isSaveError: false);
        }

        ApplySeverity(state);

        var spokenName = ComputeSpokenName(state, status, presentation.Title);

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(StateTitle, spokenName);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ProcessingLabel, spokenName);
        PositionOnForegroundMonitor();
        AppWindow.Show(activateWindow: false);

        if (state == DictationOverlayState.Recording)
        {
            AnnounceRecording(spokenName);
        }
        else if (state == DictationOverlayState.Processing)
        {
            AnnounceStateChange(ProcessingLabel, state);
        }
        else if (state is DictationOverlayState.BluetoothCard or DictationOverlayState.PolishSetupCard)
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
            _dwell = TimeSpan.FromSeconds(status.DwellSeconds ?? EscapeRecoveryDwellSeconds);
            if (!(_isPreviewSession && _previewHoldOpen))
            {
                StartEscapeCountdown();
            }
            else
            {
                UpdateEscapeRailWidth(1.0);
            }
        }
        else if (ShouldAutoDwell(state))
        {
            _dwell = TimeSpan.FromSeconds(status.DwellSeconds ?? DefaultDwellFor(status));
            ArmDwell();
        }
        else
        {
            _dwell = TimeSpan.Zero;
        }
    }

    private string ComputeSpokenName(DictationOverlayState state, DictationStatus status, string title) =>
        state switch
        {
            DictationOverlayState.Recording => RecordingSpokenName(),
            DictationOverlayState.Processing or DictationOverlayState.EngineReady
                or DictationOverlayState.ImportStatus or DictationOverlayState.Success => title,
            DictationOverlayState.ColdStart => string.IsNullOrWhiteSpace(status.Subtitle)
                ? title
                : $"{title}. {status.Subtitle}",
            DictationOverlayState.BluetoothCard => "Bluetooth mic detected. Bluetooth microphones can take a moment on a cold start.",
            DictationOverlayState.PolishSetupCard => $"{title}. {status.Subtitle ?? "Pasted without AI polish."}",
            _ => string.IsNullOrWhiteSpace(status.Subtitle)
                ? title
                : $"{title}. {status.Subtitle}",
        };

    private static (string Title, string IconGlyph) ResolvePresentation(DictationStatus status, string detail)
    {
        var fallback = string.IsNullOrWhiteSpace(detail) ? "Working locally" : detail;
        return status.State switch
        {
            DictationOverlayState.Recording => ("Listening", "\uE720"),
            DictationOverlayState.Processing => (fallback, string.Empty),
            DictationOverlayState.ColdStart => (string.IsNullOrWhiteSpace(detail) ? "Getting dictation ready..." : detail, string.Empty),
            DictationOverlayState.EngineReady => (string.IsNullOrWhiteSpace(detail) ? "Ready, press to dictate" : detail, string.Empty),
            DictationOverlayState.ImportStatus => (fallback, "\uE895"),
            DictationOverlayState.Success => (string.IsNullOrWhiteSpace(detail) ? "Copied to your clipboard" : detail, string.Empty),
            DictationOverlayState.Advisory => (fallback, "\uF781"),
            DictationOverlayState.Suggestion => (fallback, "\uE774"),
            DictationOverlayState.Warning => (fallback, string.Empty),
            DictationOverlayState.Busy => (fallback, string.Empty),
            DictationOverlayState.Distress => (string.IsNullOrWhiteSpace(detail) ? "Recording interrupted." : detail, string.Empty),
            DictationOverlayState.Error => (string.IsNullOrWhiteSpace(detail) ? "Something went wrong. Try again." : detail, "\uEA39"),
            DictationOverlayState.EscapeRecovery => (string.IsNullOrWhiteSpace(detail) ? "Dictation cancelled" : detail, string.Empty),
            DictationOverlayState.BluetoothCard => ("Bluetooth mic detected", "\uE7F6"),
            DictationOverlayState.PolishSetupCard => (string.IsNullOrWhiteSpace(detail) ? "Polish needs setup." : detail, "\uE82F"),
            _ => (fallback, "\uE720"),
        };
    }

    private static bool ShouldAutoDwell(DictationOverlayState state) => state is
        DictationOverlayState.Success
        or DictationOverlayState.EngineReady
        or DictationOverlayState.ImportStatus
        or DictationOverlayState.Advisory
        or DictationOverlayState.Suggestion
        or DictationOverlayState.Warning
        or DictationOverlayState.Distress
        or DictationOverlayState.Error
        or DictationOverlayState.Busy;

    private static double DefaultDwellFor(DictationStatus status) => status.State switch
    {
        DictationOverlayState.EngineReady => 1.5d,
        DictationOverlayState.Distress => 2.0d,
        DictationOverlayState.Success or DictationOverlayState.Warning => 2.5d,
        DictationOverlayState.ImportStatus => 3.0d,
        DictationOverlayState.Error => status.ErrorVariant switch
        {
            RichErrorVariant.MicNotResponding => 8.0d,
            RichErrorVariant.PermissionDenied => 6.0d,
            _ => 3.0d,
        },
        DictationOverlayState.Suggestion or DictationOverlayState.Busy => 6.0d,
        DictationOverlayState.Advisory => 8.0d,
        _ => 3.0d,
    };

    private void ResetExtraPanels()
    {
        ContentPanel.Visibility = Visibility.Collapsed;
        LearnedPillPanel.Visibility = Visibility.Collapsed;
        LearnedCountdownRail.Visibility = Visibility.Collapsed;
        EscapeCountdownRail.Visibility = Visibility.Collapsed;
        BluetoothAwarenessCardPanel.Visibility = Visibility.Collapsed;
        PolishSetupCardPanel.Visibility = Visibility.Collapsed;
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

    public void SetPreview(string? text)
    {
        _previewText = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        if (_state == DictationOverlayState.Recording &&
            _activeDesign == RecordingPillDesign.ReadingWell)
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

    public void SetAudioLevel(float rootMeanSquare) =>
        AccumulatePeak(RecordingLevelHistory.Normalize(rootMeanSquare));

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

    private void OnLevelTick()
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
            DrawLips(framePeak);
        }

        DrawLevelHistory();
    }

    private void DrawSampleLevelRailForPreview()
    {
        var count = LevelBars.Children.Count;
        for (var index = 0; index < count; index++)
        {
            if (LevelBars.Children[index] is not Border bar)
            {
                continue;
            }

            var wave = (float)(0.25 + 0.55 * Math.Abs(Math.Sin(index * 0.45)));
            bar.Height = RecordingPillGeometry.MeterBarHeight(wave, LevelBars.Height);
            bar.Opacity = 1;
        }
    }

    private void DrawLevelHistory()
    {
        switch (_activeDesign)
        {
            case RecordingPillDesign.LevelRail:
                DrawMeter(LevelBars, LevelBars.Height);
                break;
            case RecordingPillDesign.ReadingWell:
                DrawMeter(WellMeter, WellMeter.Height);
                break;
        }
    }

    private void DrawMeter(StackPanel strip, double stripHeight)
    {
        var levels = _levelHistory.Levels;
        for (var index = 0; index < strip.Children.Count; index++)
        {
            if (strip.Children[index] is not Border bar)
            {
                continue;
            }

            var sample = index < levels.Count ? levels[index] : 0f;
            bar.Height = RecordingPillGeometry.MeterBarHeight(sample, stripHeight);
            bar.Opacity = 1;
        }
    }

    private void DrawLips(float level)
    {
        _lastDrawnLevel = level;
        var size = LipsMark.Width;
        var count = Math.Min(LipsMark.Children.Count, RecordingPillGeometry.LipsBarCount);
        for (var index = 0; index < count; index++)
        {
            if (LipsMark.Children[index] is not Border bar)
            {
                continue;
            }

            var rect = RecordingPillGeometry.LipsBar(index, level, size);
            Canvas.SetLeft(bar, rect.X);
            Canvas.SetTop(bar, rect.Y);
            bar.Width = rect.Width;
            bar.Height = rect.Height;
            bar.CornerRadius = new CornerRadius(RecordingPillGeometry.LipsCornerRadius(size));
        }
    }

    private static readonly int[] LipsSpectrumColorIndices =
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 7, 6, 5, 4, 3, 2, 1, 0, 8];

    private void ApplyLipsColoring(bool distress)
    {
        var redBrush = DistressRedProbe.Background;
        var count = Math.Min(LipsMark.Children.Count, RecordingPillGeometry.LipsBarCount);
        for (var index = 0; index < count; index++)
        {
            if (LipsMark.Children[index] is not Border bar)
            {
                continue;
            }

            if (distress && redBrush is not null)
            {
                bar.Background = redBrush;
            }
            else
            {
                var paletteIndex = LipsSpectrumColorIndices[index];
                if (paletteIndex >= 0 &&
                    paletteIndex < LipsSpectrumPalette.Children.Count &&
                    LipsSpectrumPalette.Children[paletteIndex] is Border swatch)
                {
                    bar.Background = swatch.Background;
                }
            }
        }
    }

    private void ApplySpectrumSweep()
    {
        var stops = new Windows.UI.Color[RecordingPillGeometry.SpectrumStops];
        for (var index = 0; index < stops.Length; index++)
        {
            if (index >= LipsSpectrumPalette.Children.Count
                || LipsSpectrumPalette.Children[index] is not Border { Background: SolidColorBrush brush })
            {
                return;
            }

            stops[index] = brush.Color;
        }

        PaintSweep(LevelBars, stops);
        PaintSweep(WellMeter, stops);
    }

    private static void PaintSweep(StackPanel strip, Windows.UI.Color[] stops)
    {
        var count = strip.Children.Count;
        for (var index = 0; index < count; index++)
        {
            if (strip.Children[index] is not Border bar)
            {
                continue;
            }

            var (lower, upper, fraction) = RecordingPillGeometry.SpectrumPosition(index, count);
            bar.Background = new SolidColorBrush(Blend(stops[lower], stops[upper], fraction));
        }
    }

    private static Windows.UI.Color Blend(Windows.UI.Color from, Windows.UI.Color to, double amount)
    {
        static byte Mix(byte a, byte b, double t) => (byte)Math.Round(a + (b - a) * t);
        return Microsoft.UI.ColorHelper.FromArgb(
            Mix(from.A, to.A, amount),
            Mix(from.R, to.R, amount),
            Mix(from.G, to.G, amount),
            Mix(from.B, to.B, amount));
    }

    private void ResetLevelHistory()
    {
        _levelHistory.Reset();
        Volatile.Write(ref _latestLevel, 0f);
        DrawLips(0f);
        DrawMeter(LevelBars, LevelBars.Height);
        DrawMeter(WellMeter, WellMeter.Height);
    }

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

    public void Shutdown()
    {
        _hideTimer.Stop();
        _elapsedTimer.Stop();
        _levelTimer.Stop();
        _learnedTimer.Stop();
        _escapeTimer.Stop();
        if (_subclassedHandle != nint.Zero && _subclassProc is not null)
        {
            _ = RemoveWindowSubclass(_subclassedHandle, _subclassProc, OverlaySubclassId);
            _subclassedHandle = nint.Zero;
        }
        Close();
    }

    private void ConfigureRecordingDesign()
    {
        _spectrumWheelPulse.Stop();
        SpectrumWheelTransform.Rotation = 0;
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
            DrawLips(_lastDrawnLevel);
        }

        ElapsedText.Visibility = classic && _handsFree ? Visibility.Collapsed : Visibility.Visible;
        ElapsedText.Style = GetPillStyle(readingWell ? "PillWellTimerTextStyle" : "PillCapsuleTimerTextStyle");
        ElapsedText.Padding = new Thickness(1, 0, 1, 0);
        LevelBars.Visibility = rail ? Visibility.Visible : Visibility.Collapsed;
        RailHandsFreeBadge.Visibility = rail && _handsFree ? Visibility.Visible : Visibility.Collapsed;
        WellMeter.Visibility = readingWell ? Visibility.Visible : Visibility.Collapsed;
        ModeLabel.Visibility = readingWell && !_handsFree ? Visibility.Visible : Visibility.Collapsed;
        WellHandsFreeBadge.Visibility = readingWell && _handsFree ? Visibility.Visible : Visibility.Collapsed;

        HeaderLeading.Spacing = readingWell ? 12 : CapsuleHeaderGap;
        HeaderRow.ColumnSpacing = readingWell ? 8 : 0;
        HeaderRow.HorizontalAlignment = readingWell ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
        HeaderRow.Height = readingWell ? ReadingWellHeaderHeight - FrameBorderThickness * 2 : double.NaN;
        HeaderRow.Padding = readingWell ? new Thickness(16, 0, 16, 0) : new Thickness(0);
        ContentPanel.Margin = readingWell
            ? new Thickness(0)
            : new Thickness(CapsuleHorizontalPadding, _recordingNotice == RecordingNoticeKind.None ? 0 : 8, CapsuleHorizontalPadding, _recordingNotice == RecordingNoticeKind.None ? 0 : 6);
        ContentPanel.Spacing = _recordingNotice == RecordingNoticeKind.None ? 0 : 4;
        ContentPanel.VerticalAlignment = readingWell ? VerticalAlignment.Top : VerticalAlignment.Center;
        PreviewWell.Padding = ReadingWellInsets;

        ApplySurface(
            readingWell ? "PillWellSurfaceStyle" : "PillCapsuleSurfaceStyle",
            readingWell ? ReadingWellRadius : RecordingCapsuleRadius);

        ApplyHairline(visible: true, breathes: !readingWell, distress: false);
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

        DrawLevelHistory();
    }

    private void ApplyInPanelNotice(RecordingNoticeKind notice, bool readingWell)
    {
        if (notice == RecordingNoticeKind.None)
        {
            InPanelNoticeRow.Visibility = Visibility.Collapsed;
            return;
        }

        InPanelNoticeText.Text = notice switch
        {
            RecordingNoticeKind.ApproachingCap => "Approaching 5 min recording limit",
            RecordingNoticeKind.AutoStopUnavailable => "Auto-stop unavailable right now",
            _ => string.Empty,
        };
        InPanelNoticeText.Style = GetPillStyle(readingWell ? "PillWellModeTextStyle" : "PillNoticeTextStyle");
        InPanelNoticeRow.Visibility = Visibility.Visible;
    }

    private int InPanelNoticeExtraHeight()
    {
        if (_recordingNotice == RecordingNoticeKind.None)
        {
            return 0;
        }

        InPanelNoticeRow.InvalidateMeasure();
        InPanelNoticeRow.Measure(new Windows.Foundation.Size(260, double.PositiveInfinity));
        return (int)Math.Ceiling(Math.Max(26, InPanelNoticeRow.DesiredSize.Height + 10));
    }

    private int InPanelNoticeMinimumWidth()
    {
        if (_recordingNotice == RecordingNoticeKind.None)
        {
            return 0;
        }

        InPanelNoticeRow.InvalidateMeasure();
        InPanelNoticeRow.Measure(new Windows.Foundation.Size(260, double.PositiveInfinity));
        var inner = Math.Max(190, InPanelNoticeRow.DesiredSize.Width);
        return (int)Math.Ceiling(FrameBorderThickness * 2 + CapsuleHorizontalPadding * 2 + inner);
    }

    private void ApplySurface(string surfaceStyle, double radius)
    {
        OverlayRoot.Style = GetPillStyle(surfaceStyle);
        OverlayRoot.CornerRadius = new CornerRadius(0);
        var innerCorners = new CornerRadius(radius);
        FrameBorder.CornerRadius = innerCorners;
        SeverityWash.CornerRadius = innerCorners;
    }

    private void ApplyHairline(bool visible, bool breathes, bool distress)
    {
        _hairlineBreath.Stop();
        _distressPulse.Stop();
        LipsMark.Opacity = 1;
        BottomDistressHairline.Visibility = distress ? Visibility.Visible : Visibility.Collapsed;
        BottomRainbowHairline.Opacity = RecordingPillGeometry.HairlineSteadyOpacity;
        BottomRainbowHairline.Visibility = visible && !distress ? Visibility.Visible : Visibility.Collapsed;
        if (distress)
        {
            BottomDistressHairline.Opacity = 0.6;
            if (AnimationsEnabled)
            {
                _distressPulse.Begin();
            }

            return;
        }

        if (visible && breathes && AnimationsEnabled)
        {
            _hairlineBreath.Begin();
        }
    }

    public event Action<PillActionKind>? ActionInvoked;

    public event Action<Guid>? LearnedUndoRequested;

    public event Action<Guid>? LearnedDismissed;

    public event Action<Guid>? LearnedEnded;

    public bool ShowLearnedCorrection(LearnedCorrectionPillModel model)
    {
        _isPreviewSession = false;
        _previewHoldOpen = false;
        _previewDesignOverride = null;
        return ShowLearnedCorrectionInternal(model, isPreview: false);
    }

    private bool ShowLearnedCorrectionInternal(LearnedCorrectionPillModel model, bool isPreview)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!isPreview && _state is DictationOverlayState.Recording or DictationOverlayState.Processing)
        {
            return false;
        }

        _hideTimer.Stop();
        _elapsedTimer.Stop();
        _levelTimer.Stop();
        StopEscapeCountdown();
        _spectrumWheelPulse.Stop();
        SpectrumWheelTransform.Rotation = 0;
        SpectrumWheelIcon.Visibility = Visibility.Collapsed;
        ResetExtraPanels();
        ApplyHairline(visible: false, breathes: false, distress: false);
        _handsFree = false;
        _recordingNotice = RecordingNoticeKind.None;
        _previewText = null;
        _action = null;

        StopLearnedCountdown(notifyEnded: !isPreview && _learnedModel is not null && _learnedModel.PillId != model.PillId);
        _learnedModel = model;
        _state = DictationOverlayState.CorrectionLearned;
        _preWarmed = true;

        RenderLearnedPill(model, announceNow: false);
        PositionOnForegroundMonitor();
        AppWindow.Show(activateWindow: false);
        AnnounceStateChange(LearnedPillTitle, DictationOverlayState.CorrectionLearned);

        if (model.Phase == LearnedCorrectionPillPhase.Learned)
        {
            if (!(isPreview && _previewHoldOpen))
            {
                StartLearnedCountdown(LearnedDwellSeconds);
            }
            else
            {
                UpdateLearnedRailWidth(0.35);
            }
        }
        else if (model.Phase == LearnedCorrectionPillPhase.UndoPending)
        {
            if (!(isPreview && _previewHoldOpen))
            {
                StartLearnedCountdown(LearnedUndoPendingFloorSeconds);
            }
        }
        else if (model.Phase == LearnedCorrectionPillPhase.Undone)
        {
            if (!(isPreview && _previewHoldOpen))
            {
                StartLearnedCountdown(LearnedUndoneDwellSeconds);
            }
        }

        return true;
    }

    public void UpdateLearnedCorrection(LearnedCorrectionPillModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (_state != DictationOverlayState.CorrectionLearned ||
            _learnedModel?.PillId != model.PillId)
        {
            return;
        }

        _learnedModel = model;
        RenderLearnedPill(model, announceNow: true);
        PositionOnForegroundMonitor();

        if (_isPreviewSession && _previewHoldOpen)
        {
            _learnedTimer.Stop();
            return;
        }

        if (model.Phase == LearnedCorrectionPillPhase.Undone)
        {
            StartLearnedCountdown(LearnedUndoneDwellSeconds);
        }
        else if (model.Phase == LearnedCorrectionPillPhase.UndoError)
        {
            StartLearnedCountdown(LearnedErrorDwellSeconds);
        }
        else if (model.Phase == LearnedCorrectionPillPhase.UndoPending)
        {
            var elapsedMs = _learnedElapsedBaseMs + _learnedWatch.Elapsed.TotalMilliseconds;
            var remainingSeconds = Math.Max(0, _learnedDuration.TotalSeconds - elapsedMs / 1000.0);
            StartLearnedCountdown(Math.Max(remainingSeconds, LearnedUndoPendingFloorSeconds));
        }
    }

    public void CloseLearnedCorrection(Guid pillId)
    {
        if (_state != DictationOverlayState.CorrectionLearned ||
            _learnedModel?.PillId != pillId)
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

    private void ShowLearnedSaveErrorInternal(string canonical, bool isPreview)
    {
        if (!isPreview && _state is DictationOverlayState.Recording or DictationOverlayState.Processing)
        {
            return;
        }

        var word = string.IsNullOrWhiteSpace(canonical) ? "word" : canonical.Trim();
        var message = $"Couldn\u2019t save \u201C{word}\u201D";
        StopLearnedCountdown(notifyEnded: !isPreview && _learnedModel is not null);
        StopEscapeCountdown();
        _hideTimer.Stop();
        _elapsedTimer.Stop();
        _levelTimer.Stop();
        ResetExtraPanels();

        _learnedModel = null;
        _action = null;
        _handsFree = false;
        _recordingNotice = RecordingNoticeKind.None;
        _state = DictationOverlayState.Error;
        _preWarmed = true;

        ContentPanel.Visibility = Visibility.Visible;
        StateTitle.Text = message;
        ApplyAction(null);
        ConfigureNotice(DictationStatus.Error(message), message, isSaveError: true);
        ApplySeverity(DictationOverlayState.Error);

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(StateTitle, message);
        PositionOnForegroundMonitor();
        AppWindow.Show(activateWindow: false);
        AnnounceStateChange(StateTitle, DictationOverlayState.Error);

        _dwell = TimeSpan.FromSeconds(LearnedErrorDwellSeconds);
        ArmDwell();
    }

    private void RenderLearnedPill(LearnedCorrectionPillModel model, bool announceNow = true)
    {
        ResetExtraPanels();
        LearnedPillPanel.Visibility = Visibility.Visible;
        ApplySurface("PillLearnedSurfaceStyle", LearnedCapsuleRadius);
        ApplySeverity(DictationOverlayState.CorrectionLearned);

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
        LearnedPillTitle.Style = model.Phase == LearnedCorrectionPillPhase.UndoError
            ? GetPillStyle("PillLearnedErrorTitleStyle")
            : GetPillStyle("PillLearnedTitleStyle");
        LearnedPillSubtitle.Text = subtitleText;
        LearnedPillSubtitle.Visibility = string.IsNullOrEmpty(subtitleText)
            ? Visibility.Collapsed
            : Visibility.Visible;

        var controlsVisible = model.Phase == LearnedCorrectionPillPhase.Learned;
        LearnedPillControls.Visibility = controlsVisible ? Visibility.Visible : Visibility.Collapsed;
        LearnedPillUndoButton.IsEnabled = controlsVisible;
        LearnedPillCloseButton.IsEnabled = controlsVisible;

        var showRail = model.Phase is LearnedCorrectionPillPhase.Learned or LearnedCorrectionPillPhase.UndoPending;
        LearnedCountdownRail.Visibility = showRail ? Visibility.Visible : Visibility.Collapsed;

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            LearnedPillTitle,
            string.IsNullOrEmpty(subtitleText) ? titleText : $"{titleText}. {subtitleText}. Undo available.");
        if (announceNow)
        {
            AnnounceStateChange(LearnedPillTitle, DictationOverlayState.CorrectionLearned);
        }

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            LearnedPillCloseButton,
            $"Close and keep \u201C{model.Canonical}\u201D");

        LearnedPillPanel.InvalidateMeasure();
        LearnedPillPanel.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var desiredW = LearnedPillPanel.DesiredSize.Width > 0 ? LearnedPillPanel.DesiredSize.Width : 300;
        var width = Math.Clamp((int)Math.Ceiling(FrameBorderThickness * 2 + desiredW + 24), 240, 520);
        var height = (int)Math.Ceiling(LearnedPillHeight);
        Resize(width, height);
        UpdateLearnedRailWidth(0);
        OverlayRoot.UpdateLayout();
    }

    private void StartLearnedCountdown(double seconds)
    {
        _learnedDuration = TimeSpan.FromSeconds(seconds);
        _learnedElapsedBaseMs = 0;
        _learnedWatch.Restart();
        UpdateLearnedRailWidth(0);
        _learnedTimer.Start();
    }

    private void StopLearnedCountdown(bool notifyEnded)
    {
        _learnedTimer.Stop();
        _learnedWatch.Reset();
        _learnedElapsedBaseMs = 0;
        var endedModel = _learnedModel;
        _learnedModel = null;
        if (notifyEnded && endedModel is not null)
        {
            LearnedEnded?.Invoke(endedModel.PillId);
        }
    }

    private void TickLearnedCountdown()
    {
        if (_state != DictationOverlayState.CorrectionLearned || _learnedModel is null)
        {
            _learnedTimer.Stop();
            return;
        }

        if (_isPreviewSession && _previewHoldOpen)
        {
            _learnedTimer.Stop();
            return;
        }

        var totalMs = _learnedDuration.TotalMilliseconds;
        var elapsedMs = _learnedElapsedBaseMs + _learnedWatch.Elapsed.TotalMilliseconds;
        var fraction = totalMs > 0 ? Math.Clamp(elapsedMs / totalMs, 0, 1) : 1;
        UpdateLearnedRailWidth(fraction);

        if (elapsedMs >= totalMs)
        {
            if (_learnedModel.Phase == LearnedCorrectionPillPhase.UndoPending)
            {
                return;
            }

            HideOverlay();
        }
    }

    private void UpdateLearnedRailWidth(double fraction)
    {
        var trackWidth = Math.Max(0, _logicalWidth - 40 - FrameBorderThickness * 2);
        LearnedCountdownRail.Width = trackWidth * Math.Clamp(fraction, 0, 1);
    }

    private void StartEscapeCountdown()
    {
        var seconds = _dwell > TimeSpan.Zero ? _dwell.TotalSeconds : EscapeRecoveryDwellSeconds;
        _escapeDuration = TimeSpan.FromSeconds(seconds);
        _escapeWatch.Restart();
        EscapeCountdownRail.Visibility = Visibility.Visible;
        UpdateEscapeRailWidth(1.0);
        _escapeTimer.Start();
    }

    private void StopEscapeCountdown()
    {
        _escapeTimer.Stop();
        _escapeWatch.Reset();
        EscapeCountdownRail.Visibility = Visibility.Collapsed;
        EscapeCountdownRail.Width = 0;
    }

    private void TickEscapeCountdown()
    {
        if (_state != DictationOverlayState.EscapeRecovery)
        {
            StopEscapeCountdown();
            return;
        }

        if (_pointerIsOver || (_isPreviewSession && _previewHoldOpen))
        {
            _escapeWatch.Reset();
            return;
        }

        var totalMs = _escapeDuration.TotalMilliseconds;
        var elapsedMs = _escapeWatch.Elapsed.TotalMilliseconds;
        var remainingFraction = totalMs > 0 ? Math.Clamp(1.0 - elapsedMs / totalMs, 0, 1) : 0;
        UpdateEscapeRailWidth(remainingFraction);

        if (elapsedMs >= totalMs)
        {
            HideOverlay();
        }
    }

    private void UpdateEscapeRailWidth(double remainingFraction)
    {
        var trackWidth = Math.Max(0, _logicalWidth - 40 - FrameBorderThickness * 2);
        EscapeCountdownRail.Width = trackWidth * Math.Clamp(remainingFraction, 0, 1);
    }

    private void LearnedUndoButton_Click(object sender, RoutedEventArgs e)
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

        var elapsedMs = _learnedElapsedBaseMs + _learnedWatch.Elapsed.TotalMilliseconds;
        var remainingSeconds = Math.Max(0, _learnedDuration.TotalSeconds - elapsedMs / 1000.0);
        var pendingDurationSeconds = Math.Max(remainingSeconds, LearnedUndoPendingFloorSeconds);

        _learnedModel = model with { Phase = LearnedCorrectionPillPhase.UndoPending };
        RenderLearnedPill(_learnedModel);
        _learnedDuration = TimeSpan.FromSeconds(pendingDurationSeconds);
        _learnedElapsedBaseMs = 0;
        _learnedWatch.Restart();
        LearnedUndoRequested?.Invoke(model.PillId);
    }

    private void LearnedCloseButton_Click(object sender, RoutedEventArgs e)
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
            DictationOverlayState.EscapeRecovery => GetPillStyle("PillEscapeUndoButtonStyle"),
            DictationOverlayState.Suggestion when action.Kind == PillActionKind.LockDetectedLanguage =>
                GetPillStyle("PillLanguageLockButtonStyle"),
            _ => GetPillStyle("PillInlineActionButtonStyle"),
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

    private void CardDismissButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isPreviewSession)
        {
            var label = sender is Button { Content: string text } && !string.IsNullOrWhiteSpace(text)
                ? text
                : "Close";
            PreviewActionPressed?.Invoke($"Action pressed: {label} (preview only)");
            return;
        }

        HideOverlay();
    }

    private void ArmDwell()
    {
        _hideTimer.Stop();
        if (_dwell <= TimeSpan.Zero || _pointerIsOver || (_isPreviewSession && _previewHoldOpen))
        {
            return;
        }

        _hideTimer.Interval = _dwell;
        _hideTimer.Start();
    }

    private void OverlayRoot_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _pointerIsOver = true;
        _hideTimer.Stop();
        if (_state == DictationOverlayState.EscapeRecovery)
        {
            _escapeTimer.Stop();
            _escapeWatch.Reset();
            UpdateEscapeRailWidth(1.0);
            return;
        }

        if (_state == DictationOverlayState.CorrectionLearned &&
            _learnedModel?.Phase == LearnedCorrectionPillPhase.Learned)
        {
            _learnedElapsedBaseMs += _learnedWatch.Elapsed.TotalMilliseconds;
            _learnedWatch.Reset();
            _learnedTimer.Stop();
        }
    }

    private void OverlayRoot_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointerIsOver = false;
        if (_isPreviewSession && _previewHoldOpen)
        {
            return;
        }

        if (_state == DictationOverlayState.EscapeRecovery)
        {
            StartEscapeCountdown();
            return;
        }

        if (_state == DictationOverlayState.CorrectionLearned &&
            _learnedModel?.Phase == LearnedCorrectionPillPhase.Learned)
        {
            var maxBaseMs = (_learnedDuration.TotalSeconds - LearnedHoverResumeFloorSeconds) * 1000.0;
            if (maxBaseMs > 0 && _learnedElapsedBaseMs > maxBaseMs)
            {
                _learnedElapsedBaseMs = maxBaseMs;
            }

            _learnedWatch.Restart();
            _learnedTimer.Start();
            return;
        }

        ArmDwell();
    }

    private void OverlayRoot_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(null);
        if (!point.Properties.IsLeftButtonPressed || !OverlayRoot.CapturePointer(e.Pointer))
        {
            return;
        }

        _dragPointer = e.Pointer.PointerId;
        _dragFromWindow = new PointInt32(AppWindow.Position.X, AppWindow.Position.Y);
        GetCursorPos(out var cursor);
        _dragFromCursor = cursor;
    }

    private void OverlayRoot_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragPointer != e.Pointer.PointerId)
        {
            return;
        }

        GetCursorPos(out var cursor);
        var moveX = cursor.X - _dragFromCursor.X;
        var moveY = cursor.Y - _dragFromCursor.Y;
        if (moveX == 0 && moveY == 0)
        {
            return;
        }

        _draggedThisPresentation = true;
        AppWindow.Move(new PointInt32(_dragFromWindow.X + moveX, _dragFromWindow.Y + moveY));
    }

    private void OverlayRoot_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragPointer != e.Pointer.PointerId)
        {
            return;
        }

        OverlayRoot.ReleasePointerCapture(e.Pointer);
        _dragPointer = null;
    }

    private void HideOverlay()
    {
        _levelTimer.Stop();
        _hideTimer.Stop();
        _elapsedTimer.Stop();
        StopEscapeCountdown();
        StopLearnedCountdown(notifyEnded: !_isPreviewSession);
        ResetExtraPanels();
        ContentPanel.Visibility = Visibility.Visible;
        _spectrumWheelPulse.Stop();
        SpectrumWheelTransform.Rotation = 0;
        SpectrumWheelIcon.Visibility = Visibility.Collapsed;
        ApplyHairline(visible: false, breathes: false, distress: false);
        _dwell = TimeSpan.Zero;
        _pointerIsOver = false;
        _handsFree = false;
        _recordingNotice = RecordingNoticeKind.None;
        _previewText = null;
        _action = null;
        _draggedThisPresentation = false;
        _dragPointer = null;
        _isPreviewSession = false;
        _previewHoldOpen = false;
        _previewDesignOverride = null;
        _state = DictationOverlayState.Hidden;
        AppWindow.Hide();
    }

    private void ApplySeverity(DictationOverlayState state)
    {
        var (icon, edge, wash) = state switch
        {
            DictationOverlayState.Success =>
                ("PillSuccessIconStyle", "PillSuccessEdgeStyle", "PillSuccessWashStyle"),
            DictationOverlayState.Advisory =>
                ("PillMutedIconStyle", "PillAdvisoryEdgeStyle", "PillAdvisoryWashStyle"),
            DictationOverlayState.Suggestion =>
                ("PillAdvisoryIconStyle", "PillAdvisoryEdgeStyle", "PillAdvisoryWashStyle"),
            DictationOverlayState.Warning or DictationOverlayState.Busy =>
                ("PillWarningIconStyle", "PillWarningEdgeStyle", "PillWarningWashStyle"),
            DictationOverlayState.Distress =>
                ("PillDistressIconStyle", "PillDistressEdgeStyle", "PillDistressWashStyle"),
            DictationOverlayState.Error =>
                ("PillErrorIconStyle", "PillErrorEdgeStyle", "PillErrorWashStyle"),
            DictationOverlayState.Hidden
                or DictationOverlayState.Recording
                or DictationOverlayState.Processing
                or DictationOverlayState.ColdStart
                or DictationOverlayState.EngineReady
                or DictationOverlayState.ImportStatus
                or DictationOverlayState.EscapeRecovery
                or DictationOverlayState.BluetoothCard
                or DictationOverlayState.PolishSetupCard =>
                ("PillNeutralIconStyle", "PillNeutralEdgeStyle", "PillNeutralWashStyle"),
            DictationOverlayState.CorrectionLearned =>
                ("PillLearnedIconStyle", "PillLearnedEdgeStyle", "PillLearnedWashStyle"),
            _ => ("PillNeutralIconStyle", "PillNeutralEdgeStyle", "PillNeutralWashStyle"),
        };

        StateIcon.Style = GetPillStyle(icon);
        FrameBorder.Style = state switch
        {
            DictationOverlayState.Recording => GetPillStyle(
                _activeDesign == RecordingPillDesign.ReadingWell ? "PillWellEdgeStyle" : "PillCapsuleEdgeStyle"),
            DictationOverlayState.EscapeRecovery => GetPillStyle("PillEscapeEdgeStyle"),
            DictationOverlayState.BluetoothCard or DictationOverlayState.PolishSetupCard => GetPillStyle("PillCardEdgeStyle"),
            DictationOverlayState.CorrectionLearned => GetPillStyle("PillLearnedEdgeStyle"),
            _ => GetPillStyle("PillCapsuleEdgeStyle"),
        };

        // Dark capsules keep a clean charcoal surface without severity tint washes; keep SeverityWash neutral.
        SeverityWash.Style = state == DictationOverlayState.CorrectionLearned
            ? GetPillStyle(wash)
            : GetPillStyle("PillNeutralWashStyle");
        SeverityWash.CornerRadius = FrameBorder.CornerRadius;
    }

    private void ConfigureFloatingCard(DictationStatus status)
    {
        _spectrumWheelPulse.Stop();
        SpectrumWheelTransform.Rotation = 0;
        ApplyHairline(visible: false, breathes: false, distress: false);

        var isBluetooth = status.State == DictationOverlayState.BluetoothCard;
        _action = status.Action;

        BluetoothAwarenessCardPanel.Visibility = isBluetooth ? Visibility.Visible : Visibility.Collapsed;
        PolishSetupCardPanel.Visibility = isBluetooth ? Visibility.Collapsed : Visibility.Visible;
        CardBottomRainbowBar.Visibility = isBluetooth ? Visibility.Visible : Visibility.Collapsed;

        if (!isBluetooth)
        {
            PolishSetupTitle.Text = string.IsNullOrWhiteSpace(status.Text) ? "Polish needs setup." : status.Text;
            PolishSetupSubtitle.Text = string.IsNullOrWhiteSpace(status.Subtitle) ? "Pasted without AI polish." : status.Subtitle;
            PolishSetupActionButton.Content = status.Action?.Label ?? "Set up AI polish";
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                PolishSetupActionButton,
                status.Action?.SpokenLabel ?? "Set up AI polish");
        }

        ApplySurface("PillCardSurfaceStyle", CardCornerRadius);
        ApplySeverity(status.State);

        FrameworkElement activeCard = isBluetooth ? BluetoothAwarenessCardPanel : PolishSetupCardPanel;
        activeCard.InvalidateMeasure();
        activeCard.Measure(new Windows.Foundation.Size(FloatingCardWidth, double.PositiveInfinity));
        var measuredH = activeCard.DesiredSize.Height > 0
            ? activeCard.DesiredSize.Height
            : (isBluetooth ? 344 : 184);
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
            ContentPanel.Margin = new Thickness(CapsuleHorizontalPadding, 0, CapsuleHorizontalPadding, 0);
            ApplySurface("PillCapsuleSurfaceStyle", RecordingCapsuleRadius);
            ApplyHairline(visible: true, breathes: true, distress: false);
            Resize(ProcessingPillWidth(), RecordingCapsuleHeight);

            if (AnimationsEnabled)
            {
                _spectrumWheelPulse.Begin();
            }
            else
            {
                _spectrumWheelPulse.Stop();
                SpectrumWheelTransform.Rotation = 0;
            }

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
        if (showWheel && AnimationsEnabled)
        {
            _spectrumWheelPulse.Begin();
        }
        else
        {
            _spectrumWheelPulse.Stop();
            SpectrumWheelTransform.Rotation = 0;
        }

        if (showDistressLips)
        {
            ApplyLipsColoring(distress: true);
            DrawLips(0.15f);
            LipsMark.Visibility = Visibility.Visible;
        }
        else
        {
            ApplyLipsColoring(distress: false);
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
            StateIcon.Style = GetPillStyle("PillMutedIconStyle");
        }

        HeaderLeading.Visibility = (showWheel || showDistressLips || showWarningGlyph || showErrorCircle || showReadyCircle || showFontIcon || isRichError)
            ? Visibility.Visible
            : Visibility.Collapsed;

        StateTitle.Style = isSaveError
            ? GetPillStyle("PillSaveErrorTextStyle")
            : state == DictationOverlayState.EscapeRecovery
                ? GetPillStyle("PillEscapeTextStyle")
                : state == DictationOverlayState.Distress
                    ? GetPillStyle("PillDistressMessageTextStyle")
                    : GetPillStyle("PillCapsuleTimerTextStyle");

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

        var horizontalPad = isRichError ? 16 : CapsuleHorizontalPadding;
        var verticalPad = (isRichError || hasSubtitle) ? 12 : 9;
        HeaderRow.ColumnSpacing = HeaderLeading.Visibility == Visibility.Visible || _action is not null ? _noticeColumnSpacing : 0;
        ContentPanel.Spacing = 0;

        var maxOuterWidth = isRichError
            ? RichErrorCardWidth
            : state == DictationOverlayState.Advisory
                ? AdvisoryNoticeMaxWidth
                : state == DictationOverlayState.Suggestion
                    ? LanguageChipMaxWidth
                    : isSaveError
                        ? 440
                        : StandardNoticeMaxWidth;

        // Calculate inner text width ceiling cleanly so StateTitle never exceeds its intended column width.
        var leadingReserved = isRichError ? 52d : (HeaderLeading.Visibility == Visibility.Visible ? 30d : 0d);
        var actionReserved = _action is null
            ? 0d
            : MeasureElementWidth(InlineActionRow, 90d) + _noticeColumnSpacing;
        var maxTextWidth = Math.Max(140d, maxOuterWidth - horizontalPad * 2 - FrameBorderThickness * 2 - leadingReserved - actionReserved);
        StateTitle.MaxWidth = maxTextWidth;
        StateSubtitle.MaxWidth = maxTextWidth;

        ContentPanel.Margin = new Thickness(
            isRichError ? horizontalPad + 2 : horizontalPad,
            verticalPad,
            horizontalPad,
            verticalPad);

        StateTitle.InvalidateMeasure();
        StateTitle.Measure(new Windows.Foundation.Size(maxTextWidth, double.PositiveInfinity));
        StateSubtitle.InvalidateMeasure();
        if (hasSubtitle)
        {
            StateSubtitle.Measure(new Windows.Foundation.Size(maxTextWidth, double.PositiveInfinity));
        }

        var textW = Math.Max(StateTitle.DesiredSize.Width, hasSubtitle ? StateSubtitle.DesiredSize.Width : 0);
        var textH = StateTitle.DesiredSize.Height + (hasSubtitle ? StateSubtitle.DesiredSize.Height + 2 : 0);
        var computedWidth = (int)Math.Ceiling(FrameBorderThickness * 2 + horizontalPad * 2 + leadingReserved + textW + actionReserved + (isRichError ? 6 : 2));
        var width = isRichError
            ? RichErrorCardWidth
            : Math.Clamp(computedWidth, isEscapeRecovery ? 210 : 180, maxOuterWidth);
        var contentHeight = Math.Max(textH, isRichError ? 40 : 24);
        var height = isEscapeRecovery
            ? RecordingCapsuleHeight
            : Math.Max(RecordingCapsuleHeight, (int)Math.Ceiling(FrameBorderThickness * 2 + verticalPad * 2 + contentHeight));

        var cornerRadius = isRichError || height > 58 ? NoticeCapsuleRadius : height / 2.0;

        ApplySurface(
            isEscapeRecovery ? "PillEscapeSurfaceStyle" : "PillCapsuleSurfaceStyle",
            cornerRadius);

        ApplyHairline(
            visible: !isEscapeRecovery,
            breathes: state == DictationOverlayState.Success || state == DictationOverlayState.ColdStart,
            distress: state == DictationOverlayState.Distress);

        Resize(width, height);
        OverlayRoot.UpdateLayout();
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
        return (int)Math.Ceiling(FrameBorderThickness * 2 + CapsuleHorizontalPadding * 2 + contentWidth);
    }

    private int LevelRailPillWidth()
    {
        var scale = _rasterScale > 0 ? _rasterScale : 1.0;
        var roundedRailWidth = 24.0 * (Math.Ceiling(3.0 * scale) / scale) +
            23.0 * (Math.Ceiling(2.0 * scale) / scale);
        var elapsedWidth = Math.Max(
            ElapsedTextFallbackWidth(),
            MeasureElementWidth(ElapsedText, ElapsedTextFallbackWidth()));
        var railWidth = Math.Max(
            Math.Max(LevelRailBarsWidth, roundedRailWidth),
            MeasureElementWidth(LevelBars, LevelRailBarsWidth));
        var computedWidth = elapsedWidth + CapsuleHeaderGap + railWidth;
        if (_handsFree)
        {
            computedWidth += CapsuleHeaderGap + MeasureElementWidth(RailHandsFreeBadge, 76.0);
        }

        HeaderLeading.InvalidateMeasure();
        var leadingWidth = MeasureElementWidth(HeaderLeading, computedWidth);
        var contentWidth = Math.Max(computedWidth, leadingWidth) + 8.0;
        return (int)Math.Ceiling(FrameBorderThickness * 2 + CapsuleHorizontalPadding * 2 + contentWidth);
    }

    private int ProcessingPillWidth()
    {
        var label = MeasureElementWidth(ProcessingLabel, ProcessingLabel.Text.Length * 7.0);
        return (int)Math.Ceiling(ProcessingChromeWidth + label);
    }

    private static Style GetPillStyle(string key) =>
        (Style)Application.Current.Resources[key];

    private void ResizeReadingWell()
    {
        var noticeExtra = InPanelNoticeExtraHeight();
        if (_previewText is null)
        {
            PreviewWell.Visibility = Visibility.Collapsed;
            PreviewFade.Visibility = Visibility.Collapsed;
            Resize(ReadingWellWidth, ReadingWellHeaderHeight + noticeExtra);
        }
        else
        {
            PreviewWell.Visibility = Visibility.Visible;
            var textWidth = ReadingWellWidth - FrameBorderThickness * 2 - ReadingWellInsets.Left - ReadingWellInsets.Right;
            PreviewText.Width = textWidth;
            PreviewText.Measure(new Windows.Foundation.Size(textWidth, double.PositiveInfinity));
            var natural = PreviewText.DesiredSize.Height;
            if (natural <= 0)
            {
                var estimatedLines = Math.Max(1, (_previewText.Length + 43) / 44);
                natural = estimatedLines * PreviewLineHeight;
            }

            var cap = PreviewLineHeight * PreviewMaxLines;
            var viewport = Math.Min(natural, cap);
            PreviewViewport.Width = textWidth;
            PreviewViewport.Height = viewport;
            PreviewViewport.Clip = new RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(0, 0, textWidth, viewport),
            };
            Canvas.SetTop(PreviewText, viewport - natural);
            PreviewFade.Width = textWidth;
            PreviewFade.Height = viewport * PreviewFadeFraction;
            PreviewFade.Visibility = natural > cap + 0.5 ? Visibility.Visible : Visibility.Collapsed;

            Resize(
                ReadingWellWidth,
                (int)Math.Ceiling(ReadingWellHeaderHeight + 1 + ReadingWellInsets.Top + viewport + ReadingWellInsets.Bottom + noticeExtra));
        }

        if (_state == DictationOverlayState.Recording)
        {
            PositionOnForegroundMonitor();
        }
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) =>
        ApplyScale(sender.RasterizationScale);

    private void ApplyScale(double scale)
    {
        if (scale <= 0 || Math.Abs(scale - _rasterScale) < 0.001)
        {
            return;
        }

        _rasterScale = scale;
        Resize(_logicalWidth, _logicalHeight);
        PositionOnForegroundMonitor();
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

    private void Resize(int width, int height)
    {
        _logicalWidth = width;
        _logicalHeight = height;
        _overlayWidth = Math.Max(1, (int)Math.Ceiling((width + WindowBleed * 2) * _rasterScale));
        _overlayHeight = Math.Max(1, (int)Math.Ceiling((height + WindowBleed * 2) * _rasterScale));
        AppWindow.Resize(new SizeInt32(_overlayWidth, _overlayHeight));
        var actualSize = AppWindow.Size;
        if (actualSize.Width > 0 && actualSize.Height > 0)
        {
            _overlayWidth = actualSize.Width;
            _overlayHeight = actualSize.Height;
        }

        ClipToPillShape();
    }

    private void ClipToPillShape()
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);

        var fringe = Math.Max(0, Math.Min(
            (int)Math.Floor(WindowBleed * _rasterScale),
            (Math.Min(_overlayWidth, _overlayHeight) - 1) / 2));
        var radius = Math.Max(0, (int)Math.Ceiling(
            FrameBorder.CornerRadius.TopLeft * _rasterScale * 2));
        var region = CreateRoundRectRgn(
            fringe,
            fringe,
            _overlayWidth - fringe,
            _overlayHeight - fringe,
            radius,
            radius);
        if (region == nint.Zero)
        {
            return;
        }

        if (SetWindowRgn(handle, region, bRedraw: true) == 0)
        {
            DeleteObject(region);
        }
    }

    private void UpdateElapsed()
    {
        var elapsed = DateTimeOffset.UtcNow - _recordingStartedAt;
        var formatted = RecordingPillGeometry.FormatElapsed(elapsed);
        var showCapNotice = elapsed >= ApproachingCapElapsedThreshold && !_autoCapNoticeShown;
        if (showCapNotice)
        {
            _autoCapNoticeShown = true;
            _recordingNotice = RecordingNoticeKind.ApproachingCap;
            ConfigureRecordingDesign();
            PositionOnForegroundMonitor();
            return;
        }

        if (string.Equals(ElapsedText.Text, formatted, StringComparison.Ordinal))
        {
            return;
        }

        ElapsedText.Text = formatted;
        if (_state == DictationOverlayState.Recording &&
            (_activeDesign == RecordingPillDesign.LevelRail ||
             (_activeDesign == RecordingPillDesign.Classic && !_handsFree)))
        {
            var width = _activeDesign == RecordingPillDesign.Classic
                ? Math.Max(CapsulePillWidth(), InPanelNoticeMinimumWidth())
                : Math.Max(LevelRailPillWidth(), InPanelNoticeMinimumWidth());
            if (width != _logicalWidth)
            {
                Resize(width, RecordingCapsuleHeight + InPanelNoticeExtraHeight());
                PositionOnForegroundMonitor();
            }
        }
    }

    private void PositionOnForegroundMonitor()
    {
        if (_draggedThisPresentation)
        {
            return;
        }

        var foreground = GetForegroundWindow();
        var monitor = MonitorFromWindow(foreground, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == nint.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        var workArea = new DisplayWorkArea(
            info.WorkArea.Left,
            info.WorkArea.Top,
            info.WorkArea.Right,
            info.WorkArea.Bottom);
        var physicalMargin = Math.Max(1, (int)Math.Ceiling(WorkAreaMargin * _rasterScale));
        var position = _position == OverlayPillPosition.Bottom
            ? OverlayPlacement.BottomCenter(workArea, _overlayWidth, _overlayHeight, physicalMargin)
            : OverlayPlacement.TopCenter(workArea, _overlayWidth, _overlayHeight, physicalMargin);
        AppWindow.Move(new PointInt32(position.X, position.Y));
    }

    private const uint DwmColorNone = 0xFFFFFFFE;
    private const int DwmwaBorderColor = 34;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint windowHandle, int attribute, ref uint value, int size);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRoundRectRgn(
        int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint handle);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(nint windowHandle, nint region, bool bRedraw);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private void InstallMinTrackOverride(nint windowHandle)
    {
        if (windowHandle == nint.Zero || _subclassedHandle != nint.Zero)
        {
            return;
        }

        _subclassProc = OnOverlayWindowMessage;
        if (SetWindowSubclass(windowHandle, _subclassProc, OverlaySubclassId, 0))
        {
            _subclassedHandle = windowHandle;
        }
        else
        {
            _subclassProc = null;
        }
    }

    private nint OnOverlayWindowMessage(
        nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData)
    {
        if (uMsg == WmGetMinMaxInfo && lParam != nint.Zero)
        {
            var result = DefSubclassProc(hWnd, uMsg, wParam, lParam);
            var minMax = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            minMax.PtMinTrackSize.X = 1;
            minMax.PtMinTrackSize.Y = 1;
            Marshal.StructureToPtr(minMax, lParam, fDeleteOld: false);
            return result;
        }

        if (uMsg == WmNcDestroy && _subclassProc is not null)
        {
            _ = RemoveWindowSubclass(hWnd, _subclassProc, uIdSubclass);
            _subclassedHandle = nint.Zero;
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        nint hWnd, SubclassProc pfnSubclass, nuint uIdSubclass, nuint dwRefData);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        nint hWnd, SubclassProc pfnSubclass, nuint uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(
        nint hWnd, uint uMsg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint PtReserved;
        public NativePoint PtMaxSize;
        public NativePoint PtMaxPosition;
        public NativePoint PtMinTrackSize;
        public NativePoint PtMaxTrackSize;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }
}
