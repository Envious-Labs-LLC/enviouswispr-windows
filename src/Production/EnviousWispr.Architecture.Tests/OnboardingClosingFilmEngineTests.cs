using EnviousWispr.Core.Presentation;

namespace EnviousWispr.Architecture.Tests;

public sealed class OnboardingClosingFilmEngineTests
{
    private static readonly ClosingFilmSummary SampleSummary = new(
        SpeechModel: "Parakeet",
        Polish: "EG-1 Mini",
        KeyLabel: "Ctrl+Win",
        KeyCap: "Ctrl+Win");

    [Fact]
    public void ScriptDefinesSevenScenesTotalling35Point5Seconds()
    {
        Assert.Equal(7, OnboardingClosingFilmEngine.SceneCount);
        Assert.Equal(7, OnboardingClosingFilmEngine.Scenes.Count);

        double[] expectedDurations = [3.5, 4.5, 7.5, 6.5, 6.0, 4.5, 3.0];
        var cumulative = 0.0;
        for (var i = 0; i < expectedDurations.Length; i++)
        {
            var spec = OnboardingClosingFilmEngine.Scenes[i];
            Assert.Equal(i, spec.Index);
            Assert.Equal(expectedDurations[i], spec.DurationSeconds, precision: 6);
            Assert.Equal(cumulative, spec.StartSeconds, precision: 6);
            Assert.Equal(cumulative, OnboardingClosingFilmEngine.GetSceneStartSeconds(i), precision: 6);
            cumulative += expectedDurations[i];
        }

        Assert.Equal(35.5, OnboardingClosingFilmEngine.TotalDurationSeconds, precision: 6);
    }

    [Fact]
    public void CubicBezierCurvesRespectEndpointsAndProduceDistinctEasings()
    {
        FilmCubicBezier[] curves =
        [
            FilmCubicBezier.Scene,
            FilmCubicBezier.Soft,
            FilmCubicBezier.EaseOut,
            FilmCubicBezier.Linear,
        ];

        foreach (var curve in curves)
        {
            Assert.Equal(0.0, curve.Evaluate(0.0), precision: 6);
            Assert.Equal(1.0, curve.Evaluate(1.0), precision: 6);
            Assert.Equal(0.0, curve.Evaluate(-0.5), precision: 6);
            Assert.Equal(1.0, curve.Evaluate(1.5), precision: 6);

            var prev = 0.0;
            for (var step = 1; step <= 20; step++)
            {
                var value = curve.Evaluate(step / 20.0);
                Assert.True(value >= prev - 1e-6, $"Curve {curve} was non-monotonic at step {step}.");
                prev = value;
            }
        }

        // Soft (0.22, 1, 0.36, 1) accelerates much faster early than Linear at p = 0.25.
        Assert.True(FilmCubicBezier.Soft.Evaluate(0.25) > FilmCubicBezier.Linear.Evaluate(0.25) + 0.25);
        Assert.Equal(0.5, FilmCubicBezier.Linear.Evaluate(0.5), precision: 6);
    }

    [Fact]
    public void LocateHoldsFinalSceneFrameIndefinitelyPast35Point5Seconds()
    {
        var atStart = OnboardingClosingFilmEngine.Evaluate(0.0, SampleSummary);
        Assert.Equal(0, atStart.SceneIndex);
        Assert.Equal(ClosingFilmSceneKind.Ready, atStart.SceneKind);
        Assert.False(atStart.IsEnded);

        var atEnd = OnboardingClosingFilmEngine.Evaluate(35.5, SampleSummary);
        Assert.Equal(6, atEnd.SceneIndex);
        Assert.Equal(ClosingFilmSceneKind.Yours, atEnd.SceneKind);
        Assert.Equal(3.0, atEnd.SceneElapsedSeconds, precision: 6);
        Assert.True(atEnd.IsEnded);
        Assert.All(atEnd.SegmentProgress, p => Assert.Equal(1.0, p, precision: 6));

        var wayPastEnd = OnboardingClosingFilmEngine.Evaluate(120.0, SampleSummary);
        Assert.Equal(6, wayPastEnd.SceneIndex);
        Assert.Equal(ClosingFilmSceneKind.Yours, wayPastEnd.SceneKind);
        Assert.Equal(3.0, wayPastEnd.SceneElapsedSeconds, precision: 6);
        Assert.True(wayPastEnd.SceneLoopSeconds > 80.0);
        Assert.True(wayPastEnd.IsEnded);
    }

    [Fact]
    public void ReduceMotionEvaluatesSettledFinalFrameAndHidesTransientParts()
    {
        var startOfLand = OnboardingClosingFilmEngine.GetSceneStartSeconds(2);
        var stillLand = OnboardingClosingFilmEngine.Evaluate(startOfLand, SampleSummary, reduceMotion: true);

        Assert.Equal(ClosingFilmSceneKind.Land, stillLand.SceneKind);
        Assert.True(stillLand.ReduceMotion);
        Assert.Equal(1.0, stillLand.SegmentProgress[2], precision: 6);
        Assert.Equal(1.0, stillLand.Kept(OnboardingClosingFilmEngine.Land.EmailLine(0)), precision: 6);
        Assert.Equal(0.0, stillLand.Moving(OnboardingClosingFilmEngine.Land.Cursor), precision: 6);
        Assert.Equal(0.0, stillLand.Moving(OnboardingClosingFilmEngine.Land.PillOpacity), precision: 6);
        Assert.Equal("Your words land where you started.", stillLand.ActiveCaptionTitle);
        Assert.Equal(RainbowLipsMood.Smile, stillLand.LipsMood);
    }

    [Fact]
    public void TimedLipsBeatsTransitionCleanlyAcrossLandCopiedAndLearnScenes()
    {
        // Scene 3 (Land): 0.0 ready, 0.3 equalizer, 4.6 shimmer, 5.5 happy, 6.4 smile
        var landStart = OnboardingClosingFilmEngine.GetSceneStartSeconds(2);
        Assert.Equal(RainbowLipsMood.Ready, OnboardingClosingFilmEngine.Evaluate(landStart + 0.1, SampleSummary).LipsMood);
        Assert.Equal(RainbowLipsMood.Equalizer, OnboardingClosingFilmEngine.Evaluate(landStart + 1.0, SampleSummary).LipsMood);
        Assert.Equal(RainbowLipsMood.Shimmer, OnboardingClosingFilmEngine.Evaluate(landStart + 4.8, SampleSummary).LipsMood);
        Assert.Equal(RainbowLipsMood.Happy, OnboardingClosingFilmEngine.Evaluate(landStart + 5.7, SampleSummary).LipsMood);
        Assert.Equal(RainbowLipsMood.Smile, OnboardingClosingFilmEngine.Evaluate(landStart + 6.8, SampleSummary).LipsMood);

        // Scene 4 (Copied): 0.0 idle, 0.5 equalizer, 2.1 shimmer, 2.5 pulse, 4.4 happy, 5.4 smile
        var copiedStart = OnboardingClosingFilmEngine.GetSceneStartSeconds(3);
        Assert.Equal(RainbowLipsMood.Idle, OnboardingClosingFilmEngine.Evaluate(copiedStart + 0.1, SampleSummary).LipsMood);
        Assert.Equal(RainbowLipsMood.Equalizer, OnboardingClosingFilmEngine.Evaluate(copiedStart + 1.0, SampleSummary).LipsMood);
        Assert.Equal(RainbowLipsMood.Pulse, OnboardingClosingFilmEngine.Evaluate(copiedStart + 3.0, SampleSummary).LipsMood);
        Assert.Equal(RainbowLipsMood.Smile, OnboardingClosingFilmEngine.Evaluate(copiedStart + 5.8, SampleSummary).LipsMood);

        // Scene 5 (Learn): 0.0 idle, 1.0 heart, 1.7 happy, 2.5 smile, 3.4 equalizer, 4.0 happy, 4.8 triumph
        var learnStart = OnboardingClosingFilmEngine.GetSceneStartSeconds(4);
        Assert.Equal(RainbowLipsMood.Heart, OnboardingClosingFilmEngine.Evaluate(learnStart + 1.2, SampleSummary).LipsMood);
        Assert.Equal(RainbowLipsMood.Triumph, OnboardingClosingFilmEngine.Evaluate(learnStart + 5.1, SampleSummary).LipsMood);
    }

    [Fact]
    public void WindowsNativeCopyUsesCtrlVSystemTrayYourWordsImportAndTenShippedFeaturesWithoutDashes()
    {
        Assert.Equal(10, OnboardingClosingFilmEngine.ShippedWindowsFeatures.Count);
        Assert.Equal(18, OnboardingClosingFilmEngine.ShowcaseApps.Count);

        var allText = new List<string>
        {
            OnboardingClosingFilmEngine.TrayTeachingText,
            OnboardingClosingFilmEngine.BringWordsTitle,
            OnboardingClosingFilmEngine.BringWordsSubtitle,
            OnboardingClosingFilmEngine.BringWordsAppsLine,
            OnboardingClosingFilmEngine.BringWordsImportPath,
            OnboardingClosingFilmEngine.ClipboardSavedBadgeText,
        };

        foreach (var feature in OnboardingClosingFilmEngine.ShippedWindowsFeatures)
        {
            allText.Add(feature.Name);
        }

        foreach (var app in OnboardingClosingFilmEngine.ShowcaseApps)
        {
            allText.Add(app.Name);
            Assert.StartsWith("Brand", app.AccentBrushKey, StringComparison.Ordinal);
            Assert.EndsWith("Brush", app.AccentBrushKey, StringComparison.Ordinal);
        }

        foreach (var scene in OnboardingClosingFilmEngine.Scenes)
        {
            allText.Add(OnboardingClosingFilmEngine.GetSpokenSummary(scene.Kind, SampleSummary));
            foreach (var caption in OnboardingClosingFilmEngine.GetCaptions(scene.Kind))
            {
                allText.Add(caption.Title);
                if (!string.IsNullOrEmpty(caption.Subtitle))
                {
                    allText.Add(caption.Subtitle);
                }
            }
        }

        // Scene 4 uses Windows Ctrl+V (never Command-V) and does not falsely claim prior clipboard restoration.
        var copiedCaptions = OnboardingClosingFilmEngine.GetCaptions(ClosingFilmSceneKind.Copied);
        var copiedSummary = OnboardingClosingFilmEngine.GetSpokenSummary(ClosingFilmSceneKind.Copied, SampleSummary);
        Assert.Contains(copiedCaptions, c => c.Title.Contains("Ctrl+V", StringComparison.Ordinal));
        Assert.Contains(copiedCaptions, c => c.Subtitle.Contains("until you copy something else", StringComparison.Ordinal));
        Assert.Contains("Ctrl+V", copiedSummary, StringComparison.Ordinal);
        Assert.DoesNotContain(copiedCaptions, c => c.Subtitle.Contains("comes back after", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("earlier clipboard", copiedSummary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("comes back after", copiedSummary, StringComparison.OrdinalIgnoreCase);

        // Scene 7 teaches Windows System Tray and Your Words -> Import.
        Assert.Equal("Find EnviousWispr in your system tray.", OnboardingClosingFilmEngine.TrayTeachingText);
        Assert.Equal("Bring your words with you.", OnboardingClosingFilmEngine.BringWordsTitle);
        Assert.Equal("Check out Your Words -> Import", OnboardingClosingFilmEngine.BringWordsImportPath);
        Assert.Contains("system tray", OnboardingClosingFilmEngine.GetSpokenSummary(ClosingFilmSceneKind.Yours, SampleSummary), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Your Words", OnboardingClosingFilmEngine.GetSpokenSummary(ClosingFilmSceneKind.Yours, SampleSummary), StringComparison.Ordinal);

        // Zero macOS-only terms, zero EnviousMeet, and zero em/en dashes.
        Assert.All(allText, text =>
        {
            Assert.DoesNotContain('\u2014', text);
            Assert.DoesNotContain('\u2013', text);
            Assert.DoesNotContain('\u2318', text);
            Assert.DoesNotContain("EnviousMeet", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("menu bar", text, StringComparison.OrdinalIgnoreCase);
        });
    }
}
