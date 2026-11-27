using System.Text.RegularExpressions;
using System.Xml.Linq;
using EnviousWispr.Core.Learning;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// Structural, behavioral, and allocation-hygiene checks for the Wave 3B DictationOverlayWindow refactor (#396).
/// </summary>
public sealed class OverlayModularityAndClockTests
{
    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void OverlayPillCatalogResolvesEveryStateWithoutFallingBack()
    {
        foreach (var state in Enum.GetValues<DictationOverlayState>())
        {
            var look = OverlayPillCatalog.ResolveSeverity(state);
            Assert.False(string.IsNullOrWhiteSpace(look.IconStyleKey));
            Assert.False(string.IsNullOrWhiteSpace(look.EdgeStyleKey));
            Assert.False(string.IsNullOrWhiteSpace(look.WashStyleKey));

            var edgeStyle = OverlayPillCatalog.ResolveFrameEdgeStyle(state, RecordingPillDesign.ReadingWell);
            Assert.StartsWith("Pill", edgeStyle, StringComparison.Ordinal);
        }

        Assert.Equal("PillWellEdgeStyle", OverlayPillCatalog.ResolveFrameEdgeStyle(DictationOverlayState.Recording, RecordingPillDesign.ReadingWell));
        Assert.Equal("PillCapsuleEdgeStyle", OverlayPillCatalog.ResolveFrameEdgeStyle(DictationOverlayState.Recording, RecordingPillDesign.Classic));
        Assert.Equal("PillEscapeEdgeStyle", OverlayPillCatalog.ResolveFrameEdgeStyle(DictationOverlayState.EscapeRecovery, RecordingPillDesign.Classic));
        Assert.Equal("PillCardEdgeStyle", OverlayPillCatalog.ResolveFrameEdgeStyle(DictationOverlayState.BluetoothCard, RecordingPillDesign.Classic));
        Assert.Equal("PillLearnedEdgeStyle", OverlayPillCatalog.ResolveFrameEdgeStyle(DictationOverlayState.CorrectionLearned, RecordingPillDesign.Classic));

        Assert.Equal(OverlayPillSurfaceKind.Hidden, OverlayPillCatalog.ResolveSurface(DictationOverlayState.Hidden));
        Assert.Equal(OverlayPillSurfaceKind.Recording, OverlayPillCatalog.ResolveSurface(DictationOverlayState.Recording));
        Assert.Equal(OverlayPillSurfaceKind.Processing, OverlayPillCatalog.ResolveSurface(DictationOverlayState.Processing));
        Assert.Equal(OverlayPillSurfaceKind.EscapeRecovery, OverlayPillCatalog.ResolveSurface(DictationOverlayState.EscapeRecovery));
        Assert.Equal(OverlayPillSurfaceKind.LearnedCorrection, OverlayPillCatalog.ResolveSurface(DictationOverlayState.CorrectionLearned));
        Assert.Equal(OverlayPillSurfaceKind.BluetoothCard, OverlayPillCatalog.ResolveSurface(DictationOverlayState.BluetoothCard));
        Assert.Equal(OverlayPillSurfaceKind.PolishSetupCard, OverlayPillCatalog.ResolveSurface(DictationOverlayState.PolishSetupCard));
        Assert.Equal(OverlayPillSurfaceKind.Notice, OverlayPillCatalog.ResolveSurface(DictationOverlayState.Error));
    }

    [Fact]
    public void OverlayPillCatalogComputesSpokenNamesAndLearnedDwellsAccurately()
    {
        var bt = DictationStatus.BluetoothCard();
        var btDef = OverlayPillCatalog.Resolve(bt);
        Assert.Equal(OverlayPillSurfaceKind.BluetoothCard, btDef.Surface);
        Assert.Equal(
            "Bluetooth mic detected. Bluetooth microphones can take a moment on a cold start.",
            OverlayPillCatalog.ComputeSpokenName(DictationOverlayState.BluetoothCard, bt, btDef.Title));

        var cold = DictationStatus.ColdStart("Warming", "Parakeet");
        Assert.Equal("Warming. Parakeet", OverlayPillCatalog.ComputeSpokenName(DictationOverlayState.ColdStart, cold, "Warming"));

        Assert.Equal(12.0, OverlayPillCatalog.ResolveLearnedDwellSeconds(LearnedCorrectionPillPhase.Learned));
        Assert.Equal(3.0, OverlayPillCatalog.ResolveLearnedDwellSeconds(LearnedCorrectionPillPhase.UndoPending));
        Assert.Equal(1.5, OverlayPillCatalog.ResolveLearnedDwellSeconds(LearnedCorrectionPillPhase.Undone));
        Assert.Equal(3.0, OverlayPillCatalog.ResolveLearnedDwellSeconds(LearnedCorrectionPillPhase.UndoError));
    }

    [Fact]
    public void ExpiryClockFixedDwellInvalidatesPriorArmsAndSkipsVisualTicks()
    {
        var now = TimeSpan.FromSeconds(10);
        var clock = new OverlayExpiryClock(() => now);

        var firstArm = clock.ArmFixedDwell(TimeSpan.FromSeconds(5));
        var secondArm = clock.ArmFixedDwell(TimeSpan.FromSeconds(3));

        Assert.NotEqual(firstArm, secondArm);
        Assert.Equal(OverlayExpiryMode.FixedDwell, clock.Mode);
        Assert.Equal(TimeSpan.FromSeconds(3), clock.RecommendedNextInterval(animationsEnabled: true));

        now += TimeSpan.FromSeconds(4);
        Assert.False(clock.Evaluate(firstArm).Expired);
        Assert.False(clock.Evaluate(firstArm).IsActive);

        var secondResult = clock.Evaluate(secondArm);
        Assert.True(secondResult.Expired);
        Assert.False(clock.IsActive);
        Assert.Null(clock.RecommendedNextInterval(animationsEnabled: true));
    }

    [Fact]
    public void ExpiryClockEscapeCountdownResetsOnHoverAndRespectsAnimationsFlag()
    {
        var now = TimeSpan.Zero;
        var clock = new OverlayExpiryClock(() => now);

        var arm = clock.ArmEscapeCountdown(TimeSpan.FromSeconds(3));
        Assert.Equal(TimeSpan.FromMilliseconds(33), clock.RecommendedNextInterval(animationsEnabled: true));
        Assert.Equal(TimeSpan.FromSeconds(3), clock.RecommendedNextInterval(animationsEnabled: false));

        now += TimeSpan.FromSeconds(1.5);
        var mid = clock.Evaluate(arm);
        Assert.True(mid.IsActive);
        Assert.False(mid.Expired);
        Assert.Equal(0.5, mid.RailFraction, 3);

        clock.PauseForHover();
        Assert.Null(clock.RecommendedNextInterval(animationsEnabled: true));
        now += TimeSpan.FromSeconds(10);
        var paused = clock.Evaluate(arm);
        Assert.True(paused.IsActive);
        Assert.False(paused.Expired);
        Assert.Equal(1.0, paused.RailFraction, 3);

        clock.ResumeFromHover(TimeSpan.FromSeconds(2));
        now += TimeSpan.FromSeconds(3);
        Assert.True(clock.Evaluate(arm).Expired);
    }

    [Fact]
    public void ExpiryClockLearnedCountdownAccumulatesWithResumeFloorAndUndoPendingFloor()
    {
        var now = TimeSpan.Zero;
        var clock = new OverlayExpiryClock(() => now);

        var arm = clock.ArmLearnedCountdown(TimeSpan.FromSeconds(12));
        now += TimeSpan.FromSeconds(11);
        var nearEnd = clock.Evaluate(arm);
        Assert.InRange(nearEnd.RailFraction, 0.90, 0.93);

        clock.PauseForHover();
        clock.ResumeFromHover(TimeSpan.FromSeconds(2));

        // Clamped to 10s elapsed so at least 2s remain after hover exit.
        var resumed = clock.Evaluate(arm);
        Assert.False(resumed.Expired);
        Assert.InRange(resumed.RailFraction, 10.0 / 12.0 - 0.01, 10.0 / 12.0 + 0.01);

        now += TimeSpan.FromSeconds(1);
        var pendingArm = clock.EnterUndoPending(TimeSpan.FromSeconds(3));
        Assert.NotEqual(arm, pendingArm);

        // UndoPending stays up and does not auto-expire on its own even past the floor.
        now += TimeSpan.FromSeconds(5);
        var pendingDone = clock.Evaluate(pendingArm);
        Assert.True(pendingDone.IsActive);
        Assert.False(pendingDone.Expired);
        Assert.Equal(1.0, pendingDone.RailFraction, 3);
    }

    [Fact]
    public void DeferredCardAndLearnedSubViewsAreExtractedFromMainOverlayWindowAndUseTransforms()
    {
        var root = FindRepositoryRoot();
        var overlayXamlPath = Path.Combine(root, "src", "Production", "EnviousWispr.App", "DictationOverlayWindow.xaml");
        var overlayDoc = XDocument.Load(overlayXamlPath);
        var declaredNames = overlayDoc.Descendants()
            .Select(e => (string?)e.Attribute(XamlNamespace + "Name"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("DeferredSurfaceHost", declaredNames);
        Assert.Contains("LearnedCountdownScale", declaredNames);
        Assert.Contains("EscapeCountdownScale", declaredNames);

        Assert.DoesNotContain("LearnedPillPanel", declaredNames);
        Assert.DoesNotContain("BluetoothAwarenessCardPanel", declaredNames);
        Assert.DoesNotContain("PolishSetupCardPanel", declaredNames);

        var subviewPaths = new[]
        {
            Path.Combine(root, "src", "Production", "EnviousWispr.App", "Overlay", "LearnedCorrectionPillView.xaml"),
            Path.Combine(root, "src", "Production", "EnviousWispr.App", "Overlay", "BluetoothAwarenessCardView.xaml"),
            Path.Combine(root, "src", "Production", "EnviousWispr.App", "Overlay", "PolishSetupCardView.xaml"),
        };
        Assert.All(subviewPaths, path => Assert.True(File.Exists(path), $"Expected deferred view: {path}"));
    }

    [Fact]
    public void OverlayHotPathAvoidsPerTickAllocationsAndUncachedSpiQueriesAndCleansUpOnShutdown()
    {
        var root = FindRepositoryRoot();
        var overlayDir = Path.Combine(root, "src", "Production", "EnviousWispr.App", "Overlay");
        var surfaceSource = File.ReadAllText(Path.Combine(overlayDir, "OverlayRecordingSurface.cs"));
        var hostSource = File.ReadAllText(Path.Combine(overlayDir, "OverlayWin32Host.cs"));
        var windowSource = File.ReadAllText(Path.Combine(root, "src", "Production", "EnviousWispr.App", "DictationOverlayWindow.xaml.cs"));

        // Brush and RectangleGeometry allocations happen only during field initialization/InitializeMeter.
        Assert.Equal(1, Regex.Count(surfaceSource, @"new\s+SolidColorBrush\("));
        Assert.Equal(1, Regex.Count(surfaceSource, @"RectangleGeometry\s+_previewClipGeometry\s*=\s*new\(\)"));

        // Cached AnimationsEnabled invalidated on WM_SETTINGCHANGE.
        Assert.Contains("WmSettingChange", hostSource, StringComparison.Ordinal);
        Assert.Contains("_animationsEnabledCache", hostSource, StringComparison.Ordinal);

        // At most 3 DispatcherTimers across the overlay subsystem (elapsed, level, single expiry driver).
        var allOverlayCode = AppXamlSources.LoadOverlayCodeText(root);
        var dispatcherTimerNewCount = Regex.Count(allOverlayCode, @"DispatcherTimer\b[^;=]*=\s*new");
        Assert.True(dispatcherTimerNewCount <= 3, $"Expected <= 3 DispatcherTimers, found {dispatcherTimerNewCount}.");

        // Shutdown cleans up event handlers, XamlRoot, timers, Win32 subclass, and subviews.
        Assert.Contains("_observedXamlRoot.Changed -= OnXamlRootChanged", windowSource, StringComparison.Ordinal);
        Assert.Contains("OverlayRoot.ActualThemeChanged -= OnOverlayRootActualThemeChanged", windowSource, StringComparison.Ordinal);
        Assert.Contains("_expiryDriver.Dispose()", windowSource, StringComparison.Ordinal);
        Assert.Contains("_win32Host.Dispose()", windowSource, StringComparison.Ordinal);
        Assert.Contains("_learnedPillView.Detach()", windowSource, StringComparison.Ordinal);
        Assert.Contains("_bluetoothCardView.Detach()", windowSource, StringComparison.Ordinal);
        Assert.Contains("_polishSetupCardView.Detach()", windowSource, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EnviousWispr.Windows.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
