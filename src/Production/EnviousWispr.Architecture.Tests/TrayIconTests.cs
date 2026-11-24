using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Settings;
using EnviousWispr.Services.Lifecycle;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// The tray icon says something, and says something different for each thing it can say.
/// </summary>
/// <remarks>
/// THESE ARE MEASUREMENTS OF THE DRAWING, NOT READINGS OF THE DECLARATION. The defect being closed
/// here is that the Windows tray icon never changed at all: it was assigned once at construction and
/// only its tooltip moved. A test that asserted a state had been set would have passed just as
/// happily against that app, because the state was never the thing that was missing.
///
/// So each of these renders real pixels and compares them. That is the only question worth asking of
/// an icon, and it is the one the repository's own rule about a change that ships and does nothing
/// asks for: get the number before and the number after, and treat an unchanged number as evidence
/// the change never reached anything.
/// </remarks>
public sealed class TrayIconTests
{
    /// <summary>Every state a pill can be in puts a decided icon in the tray.</summary>
    /// <remarks>
    /// THE MAPPING HAS A DEFAULT ARM, so a state nobody thought about lands on Idle and looks
    /// deliberate. This reads the real enum, so a state added later is covered on arrival rather
    /// than when somebody remembers this file.
    /// </remarks>
    [Fact]
    public void EveryPillStateDecidesATrayIcon()
    {
        // THE REAL MAPPING, NOT A LIST KEPT HERE. A roster beside the check stops covering the
        // thing it was written for the first time somebody adds a state and does not think of this
        // file, and it would have been a roster asserting against itself.
        var mapping = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Production", "EnviousWispr.Core", "Presentation",
            "TrayIconState.cs"));
        var body = mapping[mapping.IndexOf(
            "public static TrayIconState For(", StringComparison.Ordinal)..];

        // Whole names, compared as a set. A substring test would report Error as decided because
        // the file also says Errors elsewhere, and a prefix is not a value.
        var decided = Regex.Matches(body, @"DictationOverlayState\.(\w+)")
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var undecided = Enum.GetNames<DictationOverlayState>()
            .Where(state => !decided.Contains(state))
            .ToArray();

        Assert.True(
            undecided.Length == 0,
            "These pill states are not named in the tray mapping, so the icon they produce is "
                + "whatever the default arm says rather than something anybody chose: "
                + string.Join(", ", undecided));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EnviousWispr.Windows.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    /// <summary>A live recording does not look like an idle app.</summary>
    /// <remarks>
    /// THE WHOLE POINT, MEASURED. Before this the two were byte-identical, because they were the
    /// same file. A user glancing at the taskbar could not tell whether their microphone was open.
    /// </remarks>
    [Fact]
    public void EveryTrayStateDrawsSomethingDifferent()
    {
        var drawings = Enum.GetValues<TrayIconState>()
            .ToDictionary(state => state, state => Fingerprint(state));

        foreach (var (state, drawing) in drawings)
        {
            Assert.True(
                drawing.Painted > 0,
                $"The {state} tray icon drew nothing at all, so it renders as a blank square.");
        }

        var duplicates = drawings
            .SelectMany(left => drawings
                .Where(right => right.Key.CompareTo(left.Key) > 0 && right.Value == left.Value)
                .Select(right => $"{left.Key} and {right.Key}"))
            .ToArray();

        Assert.True(
            duplicates.Length == 0,
            "These tray states draw exactly the same picture, so the icon changing tells the user "
                + "nothing: " + string.Join(", ", duplicates));
    }

    /// <summary>The icon is drawn at the size it was asked for, at every scale.</summary>
    /// <remarks>
    /// A HARD-CODED 16 IS A BLURRY ICON ON EVERY HIGH-RESOLUTION DISPLAY, which is most of them.
    /// The tray asks for the small system icon size, which is 32 at 200%, and an icon drawn at 16
    /// and stretched is the single most visible way a Windows app looks cheap.
    /// </remarks>
    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    [InlineData(48)]
    public void TheIconIsDrawnAtTheSizeItWasAskedFor(int size)
    {
        foreach (var state in Enum.GetValues<TrayIconState>())
        {
            using var drawing = TrayIconRenderer.Render(state, size);
            Assert.Equal(size, drawing.Width);
            Assert.Equal(size, drawing.Height);
            Assert.True(
                CountPainted(drawing) > 0,
                $"The {state} icon is blank at {size}px, so it disappears at that scale.");
        }
    }

    /// <summary>The processing sweep actually moves.</summary>
    /// <remarks>
    /// AN ANIMATION THAT DOES NOT MOVE IS THE INERT-CHANGE CLASS WEARING A TIMER. The angle is
    /// passed in rather than held, so this can ask the only question that matters about it: do two
    /// different angles produce two different pictures. If they do not, the timer is waking the
    /// machine fifteen times a second to redraw the same square.
    /// </remarks>
    [Fact]
    public void TheProcessingSweepMovesBetweenFrames()
    {
        using var first = TrayIconRenderer.Render(TrayIconState.Processing, 32, rotationDegrees: 0);
        using var later = TrayIconRenderer.Render(TrayIconState.Processing, 32, rotationDegrees: 96);

        Assert.NotEqual(Fingerprint(first), Fingerprint(later));
    }

    /// <summary>Every state stays distinct when High Contrast takes the colours away.</summary>
    /// <remarks>
    /// THE NOTIFICATION AREA DOES NOT RECOLOUR A BITMAP. macOS can tell idle from recording by
    /// colour alone because the menu bar tints its icons; a Windows tray icon is drawn exactly as
    /// handed over, so under High Contrast every colour collapses to one system colour and a
    /// colour-only distinction leaves three states drawing the same picture.
    ///
    /// This is the test that made the renderer give each state its own SHAPE. It failed on the
    /// first version, which is how the design changed.
    /// </remarks>
    [Fact]
    public void EveryTrayStateStaysDistinctWithoutColour()
    {
        var monochrome = TrayIconPalette.ForSystem(Color.Black);
        var drawings = Enum.GetValues<TrayIconState>()
            .ToDictionary(state => state, state => Fingerprint(state, monochrome));

        var duplicates = drawings
            .SelectMany(left => drawings
                .Where(right => right.Key.CompareTo(left.Key) > 0 && right.Value == left.Value)
                .Select(right => $"{left.Key} and {right.Key}"))
            .ToArray();

        Assert.True(
            duplicates.Length == 0,
            "With one colour these tray states draw the same picture, so under High Contrast the "
                + "icon stops saying anything: " + string.Join(", ", duplicates));
    }

    /// <summary>A High Contrast icon uses only the colour the system chose.</summary>
    /// <remarks>
    /// ONLY PIXELS THE DRAWING ACTUALLY COMMITTED TO ARE READ, AND THE REASON IS MEASURED. GDI+
    /// blends against a transparent ground and rounds, so the fainter a pixel the less its colour
    /// means: at alpha 72 an edge read two off the chosen value, and at alpha 8 it read twelve off.
    /// Those are anti-aliasing artefacts, not paint, and asserting on them is asserting on
    /// arithmetic noise.
    ///
    /// AN ALPHA GATE IS ALSO HOW A CHECK LIKE THIS SILENTLY STOPS CHECKING, so the count of pixels
    /// it did read is asserted too. A gate that skipped everything would otherwise pass, complete
    /// and well-formed and about nothing.
    /// </remarks>
    [Fact]
    public void HighContrastDrawsOnlyTheSystemColour()
    {
        var chosen = Color.FromArgb(0x00, 0x33, 0x66);
        foreach (var state in Enum.GetValues<TrayIconState>())
        {
            using var drawing = TrayIconRenderer.Render(
                state, 32, TrayIconPalette.ForSystem(chosen));
            var read = 0;
            for (var x = 0; x < drawing.Width; x++)
            {
                for (var y = 0; y < drawing.Height; y++)
                {
                    var pixel = drawing.GetPixel(x, y);
                    if (pixel.A < 64)
                    {
                        continue;
                    }

                    read++;
                    Assert.True(
                        Math.Abs(pixel.R - chosen.R) <= 6
                            && Math.Abs(pixel.G - chosen.G) <= 6
                            && Math.Abs(pixel.B - chosen.B) <= 6,
                        $"The {state} icon painted {pixel} at {x},{y}, which is not the system "
                            + "colour High Contrast asked for.");
                }
            }

            Assert.True(
                read >= 40,
                $"Only {read} pixels of the {state} icon were solid enough to read, so this check "
                    + "is about almost nothing and would pass on a blank square.");
        }
    }

    private static (long Painted, long Ink) Fingerprint(
        TrayIconState state, TrayIconPalette? palette = null)
    {
        using var drawing = TrayIconRenderer.Render(state, 32, palette);
        return Fingerprint(drawing);
    }

    private static (long Painted, long Ink) Fingerprint(Bitmap drawing)
    {
        long painted = 0;
        long ink = 0;
        for (var x = 0; x < drawing.Width; x++)
        {
            for (var y = 0; y < drawing.Height; y++)
            {
                var pixel = drawing.GetPixel(x, y);
                if (pixel.A == 0)
                {
                    continue;
                }

                painted++;
                // Position is folded in so two icons with the same colours in different places are
                // told apart. Colour alone would call a red bar chart and a red ring identical.
                ink += (pixel.R + (pixel.G * 3L) + (pixel.B * 7L)) * ((x * 31L) + y + 1);
            }
        }

        return (painted, ink);
    }

    private static long CountPainted(Bitmap drawing) => Fingerprint(drawing).Painted;

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public void WaveformLipsRenderBothRowsAcrossColumnsWithMouthGap(int size)
    {
        foreach (var state in new[] { TrayIconState.Idle, TrayIconState.Recording })
        {
            using var drawing = TrayIconRenderer.Render(state, size);
            var midY = size / 2;

            var upperLit = 0;
            var lowerLit = 0;
            var leftThirdLit = 0;
            var centerThirdLit = 0;
            var rightThirdLit = 0;

            for (var x = 0; x < size; x++)
            {
                for (var y = 0; y < size; y++)
                {
                    if (drawing.GetPixel(x, y).A < 32)
                    {
                        continue;
                    }

                    if (y < midY)
                    {
                        upperLit++;
                    }
                    else if (y > midY)
                    {
                        lowerLit++;
                    }

                    if (x < size / 3)
                    {
                        leftThirdLit++;
                    }
                    else if (x < (2 * size) / 3)
                    {
                        centerThirdLit++;
                    }
                    else
                    {
                        rightThirdLit++;
                    }
                }
            }

            Assert.True(upperLit > 0, $"The {state} icon at {size}px has no painted upper-lip pixels.");
            Assert.True(lowerLit > 0, $"The {state} icon at {size}px has no painted lower-lip pixels.");
            Assert.True(leftThirdLit > 0, $"The {state} icon at {size}px has no painted left-column pixels.");
            Assert.True(centerThirdLit > 0, $"The {state} icon at {size}px has no painted center-column pixels.");
            Assert.True(rightThirdLit > 0, $"The {state} icon at {size}px has no painted right-column pixels.");

            if (size >= 24)
            {
                // Verify the mouth gap separates the upper lip's Cupid's-bow dip from the lower lip.
                var centerX = size / 2;
                var minCenterAlpha = Enumerable.Range((int)(size * 0.35), Math.Max(3, (int)(size * 0.25)))
                    .Min(y => drawing.GetPixel(centerX, y).A);
                Assert.True(
                    minCenterAlpha < 64,
                    $"Expected a crisp transparent mouth gap at center column x={centerX} for {state} {size}px, got min alpha {minCenterAlpha}.");
            }
        }
    }

    [Fact]
    public void IdleTaskbarPalettesContrastBetweenDarkAndLightTaskbars()
    {
        using var darkTaskbarIcon = TrayIconRenderer.Render(
            TrayIconState.Idle, 32, TrayIconPalette.ForTaskbar(systemUsesLightTheme: false));
        using var lightTaskbarIcon = TrayIconRenderer.Render(
            TrayIconState.Idle, 32, TrayIconPalette.ForTaskbar(systemUsesLightTheme: true));

        var darkSamples = SampleSolidLuminance(darkTaskbarIcon);
        var lightSamples = SampleSolidLuminance(lightTaskbarIcon);

        Assert.NotEmpty(darkSamples);
        Assert.NotEmpty(lightSamples);
        Assert.True(
            darkSamples.Average() > 210,
            $"Dark-taskbar idle lips should be bright monochrome (#F4F1FA), got average luminance {darkSamples.Average():F1}.");
        Assert.True(
            lightSamples.Average() < 50,
            $"Light-taskbar idle lips should be deep monochrome (#1C1826), got average luminance {lightSamples.Average():F1}.");
    }

    [Fact]
    public void RecordingLipsRenderMultipleVividSpectrumColours()
    {
        using var recording = TrayIconRenderer.Render(TrayIconState.Recording, 32, TrayIconPalette.Brand);
        var distinctColours = new HashSet<(int R, int G, int B)>();
        for (var x = 0; x < recording.Width; x++)
        {
            for (var y = 0; y < recording.Height; y++)
            {
                var p = recording.GetPixel(x, y);
                if (p.A >= 200)
                {
                    distinctColours.Add((p.R / 32, p.G / 32, p.B / 32));
                }
            }
        }

        Assert.True(
            distinctColours.Count >= 5,
            $"Recording lips should show the EnviousWispr spectrum across columns, found only {distinctColours.Count} quantized colours.");
    }

    [Fact]
    public void ErrorLipsPaintOnlyFaultColour()
    {
        var fault = Color.FromArgb(0xE5, 0x48, 0x4D);
        var palette = TrayIconPalette.Brand with { Fault = fault };
        using var error = TrayIconRenderer.Render(TrayIconState.Error, 32, palette);

        var solidPixels = 0;
        for (var x = 0; x < error.Width; x++)
        {
            for (var y = 0; y < error.Height; y++)
            {
                var p = error.GetPixel(x, y);
                if (p.A < 128)
                {
                    continue;
                }

                solidPixels++;
                Assert.True(
                    Math.Abs(p.R - fault.R) <= 8 &&
                    Math.Abs(p.G - fault.G) <= 8 &&
                    Math.Abs(p.B - fault.B) <= 8,
                    $"Error pixel {p} at {x},{y} did not match fault colour {fault}.");
            }
        }

        Assert.True(solidPixels >= 40);
    }

    [Theory]
    [InlineData(AppTheme.System, true, false, false, TrayMenuThemeMode.HighContrast)]
    [InlineData(AppTheme.Dark, true, true, true, TrayMenuThemeMode.HighContrast)]
    [InlineData(AppTheme.Light, false, false, false, TrayMenuThemeMode.Light)]
    [InlineData(AppTheme.Dark, false, true, true, TrayMenuThemeMode.Dark)]
    [InlineData(AppTheme.System, false, false, true, TrayMenuThemeMode.Light)]
    [InlineData(AppTheme.System, false, true, false, TrayMenuThemeMode.Dark)]
    [InlineData(AppTheme.System, false, true, null, TrayMenuThemeMode.Light)]
    [InlineData(AppTheme.System, false, null, null, TrayMenuThemeMode.Dark)]
    public void TrayMenuThemeResolverCoversAllModes(
        AppTheme appTheme,
        bool highContrast,
        bool? systemUsesLight,
        bool? appsUseLight,
        TrayMenuThemeMode expected)
    {
        var resolved = TrayMenuThemeResolver.Resolve(appTheme, highContrast, systemUsesLight, appsUseLight);
        Assert.Equal(expected, resolved);
    }

    [Theory]
    [InlineData(FinalAsrEngine.Automatic, PolishProvider.None, "Automatic · AI Polish Off")]
    [InlineData(FinalAsrEngine.Parakeet, PolishProvider.EgOne, "Parakeet · EG-1")]
    [InlineData(FinalAsrEngine.Whisper, PolishProvider.Ollama, "Whisper · Ollama")]
    [InlineData(FinalAsrEngine.Parakeet, PolishProvider.OpenAI, "Parakeet · OpenAI")]
    [InlineData(FinalAsrEngine.Whisper, PolishProvider.Anthropic, "Whisper · Anthropic")]
    [InlineData(FinalAsrEngine.Automatic, PolishProvider.Gemini, "Automatic · Gemini")]
    public void TrayMenuFormatsEngineAndPolishHeader(
        FinalAsrEngine engine,
        PolishProvider polish,
        string expected)
    {
        Assert.Equal(expected, TrayMenuFormatting.FormatStatusHeader(engine, polish));
    }

    [Fact]
    public void WindowsTrayIconUpdatesMenuStateMicrophonesAndAppearanceCleanly()
    {
        var previousContext = SynchronizationContext.Current;
        using var formsContext = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(formsContext);
        try
        {
            using var tray = new WindowsTrayIcon();
            var previewRequested = false;
            var micsRequested = false;
            tray.LastDictationPreviewRequested += () => previewRequested = true;
            tray.MicrophoneMenuOpeningRequested += () => micsRequested = true;

            tray.SetEngineAndPolishSummary(FinalAsrEngine.Parakeet, PolishProvider.EgOne);
            tray.SetVersion("1.0.0");
            tray.SetRecordingShortcut("Ctrl+Space");

            Assert.Equal("Parakeet · EG-1", tray.StatusHeaderText);
            Assert.Equal("Version 1.0.0", tray.VersionText);
            Assert.Equal("Start Recording", tray.RecordItemText);
            Assert.True(tray.RecordItemEnabled);
            Assert.Equal("Ctrl+Space", tray.RecordItemShortcut);

            tray.SetState(TrayIconState.Recording);
            Assert.Equal("Stop Recording", tray.RecordItemText);
            Assert.True(tray.RecordItemEnabled);

            tray.SetState(TrayIconState.Processing);
            Assert.Equal("Start Recording", tray.RecordItemText);
            Assert.False(tray.RecordItemEnabled);

            tray.SetState(TrayIconState.Idle);
            Assert.Equal("Start Recording", tray.RecordItemText);
            Assert.True(tray.RecordItemEnabled);

            tray.SetMicrophones(
                [
                    new TrayMicrophoneItem("mic-1", "USB Mic & Studio"),
                    new TrayMicrophoneItem("mic-2", "Headset Mic"),
                ],
                selectedDeviceId: "mic-1");

            Assert.Equal(
                [
                    ("System Default", false),
                    ("USB Mic && Studio", true),
                    ("Headset Mic", false),
                ],
                tray.MicrophoneMenuItems);

            tray.SetMicrophones(
                [new TrayMicrophoneItem("mic-2", "Headset Mic")],
                selectedDeviceId: "missing-id");

            Assert.Equal(
                [
                    ("System Default", true),
                    ("Headset Mic", false),
                ],
                tray.MicrophoneMenuItems);

            tray.SetTheme(AppTheme.Light);
            Assert.Equal(TrayMenuThemeMode.Light, tray.EffectiveMenuThemeMode);
            Assert.Equal(
                [("System", false), ("Light", true), ("Dark", false)],
                tray.AppearanceMenuItems);

            tray.SetTheme(AppTheme.Dark);
            Assert.Equal(TrayMenuThemeMode.Dark, tray.EffectiveMenuThemeMode);
            Assert.Equal(
                [("System", false), ("Light", false), ("Dark", true)],
                tray.AppearanceMenuItems);

            tray.SimulateMenuOpeningForTest();
            Assert.True(previewRequested);
            Assert.True(micsRequested);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private static List<double> SampleSolidLuminance(Bitmap bitmap)
    {
        var result = new List<double>();
        for (var x = 0; x < bitmap.Width; x++)
        {
            for (var y = 0; y < bitmap.Height; y++)
            {
                var p = bitmap.GetPixel(x, y);
                if (p.A >= 160)
                {
                    result.Add((0.2126 * p.R) + (0.7152 * p.G) + (0.0722 * p.B));
                }
            }
        }

        return result;
    }
}
