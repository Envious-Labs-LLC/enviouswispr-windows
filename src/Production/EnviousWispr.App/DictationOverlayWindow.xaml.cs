using System.Runtime.InteropServices;
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

    /// <summary>The notice pill's single-line height. Notices keep the shape they always had.</summary>
    private const int CapsuleHeight = 52;

    /// <summary>Classic, Level Rail and Processing: 44 tall, as the macOS atlas measures them.</summary>
    private const int RecordingCapsuleHeight = 44;

    private const double RecordingCapsuleRadius = 22;
    private const double ReadingWellRadius = 16;
    private const double NoticeCapsuleRadius = 26;
    private const double NoticePanelRadius = 18;
    private const int ClassicWidth = 185;
    private const int LevelRailWidth = 288;
    private const int ReadingWellWidth = 400;

    /// <summary>The Reading Well's header, and the whole pill while it waits for words.</summary>
    private const int ReadingWellHeaderHeight = 34;

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
    private const int NoticeSingleLineWidth = 300;
    private const int NoticeMultiLineWidth = 360;
    private const int NoticeMultiLineHeight = 78;
    private const int ActionNoticeHeight = 118;
    private const int WorkAreaMargin = 28;

    private readonly DispatcherTimer _hideTimer = new();
    private readonly DispatcherTimer _elapsedTimer = new();
    private readonly Storyboard _distressPulse = new();
    private readonly Storyboard _spectrumWheelPulse = new();

    /// <summary>The rainbow hairline's two-second breath, 0.3 to 0.65 and back.</summary>
    private readonly Storyboard _hairlineBreath = new();

    /// <summary>Whether the recording is hands-free (locked), which changes what each design draws.</summary>
    private bool _handsFree;

    /// <summary>The notice row's own spacing, read from markup so the recording designs can restore it.</summary>
    private readonly double _noticeColumnSpacing;
    private readonly double _noticeContentSpacing;
    private TimeSpan _dwell = TimeSpan.Zero;
    private bool _pointerIsOver;
    private OverlayPillPosition _position = OverlayPillPosition.Top;
    private bool _livePreviewEnabled;
    private RecordingPillDesign _withoutWordsDesign = RecordingPillDesign.Classic;
    private RecordingPillDesign _withWordsDesign = RecordingPillDesign.ReadingWell;
    private RecordingPillDesign _activeDesign = RecordingPillDesign.Classic;

    /// <summary>One level per bar, oldest first, so the rail is a history rather than a mirror.</summary>
    private readonly RecordingLevelHistory _levelHistory = new();

    /// <summary>
    /// The loudest level the capture has reported since the last frame was taken.
    /// </summary>
    /// <remarks>
    /// KEPT AS ONE NUMBER RATHER THAN AS TWO HUNDRED DISPATCHER JOBS A SECOND. Every capture buffer
    /// used to be posted to the UI thread and thrown away there by a throttle, so the work was done
    /// whatever the throttle decided - and under load the queue backed up and the meter drew samples
    /// that were already old.
    ///
    /// ONE NUMBER, AND IT IS THE LOUDEST OF THE PENDING FRAME RATHER THAN THE NEWEST. About ten
    /// levels arrive per frame and exactly one is drawn, so keeping the newest chooses at random
    /// with respect to loudness and loses the attack of every consonant that does not happen to
    /// land last. The tick takes this and clears it in one atomic operation, which is what makes
    /// the window this maximises over the same window that gets drawn.
    /// </remarks>
    private float _latestLevel;

    private readonly DispatcherTimer _levelTimer = new();

    /// <summary>A monotonic reading for the meter, so a clock change cannot stall or flood it.</summary>
    private readonly System.Diagnostics.Stopwatch _levelClock = System.Diagnostics.Stopwatch.StartNew();
    private DictationOverlayState _state = DictationOverlayState.Hidden;
    private DateTimeOffset _recordingStartedAt;
    private PillAction? _action;
    private string? _previewText;
    private int _overlayWidth = ClassicWidth;
    private int _overlayHeight = RecordingCapsuleHeight;

    /// <summary>The size the pill last asked for, in the units the layout is written in.</summary>
    /// <remarks>
    /// KEPT SO A SCALE CHANGE CAN BE REPLAYED. The physical size is the logical size times the
    /// scale, so once the scale moves, the only way back to a correct window is the number the pill
    /// originally asked for. Without these two, a monitor change could only be recovered by
    /// re-running whichever state handler happened to have set the size.
    /// </remarks>
    private int _logicalWidth = ClassicWidth;

    /// <summary>The pointer currently dragging the pill, if one is.</summary>
    private uint? _dragPointer;

    private PointInt32 _dragFromWindow;
    private NativePoint _dragFromCursor;

    /// <summary>True once this pill has been moved by hand, so placement leaves it alone.</summary>
    private bool _draggedThisPresentation;
    private bool _preWarmed;
    private int _logicalHeight = RecordingCapsuleHeight;

    /// <summary>Physical pixels per layout unit on the monitor the pill is currently on.</summary>
    /// <remarks>
    /// NOT READONLY, AND THAT IS THE FIX. This was read once in the constructor, from whichever
    /// monitor the app happened to start on. The pill then MOVES: it is placed on the monitor of
    /// whatever app the user is dictating into. On a two-monitor desk with different scales - a
    /// 150% laptop panel beside a 100% external, which is an ordinary setup - every size, the
    /// clipping region and the screen margin stayed computed for the monitor the pill had left.
    /// The pill came out too big or too small, and its clip no longer matched its own corners.
    /// </remarks>
    private double _rasterScale;

    private const uint SpiGetClientAreaAnimation = 0x1042;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref bool pvParam, uint fWinIni);

    /// <summary>
    /// Respects the user's Reduce Motion / animation effects preference (matching UISettings.AnimationsEnabled).
    /// </summary>
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
                // Fall back to enabled if system query fails.
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
        Resize(ClassicWidth, RecordingCapsuleHeight);
        DrawLips(0f);
        ApplySpectrumSweep();
        OverlayRoot.Measure(new Windows.Foundation.Size(ClassicWidth, RecordingCapsuleHeight));
        // HIGH CONTRAST REPAINTS THE SPECTRUM, so the blended bars are re-mixed from it.
        OverlayRoot.ActualThemeChanged += (_, _) => ApplySpectrumSweep();
        OverlayRoot.Loaded += (_, _) =>
        {
            if (OverlayRoot.XamlRoot is not { } root)
            {
                return;
            }

            root.Changed += OnXamlRootChanged;

            // SYNCHRONISE NOW, RATHER THAN WAIT FOR THE NEXT CHANGE. The pill is moved to the
            // foreground app's monitor BEFORE it is shown, so by the time this subscription exists
            // the scale may already be that of a different monitor - and a change that has already
            // happened raises no event. Waiting would leave the very first pill on a second monitor
            // sized for the first, which is the exact bug this subscription is here to prevent.
            ApplyScale(root.RasterizationScale);
        };
        // Windows 11 25H2 (26200.10075) with Windows App SDK 2.4.0 can throw E_NOTIMPL from
        // SetShownInSwitchers, so fall back to Win32 WS_EX_TOOLWINDOW to keep the overlay out of
        // Alt+Tab and the taskbar without crashing startup.
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

        // AND DWM STILL DRAWS ONE ANYWAY. SetBorderAndTitleBar(hasBorder: false) removes the frame
        // the presenter owns; the desktop compositor keeps painting its own hairline rounded
        // rectangle at the window bounds, which around a rounded pill reads as a second, squarer
        // outline floating outside the first. Asking DWM for no border colour is what removes it.
        // THE RESULT IS READ AND THEN DELIBERATELY IGNORED. A compositor that declines this leaves
        // a hairline border, which is cosmetic; refusing to open the pill over it would be worse
        // than the border. The discard is what says that on purpose rather than by omission.
        var borderColour = DwmColorNone;
        _ = DwmSetWindowAttribute(windowHandle, DwmwaBorderColor, ref borderColour, sizeof(uint));

        _hideTimer.Tick += (_, _) => HideOverlay();
        _elapsedTimer.Interval = TimeSpan.FromMilliseconds(250);
        _elapsedTimer.Tick += (_, _) => UpdateElapsed();

        // THE METER IS DRAWN ON ITS OWN SCHEDULE, NOT ON THE MICROPHONE'S. Capture reports a level
        // per audio buffer, roughly two hundred times a second, and posting each one to the UI
        // thread did that work whatever the throttle then decided - and under load the queue backed
        // up so the meter drew samples that were already old. The capture now records a number and
        // this asks for it at the rate the meter actually has.
        //
        // AND IT TICKS FASTER THAN THE SAMPLE INTERVAL, WHICH IS THE WHOLE REASON THE RAIL WAS DEAD.
        // Setting the timer to the sample interval put two clocks in series at the same period, and
        // the history's own gate rejects anything arriving early. Windows quantises timers to about
        // 15.6 milliseconds, so a fifty millisecond DispatcherTimer fires at 46.9 - reliably three
        // milliseconds SHORT of the gate, every single time. The rail therefore drew its first
        // sample and then never again, and sat pinned at its unlit four-DIP floor through
        // dictations that transcribed perfectly. Ticking at half the interval makes the history's
        // gate the single pacer, which is what it was written to be.
        _levelTimer.Interval = RecordingLevelHistory.SampleInterval / 2;
        _levelTimer.Tick += (_, _) => OnLevelTick();

        // Distress and error share a red, so the breathing is what separates them. Opacity is a
        // composition property, so this animates off the UI thread and does not need
        // EnableDependentAnimation - which also means it cannot stutter while a dictation is being
        // transcribed on the same machine.
        var breath = new DoubleAnimation
        {
            From = 1,
            To = 0.35,
            Duration = new Duration(TimeSpan.FromMilliseconds(650)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Storyboard.SetTarget(breath, SeverityWash);
        Storyboard.SetTargetProperty(breath, "Opacity");
        _distressPulse.Children.Add(breath);

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

        // THE HAIRLINE BREATHES ON OPACITY, a composition property, so it runs off the UI thread and
        // keeps its pace while a dictation is being transcribed on the same machine.
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

    /// <summary>Puts the recording pill in the same theme as the rest of the app.</summary>
    /// <remarks>
    /// THE PILL IS A SEPARATE TOP-LEVEL WINDOW, so setting the theme on the settings window never
    /// reached it. It followed the MACHINE instead - invisible while the two agree, and wrong the
    /// moment someone picks Light on a machine set to Dark.
    ///
    /// It is worse than a mismatched colour, because the settings window shows a PREVIEW of this
    /// pill and the preview does follow the app theme. So the preview was showing a pill that would
    /// never appear. A preview that lies is worse than no preview.
    ///
    /// Same class as the window's caption buttons: anything that is not inside the settings window's
    /// visual tree needs the theme handed to it deliberately.
    /// </remarks>
    public void ApplyTheme(ElementTheme theme) => OverlayRoot.RequestedTheme = theme;

    /// <summary>
    /// Pre-warms the overlay visual tree offscreen at startup so the very first recording press
    /// renders without cold XAML/compositor initialization delay.
    /// </summary>
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

    /// <summary>Puts one status on screen.</summary>
    /// <remarks>
    /// TAKES THE STATUS RATHER THAN A STATE AND A SENTENCE, because a status now carries a third
    /// thing - the one action the user can take about it - and three loose arguments is how the
    /// third one gets forgotten at a call site.
    /// </remarks>
    public void ShowState(DictationStatus status)
    {
        var state = status.State;
        var detail = status.Text;
        _hideTimer.Stop();
        if (state == DictationOverlayState.Hidden)
        {
            _state = state;
            HideOverlay();
            return;
        }

        var presentation = state switch
        {
            DictationOverlayState.Recording => ("Listening", "\uE720", "Release your key to finish · Escape cancels"),
            DictationOverlayState.Processing => (string.IsNullOrWhiteSpace(detail) ? "Working locally" : detail, string.Empty, string.Empty),
            // SUCCESS NOW MEANS ONE THING: THE WORDS WENT TO THE CLIPBOARD. A paste that landed says
            // nothing at all (the session goes Hidden), so this pill only ever reports a copy. ONE
            // LINE, as macOS's clipboard-fallback pill: the status sentence stays on the status line.
            DictationOverlayState.Success => ("Copied to clipboard", "\uE73E", string.Empty),
            // THE ADVISORY HEADING NAMES THE MACHINE, NOT THE APP, and that is the whole point of
            // the severity: "Dictation stopped safely" over a sentence about Ollama being switched
            // off tells the user our software broke when their setup is simply incomplete.
            DictationOverlayState.Advisory => ("Setup needs attention", "\uE946", detail),
            // THE GLOBE, AND THE HEADING SAYS THE APP WAS LISTENING RATHER THAN THAT SOMETHING IS
            // WRONG. This is the one pill that asks a question instead of reporting news.
            DictationOverlayState.Suggestion => ("A suggestion", "\uE774", detail),
            DictationOverlayState.Warning => ("Your text is safe", "\uE7BA", detail),
            DictationOverlayState.Busy => ("EnviousWispr is busy", "\uE7BA", detail),
            // Distress reuses the error glyph deliberately. It is the same bad news arriving
            // louder, and the pulse plus the deeper wash carry the difference. A codepoint chosen
            // for novelty is a hollow box on a machine whose font does not have it, and nothing in
            // this repository can see that.
            DictationOverlayState.Distress => ("Dictation interrupted", "\uEA39", detail),
            DictationOverlayState.Error => ("Dictation stopped safely", "\uEA39", detail),
            _ => ("EnviousWispr", "\uE720", detail),
        };
        var previousState = _state;
        _state = state;
        _preWarmed = true;

        if (state == DictationOverlayState.Recording)
        {
            if (previousState != DictationOverlayState.Recording)
            {
                _recordingStartedAt = DateTimeOffset.UtcNow;
                ElapsedText.Text = RecordingPillGeometry.FormatElapsed(TimeSpan.Zero);
                _previewText = null;
                _handsFree = status.IsHandsFree;
            }
            else if (status.IsHandsFree)
            {
                _handsFree = true;
            }

            _activeDesign = RecordingPillCatalog.Resolve(
                _livePreviewEnabled,
                _withoutWordsDesign,
                _withWordsDesign);
            if (previousState != DictationOverlayState.Recording)
            {
                // A NEW RECORDING STARTS EMPTY. Leaving the last one's shape on screen would show
                // somebody the sentence before this one as though they were still saying it.
                ResetLevelHistory();
            }

            ApplyAction(action: null);
            ConfigureRecordingDesign();
            _elapsedTimer.Start();
            _levelTimer.Start();
        }
        else
        {
            _elapsedTimer.Stop();
            _levelTimer.Stop();
            _handsFree = false;
            // The action is applied before the notice is sized, because the size depends on whether
            // there is a button. The other order clips it.
            ApplyAction(status.Action);
            ConfigureNotice(presentation.Item3, presentation.Item1);
        }

        ApplySeverity(state);
        // THE ANNOUNCEMENT LIVES ON A TEXT THAT IS ON SCREEN, NOT ON THE FRAME. WinUI creates no
        // automation peer for a Border, and none for a collapsed element, so the title carries it for
        // a notice, the label for Processing, and the clock or the state word for a recording pill,
        // whose title is no longer drawn.
        var spokenName = state switch
        {
            DictationOverlayState.Recording => RecordingSpokenName(),
            // THE SENTENCE ALONE, as macOS says it: "Transcribing a file. Please wait."
            DictationOverlayState.Busy => presentation.Item3,
            DictationOverlayState.Processing => presentation.Item1,
            _ => string.IsNullOrWhiteSpace(presentation.Item3)
                ? presentation.Item1
                : $"{presentation.Item1}. {presentation.Item3}",
        };
        StateTitle.Text = presentation.Item1;
        StateIcon.Glyph = presentation.Item2;
        StateDetail.Text = presentation.Item3;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(StateTitle, spokenName);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ProcessingLabel, spokenName);
        PositionOnForegroundMonitor();
        AppWindow.Show(activateWindow: false);
        // AFTER THE WINDOW IS SHOWN. Raising while it is still hidden announces something the user
        // cannot yet see, and the first state of a freshly shown pill is exactly that case.
        if (state == DictationOverlayState.Recording)
        {
            AnnounceRecording(spokenName);
        }
        else if (state == DictationOverlayState.Processing)
        {
            AnnounceStateChange(ProcessingLabel, state);
        }
        else
        {
            AnnounceStateChange(StateTitle, state);
        }

        if (state is DictationOverlayState.Success or DictationOverlayState.Advisory
            or DictationOverlayState.Suggestion or DictationOverlayState.Warning
            or DictationOverlayState.Distress or DictationOverlayState.Error
            or DictationOverlayState.Busy)
        {
            // AN ADVISORY DWELLS LONGEST BECAUSE IT ASKS THE USER TO DO SOMETHING. It names a
            // setting they have to go and change, which is more words than "your text is safe" and
            // more thought than a tick. macOS makes the same call and says so in its own source.
            _dwell = TimeSpan.FromSeconds(state switch
            {
                // A SUGGESTION DWELLS AS LONG AS AN ADVISORY. It asks a question and offers a
                // button, and a question the user has not finished reading is a question they
                // answer by default.
                // BUSY DWELLS AS LONG, because it carries a button the person may want to reach.
                DictationOverlayState.Advisory or DictationOverlayState.Suggestion or DictationOverlayState.Busy => 6d,
                DictationOverlayState.Error or DictationOverlayState.Distress => 5d,
                // "Copied to clipboard" is three words and needs no decision: macOS's 2.5 seconds.
                DictationOverlayState.Success => 2.5d,
                _ => 3d,
            });
            ArmDwell();
        }
        else
        {
            _dwell = TimeSpan.Zero;
        }
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

    /// <summary>Shows the hands-free (locked) look on the recording pill, or takes it away.</summary>
    /// <remarks>
    /// HANDS-FREE KEEPS RECORDING AFTER THE KEY IS RELEASED, so a pill that looks the same either way
    /// lets somebody walk away from an open microphone. Each design answers as macOS does: the
    /// Capsule doubles its mark and drops its clock, the Level Rail gains an inline badge, and the
    /// Reading Well swaps its state word for a filled badge.
    /// </remarks>
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

    /// <summary>Keeps the loudest level of the current frame. Draws nothing.</summary>
    /// <remarks>
    /// CALLED FROM THE CAPTURE'S OWN THREAD, ONCE PER AUDIO BUFFER. It records a number and returns,
    /// so a burst of buffers costs a burst of comparisons rather than a burst of UI work. The meter
    /// is drawn by the timer below, on the schedule the meter actually has.
    ///
    /// THE LOUDEST OF THE FRAME, NOT THE NEWEST, AND THE DIFFERENCE IS THE ATTACK OF EVERY
    /// CONSONANT. Capture reports about ten levels per frame and only one of them is ever drawn, so
    /// keeping the newest chooses at random with respect to loudness: a short loud packet landing
    /// anywhere but last in its frame was simply never seen. Keeping the loudest costs one compare
    /// and cannot lose a peak, and the frame is closed by the timer below rather than here, so the
    /// window this maximises over is exactly the window that gets drawn.
    ///
    /// LOCK-FREE BECAUSE OF WHERE IT IS CALLED FROM. A compare-and-swap loop keeps this off any
    /// lock the UI thread could ever contend for, which matters at two hundred calls a second.
    /// </remarks>
    public void SetAudioLevel(float rootMeanSquare) =>
        AccumulatePeak(RecordingLevelHistory.Normalize(rootMeanSquare));

    /// <summary>Raises the pending peak to this level, if it is louder.</summary>
    /// <remarks>
    /// SHARED WITH THE TICK, WHICH IS THE POINT. A poll that arrives before the frame is due has
    /// already taken the pending peak out, so it has to put it back - and putting it back with a
    /// plain write would erase a louder callback that arrived in between. Merging through the same
    /// loop the capture thread uses makes the two paths agree by construction.
    /// </remarks>
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
        if (_state != DictationOverlayState.Recording)
        {
            return;
        }

        // TAKING THE PEAK AND CLEARING IT IS ONE OPERATION, AND THAT ATOMICITY IS THE FRAME BOUNDARY.
        // Reading the peak and writing a zero back afterwards leaves a window between the two, and a
        // capture callback landing in that window is erased by the zero - so the loudest packet of a
        // frame could still be lost, by the very code that exists to keep it.
        var framePeak = Interlocked.Exchange(ref _latestLevel, 0f);

        // THE FRAME IS CLOSED FOR EVERY DESIGN, WHICH IS WHY THE GATE IS NOT INSIDE THE RAIL'S
        // BRANCH. The peak is taken above whatever the design, so leaving the gate to the rail meant
        // a pill with no rail never closed a frame at all.
        //
        // AND A POLL THAT IS TOO EARLY PUTS THE PEAK BACK. The timer polls at twice this rate, so
        // most ticks are rejected here, and each one has already removed the pending peak. Merging
        // it back through the same loop the capture thread uses is what stops an early poll from
        // quietly discarding the loudest packet of a frame that has not finished yet.
        if (!_levelHistory.Sample(framePeak, _levelClock.Elapsed))
        {
            AccumulatePeak(framePeak);
            return;
        }

        // THE LIPS OPEN WITH THE VOICE, on the Capsule, at the same 50 ms the meters sample. The
        // brand SVG this replaced could only fade, so a loud word and a quiet one looked the same.
        if (LipsMark.Visibility == Visibility.Visible)
        {
            DrawLips(framePeak);
        }

        DrawLevelHistory();
    }

    /// <summary>Draws the active design's meter from the level history, oldest bar on the left.</summary>
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

    /// <summary>One bar per remembered level, symmetric about the strip's centre line, at full opacity.</summary>
    /// <remarks>
    /// NO AGE FADE. macOS draws every bar of the history at full strength and lets the positional
    /// spectrum and the scroll carry time; the newest level is always the violet bar on the right.
    /// </remarks>
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

    /// <summary>Places the eighteen lips bars for this level.</summary>
    private void DrawLips(float level)
    {
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

    /// <summary>Blends both meters' bars into one red-to-violet sweep.</summary>
    /// <remarks>
    /// THE NINE STOPS ARE READ FROM THE LIPS MARK'S UPPER BARS, which carry the brand spectrum in
    /// order from their ThemeResource brushes, so a High Contrast theme that repaints the spectrum
    /// repaints the meters with it and no colour is restated here. The markup's own nearest-colour
    /// brushes stay as the fallback if that read ever fails.
    /// </remarks>
    private void ApplySpectrumSweep()
    {
        var stops = new Windows.UI.Color[RecordingPillGeometry.SpectrumStops];
        for (var index = 0; index < stops.Length; index++)
        {
            if (index >= LipsMark.Children.Count
                || LipsMark.Children[index] is not Border { Background: SolidColorBrush brush })
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
        if (!livePreviewEnabled)
        {
            SetPreview(text: null);
        }
    }

    public void Shutdown()
    {
        _hideTimer.Stop();
        _elapsedTimer.Stop();
        _levelTimer.Stop();
        Close();
    }

    private void ConfigureRecordingDesign()
    {
        _spectrumWheelPulse.Stop();
        SpectrumWheelTransform.Rotation = 0;
        SpectrumWheelIcon.Visibility = Visibility.Collapsed;
        StateIcon.Visibility = Visibility.Collapsed;
        StateDetail.Visibility = Visibility.Collapsed;
        ProcessingLabel.Visibility = Visibility.Collapsed;

        // NO WORD ON ANY RECORDING DESIGN. macOS draws the Capsule as its mark and its clock, the
        // Level Rail as its clock and its rail, and the Reading Well names its state in its own
        // header; the meter is the "listening". The screen reader still hears it, from the clock or
        // the state word, which is where the announcement moved.
        SetLiveVisibility(StateTitle, Visibility.Collapsed);

        var classic = _activeDesign == RecordingPillDesign.Classic;
        var rail = _activeDesign == RecordingPillDesign.LevelRail;
        var readingWell = _activeDesign == RecordingPillDesign.ReadingWell;

        // HANDS-FREE ON THE CAPSULE IS THE MARK AT TWICE ITS SIZE, AND NO CLOCK. The scale is a
        // render transform, so the pill does not change size; the mark overflows its own slot.
        LipsMark.Visibility = classic ? Visibility.Visible : Visibility.Collapsed;
        LipsMarkScale.ScaleX = LipsMarkScale.ScaleY = classic && _handsFree ? 2 : 1;
        ElapsedText.Visibility = classic && _handsFree ? Visibility.Collapsed : Visibility.Visible;
        ElapsedText.Style = GetPillStyle(readingWell ? "PillWellTimerTextStyle" : "PillCapsuleTimerTextStyle");
        LevelBars.Visibility = rail ? Visibility.Visible : Visibility.Collapsed;
        RailHandsFreeBadge.Visibility = rail && _handsFree ? Visibility.Visible : Visibility.Collapsed;
        WellMeter.Visibility = readingWell ? Visibility.Visible : Visibility.Collapsed;
        ModeLabel.Visibility = readingWell && !_handsFree ? Visibility.Visible : Visibility.Collapsed;
        WellHandsFreeBadge.Visibility = readingWell && _handsFree ? Visibility.Visible : Visibility.Collapsed;

        // THE CAPSULES CENTRE ONE ROW IN A 44-TALL PILL; THE READING WELL IS A RULED 34-TALL HEADER
        // with the clock hard left and the state word hard right, pinned to the top so it does not
        // move when the words arrive beneath it.
        HeaderLeading.Spacing = readingWell ? 12 : 10;
        HeaderRow.ColumnSpacing = readingWell ? 8 : 0;
        HeaderRow.HorizontalAlignment = readingWell ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
        HeaderRow.Height = readingWell ? ReadingWellHeaderHeight - FrameBorderThickness * 2 : double.NaN;
        HeaderRow.Padding = readingWell ? new Thickness(16, 0, 16, 0) : new Thickness(0);
        ContentPanel.Margin = readingWell ? new Thickness(0) : new Thickness(14, 0, 14, 0);
        ContentPanel.Spacing = 0;
        ContentPanel.VerticalAlignment = readingWell ? VerticalAlignment.Top : VerticalAlignment.Center;
        PreviewWell.Padding = ReadingWellInsets;

        ApplySurface(
            readingWell ? "PillWellSurfaceStyle" : "PillCapsuleSurfaceStyle",
            readingWell ? ReadingWellRadius : RecordingCapsuleRadius);

        // THE READING WELL'S HAIRLINE HOLDS STILL. It sits under a box that already grows a line at
        // a time, and the two movements read as one restless object.
        ApplyHairline(visible: true, breathes: !readingWell);
        PreviewText.Text = _previewText ?? string.Empty;

        switch (_activeDesign)
        {
            case RecordingPillDesign.Classic:
                PreviewWell.Visibility = Visibility.Collapsed;
                Resize(ClassicWidth, RecordingCapsuleHeight);
                break;
            // WIDER FOR TWENTY-FOUR BARS.
            case RecordingPillDesign.LevelRail:
                Resize(288, RecordingCapsuleHeight);
                PreviewWell.Visibility = Visibility.Collapsed;
                break;
            case RecordingPillDesign.ReadingWell:
                ResizeReadingWell();
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        DrawLevelHistory();
    }

    /// <summary>Puts the pill on one of its three surfaces, at that surface's corner radius.</summary>
    /// <remarks>
    /// THE SURFACE LIVES ON THE BLEED-BACKED ROOT, AND THE EDGE LIVES TWO UNITS INSIDE IT, so the
    /// window clip removes only the outer fringe and never slices off the 1px border or the bottom
    /// hairline.
    /// </remarks>
    private void ApplySurface(string surfaceStyle, double radius)
    {
        OverlayRoot.Style = GetPillStyle(surfaceStyle);
        OverlayRoot.CornerRadius = new CornerRadius(radius + WindowBleed);
        var innerCorners = new CornerRadius(radius);
        FrameBorder.CornerRadius = innerCorners;
        SeverityWash.CornerRadius = innerCorners;
    }

    /// <summary>Shows the rainbow hairline, breathing or still, or hides it.</summary>
    /// <remarks>
    /// THE STILL VALUE IS THE LOOP'S MID-POINT, NOT ITS DIM END. A line parked where a stopped loop
    /// happened to start is duller than the moving one, and nobody chose that. Reduce Motion gets
    /// the still line on the first frame rather than after a loop is started and stopped.
    /// </remarks>
    private void ApplyHairline(bool visible, bool breathes)
    {
        _hairlineBreath.Stop();
        BottomRainbowHairline.Opacity = RecordingPillGeometry.HairlineSteadyOpacity;
        BottomRainbowHairline.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible && breathes && AnimationsEnabled)
        {
            _hairlineBreath.Begin();
        }
    }

    /// <summary>Raised when the user presses the pill's button.</summary>
    public event Action<PillActionKind>? ActionInvoked;

    /// <summary>Shows the one thing the user can do about this status, or nothing.</summary>
    private void ApplyAction(PillAction? action)
    {
        _action = action;
        if (action is null)
        {
            ActionButton.Visibility = Visibility.Collapsed;
            ActionButton.Content = null;
            return;
        }

        ActionButton.Content = action.Label;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ActionButton, action.SpokenLabel);
        ActionButton.Visibility = Visibility.Visible;
    }

    private void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_action is not { } action)
        {
            return;
        }

        HideOverlay();
        ActionInvoked?.Invoke(action.Kind);
    }

    /// <summary>Starts the dwell clock, unless the pointer is on the pill.</summary>
    /// <remarks>
    /// A BUTTON ON A PILL THAT DISMISSES ITSELF IN SIX SECONDS IS A BUTTON NOBODY REACHES. Moving a
    /// mouse to it is most of that budget, and the pill would leave while the pointer was on its
    /// way. macOS pairs its action pills with hover-pause for exactly this reason, and shipping the
    /// button without it would have been a control that renders, builds clean and cannot be used.
    ///
    /// The pause applies to every dwelling pill rather than only the ones with a button, because
    /// the other thing a pointer resting on a notice means is that somebody is reading it.
    /// </remarks>
    private void ArmDwell()
    {
        _hideTimer.Stop();
        if (_dwell <= TimeSpan.Zero || _pointerIsOver)
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
    }

    /// <remarks>
    /// THE CLOCK RESTARTS FROM THE TOP RATHER THAN RESUMING WHAT WAS LEFT. A pill the user has just
    /// stopped reading deserves its whole dwell again; resuming a remainder makes a pill they
    /// looked at vanish faster than one they ignored.
    /// </remarks>
    private void OverlayRoot_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointerIsOver = false;
        ArmDwell();
    }

    /// <summary>Starts a drag, so the pill can be moved out of the way of what is underneath it.</summary>
    /// <remarks>
    /// THE PILL LANDS WHERE IT LANDS, AND SOMETIMES THAT IS ON TOP OF THE THING BEING DICTATED INTO.
    /// Top or bottom is a setting, but a setting is the wrong grain for "not there, just now" - the
    /// obstruction is about this window at this moment, not about a preference. macOS lets the pill
    /// be dragged for the presentation it is in.
    ///
    /// FOR THIS PRESENTATION ONLY, WHICH IS THE WHOLE POINT. Moving it once should not silently
    /// redefine where every future pill appears; the next one comes back where the setting says. Hide
    /// clears the override, so the rule is simply "until this pill goes away".
    ///
    /// THE POINTER IS CAPTURED so the drag survives leaving the pill's own bounds, which is otherwise
    /// the first thing that happens when somebody moves it any distance.
    /// </remarks>
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

        // MOVED BY THE CURSOR'S OWN TRAVEL, not by the pointer position inside the pill. Once the
        // window starts moving, a position measured against the window chases itself and the pill
        // slides away under the pointer.
        GetCursorPos(out var cursor);
        var moveX = cursor.X - _dragFromCursor.X;
        var moveY = cursor.Y - _dragFromCursor.Y;
        if (moveX == 0 && moveY == 0)
        {
            return;
        }

        // THE FLAG GOES UP BEFORE THE MOVE, NOT AFTER. Crossing onto a monitor with a different
        // scale makes Windows raise a DPI change, and this app answers that by re-placing the pill -
        // so a drag that crosses that boundary would snap back mid-gesture, while the flag that
        // stops exactly that was still one line away from being set.
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

    /// <summary>Takes the pill off screen and stops everything it left running.</summary>
    /// <remarks>
    /// BOTH HIDE PATHS GO THROUGH HERE, and they did not before. The dwell timer's tick stopped
    /// two timers; the Hidden state stopped one; neither stopped the distress pulse. So a pill that
    /// had breathed once went on breathing on an invisible window for as long as the app ran. It
    /// was invisible and it did not contaminate the next pill, because the next state stops the
    /// pulse before it draws - which is exactly why nothing would ever have reported it.
    ///
    /// One helper rather than three careful call sites, because the next thing added to this window
    /// that needs stopping will be added by somebody who reads one of them.
    /// </remarks>
    private void HideOverlay()
    {
        // THE METER STOPS WHEN THE PILL GOES. Hiding jumps straight here, so leaving it running woke
        // the app twenty times a second for the rest of the session to look at a pill nobody can see.
        _levelTimer.Stop();
        _hideTimer.Stop();
        _elapsedTimer.Stop();
        _spectrumWheelPulse.Stop();
        SpectrumWheelTransform.Rotation = 0;
        SpectrumWheelIcon.Visibility = Visibility.Collapsed;
        ApplyHairline(visible: false, breathes: false);
        _dwell = TimeSpan.Zero;
        _pointerIsOver = false;
        _handsFree = false;
        _previewText = null;
        // THE DRAG LASTED AS LONG AS THIS PILL DID. The next one returns to where the setting says,
        // which is what stops one nudge quietly becoming a new permanent position.
        _draggedThisPresentation = false;
        _dragPointer = null;
        ApplyDistressPulse(pulsing: false);
        AppWindow.Hide();
    }

    /// <summary>Colours the pill for how bad the news is.</summary>
    /// <remarks>
    /// BEFORE THIS, EVERY OUTCOME DREW THE SAME CAPSULE. An error, a warning and a success shared
    /// one surface, one border and one ink, and were told apart only by a small glyph most people
    /// never look at directly. The app's in-window notifications had the identical hole one surface
    /// over, and it was found the same way: nobody chose it, there was simply one tint token and
    /// the severities with none of their own fell through to the neutral card.
    ///
    /// THE SET IS THE UNIT. Every state that can reach the pill is answered here, including the two
    /// that take the neutral pair, so a state added later cannot render as a plain pill and look
    /// deliberate. The wash and the ink are chosen together in one expression for the same reason.
    /// </remarks>
    private void ApplySeverity(DictationOverlayState state)
    {
        // STYLES RATHER THAN BRUSHES, and spelled out rather than composed. A brush read from the
        // application dictionary resolves against the MACHINE's theme, while this window follows
        // the APP's - the trap ApplyTheme exists to close, one property over. A style's setters
        // resolve against the element they land on, so they follow the pill.
        //
        // Every name is written in full because the gate that checks these styles exist reads the
        // source text; a name built by interpolation is not a name it can see, so it would be
        // checked by nothing.
        var (icon, edge, wash) = state switch
        {
            DictationOverlayState.Success =>
                ("PillSuccessIconStyle", "PillSuccessEdgeStyle", "PillSuccessWashStyle"),
            // SUGGESTION SHARES THE ADVISORY PALETTE ON PURPOSE, and is listed beside it rather than
            // folded into the neutral arm. The violet already says "this is about your setup and
            // nothing is broken", which is exactly what a suggestion is.
            DictationOverlayState.Advisory or DictationOverlayState.Suggestion =>
                ("PillAdvisoryIconStyle", "PillAdvisoryEdgeStyle", "PillAdvisoryWashStyle"),
            // BUSY SHARES THE WARNING PALETTE ON PURPOSE: amber says the press did not do what was asked,
            // without saying anything broke or any text was at risk.
            DictationOverlayState.Warning or DictationOverlayState.Busy =>
                ("PillWarningIconStyle", "PillWarningEdgeStyle", "PillWarningWashStyle"),
            DictationOverlayState.Distress =>
                ("PillDistressIconStyle", "PillDistressEdgeStyle", "PillDistressWashStyle"),
            DictationOverlayState.Error =>
                ("PillErrorIconStyle", "PillErrorEdgeStyle", "PillErrorWashStyle"),
            // The three quiet states are LISTED rather than left to the default arm. A pill with
            // no severity is a choice about how it looks, and the gate that checks this set is
            // complete reads these names - an unlisted state would be covered by nothing and would
            // render as a plain capsule looking exactly as though somebody meant it.
            DictationOverlayState.Hidden or DictationOverlayState.Recording
                or DictationOverlayState.Processing =>
                ("PillNeutralIconStyle", "PillNeutralEdgeStyle", "PillNeutralWashStyle"),
            // An enum can hold a value nobody declared, so this arm has to exist. It takes the
            // neutral look rather than throwing, because a pill is not worth crashing a dictation.
            _ => ("PillNeutralIconStyle", "PillNeutralEdgeStyle", "PillNeutralWashStyle"),
        };

        StateIcon.Style = GetPillStyle(icon);
        FrameBorder.Style = state switch
        {
            DictationOverlayState.Recording => GetPillStyle(
                _activeDesign == RecordingPillDesign.ReadingWell ? "PillWellEdgeStyle" : "PillCapsuleEdgeStyle"),
            DictationOverlayState.Processing => GetPillStyle("PillCapsuleEdgeStyle"),
            _ => GetPillStyle(edge),
        };
        SeverityWash.Style = GetPillStyle(wash);
        // The wash fills the frame, so its corners have to be the frame's corners. Reading them off
        // the frame rather than restating the number is what stops a design change rounding one
        // layer and not the other.
        SeverityWash.CornerRadius = FrameBorder.CornerRadius;
        ApplyDistressPulse(state == DictationOverlayState.Distress);
    }

    /// <summary>Breathes the wash while something outside the app is interrupting a dictation.</summary>
    /// <remarks>
    /// THE PULSE IS WHAT SEPARATES DISTRESS FROM ERROR, because they share a colour. Stopping it
    /// explicitly on every other state matters more than starting it: a storyboard left running
    /// would breathe under the next success pill, and the state that started it would be long gone.
    /// </remarks>
    private void ApplyDistressPulse(bool pulsing)
    {
        _distressPulse.Stop();
        SeverityWash.Opacity = 1;
        if (!pulsing)
        {
            return;
        }

        if (AnimationsEnabled)
        {
            _distressPulse.Begin();
        }
    }

    private void ConfigureNotice(string? detailText, string title)
    {
        // A NOTICE IS NEVER THE RAIL OR READING WELL, so every recording-only piece goes and the row
        // goes back to its ordinary shape: glyph, title, and the notice's own spacing.
        LipsMark.Visibility = Visibility.Collapsed;
        LipsMarkScale.ScaleX = LipsMarkScale.ScaleY = 1;
        ElapsedText.Visibility = Visibility.Collapsed;
        LevelBars.Visibility = Visibility.Collapsed;
        WellMeter.Visibility = Visibility.Collapsed;
        RailHandsFreeBadge.Visibility = Visibility.Collapsed;
        ModeLabel.Visibility = Visibility.Collapsed;
        WellHandsFreeBadge.Visibility = Visibility.Collapsed;
        PreviewWell.Visibility = Visibility.Collapsed;
        HeaderLeading.Spacing = 10;
        HeaderRow.HorizontalAlignment = HorizontalAlignment.Stretch;
        HeaderRow.Height = double.NaN;
        HeaderRow.Padding = new Thickness(0);
        ContentPanel.VerticalAlignment = VerticalAlignment.Center;

        if (_state == DictationOverlayState.Processing)
        {
            // PROCESSING IS A CAPSULE THAT HUGS ITS LINE, as PolishingOverlayView.swift sizes it:
            // the wheel, a 10 gap, the label, and 14 either side, 44 tall, with the hairline breathing.
            StateIcon.Visibility = Visibility.Collapsed;
            SpectrumWheelIcon.Visibility = Visibility.Visible;
            StateDetail.Visibility = Visibility.Collapsed;
            SetLiveVisibility(StateTitle, Visibility.Collapsed);
            ProcessingLabel.Text = title;
            ProcessingLabel.Visibility = Visibility.Visible;
            HeaderRow.ColumnSpacing = 0;
            HeaderRow.HorizontalAlignment = HorizontalAlignment.Center;
            ContentPanel.Spacing = _noticeContentSpacing;
            ContentPanel.Margin = new Thickness(14, 0, 14, 0);
            ApplySurface("PillCapsuleSurfaceStyle", RecordingCapsuleRadius);
            ApplyHairline(visible: true, breathes: true);
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
        }
        else
        {
            _spectrumWheelPulse.Stop();
            SpectrumWheelTransform.Rotation = 0;
            SpectrumWheelIcon.Visibility = Visibility.Collapsed;
            ProcessingLabel.Visibility = Visibility.Collapsed;
            StateIcon.Visibility = Visibility.Visible;
            StateTitle.Style = GetPillStyle("PillNoticeTextStyle");
            SetLiveVisibility(StateTitle, Visibility.Visible);
            HeaderRow.ColumnSpacing = _noticeColumnSpacing;
            ContentPanel.Spacing = _noticeContentSpacing;
            ApplyHairline(visible: false, breathes: false);

            var hasDetail = !string.IsNullOrWhiteSpace(detailText);

            if (_action is not null)
            {
                ApplySurface("PillNoticeSurfaceStyle", NoticePanelRadius);
                ContentPanel.Margin = new Thickness(16, 12, 16, 12);
                StateDetail.Visibility = hasDetail ? Visibility.Visible : Visibility.Collapsed;
                Resize(NoticeMultiLineWidth, ActionNoticeHeight);
            }
            else if (hasDetail)
            {
                ApplySurface("PillNoticeSurfaceStyle", NoticePanelRadius);
                ContentPanel.Margin = new Thickness(16, 12, 16, 12);
                StateDetail.Visibility = Visibility.Visible;
                Resize(NoticeMultiLineWidth, NoticeMultiLineHeight);
            }
            else
            {
                ApplySurface("PillNoticeSurfaceStyle", NoticeCapsuleRadius);
                ContentPanel.Margin = new Thickness(14, 0, 14, 0);
                StateDetail.Visibility = Visibility.Collapsed;
                Resize(NoticeSingleLineWidth, CapsuleHeight);
            }
        }
    }

    /// <summary>The Processing capsule's width: its chrome plus the label's measured width.</summary>
    /// <remarks>
    /// MEASURED, NOT COUNTED. Only the text engine knows how wide a line is in this face at this
    /// size, and a fixed width either clips a long label or strands a short one in empty capsule.
    /// The character estimate is only the floor for a measure that returns nothing.
    /// </remarks>
    private int ProcessingPillWidth()
    {
        ProcessingLabel.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var label = ProcessingLabel.DesiredSize.Width;
        if (label <= 0)
        {
            label = ProcessingLabel.Text.Length * 7.0;
        }

        return (int)Math.Ceiling(ProcessingChromeWidth + label);
    }

    private static Style GetPillStyle(string key) =>
        (Style)Application.Current.Resources[key];

    /// <summary>Sizes the Reading Well to its header, or to its header and the words beneath it.</summary>
    /// <remarks>
    /// WAITING IS ONE HEADER TALL. The divider and the well exist only once there are words, so the
    /// growth a person sees is their own words arriving rather than space that was always reserved.
    ///
    /// THE TRANSCRIPT IS MEASURED AT ITS FULL HEIGHT AND PINNED TO THE BOTTOM OF A CLIPPED VIEWPORT
    /// of at most five lines. Overflow leaves at the top, the newest words stay where the eye is, and
    /// the top fades only when something has actually gone above it. macOS learned the hard way that
    /// a line limit with head truncation keeps the OLDEST lines and drops the middle.
    /// </remarks>
    private void ResizeReadingWell()
    {
        if (_previewText is null)
        {
            PreviewWell.Visibility = Visibility.Collapsed;
            PreviewFade.Visibility = Visibility.Collapsed;
            Resize(ReadingWellWidth, ReadingWellHeaderHeight);
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

            // The header, the 1 divider, then the well's own insets around the viewport.
            Resize(
                ReadingWellWidth,
                (int)Math.Ceiling(ReadingWellHeaderHeight + 1 + ReadingWellInsets.Top + viewport + ReadingWellInsets.Bottom));
        }

        if (_state == DictationOverlayState.Recording)
        {
            PositionOnForegroundMonitor();
        }
    }

    /// <summary>Re-applies the pill's own size when the monitor's scale changes under it.</summary>
    /// <remarks>
    /// THE SIZE IS REPLAYED, NOT RECOMPUTED FROM THE WINDOW. Reading the current physical size back
    /// and rescaling it compounds a rounding error every time the pill crosses a monitor boundary.
    /// The logical size it asked for is exact and does not drift.
    /// </remarks>
    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) =>
        ApplyScale(sender.RasterizationScale);

    /// <summary>Re-applies the pill's own size at a new scale, if it really is a new one.</summary>
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

    /// <summary>Tells a screen reader the pill has changed, and how urgently.</summary>
    /// <remarks>
    /// MARKING A LIVE REGION IS NOT ANNOUNCING IT. The pill has carried
    /// <c>AutomationProperties.LiveSetting</c> since it was written, and Narrator said nothing:
    /// WinUI raises no event of its own when the text inside a live region changes, so the app has
    /// to raise <c>LiveRegionChanged</c> itself or the setting is decoration. Every live region in
    /// this app was silent for the same reason.
    ///
    /// THE URGENCY IS PART OF THE MESSAGE, WHICH IS WHY IT IS SET HERE AND NOT IN MARKUP. A failure
    /// and an interrupted dictation are the two states where waiting for a gap in speech means
    /// hearing about it after the moment has passed, so those interrupt; everything else waits its
    /// turn, because a pill that talks over the user is worse than one that waits. macOS tags its
    /// announcements by priority for the same reason.
    ///
    /// IT HAS TO BE A CONTROL WITH A PEER. This was declared on the pill's root Border and WinUI
    /// creates no peer for one, so CreatePeerForElement returned null, the null-safe raise did
    /// nothing, and the pill stayed silent while looking fixed. It is on the title TextBlock now.
    /// </remarks>
    /// <summary>Shows or hides a live region, and tells a screen reader it changed.</summary>
    /// <remarks>
    /// A REGION THAT VANISHES SILENTLY IS WORSE THAN ONE THAT NEVER EXISTED. The Level Rail hides the
    /// state word because the meter says the same thing in the space the word would need, and a
    /// screen reader has no meter to read - so the change has to be announced or that user simply
    /// loses the sentence.
    /// </remarks>
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
        ClipToPillShape();
    }

    /// <summary>Cuts the window down to the rounded shape the pill is drawn in.</summary>
    /// <remarks>
    /// FOUR BLACK CORNERS SHIPPED, AND ONLY A PHOTOGRAPH OF A REAL SCREEN FOUND THEM. The window is
    /// a rectangle. The pill is a rounded Border inside it. Nothing made the rectangle transparent,
    /// so the four areas outside the rounded corners painted the window's own background - solid
    /// black - straight onto whatever the user was looking at. On a dark wallpaper it reads as a
    /// shadow; on a light one it is four black wedges around a floating notice.
    ///
    /// EVERY GATE IN THIS REPOSITORY PASSED WITH THIS ON SCREEN, because a gate reads markup and
    /// tokens and this is a property of the WINDOW, which markup does not describe.
    ///
    /// A REGION IS THE FIX, NOT A TRANSPARENT BACKDROP. Clipping the window makes the corners not
    /// belong to the window at all, so the desktop shows through with no compositing, no per-pixel
    /// alpha and nothing for a theme to get wrong. It also means clicks land on the desktop rather
    /// than on an invisible corner of ours.
    ///
    /// THE RADIUS IS READ FROM THE BORDER RATHER THAN REPEATED HERE. The pill has three radii - 18
    /// for a notice and the Reading Well, 29 for the capsules - and a second copy of that number is
    /// a second thing to keep in step. Called from Resize, so every size change re-clips: a region
    /// is measured in pixels and does not follow a window that changed shape.
    /// </remarks>
    private void ClipToPillShape()
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);

        // TIGHTER THAN THE WINDOW BY A HAIR, AND THAT MARGIN IS MEASURED RATHER THAN GUESSED. A
        // region exactly the size of the window left a three-pixel black outline tracing the pill,
        // photographed at 150% scale on a real screen: the window's background shows through
        // wherever the pill's antialiased edge is not fully opaque, and a hard-edged region cannot
        // clip a soft edge. Pulling the region in by the width of that fringe removes it. The cost
        // is a fraction of a pixel off the pill's own edge, which no one can see; the black halo
        // around a floating notice is the thing people would.
        // CLAMPED SO THE RECTANGLE CANNOT INVERT. Every shipping size stays far clear of this - the
        // smallest pill is 185 by 92 with a radius of 29 - but a region built from a negative width
        // is a window clipped to nothing, which is a pill that silently never appears. A guard that
        // costs two comparisons is cheaper than that failure being possible at all.
        var fringe = Math.Max(0, Math.Min(
            (int)Math.Floor(WindowBleed * _rasterScale),
            (Math.Min(_overlayWidth, _overlayHeight) - 1) / 2));
        var radius = Math.Max(0, (int)Math.Ceiling(
            FrameBorder.CornerRadius.TopLeft * _rasterScale * 2));
        var region = CreateRoundRectRgn(
            fringe,
            fringe,
            _overlayWidth - fringe + 1,
            _overlayHeight - fringe + 1,
            radius,
            radius);
        if (region == nint.Zero)
        {
            return;
        }

        // OWNERSHIP PASSES TO THE WINDOW ON SUCCESS, so the region is deleted only when the call
        // failed. Deleting it after a successful SetWindowRgn destroys the shape the window is
        // now using, and freeing it on every resize instead leaks one region per resize.
        if (SetWindowRgn(handle, region, bRedraw: true) == 0)
        {
            DeleteObject(region);
        }
    }

    private void UpdateElapsed() =>
        ElapsedText.Text = RecordingPillGeometry.FormatElapsed(DateTimeOffset.UtcNow - _recordingStartedAt);

    private void PositionOnForegroundMonitor()
    {
        // A PILL SOMEBODY HAS MOVED STAYS MOVED. Placement runs again on every status change within
        // one presentation, so without this the pill would snap back the moment its words changed -
        // which reads as the drag not having worked at all.
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

    /// <summary>Tells the compositor to paint no border colour at all.</summary>
    private const uint DwmColorNone = 0xFFFFFFFE;

    /// <summary>DWMWA_BORDER_COLOR.</summary>
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

    /// <summary>A screen position in physical pixels, which is what the cursor is reported in.</summary>
    /// <remarks>
    /// ITS OWN TYPE RATHER THAN ONE OF THE THREE Points ALREADY IN SCOPE. Windows.Foundation.Point is
    /// doubles in layout units, Windows.Graphics.PointInt32 is a window position, and a using
    /// directive decides which bare "Point" means what. A name that cannot be confused costs nothing.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
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
