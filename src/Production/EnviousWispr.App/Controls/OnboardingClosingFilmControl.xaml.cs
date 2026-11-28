using System.Diagnostics;
using EnviousWispr.Core.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace EnviousWispr.App.Controls;

/// <summary>
/// Single-clock 7-scene (35.5 s) animated Windows 11 Closing Film driven by
/// <see cref="OnboardingClosingFilmEngine"/> with clock-synced <see cref="RainbowLipsControl"/>,
/// clickable progress segments, Pause/Play, Watch again, accessibility announcements,
/// and settled-frame Reduce Motion support.
/// </summary>
public sealed partial class OnboardingClosingFilmControl : UserControl
{
    public static readonly DependencyProperty SpeechModelNameProperty = DependencyProperty.Register(
        nameof(SpeechModelName),
        typeof(string),
        typeof(OnboardingClosingFilmControl),
        new PropertyMetadata("Parakeet", OnSummaryChanged));

    public static readonly DependencyProperty PolishNameProperty = DependencyProperty.Register(
        nameof(PolishName),
        typeof(string),
        typeof(OnboardingClosingFilmControl),
        new PropertyMetadata("EG-1 Mini", OnSummaryChanged));

    public static readonly DependencyProperty KeyLabelProperty = DependencyProperty.Register(
        nameof(KeyLabel),
        typeof(string),
        typeof(OnboardingClosingFilmControl),
        new PropertyMetadata("Ctrl+Win", OnSummaryChanged));

    public static readonly DependencyProperty KeyCapProperty = DependencyProperty.Register(
        nameof(KeyCap),
        typeof(string),
        typeof(OnboardingClosingFilmControl),
        new PropertyMetadata("Ctrl+Win", OnSummaryChanged));

    public static readonly DependencyProperty ReduceMotionProperty = DependencyProperty.Register(
        nameof(ReduceMotion),
        typeof(bool),
        typeof(OnboardingClosingFilmControl),
        new PropertyMetadata(false, OnReduceMotionChanged));

    public static readonly DependencyProperty ShowBuiltInControlsProperty = DependencyProperty.Register(
        nameof(ShowBuiltInControls),
        typeof(bool),
        typeof(OnboardingClosingFilmControl),
        new PropertyMetadata(true, OnShowBuiltInControlsChanged));

    private readonly ScaleTransform[] _segmentScales;
    private readonly Border[] _segmentFills;
    private readonly UIElement[] _scenePanels;
    private readonly List<Border> _moreTileBorders = [];
    private readonly List<TranslateTransform> _moreTileTranslates = [];
    private readonly List<(Border Badge, string BrushKey)> _appBadges = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private double _anchorWallSeconds;
    private double _anchorFilmSeconds;
    private bool _isPaused;
    private bool _isLoaded;
    private bool _isRenderingSubscribed;
    private int _lastAnnouncedScene = -1;
    private long _visibilityCallbackToken;

    public OnboardingClosingFilmControl()
    {
        InitializeComponent();

        _segmentScales =
        [
            SegmentScale0, SegmentScale1, SegmentScale2, SegmentScale3,
            SegmentScale4, SegmentScale5, SegmentScale6,
        ];
        _segmentFills =
        [
            SegmentFill0, SegmentFill1, SegmentFill2, SegmentFill3,
            SegmentFill4, SegmentFill5, SegmentFill6,
        ];
        _scenePanels =
        [
            SceneReadyPanel, SceneAppsPanel, SceneLandPanel, SceneCopiedPanel,
            SceneLearnPanel, SceneMorePanel, SceneYoursPanel,
        ];

        BuildAppsWall();
        BuildMoreFeaturesWall();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += OnActualThemeChanged;
    }

    public event EventHandler? WatchAgainRequested;

    public event EventHandler<int>? SceneChanged;

    public event EventHandler<ClosingFilmEvaluation>? FrameEvaluated;

    public event EventHandler? PlaybackStateChanged;

    /// <summary>Optional accessibility callback invoked once each time the active scene changes.</summary>
    public Action<string>? SceneAnnounced { get; set; }

    public string SpeechModelName
    {
        get => (string)GetValue(SpeechModelNameProperty);
        set => SetValue(SpeechModelNameProperty, value);
    }

    public string? PolishName
    {
        get => (string?)GetValue(PolishNameProperty);
        set => SetValue(PolishNameProperty, value);
    }

    public string KeyLabel
    {
        get => (string)GetValue(KeyLabelProperty);
        set => SetValue(KeyLabelProperty, value);
    }

    public string KeyCap
    {
        get => (string)GetValue(KeyCapProperty);
        set => SetValue(KeyCapProperty, value);
    }

    public bool ReduceMotion
    {
        get => (bool)GetValue(ReduceMotionProperty);
        set => SetValue(ReduceMotionProperty, value);
    }

    public bool ShowBuiltInControls
    {
        get => (bool)GetValue(ShowBuiltInControlsProperty);
        set => SetValue(ShowBuiltInControlsProperty, value);
    }

    public bool IsPaused => _isPaused;

    public void AdvanceOrTogglePause()
    {
        if (ReduceMotion)
        {
            var next = (CurrentSceneIndex + 1) % OnboardingClosingFilmEngine.SceneCount;
            SeekToScene(next);
            return;
        }

        TogglePause();
    }

    public int CurrentSceneIndex { get; private set; }

    public double CurrentFilmSeconds =>
        Math.Max(0.0, _isPaused ? _anchorFilmSeconds : _anchorFilmSeconds + Math.Max(0.0, _clock.Elapsed.TotalSeconds - _anchorWallSeconds));

    public void Pause()
    {
        if (_isPaused)
        {
            return;
        }

        _anchorFilmSeconds = CurrentFilmSeconds;
        _anchorWallSeconds = _clock.Elapsed.TotalSeconds;
        _isPaused = true;
        UpdatePausePlayButton();
        SyncRenderingSubscription();
        RenderAt(CurrentFilmSeconds);
    }

    public void Play()
    {
        if (!_isPaused)
        {
            return;
        }

        _anchorWallSeconds = _clock.Elapsed.TotalSeconds;
        _isPaused = false;
        UpdatePausePlayButton();
        SyncRenderingSubscription();
        RenderAt(CurrentFilmSeconds);
    }

    public void TogglePause()
    {
        if (_isPaused)
        {
            Play();
        }
        else
        {
            Pause();
        }
    }

    public void SeekToScene(int sceneIndex)
    {
        var clamped = Math.Clamp(sceneIndex, 0, OnboardingClosingFilmEngine.SceneCount - 1);
        _anchorFilmSeconds = OnboardingClosingFilmEngine.GetSceneStartSeconds(clamped);
        _anchorWallSeconds = _clock.Elapsed.TotalSeconds;
        _isPaused = ReduceMotion;
        UpdatePausePlayButton();
        SyncRenderingSubscription();
        RenderAt(_anchorFilmSeconds);
    }

    public void Restart()
    {
        _anchorFilmSeconds = 0.0;
        _anchorWallSeconds = _clock.Elapsed.TotalSeconds;
        _isPaused = ReduceMotion;
        _lastAnnouncedScene = -1;
        UpdatePausePlayButton();
        SyncRenderingSubscription();
        RenderAt(0.0);
    }

    private static void OnSummaryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is OnboardingClosingFilmControl control)
        {
            control.SyncSummaryText();
            control.RenderAt(control.CurrentFilmSeconds);
        }
    }

    private static void OnShowBuiltInControlsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is OnboardingClosingFilmControl control && control.BuiltInTransportGrid is not null)
        {
            control.BuiltInTransportGrid.Visibility = control.ShowBuiltInControls
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private static void OnReduceMotionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is OnboardingClosingFilmControl control)
        {
            control.FilmLips.ReduceMotion = control.ReduceMotion;
            if (control.ReduceMotion)
            {
                control._anchorFilmSeconds = OnboardingClosingFilmEngine.GetSceneStartSeconds(control.CurrentSceneIndex);
                control._isPaused = true;
            }
            else
            {
                control._anchorWallSeconds = control._clock.Elapsed.TotalSeconds;
                control._isPaused = false;
            }

            control.UpdatePausePlayButton();
            control.SyncRenderingSubscription();
            control.RenderAt(control.CurrentFilmSeconds);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        if (_visibilityCallbackToken == 0)
        {
            _visibilityCallbackToken = RegisterPropertyChangedCallback(VisibilityProperty, OnVisibilityChanged);
        }

        _anchorWallSeconds = _clock.Elapsed.TotalSeconds;
        if (ReduceMotion)
        {
            _isPaused = true;
        }

        FilmLips.ReduceMotion = ReduceMotion;
        if (BuiltInTransportGrid is not null)
        {
            BuiltInTransportGrid.Visibility = ShowBuiltInControls ? Visibility.Visible : Visibility.Collapsed;
        }
        SyncSummaryText();
        RefreshThemeBrushes();
        UpdatePausePlayButton();
        SyncRenderingSubscription();
        RenderAt(CurrentFilmSeconds);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        if (_visibilityCallbackToken != 0)
        {
            UnregisterPropertyChangedCallback(VisibilityProperty, _visibilityCallbackToken);
            _visibilityCallbackToken = 0;
        }

        StopRendering();
    }

    private void OnVisibilityChanged(DependencyObject sender, DependencyProperty dp)
    {
        if (!IsEffectivelyVisible())
        {
            _anchorFilmSeconds = CurrentFilmSeconds;
            _anchorWallSeconds = _clock.Elapsed.TotalSeconds;
            StopRendering();
            return;
        }

        _anchorWallSeconds = _clock.Elapsed.TotalSeconds;
        SyncRenderingSubscription();
        RenderAt(CurrentFilmSeconds);
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        RefreshThemeBrushes();
        RenderAt(CurrentFilmSeconds);
    }

    private void SegmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string raw && int.TryParse(raw, out var index))
        {
            SeekToScene(index);
        }
    }

    private void PausePlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (ReduceMotion)
        {
            var next = (CurrentSceneIndex + 1) % OnboardingClosingFilmEngine.SceneCount;
            SeekToScene(next);
            return;
        }

        TogglePause();
    }

    private void WatchAgainButton_Click(object sender, RoutedEventArgs e)
    {
        Restart();
        WatchAgainRequested?.Invoke(this, EventArgs.Empty);
    }

    private bool IsEffectivelyVisible()
    {
        for (DependencyObject? current = this; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement element && element.Visibility != Visibility.Visible)
            {
                return false;
            }
        }

        return true;
    }

    private void SyncRenderingSubscription()
    {
        var shouldRun = _isLoaded
            && IsEffectivelyVisible()
            && !_isPaused
            && !ReduceMotion;

        if (shouldRun)
        {
            if (!_isRenderingSubscribed)
            {
                CompositionTarget.Rendering += OnCompositionTargetRendering;
                _isRenderingSubscribed = true;
            }
        }
        else
        {
            StopRendering();
        }
    }

    private void StopRendering()
    {
        if (!_isRenderingSubscribed)
        {
            return;
        }

        CompositionTarget.Rendering -= OnCompositionTargetRendering;
        _isRenderingSubscribed = false;
    }

    private void OnCompositionTargetRendering(object? sender, object e)
    {
        if (!_isLoaded || !IsEffectivelyVisible() || _isPaused || ReduceMotion)
        {
            StopRendering();
            return;
        }

        RenderAt(CurrentFilmSeconds);
    }

    private ClosingFilmSummary CurrentSummary() =>
        new(SpeechModelName, PolishName, KeyLabel, KeyCap);

    private void SyncSummaryText()
    {
        var summary = CurrentSummary();
        ReadySpeechValueText.Text = summary.EffectiveSpeechModel;
        ReadyPolishValueText.Text = summary.EffectivePolish;
        ReadyKeyValueText.Text = summary.EffectiveKeyLabel;
        LandHeldKeyText.Text = summary.EffectiveKeyCap;
    }

    private void UpdatePausePlayButton()
    {
        if (ReduceMotion)
        {
            PausePlayButton.Content = "Next tip";
            AutomationProperties.SetName(PausePlayButton, "Next closing film tip");
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        PausePlayButton.Content = _isPaused ? "Play" : "Pause";
        AutomationProperties.SetName(PausePlayButton, _isPaused ? "Play closing film" : "Pause closing film");
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RenderAt(double filmSeconds)
    {
        var summary = CurrentSummary();
        var frame = OnboardingClosingFilmEngine.Evaluate(filmSeconds, summary, ReduceMotion);

        CurrentSceneIndex = frame.SceneIndex;

        // Clock-synced Rainbow Lips
        FilmLips.ReduceMotion = ReduceMotion;
        FilmLips.Mood = frame.LipsMood;
        FilmLips.ExternalMoodElapsedSeconds = frame.LipsMoodElapsedSeconds;
        FilmLips.ExternalClockSeconds = frame.FilmSeconds;

        // Caption opacity and rise
        CaptionTitleText.Text = frame.ActiveCaptionTitle;
        CaptionSubtitleText.Text = frame.ActiveCaptionSubtitle;
        CaptionSubtitleText.Visibility = string.IsNullOrWhiteSpace(frame.ActiveCaptionSubtitle)
            ? Visibility.Collapsed
            : Visibility.Visible;

        var leadCaption = frame.CaptionLines
            .OrderByDescending(line => line.Opacity)
            .FirstOrDefault();
        CaptionStack.Opacity = ReduceMotion ? 1.0 : Math.Clamp(leadCaption?.Opacity ?? 1.0, 0.0, 1.0);
        CaptionTranslate.Y = ReduceMotion ? 0.0 : (leadCaption?.OffsetY ?? 0.0);

        // Progress segments
        for (var i = 0; i < _segmentScales.Length && i < frame.SegmentProgress.Length; i++)
        {
            _segmentScales[i].ScaleX = Math.Clamp(frame.SegmentProgress[i], 0.0, 1.0);
        }

        // Active scene visibility
        for (var i = 0; i < _scenePanels.Length; i++)
        {
            _scenePanels[i].Visibility = i == frame.SceneIndex ? Visibility.Visible : Visibility.Collapsed;
        }

        switch (frame.SceneKind)
        {
            case ClosingFilmSceneKind.Ready:
                RenderReadyScene(frame);
                break;
            case ClosingFilmSceneKind.Apps:
                RenderAppsScene(frame);
                break;
            case ClosingFilmSceneKind.Land:
                RenderLandScene(frame);
                break;
            case ClosingFilmSceneKind.Copied:
                RenderCopiedScene(frame);
                break;
            case ClosingFilmSceneKind.Learn:
                RenderLearnScene(frame);
                break;
            case ClosingFilmSceneKind.More:
                RenderMoreScene(frame);
                break;
            case ClosingFilmSceneKind.Yours:
                RenderYoursScene(frame);
                break;
        }

        FrameEvaluated?.Invoke(this, frame);

        if (_lastAnnouncedScene != frame.SceneIndex)
        {
            _lastAnnouncedScene = frame.SceneIndex;
            SceneChanged?.Invoke(this, frame.SceneIndex);
            SceneAnnounced?.Invoke(frame.SpokenSummary);
        }
    }

    private void RenderReadyScene(ClosingFilmEvaluation frame)
    {
        var r0 = frame.Kept(OnboardingClosingFilmEngine.Ready.Row(0));
        var r1 = frame.Kept(OnboardingClosingFilmEngine.Ready.Row(1));
        var r2 = frame.Kept(OnboardingClosingFilmEngine.Ready.Row(2));

        ReadyRow0.Opacity = r0;
        ReadyRow0Translate.Y = 10.0 * (1.0 - r0);
        ReadyRow1.Opacity = r1;
        ReadyRow1Translate.Y = 10.0 * (1.0 - r1);
        ReadyRow2.Opacity = r2;
        ReadyRow2Translate.Y = 10.0 * (1.0 - r2);
    }

    private void RenderAppsScene(ClosingFilmEvaluation frame)
    {
        var drift = frame.ReduceMotion ? 0.0 : frame.Value(OnboardingClosingFilmEngine.Apps.Drift);
        var textBox = frame.Kept(OnboardingClosingFilmEngine.Apps.TextBox);
        var pill = frame.Kept(OnboardingClosingFilmEngine.Apps.Pill);

        AppsWallTranslate.Y = -36.0 * drift;
        AppsWallGrid.Opacity = 0.55;

        AppsTextBoxCard.Opacity = textBox;
        AppsTextBoxTranslate.Y = 14.0 * (1.0 - textBox);
        AppsCaretBar.Opacity = frame.ReduceMotion || OnboardingClosingFilmEngine.Apps.IsCaretVisible(frame.SceneElapsedSeconds)
            ? 1.0
            : 0.0;

        AppsMiniPill.Opacity = pill;
        var pillScale = 0.85 + 0.15 * pill;
        AppsMiniPillScale.ScaleX = pillScale;
        AppsMiniPillScale.ScaleY = pillScale;
    }

    private void RenderLandScene(ClosingFilmEvaluation frame)
    {
        var mailFront = frame.ReduceMotion ? 1.0 : frame.Value(OnboardingClosingFilmEngine.Land.MailFront);
        var mailActive = frame.ReduceMotion ? 1.0 : frame.Value(OnboardingClosingFilmEngine.Land.MailActive);

        Canvas.SetZIndex(LandMailWindow, mailFront >= 0.5 ? 2 : 1);
        Canvas.SetZIndex(LandChatWindow, mailFront >= 0.5 ? 1 : 2);

        var accentBrush = ThemeResourceLookup.Resolve<Brush>(this, "BrandAccentBrush");
        var dividerBrush = ThemeResourceLookup.Resolve<Brush>(this, "BrandDividerBrush");
        if (accentBrush is not null && dividerBrush is not null)
        {
            LandMailWindow.BorderBrush = mailActive >= 0.5 ? accentBrush : dividerBrush;
            LandMailWindow.BorderThickness = new Thickness(mailActive >= 0.5 ? 2 : 1);
            LandChatWindow.BorderBrush = mailActive < 0.5 ? accentBrush : dividerBrush;
            LandChatWindow.BorderThickness = new Thickness(mailActive < 0.5 ? 2 : 1);
        }

        var l0 = frame.Kept(OnboardingClosingFilmEngine.Land.EmailLine(0));
        var l1 = frame.Kept(OnboardingClosingFilmEngine.Land.EmailLine(1));
        var l2 = frame.Kept(OnboardingClosingFilmEngine.Land.EmailLine(2));
        LandEmailLine0.Opacity = l0;
        LandEmailLine0Translate.Y = 6.0 * (1.0 - l0);
        LandEmailLine1.Opacity = l1;
        LandEmailLine1Translate.Y = 6.0 * (1.0 - l1);
        LandEmailLine2.Opacity = l2;
        LandEmailLine2Translate.Y = 6.0 * (1.0 - l2);

        var ptr = frame.Moving(OnboardingClosingFilmEngine.Land.Cursor);
        LandPointerGroup.Opacity = frame.ReduceMotion ? 0.0 : (ptr > 0.01 && mailActive < 0.5 ? 1.0 : ptr);
        LandPointerTranslate.X = 110.0 * ptr;
        LandPointerTranslate.Y = 48.0 * ptr;

        var clickScale = frame.Moving(OnboardingClosingFilmEngine.Land.ClickScale);
        var clickOpacity = frame.Moving(OnboardingClosingFilmEngine.Land.ClickOpacity);
        LandClickRing.Opacity = clickScale > 0.05 ? clickOpacity : 0.0;
        LandClickRingScale.ScaleX = Math.Max(0.2, clickScale);
        LandClickRingScale.ScaleY = Math.Max(0.2, clickScale);

        var keyHeld = frame.Moving(OnboardingClosingFilmEngine.Land.KeyHeld);
        LandHeldKeyChip.Opacity = frame.ReduceMotion ? 0.0 : Math.Max(0.35, keyHeld);
        var pillOpacity = frame.Moving(OnboardingClosingFilmEngine.Land.PillOpacity);
        var pillRise = frame.Moving(OnboardingClosingFilmEngine.Land.PillRise);
        LandRecordingPill.Opacity = pillOpacity;
        LandRecordingPillTranslate.Y = 8.0 * (1.0 - pillRise);

        LandPolishingPill.Opacity = frame.Moving(OnboardingClosingFilmEngine.Land.Polishing);
    }

    private void RenderCopiedScene(ClosingFilmEvaluation frame)
    {
        var explorerActive = frame.ReduceMotion ? 0.0 : frame.Value(OnboardingClosingFilmEngine.Copied.ExplorerActive);
        CopiedExplorerWindow.Opacity = explorerActive;

        var notepad = frame.Kept(OnboardingClosingFilmEngine.Copied.Notepad);
        CopiedNotepadWindow.Opacity = notepad;
        CopiedNotepadTranslate.Y = 12.0 * (1.0 - notepad);

        var words = frame.Kept(OnboardingClosingFilmEngine.Copied.Words);
        CopiedWordsText.Opacity = words;
        CopiedWordsTranslate.Y = 6.0 * (1.0 - words);

        var clip = frame.Kept(OnboardingClosingFilmEngine.Copied.ClipboardRow);
        CopiedClipboardRow.Opacity = clip;
        CopiedClipboardTranslate.Y = 6.0 * (1.0 - clip);

        var toastOpacity = frame.Moving(OnboardingClosingFilmEngine.Copied.ToastOpacity);
        var toastRise = frame.Moving(OnboardingClosingFilmEngine.Copied.ToastRise);
        CopiedToastPill.Opacity = toastOpacity;
        CopiedToastTranslate.Y = 8.0 * (1.0 - toastRise);

        var keysOpacity = frame.Moving(OnboardingClosingFilmEngine.Copied.PasteKeysOpacity);
        var keysRise = frame.Moving(OnboardingClosingFilmEngine.Copied.PasteKeysRise);
        var keysHeld = frame.Moving(OnboardingClosingFilmEngine.Copied.PasteKeysHeld);
        CopiedPasteKeysPanel.Opacity = keysOpacity;
        CopiedPasteKeysTranslate.Y = 8.0 * (1.0 - keysRise) + 2.0 * keysHeld;

        var pressed = keysHeld > 0.5;
        var keyBg = ThemeResourceLookup.Resolve<Brush>(this, pressed ? "BrandAccentSolidBrush" : "BrandCardBgBrush");
        var keyFg = ThemeResourceLookup.Resolve<Brush>(this, pressed ? "BrandOnAccentBrush" : "BrandAccentBrush");
        if (keyBg is not null && keyFg is not null)
        {
            CopiedCtrlKeycap.Background = keyBg;
            CopiedVKeycap.Background = keyBg;
            CopiedCtrlKeyText.Foreground = keyFg;
            CopiedVKeyText.Foreground = keyFg;
        }
    }

    private void RenderLearnScene(ClosingFilmEvaluation frame)
    {
        var misheard = frame.Kept(OnboardingClosingFilmEngine.Learn.Misheard);
        var fix = frame.Kept(OnboardingClosingFilmEngine.Learn.Fix);
        var pillOpacity = frame.ReduceMotion ? 1.0 : frame.Value(OnboardingClosingFilmEngine.Learn.PillOpacity);
        var pillRise = frame.Kept(OnboardingClosingFilmEngine.Learn.PillRise);
        var undoProgress = frame.ReduceMotion ? 1.0 : frame.Value(OnboardingClosingFilmEngine.Learn.UndoWindow);
        var nextTime = frame.Kept(OnboardingClosingFilmEngine.Learn.NextTime);

        LearnStrikeLine.Opacity = misheard;
        LearnMisheardChip.Opacity = 1.0 - 0.35 * fix;
        LearnFixChip.Opacity = fix;

        LearnLearnedCard.Opacity = pillOpacity;
        LearnLearnedTranslate.Y = 8.0 * (1.0 - pillRise);
        LearnUndoRailScale.ScaleX = frame.ReduceMotion ? 0.35 : Math.Clamp(1.0 - undoProgress, 0.0, 1.0);

        LearnNextWindow.Opacity = nextTime;
        LearnNextTranslate.Y = 10.0 * (1.0 - nextTime);
    }

    private void RenderMoreScene(ClosingFilmEvaluation frame)
    {
        var drift = frame.ReduceMotion ? 0.0 : frame.Value(OnboardingClosingFilmEngine.More.Drift);
        MoreFeaturesTranslate.Y = -36.0 * drift;

        for (var i = 0; i < _moreTileBorders.Count; i++)
        {
            var shown = frame.Kept(OnboardingClosingFilmEngine.More.Tile(i));
            _moreTileBorders[i].Opacity = shown;
            _moreTileTranslates[i].Y = 10.0 * (1.0 - shown);
        }
    }

    private void RenderYoursScene(ClosingFilmEvaluation frame)
    {
        var taskbar = frame.Kept(OnboardingClosingFilmEngine.Yours.Taskbar);
        var pointer = frame.Kept(OnboardingClosingFilmEngine.Yours.TrayCallout);
        var words = frame.Kept(OnboardingClosingFilmEngine.Yours.Words);
        var path = frame.Kept(OnboardingClosingFilmEngine.Yours.Path);

        YoursTrayCard.Opacity = taskbar;
        YoursPointerStack.Opacity = pointer;
        YoursWordsCard.Opacity = words;
        YoursImportPathChip.Opacity = path;

        var breath = frame.ReduceMotion
            ? 1.0
            : OnboardingClosingFilmEngine.Yours.BreathScale(frame.SceneLoopSeconds);
        YoursTrayIconScale.ScaleX = breath;
        YoursTrayIconScale.ScaleY = breath;
    }

    private void BuildAppsWall()
    {
        AppsWallGrid.Children.Clear();
        AppsWallGrid.ColumnDefinitions.Clear();
        AppsWallGrid.RowDefinitions.Clear();
        _appBadges.Clear();

        for (var c = 0; c < 6; c++)
        {
            AppsWallGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (var r = 0; r < 3; r++)
        {
            AppsWallGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        var apps = OnboardingClosingFilmEngine.ShowcaseApps;
        for (var i = 0; i < apps.Count; i++)
        {
            var app = apps[i];
            var dot = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(8),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = new TextBlock
                {
                    Text = app.Monogram,
                    FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            _appBadges.Add((dot, app.AccentBrushKey));

            var label = new TextBlock
            {
                Text = app.Name,
                FontSize = 11,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
            };

            var stack = new StackPanel
            {
                Spacing = 4,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            stack.Children.Add(dot);
            stack.Children.Add(label);

            var card = new Border
            {
                Padding = new Thickness(6, 10, 6, 10),
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                Child = stack,
            };

            Grid.SetRow(card, i / 6);
            Grid.SetColumn(card, i % 6);
            AppsWallGrid.Children.Add(card);
        }
    }

    private void BuildMoreFeaturesWall()
    {
        MoreFeaturesGrid.Children.Clear();
        MoreFeaturesGrid.ColumnDefinitions.Clear();
        MoreFeaturesGrid.RowDefinitions.Clear();
        _moreTileBorders.Clear();
        _moreTileTranslates.Clear();

        MoreFeaturesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        MoreFeaturesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var features = OnboardingClosingFilmEngine.ShippedWindowsFeatures;
        var rows = (features.Count + 1) / 2;
        for (var r = 0; r < rows; r++)
        {
            MoreFeaturesGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        for (var i = 0; i < features.Count; i++)
        {
            var feature = features[i];
            var iconTile = new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(8),
                Child = new FontIcon
                {
                    Glyph = feature.Glyph,
                    FontSize = 14,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };

            var text = new TextBlock
            {
                Text = feature.Name,
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            var content = new Grid { ColumnSpacing = 10 };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.Children.Add(iconTile);
            Grid.SetColumn(text, 1);
            content.Children.Add(text);

            var translate = new TranslateTransform();
            var tile = new Border
            {
                Padding = new Thickness(12, 10, 12, 10),
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                RenderTransform = translate,
                Child = content,
            };

            Grid.SetRow(tile, i / 2);
            Grid.SetColumn(tile, i % 2);
            MoreFeaturesGrid.Children.Add(tile);
            _moreTileBorders.Add(tile);
            _moreTileTranslates.Add(translate);
        }
    }

    private void RefreshThemeBrushes()
    {
        var cardBg = ThemeResourceLookup.Resolve<Brush>(this, "BrandCardBgBrush");
        var divider = ThemeResourceLookup.Resolve<Brush>(this, "BrandDividerBrush");
        var accent = ThemeResourceLookup.Resolve<Brush>(this, "BrandAccentBrush");
        var accentSolid = ThemeResourceLookup.Resolve<Brush>(this, "BrandAccentSolidBrush");
        var accentLight = ThemeResourceLookup.Resolve<Brush>(this, "BrandAccentLightBrush");
        var onAccent = ThemeResourceLookup.Resolve<Brush>(this, "BrandOnAccentBrush");
        var textPrimary = ThemeResourceLookup.Resolve<Brush>(this, "BrandTextPrimaryBrush");
        var textSecondary = ThemeResourceLookup.Resolve<Brush>(this, "BrandTextSecondaryBrush");

        foreach (var fill in _segmentFills)
        {
            if (accentSolid is not null)
            {
                fill.Background = accentSolid;
            }
        }

        foreach (var child in AppsWallGrid.Children)
        {
            if (child is Border card)
            {
                if (cardBg is not null)
                {
                    card.Background = cardBg;
                }

                if (divider is not null)
                {
                    card.BorderBrush = divider;
                }

                if (card.Child is StackPanel stack && stack.Children.Count > 1 && stack.Children[1] is TextBlock label && textSecondary is not null)
                {
                    label.Foreground = textSecondary;
                }
            }
        }

        foreach (var (badge, brushKey) in _appBadges)
        {
            if (ThemeResourceLookup.Resolve<Brush>(this, brushKey) is { } badgeBg)
            {
                badge.Background = badgeBg;
            }

            if (badge.Child is TextBlock monogram && onAccent is not null)
            {
                monogram.Foreground = onAccent;
            }
        }

        foreach (var tile in _moreTileBorders)
        {
            if (cardBg is not null)
            {
                tile.Background = cardBg;
            }

            if (divider is not null)
            {
                tile.BorderBrush = divider;
            }

            if (tile.Child is Grid row && row.Children.Count >= 2)
            {
                if (row.Children[0] is Border iconTile)
                {
                    if (accentLight is not null)
                    {
                        iconTile.Background = accentLight;
                    }

                    if (iconTile.Child is FontIcon icon && accent is not null)
                    {
                        icon.Foreground = accent;
                    }
                }

                if (row.Children[1] is TextBlock label && textPrimary is not null)
                {
                    label.Foreground = textPrimary;
                }
            }
        }
    }
}
