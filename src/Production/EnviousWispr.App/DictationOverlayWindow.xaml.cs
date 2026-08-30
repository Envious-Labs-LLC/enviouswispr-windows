using System.Runtime.InteropServices;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Settings;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;

namespace EnviousWispr.App;

public sealed partial class DictationOverlayWindow : Window
{
    private const uint MonitorDefaultToNearest = 2;
    private const int NoticeWidth = 380;
    private const int NoticeHeight = 108;

    /// <summary>The notice height when the pill carries a button.</summary>
    /// <remarks>
    /// A PILL IS SIZED BY AN EXPLICIT Resize, NOT BY ITS CONTENTS, so a control added to the stack
    /// does not make the window taller - it renders outside it and is clipped. A button whose
    /// bottom half is cut off is the same family as a change that ships and does nothing: it
    /// builds, it passes every gate, and the user cannot use it.
    ///
    /// DERIVED, NOT CHOSEN, so the next person can check it rather than trust it.
    /// <see cref="NoticeHeight"/> is 108 for a heading and two lines of detail. Adding the button
    /// costs one <c>BrandSpacingS</c> gap, which is 8, plus the button itself: a 14px line at
    /// roughly 20, its 14,6 padding at 12, and its 1px border at 2, which is 34 and clears the
    /// platform Button's own 32 minimum. 108 + 8 + 34 = 150.
    /// </remarks>
    private const int ActionNoticeHeight = 150;
    private const int WorkAreaMargin = 28;

    private readonly DispatcherTimer _hideTimer = new();
    private readonly DispatcherTimer _elapsedTimer = new();
    private readonly Storyboard _distressPulse = new();
    private TimeSpan _dwell = TimeSpan.Zero;
    private bool _pointerIsOver;
    private OverlayPillPosition _position = OverlayPillPosition.Top;
    private bool _livePreviewEnabled;
    private RecordingPillDesign _withoutWordsDesign = RecordingPillDesign.Classic;
    private RecordingPillDesign _withWordsDesign = RecordingPillDesign.ReadingWell;
    private RecordingPillDesign _activeDesign = RecordingPillDesign.Classic;
    private DictationOverlayState _state = DictationOverlayState.Hidden;
    private DateTimeOffset _recordingStartedAt;
    private PillAction? _action;
    private string? _previewText;
    private int _overlayWidth = NoticeWidth;
    private int _overlayHeight = NoticeHeight;

    /// <summary>The size the pill last asked for, in the units the layout is written in.</summary>
    /// <remarks>
    /// KEPT SO A SCALE CHANGE CAN BE REPLAYED. The physical size is the logical size times the
    /// scale, so once the scale moves, the only way back to a correct window is the number the pill
    /// originally asked for. Without these two, a monitor change could only be recovered by
    /// re-running whichever state handler happened to have set the size.
    /// </remarks>
    private int _logicalWidth = NoticeWidth;
    private int _logicalHeight = NoticeHeight;

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

    public DictationOverlayWindow()
    {
        InitializeComponent();
        var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = GetDpiForWindow(windowHandle);
        _rasterScale = dpi > 0 ? dpi / 96d : 1d;
        Resize(NoticeWidth, NoticeHeight);
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
        AppWindow.IsShownInSwitchers = false;
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
            DictationOverlayState.Processing => ("Working locally", "\uE895", detail),
            DictationOverlayState.Success => ("Dictation complete", "\uE73E", detail),
            // THE ADVISORY HEADING NAMES THE MACHINE, NOT THE APP, and that is the whole point of
            // the severity: "Dictation stopped safely" over a sentence about Ollama being switched
            // off tells the user our software broke when their setup is simply incomplete.
            DictationOverlayState.Advisory => ("Setup needs attention", "\uE946", detail),
            DictationOverlayState.Warning => ("Your text is safe", "\uE7BA", detail),
            // Distress reuses the error glyph deliberately. It is the same bad news arriving
            // louder, and the pulse plus the deeper wash carry the difference. A codepoint chosen
            // for novelty is a hollow box on a machine whose font does not have it, and nothing in
            // this repository can see that.
            DictationOverlayState.Distress => ("Dictation interrupted", "\uEA39", detail),
            DictationOverlayState.Error => ("Dictation stopped safely", "\uEA39", detail),
            _ => ("EnviousWispr", "\uE720", detail),
        };
        if (state == DictationOverlayState.Recording)
        {
            if (_state != DictationOverlayState.Recording)
            {
                _recordingStartedAt = DateTimeOffset.UtcNow;
                ElapsedText.Text = "00:00";
            }

            _activeDesign = RecordingPillCatalog.Resolve(
                _livePreviewEnabled,
                _withoutWordsDesign,
                _withWordsDesign);
            ApplyAction(action: null);
            ConfigureRecordingDesign();
            _elapsedTimer.Start();
        }
        else
        {
            _elapsedTimer.Stop();
            // The action is applied before the notice is sized, because the size depends on whether
            // there is a button. The other order clips it.
            ApplyAction(status.Action);
            ConfigureNotice();
        }

        _state = state;
        ApplySeverity(state);
        StateTitle.Text = presentation.Item1;
        StateIcon.Glyph = presentation.Item2;
        StateDetail.Text = presentation.Item3;
        // THE ANNOUNCEMENT LIVES ON THE TITLE, NOT ON THE FRAME. WinUI creates no automation peer for
        // a Border, so a live region declared there has nothing to raise through and the raise
        // silently does nothing - which is how this shipped "fixed" and still said nothing at all.
        // A TextBlock has a peer. The whole sentence goes on its Name, because that is what a screen
        // reader reads when a live region changes.
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            StateTitle,
            state == DictationOverlayState.Recording
                ? $"{RecordingPillCatalog.DisplayName(_activeDesign)} recording pill. {presentation.Item1}."
                : $"{presentation.Item1}. {presentation.Item3}");
        PositionOnForegroundMonitor();
        AppWindow.Show(activateWindow: false);
        // AFTER THE WINDOW IS SHOWN. Raising while it is still hidden announces something the user
        // cannot yet see, and the first state of a freshly shown pill is exactly that case.
        AnnounceStateChange(StateTitle, state);

        if (state is DictationOverlayState.Success or DictationOverlayState.Advisory
            or DictationOverlayState.Warning or DictationOverlayState.Distress
            or DictationOverlayState.Error)
        {
            // AN ADVISORY DWELLS LONGEST BECAUSE IT ASKS THE USER TO DO SOMETHING. It names a
            // setting they have to go and change, which is more words than "your text is safe" and
            // more thought than a tick. macOS makes the same call and says so in its own source.
            _dwell = TimeSpan.FromSeconds(state switch
            {
                DictationOverlayState.Advisory => 6,
                DictationOverlayState.Error or DictationOverlayState.Distress => 5,
                _ => 3,
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
        ApplyPreviewInk();
        if (_state == DictationOverlayState.Recording &&
            _activeDesign == RecordingPillDesign.ReadingWell)
        {
            PreviewText.Text = _previewText ?? "Listening…";
            ResizeReadingWell();
        }
    }

    public void SetAudioLevel(float rootMeanSquare)
    {
        if (_state != DictationOverlayState.Recording ||
            _activeDesign != RecordingPillDesign.LevelRail)
        {
            return;
        }

        var normalized = Math.Clamp(MathF.Sqrt(Math.Max(0, rootMeanSquare) * 4f), 0f, 1f);
        for (var index = 0; index < LevelBars.Children.Count; index++)
        {
            if (LevelBars.Children[index] is not Border bar)
            {
                continue;
            }

            var wave = 0.45f + 0.55f * MathF.Abs(MathF.Sin(index * 1.7f + normalized * 5.1f));
            bar.Height = 5 + 20 * normalized * wave;
            bar.Opacity = 0.35 + 0.65 * normalized;
        }
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
        Close();
    }

    private void ConfigureRecordingDesign()
    {
        StateTitle.Style = GetPillStyle("PillModeQuietTextStyle");
        StateIcon.Visibility = Visibility.Collapsed;
        StateDetail.Visibility = Visibility.Collapsed;
        ElapsedText.Visibility = Visibility.Visible;
        RainbowMark.Visibility = _activeDesign == RecordingPillDesign.Classic
            ? Visibility.Visible
            : Visibility.Collapsed;
        LevelBars.Visibility = _activeDesign == RecordingPillDesign.LevelRail
            ? Visibility.Visible
            : Visibility.Collapsed;
        PreviewWell.Visibility = _activeDesign == RecordingPillDesign.ReadingWell
            ? Visibility.Visible
            : Visibility.Collapsed;
        PreviewText.Text = _previewText ?? "Listening…";
        ApplyPreviewInk();
        OverlayRoot.CornerRadius = _activeDesign == RecordingPillDesign.ReadingWell
            ? new CornerRadius(18)
            : new CornerRadius(29);
        // On the CONTENT, not the frame: a Border lays its child out inside its padding, so a
        // frame that owns the padding insets the severity wash away from the capsule edge.
        ContentPanel.Margin = new Thickness(16, 12, 16, 12);

        switch (_activeDesign)
        {
            case RecordingPillDesign.Classic:
                Resize(185, 92);
                break;
            case RecordingPillDesign.LevelRail:
                Resize(288, 92);
                break;
            case RecordingPillDesign.ReadingWell:
                ResizeReadingWell();
                break;
            default:
                throw new ArgumentOutOfRangeException();
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
        _hideTimer.Stop();
        _elapsedTimer.Stop();
        _dwell = TimeSpan.Zero;
        _pointerIsOver = false;
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
            DictationOverlayState.Advisory =>
                ("PillAdvisoryIconStyle", "PillAdvisoryEdgeStyle", "PillAdvisoryWashStyle"),
            DictationOverlayState.Warning =>
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
        OverlayRoot.Style = GetPillStyle(edge);
        SeverityWash.Style = GetPillStyle(wash);
        // The wash fills the pill, so its corners have to be the pill's corners. Reading them off
        // the root rather than restating the number is what stops a design change rounding one
        // layer and not the other.
        SeverityWash.CornerRadius = OverlayRoot.CornerRadius;
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

        _distressPulse.Begin();
    }

    private void ConfigureNotice()
    {
        StateTitle.Style = GetPillStyle("PillNoticeTextStyle");
        RainbowMark.Visibility = Visibility.Collapsed;
        StateIcon.Visibility = Visibility.Visible;
        StateDetail.Visibility = Visibility.Visible;
        ElapsedText.Visibility = Visibility.Collapsed;
        LevelBars.Visibility = Visibility.Collapsed;
        PreviewWell.Visibility = Visibility.Collapsed;
        OverlayRoot.CornerRadius = new CornerRadius(18);
        ContentPanel.Margin = new Thickness(18, 14, 18, 14);
        Resize(NoticeWidth, _action is null ? NoticeHeight : ActionNoticeHeight);
    }

    private void ApplyPreviewInk()
    {
        var styleKey = _previewText is null ? "PillDimmedTextStyle" : "PillLiveTextStyle";
        PreviewText.Style = GetPillStyle(styleKey);
    }

    private static Style GetPillStyle(string key) =>
        (Style)Application.Current.Resources[key];



    private void ResizeReadingWell()
    {
        var textLength = _previewText?.Length ?? 0;
        var estimatedLines = Math.Clamp((textLength + 43) / 44, 1, 5);
        Resize(400, 86 + estimatedLines * 22);
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
        _overlayWidth = Math.Max(1, (int)Math.Ceiling(width * _rasterScale));
        _overlayHeight = Math.Max(1, (int)Math.Ceiling(height * _rasterScale));
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
            (int)Math.Ceiling(2 * _rasterScale),
            (Math.Min(_overlayWidth, _overlayHeight) - 1) / 2));
        var radius = Math.Max(0, (int)Math.Ceiling(
            (OverlayRoot.CornerRadius.TopLeft * _rasterScale - fringe) * 2));
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

    private void UpdateElapsed()
    {
        var elapsed = DateTimeOffset.UtcNow - _recordingStartedAt;
        var totalMinutes = Math.Clamp((int)elapsed.TotalMinutes, 0, 99);
        ElapsedText.Text = $"{totalMinutes:00}:{elapsed.Seconds:00}";
    }

    private void PositionOnForegroundMonitor()
    {
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
