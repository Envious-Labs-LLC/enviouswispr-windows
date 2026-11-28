namespace EnviousWispr.Core.Presentation;

/// <summary>The 7 scenes of the 35.5-second Windows 11 onboarding Closing Film.</summary>
public enum ClosingFilmSceneKind
{
    Ready,
    Apps,
    Land,
    Copied,
    Learn,
    More,
    Yours,
}

/// <summary>A cubic-bezier easing curve matching CSS timing functions used in the approved closing film.</summary>
public readonly record struct FilmCubicBezier(double X1, double Y1, double X2, double Y2)
{
    public static FilmCubicBezier Scene { get; } = new(0.45, 0.0, 0.55, 1.0);
    public static FilmCubicBezier Soft { get; } = new(0.22, 1.0, 0.36, 1.0);
    public static FilmCubicBezier EaseOut { get; } = new(0.0, 0.0, 0.58, 1.0);
    public static FilmCubicBezier Linear { get; } = new(0.0, 0.0, 1.0, 1.0);

    public double Evaluate(double progress)
    {
        var p = Math.Clamp(progress, 0.0, 1.0);
        if (this == Linear || p == 0.0 || p == 1.0)
        {
            return p;
        }

        var s = p;
        for (var i = 0; i < 8; i++)
        {
            var x = Bezier(s, X1, X2) - p;
            if (Math.Abs(x) < 1e-6)
            {
                return Bezier(s, Y1, Y2);
            }

            var slope = Slope(s, X1, X2);
            if (Math.Abs(slope) < 1e-6)
            {
                break;
            }

            s -= x / slope;
        }

        var lo = 0.0;
        var hi = 1.0;
        s = p;
        for (var i = 0; i < 40; i++)
        {
            var x = Bezier(s, X1, X2);
            if (Math.Abs(x - p) < 1e-6)
            {
                break;
            }

            if (x < p)
            {
                lo = s;
            }
            else
            {
                hi = s;
            }

            s = (lo + hi) * 0.5;
        }

        return Bezier(s, Y1, Y2);
    }

    private static double Bezier(double s, double a, double b)
    {
        var u = 1.0 - s;
        return 3.0 * u * u * s * a + 3.0 * u * s * s * b + s * s * s;
    }

    private static double Slope(double s, double a, double b)
    {
        var u = 1.0 - s;
        return 3.0 * u * u * a + 6.0 * u * s * (b - a) + 3.0 * s * s * (1.0 - b);
    }
}

/// <summary>One keyframe stop in scene seconds.</summary>
public readonly record struct FilmTrackStop(double Time, double Value);

/// <summary>Piecewise eased animation track evaluated from a single scene clock.</summary>
public sealed class FilmTrack
{
    public FilmTrack(FilmCubicBezier ease, IReadOnlyList<FilmTrackStop> stops, double delaySeconds = 0.0)
    {
        ArgumentNullException.ThrowIfNull(stops);
        Ease = ease;
        Stops = stops;
        DelaySeconds = delaySeconds;
    }

    public FilmTrack(FilmCubicBezier ease, params (double Time, double Value)[] stops)
        : this(ease, stops.Select(s => new FilmTrackStop(s.Time, s.Value)).ToArray())
    {
    }

    public FilmCubicBezier Ease { get; }

    public IReadOnlyList<FilmTrackStop> Stops { get; }

    public double DelaySeconds { get; }

    public FilmTrack Delayed(double seconds) => new(Ease, Stops, DelaySeconds + seconds);

    public double Evaluate(double sceneSeconds)
    {
        if (Stops.Count == 0)
        {
            return 0.0;
        }

        var t = sceneSeconds - DelaySeconds;
        var first = Stops[0];
        var last = Stops[^1];
        if (t <= first.Time)
        {
            return first.Value;
        }

        if (t >= last.Time)
        {
            return last.Value;
        }

        for (var i = 0; i < Stops.Count - 1; i++)
        {
            var a = Stops[i];
            var b = Stops[i + 1];
            if (t < b.Time)
            {
                if (b.Time <= a.Time)
                {
                    return b.Value;
                }

                var linear = (t - a.Time) / (b.Time - a.Time);
                return a.Value + (b.Value - a.Value) * Ease.Evaluate(linear);
            }
        }

        return last.Value;
    }

    public double EvaluateSettled() => Stops.Count == 0 ? 0.0 : Stops[^1].Value;

    public static FilmTrack Entrance(double delaySeconds, double durationSeconds, FilmCubicBezier? ease = null) =>
        new(ease ?? FilmCubicBezier.Soft, (delaySeconds, 0.0), (delaySeconds + durationSeconds, 1.0));
}

/// <summary>Timed RainbowLips mood cue inside a Closing Film scene.</summary>
public readonly record struct ClosingFilmLipsBeat(double AtSeconds, RainbowLipsMood Mood);

/// <summary>One caption title/subtitle pair animated on its own track inside a scene.</summary>
public sealed record ClosingFilmCaptionLine(string Title, string Subtitle, FilmTrack Track);

/// <summary>Evaluated caption line state for the current frame.</summary>
public sealed record ClosingFilmCaptionLineState(
    string Title,
    string Subtitle,
    double Opacity,
    double OffsetY);

/// <summary>A real shipped Windows feature shown in Scene 6.</summary>
public sealed record ClosingFilmFeatureTile(string Glyph, string Name);

/// <summary>One Windows-friendly app badge in Scene 2's drifting wall.</summary>
public sealed record ClosingFilmAppTile(string Name, string Monogram, string AccentBrushKey);

/// <summary>User choices surfaced in Scenes 1, 3, 4, and accessibility summaries.</summary>
public sealed record ClosingFilmSummary(
    string SpeechModel,
    string? Polish,
    string KeyLabel,
    string KeyCap)
{
    public string EffectiveSpeechModel => string.IsNullOrWhiteSpace(SpeechModel) ? "Parakeet" : SpeechModel.Trim();

    public string EffectivePolish => string.IsNullOrWhiteSpace(Polish) ? "Off for now" : Polish.Trim();

    public string EffectiveKeyLabel => string.IsNullOrWhiteSpace(KeyLabel) ? "Ctrl+Win" : KeyLabel.Trim();

    public string EffectiveKeyCap => string.IsNullOrWhiteSpace(KeyCap) ? EffectiveKeyLabel : KeyCap.Trim();
}

/// <summary>Timing and lips choreography specification for one scene.</summary>
public sealed record ClosingFilmSceneSpec(
    int Index,
    ClosingFilmSceneKind Kind,
    double DurationSeconds,
    double StartSeconds,
    IReadOnlyList<ClosingFilmLipsBeat> LipsBeats);

/// <summary>Complete single-clock frame evaluation for the 7-scene Closing Film.</summary>
public sealed record ClosingFilmEvaluation(
    int SceneIndex,
    ClosingFilmSceneKind SceneKind,
    double FilmSeconds,
    double SceneElapsedSeconds,
    double SceneLoopSeconds,
    double SceneDurationSeconds,
    bool IsEnded,
    bool ReduceMotion,
    RainbowLipsMood LipsMood,
    double LipsMoodElapsedSeconds,
    string ActiveCaptionTitle,
    string ActiveCaptionSubtitle,
    IReadOnlyList<ClosingFilmCaptionLineState> CaptionLines,
    double[] SegmentProgress,
    string SpokenSummary)
{
    /// <summary>Track value at the current frame, or the settled final value under Reduce Motion.</summary>
    public double Value(FilmTrack track) =>
        ReduceMotion ? track.EvaluateSettled() : track.Evaluate(SceneElapsedSeconds);

    /// <summary>Element that remains fully visible on the settled Reduce Motion frame.</summary>
    public double Kept(FilmTrack track) =>
        ReduceMotion ? 1.0 : track.Evaluate(SceneElapsedSeconds);

    /// <summary>Transient demonstration element (pointer, held key, flying pill) hidden under Reduce Motion.</summary>
    public double Moving(FilmTrack track) =>
        ReduceMotion ? 0.0 : track.Evaluate(SceneElapsedSeconds);
}

/// <summary>
/// Pure single-clock 7-scene (35.5 s) deterministic choreography and Windows-native copy engine
/// for the onboarding Closing Film.
/// </summary>
public static class OnboardingClosingFilmEngine
{
    public const int SceneCount = 7;

    public const string TrayTeachingText = "Find EnviousWispr in your system tray.";
    public const string BringWordsTitle = "Bring your words with you.";
    public const string BringWordsSubtitle = "Import your custom words from another dictation app.";
    public const string BringWordsAppsLine = "Wispr Flow \u00B7 FluidVoice \u00B7 Superwhisper \u00B7 and more";
    public const string BringWordsImportPath = "Check out Your Words -> Import";
    public const string ClipboardSavedBadgeText = "Pasted from your clipboard";
    public const string LearnedWordExample = "Tuist";
    public const string MisheardWordExample = "twist";

    public static IReadOnlyList<ClosingFilmSceneSpec> Scenes { get; } = BuildScenes();

    public static double TotalDurationSeconds { get; } = Scenes.Sum(scene => scene.DurationSeconds);

    /// <summary>10 real shipped Windows features displayed in Scene 6 (zero EnviousMeet or macOS-only items).</summary>
    public static IReadOnlyList<ClosingFilmFeatureTile> ShippedWindowsFeatures { get; } =
    [
        new("\uE8D6", "Transcribe a File"),
        new("\uE943", "Snippets"),
        new("\uE82D", "Your Words"),
        new("\uE8D2", "Spoken punctuation"),
        new("\uE81C", "History"),
        new("\uE7B3", "Live preview"),
        new("\uE774", "Many languages"),
        new("\uE765", "Keyboard shortcuts"),
        new("\uE720", "Hands-free mode"),
        new("\uE8C8", "Copy last dictation"),
    ];

    /// <summary>18 real Windows-relevant apps shown in Scene 2's 6x3 drifting badge wall.</summary>
    public static IReadOnlyList<ClosingFilmAppTile> ShowcaseApps { get; } =
    [
        new("Slack", "Sl", "BrandSpectrumRedBrush"),
        new("Outlook", "Ol", "BrandSpectrumBlueBrush"),
        new("VS Code", "VS", "BrandSpectrumCyanBrush"),
        new("Notion", "N", "BrandTextPrimaryBrush"),
        new("Discord", "Dc", "BrandSpectrumRoyalBrush"),
        new("Teams", "Tm", "BrandSpectrumVioletBrush"),
        new("Edge", "Ed", "BrandSpectrumSpringBrush"),
        new("Chrome", "Ch", "BrandSpectrumGoldBrush"),
        new("Word", "W", "BrandSpectrumBlueBrush"),
        new("Notepad", "Np", "BrandSpectrumCyanBrush"),
        new("Obsidian", "Ob", "BrandSpectrumVioletBrush"),
        new("Figma", "Fg", "BrandSpectrumOrangeBrush"),
        new("Linear", "Ln", "BrandSpectrumRoyalBrush"),
        new("Gmail", "Gm", "BrandSpectrumRedBrush"),
        new("WhatsApp", "Wa", "BrandSpectrumSpringBrush"),
        new("Telegram", "Tg", "BrandSpectrumBlueBrush"),
        new("ChatGPT", "AI", "BrandSpectrumLimeBrush"),
        new("Cursor", "Cu", "BrandAccentBrush"),
    ];

    public static double GetSceneStartSeconds(int sceneIndex)
    {
        var clamped = Math.Clamp(sceneIndex, 0, Scenes.Count - 1);
        return Scenes[clamped].StartSeconds;
    }

    public static ClosingFilmSceneSpec GetSpec(ClosingFilmSceneKind kind) =>
        Scenes[(int)kind];

    /// <summary>
    /// Locates the scene at <paramref name="filmSeconds"/>. At or beyond 35.5 s the final scene
    /// holds its end frame indefinitely while <c>LoopSeconds</c> continues advancing for gentle loops.
    /// </summary>
    public static (int SceneIndex, double SceneElapsedSeconds, double SceneLoopSeconds) Locate(double filmSeconds)
    {
        var t = Math.Max(0.0, filmSeconds);
        for (var i = 0; i < Scenes.Count; i++)
        {
            var spec = Scenes[i];
            if (t < spec.StartSeconds + spec.DurationSeconds || i == Scenes.Count - 1)
            {
                var loop = Math.Max(0.0, t - spec.StartSeconds);
                var clampedElapsed = Math.Min(spec.DurationSeconds, loop);
                return (i, clampedElapsed, loop);
            }
        }

        return (0, 0.0, 0.0);
    }

    /// <summary>Resolves the active RainbowLips mood and mood-local elapsed seconds within one scene.</summary>
    public static (RainbowLipsMood Mood, double ElapsedSeconds) EvaluateLipsBeat(
        ClosingFilmSceneKind kind,
        double sceneElapsedSeconds)
    {
        var beats = GetSpec(kind).LipsBeats;
        var chosen = beats[0];
        var elapsed = Math.Max(0.0, sceneElapsedSeconds);
        foreach (var candidate in beats)
        {
            if (elapsed >= candidate.AtSeconds)
            {
                chosen = candidate;
            }
        }

        return (chosen.Mood, Math.Max(0.0, elapsed - chosen.AtSeconds));
    }

    /// <summary>Evaluates the entire Closing Film state deterministically at <paramref name="filmSeconds"/>.</summary>
    public static ClosingFilmEvaluation Evaluate(
        double filmSeconds,
        ClosingFilmSummary summary,
        bool reduceMotion = false)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var t = Math.Max(0.0, filmSeconds);
        var (sceneIndex, sceneElapsed, sceneLoop) = Locate(t);
        var spec = Scenes[sceneIndex];

        var lipsTime = reduceMotion ? spec.DurationSeconds : sceneElapsed;
        var (lipsMood, lipsElapsed) = EvaluateLipsBeat(spec.Kind, lipsTime);

        var captionSpecs = GetCaptions(spec.Kind);
        var captionStates = new ClosingFilmCaptionLineState[captionSpecs.Count];
        var activeTitle = captionSpecs[^1].Title;
        var activeSubtitle = captionSpecs[^1].Subtitle;
        var bestOpacity = -1.0;

        for (var i = 0; i < captionSpecs.Count; i++)
        {
            var line = captionSpecs[i];
            var opacity = reduceMotion
                ? (i == captionSpecs.Count - 1 ? 1.0 : 0.0)
                : line.Track.Evaluate(sceneElapsed);
            var offsetY = 10.0 * (1.0 - opacity);
            captionStates[i] = new ClosingFilmCaptionLineState(line.Title, line.Subtitle, opacity, offsetY);

            if (opacity >= bestOpacity)
            {
                bestOpacity = opacity;
                activeTitle = line.Title;
                activeSubtitle = line.Subtitle;
            }
        }

        var progress = new double[Scenes.Count];
        for (var i = 0; i < Scenes.Count; i++)
        {
            if (i < sceneIndex)
            {
                progress[i] = 1.0;
            }
            else if (i > sceneIndex)
            {
                progress[i] = 0.0;
            }
            else
            {
                progress[i] = reduceMotion
                    ? 1.0
                    : Math.Clamp(sceneElapsed / spec.DurationSeconds, 0.0, 1.0);
            }
        }

        return new ClosingFilmEvaluation(
            SceneIndex: sceneIndex,
            SceneKind: spec.Kind,
            FilmSeconds: t,
            SceneElapsedSeconds: sceneElapsed,
            SceneLoopSeconds: sceneLoop,
            SceneDurationSeconds: spec.DurationSeconds,
            IsEnded: t >= TotalDurationSeconds,
            ReduceMotion: reduceMotion,
            LipsMood: lipsMood,
            LipsMoodElapsedSeconds: reduceMotion ? 0.0 : lipsElapsed,
            ActiveCaptionTitle: activeTitle,
            ActiveCaptionSubtitle: activeSubtitle,
            CaptionLines: captionStates,
            SegmentProgress: progress,
            SpokenSummary: GetSpokenSummary(spec.Kind, summary));
    }

    public static IReadOnlyList<ClosingFilmCaptionLine> GetCaptions(ClosingFilmSceneKind kind) => kind switch
    {
        ClosingFilmSceneKind.Ready =>
        [
            new("Congratulations! You are all set.", "Here is a quick refresher.", Captions.SingleLine),
        ],
        ClosingFilmSceneKind.Apps =>
        [
            new("Works in any app with a text box.", "Email, chat, docs, code and more.", Captions.SingleLine),
        ],
        ClosingFilmSceneKind.Land =>
        [
            new("Hold your key and talk.", "The pill shows EnviousWispr is listening.", Captions.Land[0]),
            new("Click away if you need to.", "Keep talking. Your place is remembered.", Captions.Land[1]),
            new("Your words land where you started.", "EnviousWispr brings Mail back and pastes.", Captions.Land[2]),
        ],
        ClosingFilmSceneKind.Copied =>
        [
            new("No text box selected?", "You clicked File Explorer. Talk anyway.", Captions.Copied[0]),
            new("EnviousWispr copies your words.", "The pill says: Copied. Press Ctrl+V to paste.", Captions.Copied[1]),
            new("Press Ctrl+V wherever you want them.", "Your words stay on your clipboard until you copy something else.", Captions.Copied[2]),
        ],
        ClosingFilmSceneKind.Learn =>
        [
            new("Fix a word once.", "It heard \"twist\". You type \"Tuist\".", Captions.Learn[0]),
            new("EnviousWispr learns it.", "Next time it writes Tuist. Use Undo if it learned wrong.", Captions.Learn[1]),
        ],
        ClosingFilmSceneKind.More =>
        [
            new("And there is a lot more.", "Find all of it in Settings whenever you like.", Captions.SingleLine),
        ],
        ClosingFilmSceneKind.Yours =>
        [
            new("You're all set.", string.Empty, Captions.SingleLine),
        ],
        _ =>
        [
            new("You're all set.", string.Empty, Captions.SingleLine),
        ],
    };

    public static string GetSpokenSummary(ClosingFilmSceneKind kind, ClosingFilmSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return kind switch
        {
            ClosingFilmSceneKind.Ready =>
                $"Congratulations! You are all set. Speech model: {summary.EffectiveSpeechModel}. AI polish: {summary.EffectivePolish}. Dictation key: {summary.EffectiveKeyLabel}.",
            ClosingFilmSceneKind.Apps =>
                "Works in any app with a text box. Email, chat, docs, code and more.",
            ClosingFilmSceneKind.Land =>
                $"Hold {summary.EffectiveKeyLabel} and talk. Click away if you need to. Your words land where you started.",
            ClosingFilmSceneKind.Copied =>
                "No text box selected? EnviousWispr copies your words. Click any text box and press Ctrl+V to paste them.",
            ClosingFilmSceneKind.Learn =>
                "Fix a word once and EnviousWispr learns it. Use Undo if it learned the wrong word.",
            ClosingFilmSceneKind.More =>
                "And there is a lot more in Settings, including Transcribe a File, Snippets, Your Words, History, and Live preview.",
            ClosingFilmSceneKind.Yours =>
                "You're all set. Find EnviousWispr in your system tray. Bring your words with you: to import your custom words from another dictation app, open Settings, then Your Words, then Import.",
            _ => "You're all set.",
        };
    }

    public static class Captions
    {
        public static FilmTrack SingleLine { get; } = FilmTrack.Entrance(0.0, 0.45);

        public static IReadOnlyList<FilmTrack> Land { get; } =
        [
            new(FilmCubicBezier.Scene, (0.0, 1.0), (1.45, 1.0), (1.6, 0.0)),
            new(FilmCubicBezier.Scene, (0.0, 0.0), (1.6, 0.0), (1.8, 1.0), (4.45, 1.0), (4.6, 0.0)),
            new(FilmCubicBezier.Scene, (0.0, 0.0), (4.6, 0.0), (4.8, 1.0)),
        ];

        public static IReadOnlyList<FilmTrack> Copied { get; } =
        [
            new(FilmCubicBezier.Scene, (0.0, 1.0), (2.05, 1.0), (2.2, 0.0)),
            new(FilmCubicBezier.Scene, (0.0, 0.0), (2.2, 0.0), (2.4, 1.0), (4.05, 1.0), (4.2, 0.0)),
            new(FilmCubicBezier.Scene, (0.0, 0.0), (4.2, 0.0), (4.4, 1.0)),
        ];

        public static IReadOnlyList<FilmTrack> Learn { get; } =
        [
            new(FilmCubicBezier.Scene, (0.0, 1.0), (1.55, 1.0), (1.7, 0.0)),
            new(FilmCubicBezier.Scene, (0.0, 0.0), (1.7, 0.0), (1.9, 1.0)),
        ];
    }

    public static class Ready
    {
        public static FilmTrack Row(int index) => FilmTrack.Entrance(0.1 + index * 0.15, 0.45);
    }

    public static class Apps
    {
        public static FilmTrack Drift { get; } = new(FilmCubicBezier.Linear, (0.0, 0.0), (4.5, 1.0));
        public static FilmTrack TextBox { get; } = FilmTrack.Entrance(0.85, 0.65);
        public static FilmTrack Pill { get; } = FilmTrack.Entrance(1.55, 0.40);

        public static bool IsCaretVisible(double sceneSeconds) =>
            RainbowLipsMotion.PositiveMod(sceneSeconds, 1.0) < 0.5;
    }

    public static class Land
    {
        public static FilmTrack KeyHeld { get; } = new(FilmCubicBezier.Scene, (0.0, 0.0), (0.3, 1.0), (4.6, 1.0), (4.9, 0.0));
        public static FilmTrack PillOpacity { get; } = new(FilmCubicBezier.Scene, (0.0, 0.0), (0.3, 0.0), (0.6, 1.0), (4.5, 1.0), (4.7, 0.0));
        public static FilmTrack PillRise { get; } = new(FilmCubicBezier.Scene, (0.0, 0.0), (0.3, 0.0), (0.6, 1.0));
        public static FilmTrack Polishing { get; } = new(FilmCubicBezier.Scene, (0.0, 0.0), (4.55, 0.0), (4.75, 1.0), (5.4, 1.0), (5.6, 0.0));
        public static FilmTrack Cursor { get; } = new(FilmCubicBezier.Scene, (0.0, 0.0), (1.6, 0.0), (2.2, 1.0));
        public static FilmTrack ClickScale { get; } = new(FilmCubicBezier.EaseOut, (0.0, 0.0), (2.2, 0.0), (2.25, 0.3), (2.7, 1.6));
        public static FilmTrack ClickOpacity { get; } = new(FilmCubicBezier.EaseOut, (0.0, 0.6), (2.25, 0.6), (2.7, 0.0));
        public static FilmTrack MailFront { get; } = new(FilmCubicBezier.Linear, (0.0, 1.0), (2.29, 1.0), (2.3, 0.0), (5.49, 0.0), (5.5, 1.0));
        public static FilmTrack MailActive { get; } = new(FilmCubicBezier.Linear, (0.0, 1.0), (2.2, 1.0), (2.3, 0.0), (5.4, 0.0), (5.5, 1.0));

        public static FilmTrack EmailLine(int index) =>
            new FilmTrack(FilmCubicBezier.Soft, (0.0, 0.0), (5.5, 0.0), (6.0, 1.0)).Delayed(index * 0.06);
    }

    public static class Copied
    {
        public static FilmTrack KeyHeld { get; } = new(FilmCubicBezier.Scene, (0.0, 0.0), (0.5, 1.0), (2.1, 1.0), (2.4, 0.0));
        public static FilmTrack KeyChip { get; } = new(FilmCubicBezier.Linear, (0.0, 1.0), (2.3, 1.0), (2.6, 0.0));
        public static FilmTrack PillOpacity { get; } = new(FilmCubicBezier.Scene, (0.0, 0.0), (0.3, 0.0), (0.6, 1.0), (2.1, 1.0), (2.25, 0.0));
        public static FilmTrack PillRise { get; } = new(FilmCubicBezier.Scene, (0.0, 0.0), (0.3, 0.0), (0.6, 1.0));
        public static FilmTrack ToastOpacity { get; } = new(FilmCubicBezier.Scene, (0.0, 0.0), (2.2, 0.0), (2.5, 1.0), (3.7, 1.0), (4.0, 0.0));
        public static FilmTrack ToastRise { get; } = new(FilmCubicBezier.Scene, (0.0, 0.0), (2.2, 0.0), (2.5, 1.0));
        public static FilmTrack Notepad { get; } = new(FilmCubicBezier.Soft, (0.0, 0.0), (3.5, 0.0), (3.95, 1.0));
        public static FilmTrack Words { get; } = new(FilmCubicBezier.Soft, (0.0, 0.0), (4.5, 0.0), (4.9, 1.0));
        public static FilmTrack ClipboardRow { get; } = new(FilmCubicBezier.Soft, (0.0, 0.0), (5.0, 0.0), (5.4, 1.0));
        public static FilmTrack ExplorerActive { get; } = new(FilmCubicBezier.Linear, (0.0, 1.0), (3.6, 1.0), (3.75, 0.0));
        public static FilmTrack PasteKeysOpacity { get; } = new(FilmCubicBezier.EaseOut, (0.0, 0.0), (4.05, 0.0), (4.2, 1.0), (4.7, 1.0), (5.2, 0.0));
        public static FilmTrack PasteKeysRise { get; } = new(FilmCubicBezier.EaseOut, (0.0, 0.0), (4.05, 0.0), (4.2, 1.0));
        public static FilmTrack PasteKeysHeld { get; } = new(FilmCubicBezier.EaseOut, (0.0, 0.0), (4.2, 0.0), (4.3, 1.0), (4.55, 1.0), (4.7, 0.0));
    }

    public static class Learn
    {
        public static FilmTrack Selected { get; } = new(FilmCubicBezier.EaseOut, (0.0, 0.0), (0.6, 0.0), (0.75, 1.0), (1.0, 1.0), (1.05, 0.0));
        public static FilmTrack Misheard { get; } = new(FilmCubicBezier.EaseOut, (0.0, 0.0), (1.0, 0.0), (1.05, 1.0));
        public static FilmTrack Fix { get; } = new(FilmCubicBezier.EaseOut, (0.0, 0.0), (1.0, 0.0), (1.5, 1.0));
        public static FilmTrack PillOpacity { get; } = new(FilmCubicBezier.EaseOut, (0.0, 0.0), (1.5, 0.0), (1.7, 1.0), (5.7, 1.0), (5.9, 0.0));
        public static FilmTrack PillRise { get; } = new(FilmCubicBezier.EaseOut, (0.0, 0.0), (1.5, 0.0), (1.7, 1.0));
        public static FilmTrack UndoWindow { get; } = new(FilmCubicBezier.Linear, (0.0, 0.0), (1.7, 0.0), (5.7, 1.0));
        public static FilmTrack NextTime { get; } = new(FilmCubicBezier.Soft, (0.0, 0.0), (3.6, 0.0), (4.0, 1.0));
    }

    public static class More
    {
        public static FilmTrack Tile(int index) => FilmTrack.Entrance(0.15 + index * 0.11, 0.45);
        public static FilmTrack Drift { get; } = new(FilmCubicBezier.Linear, (0.0, 0.0), (4.5, 1.0));
    }

    public static class Yours
    {
        public static FilmTrack Taskbar { get; } = FilmTrack.Entrance(0.0, 0.45);
        public static FilmTrack TrayCallout { get; } = FilmTrack.Entrance(0.35, 0.45);
        public static FilmTrack Words { get; } = FilmTrack.Entrance(0.8, 0.45);
        public static FilmTrack Path { get; } = FilmTrack.Entrance(1.2, 0.45);

        public static double BreathScale(double loopSeconds, double periodSeconds = 2.0, double delaySeconds = 0.5)
        {
            if (loopSeconds <= delaySeconds || periodSeconds <= 0.0)
            {
                return 1.0;
            }

            var p = RainbowLipsMotion.PositiveMod(loopSeconds - delaySeconds, periodSeconds) / periodSeconds;
            var triangle = p < 0.5 ? p * 2.0 : 2.0 - p * 2.0;
            return 1.0 + 0.06 * FilmCubicBezier.Scene.Evaluate(triangle);
        }
    }

    private static ClosingFilmSceneSpec[] BuildScenes()
    {
        var specs = new (ClosingFilmSceneKind Kind, double Duration, ClosingFilmLipsBeat[] Beats)[]
        {
            (ClosingFilmSceneKind.Ready, 3.5, [new(0.0, RainbowLipsMood.Triumph)]),
            (ClosingFilmSceneKind.Apps, 4.5, [new(0.0, RainbowLipsMood.Welcome)]),
            (ClosingFilmSceneKind.Land, 7.5,
            [
                new(0.0, RainbowLipsMood.Ready),
                new(0.3, RainbowLipsMood.Equalizer),
                new(4.6, RainbowLipsMood.Shimmer),
                new(5.5, RainbowLipsMood.Happy),
                new(6.4, RainbowLipsMood.Smile),
            ]),
            (ClosingFilmSceneKind.Copied, 6.5,
            [
                new(0.0, RainbowLipsMood.Idle),
                new(0.5, RainbowLipsMood.Equalizer),
                new(2.1, RainbowLipsMood.Shimmer),
                new(2.5, RainbowLipsMood.Pulse),
                new(4.4, RainbowLipsMood.Happy),
                new(5.4, RainbowLipsMood.Smile),
            ]),
            (ClosingFilmSceneKind.Learn, 6.0,
            [
                new(0.0, RainbowLipsMood.Idle),
                new(1.0, RainbowLipsMood.Heart),
                new(1.7, RainbowLipsMood.Happy),
                new(2.5, RainbowLipsMood.Smile),
                new(3.4, RainbowLipsMood.Equalizer),
                new(4.0, RainbowLipsMood.Happy),
                new(4.8, RainbowLipsMood.Triumph),
            ]),
            (ClosingFilmSceneKind.More, 4.5, [new(0.0, RainbowLipsMood.Shimmer)]),
            (ClosingFilmSceneKind.Yours, 3.0, [new(0.0, RainbowLipsMood.Ready)]),
        };

        var result = new ClosingFilmSceneSpec[specs.Length];
        var start = 0.0;
        for (var i = 0; i < specs.Length; i++)
        {
            result[i] = new ClosingFilmSceneSpec(i, specs[i].Kind, specs[i].Duration, start, specs[i].Beats);
            start += specs[i].Duration;
        }

        return result;
    }
}
