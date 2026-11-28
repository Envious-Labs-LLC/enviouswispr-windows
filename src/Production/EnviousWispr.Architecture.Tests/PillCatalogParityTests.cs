using System.Xml.Linq;
using EnviousWispr.Core.Presentation;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EnviousWispr.Architecture.Tests;

public sealed class PillCatalogParityTests
{
    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void CatalogGroupsAreCompleteAndOrdered()
    {
        Assert.Equal(
            [
                PillPreviewGroup.Recording,
                PillPreviewGroup.ProcessingAndReadiness,
                PillPreviewGroup.WarningsErrorsAndInterruptions,
                PillPreviewGroup.UndoAndLearning,
                PillPreviewGroup.CardsAndChips,
            ],
            PillPreviewCatalog.OrderedGroups);
    }

    [Fact]
    public void EveryPreviewKindAppearsOnceAndCyclesCleanly()
    {
        var allKinds = Enum.GetValues<PillPreviewKind>();
        var catalogKinds = PillPreviewCatalog.Entries.Select(entry => entry.Kind).ToArray();

        Assert.Equal(allKinds.Length, catalogKinds.Length);
        Assert.Equal(allKinds.Length, catalogKinds.Distinct().Count());

        var entries = PillPreviewCatalog.Entries;
        Assert.All(entries, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Title));
            Assert.False(string.IsNullOrWhiteSpace(entry.Summary));
            Assert.False(string.IsNullOrWhiteSpace(entry.GroupTitle));
            Assert.False(string.IsNullOrWhiteSpace(entry.DwellDescription));
            Assert.Equal(entry, PillPreviewCatalog.Find(entry.Kind));
        });

        Assert.Equal(entries[0], PillPreviewCatalog.NextAfter(entries[^1].Kind));
    }

    [Fact]
    public void EveryNonHiddenOverlayStateIsRepresentedInGallery()
    {
        var represented = PillPreviewCatalog.Entries
            .Select(entry => entry.LearnedModel is not null
                ? DictationOverlayState.CorrectionLearned
                : entry.PreviewStatus().State)
            .ToHashSet();

        foreach (var state in Enum.GetValues<DictationOverlayState>())
        {
            if (state == DictationOverlayState.Hidden)
            {
                continue;
            }

            Assert.Contains(state, represented);
        }
    }

    [Fact]
    public void DeliberateMacExclusionsAreExplicitlyDocumented()
    {
        var titles = PillPreviewCatalog.ExcludedFromWindows
            .Select(item => item.Title)
            .ToArray();

        Assert.Contains(titles, t => t.Contains("Accessibility", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(titles, t => t.Contains("Meeting Summary Limit", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(titles, t => t.Contains("Call Pop-ups", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(titles, t => t.Contains("Crash Recovery", StringComparison.OrdinalIgnoreCase));
        Assert.All(PillPreviewCatalog.ExcludedFromWindows, exclusion =>
            Assert.True(exclusion.Reason.Length >= 25, $"{exclusion.Title} exclusion needs rationale."));
    }

    [Fact]
    public void CanonicalDwellTimesAndClipboardStatusTextHold()
    {
        Assert.Equal(2.5, PillPreviewCatalog.Find(PillPreviewKind.WarningPolishFailed).PreviewStatus().DwellSeconds);
        Assert.Equal(3.0, PillPreviewCatalog.Find(PillPreviewKind.ErrorAudioCapture).PreviewStatus().DwellSeconds);
        Assert.Equal(6.0, PillPreviewCatalog.Find(PillPreviewKind.ErrorPermissionDenied).PreviewStatus().DwellSeconds);
        Assert.Equal(8.0, PillPreviewCatalog.Find(PillPreviewKind.ErrorMicNotResponding).PreviewStatus().DwellSeconds);
        Assert.Equal(8.0, PillPreviewCatalog.Find(PillPreviewKind.AdvisoryZeroSignal).PreviewStatus().DwellSeconds);
        Assert.Equal(2.0, PillPreviewCatalog.Find(PillPreviewKind.DistressDeviceDisconnected).PreviewStatus().DwellSeconds);
        Assert.Equal(2.5, PillPreviewCatalog.Find(PillPreviewKind.ClipboardFallback).PreviewStatus().DwellSeconds);
        Assert.Equal(3.0, PillPreviewCatalog.Find(PillPreviewKind.EscapeRecovery).PreviewStatus().DwellSeconds);
        Assert.Equal(6.0, PillPreviewCatalog.Find(PillPreviewKind.LanguageChip).PreviewStatus().DwellSeconds);
        Assert.Equal(1.5, PillPreviewCatalog.Find(PillPreviewKind.EngineReady).PreviewStatus().DwellSeconds);
        Assert.Equal(3.0, PillPreviewCatalog.Find(PillPreviewKind.ImportStatus).PreviewStatus().DwellSeconds);

        Assert.True(PillPreviewCatalog.Find(PillPreviewKind.EscapeRecovery).HoverPauses);
        Assert.True(PillPreviewCatalog.Find(PillPreviewKind.LanguageChip).HoverPauses);
        Assert.True(PillPreviewCatalog.Find(PillPreviewKind.CorrectionLearned).HoverPauses);
        Assert.True(PillPreviewCatalog.Find(PillPreviewKind.BluetoothAwarenessCard).PersistsUntilDismissed);
        Assert.True(PillPreviewCatalog.Find(PillPreviewKind.PolishSetupCard).PersistsUntilDismissed);

        Assert.Equal("Copied. Press Ctrl+V to paste", PillPreviewCatalog.Find(PillPreviewKind.ClipboardFallback).PreviewStatus().Text);
        Assert.Equal("Ready, press to dictate", DictationStatus.EngineReady().Text);
        Assert.Equal(TimeSpan.FromMinutes(59), OverlayPillCatalog.ApproachingCapElapsedThreshold);
        Assert.Equal(
            "Recording auto-stops in under a minute (60-minute cap)",
            OverlayPillCatalog.FormatRecordingNoticeText(RecordingNoticeKind.ApproachingCap));
        Assert.Equal(
            "We\u2019re not hearing anything from your microphone.",
            OverlayPillCatalog.FormatRecordingNoticeText(RecordingNoticeKind.MicSilentCalm));
        Assert.Equal(
            "Your microphone isn\u2019t sending sound. Stop and check it in Settings.",
            OverlayPillCatalog.FormatRecordingNoticeText(RecordingNoticeKind.MicSilentStrong));
        Assert.Equal(
            PillActionKind.OpenMicrophoneSettings,
            PillPreviewCatalog.Find(PillPreviewKind.AdvisoryZeroSignal).PreviewStatus().Action?.Kind);
        Assert.Equal(
            RecordingNoticeKind.MicSilentCalm,
            PillPreviewCatalog.Find(PillPreviewKind.RecordingCapsuleSilentMicCalm).PreviewStatus().RecordingNotice);
        Assert.Equal(
            RecordingNoticeKind.MicSilentStrong,
            PillPreviewCatalog.Find(PillPreviewKind.RecordingCapsuleSilentMicStrong).PreviewStatus().RecordingNotice);
        Assert.Equal(
            RecordingNoticeKind.MicSilentCalm,
            PillPreviewCatalog.Find(PillPreviewKind.RecordingLevelRailSilentMicCalm).PreviewStatus().RecordingNotice);
        Assert.Equal(
            RecordingNoticeKind.MicSilentStrong,
            PillPreviewCatalog.Find(PillPreviewKind.RecordingLevelRailSilentMicStrong).PreviewStatus().RecordingNotice);
        Assert.Equal(
            RecordingNoticeKind.MicSilentCalm,
            PillPreviewCatalog.Find(PillPreviewKind.RecordingReadingWellSilentMicCalm).PreviewStatus().RecordingNotice);
        Assert.Equal(
            RecordingNoticeKind.MicSilentStrong,
            PillPreviewCatalog.Find(PillPreviewKind.RecordingReadingWellSilentMicStrong).PreviewStatus().RecordingNotice);
        Assert.Equal(
            "60-minute limit reached. Transcribing...",
            OverlayPillCatalog.MaxDurationTranscribingText);
        Assert.Equal(
            OverlayPillCatalog.MaxDurationTranscribingText,
            PillPreviewCatalog.Find(PillPreviewKind.ProcessingMaxDuration).PreviewStatus().Text);
        Assert.Equal(512L * 1024 * 1024, EnviousWispr.Core.History.DictationRecordingRetention.DefaultMaximumTotalBytes);
    }

    [Fact]
    public void CatalogAndOverlayMarkupContainNoEmOrEnDashes()
    {
        var root = FindRepositoryRoot();
        var targets = new[]
        {
            Path.Combine(root, "src", "Production", "EnviousWispr.Core", "Presentation", "DictationStatus.cs"),
            Path.Combine(root, "src", "Production", "EnviousWispr.Core", "Presentation", "PillPreviewCatalog.cs"),
            Path.Combine(root, "src", "Production", "EnviousWispr.Core", "Presentation", "OverlayPillCatalog.cs"),
            Path.Combine(root, "src", "Production", "EnviousWispr.Core", "Presentation", "OverlayExpiryClock.cs"),
            Path.Combine(root, "src", "Production", "EnviousWispr.App", "DictationOverlayWindow.xaml"),
            Path.Combine(root, "src", "Production", "EnviousWispr.App", "DictationOverlayWindow.xaml.cs"),
            Path.Combine(root, "src", "Production", "EnviousWispr.App", "MainWindow.DiagnosticsAndUpdates.cs"),
        }.Concat(Directory.EnumerateFiles(
            Path.Combine(root, "src", "Production", "EnviousWispr.App", "Overlay"),
            "*",
            SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal));

        foreach (var path in targets)
        {
            var text = File.ReadAllText(path);
            Assert.DoesNotContain('\u2014', text);
            Assert.DoesNotContain('\u2013', text);
        }

        Assert.All(PillPreviewCatalog.Entries, entry =>
        {
            Assert.DoesNotContain('\u2014', entry.Title);
            Assert.DoesNotContain('\u2013', entry.Title);
            Assert.DoesNotContain('\u2014', entry.Summary);
            Assert.DoesNotContain('\u2013', entry.Summary);
            Assert.DoesNotContain('\u2014', entry.PreviewStatus().Text);
            Assert.DoesNotContain('\u2013', entry.PreviewStatus().Text);
        });
    }

    [Fact]
    public void DiagnosticsSectionHostsAllPillPreviewControlsSafely()
    {
        var root = FindRepositoryRoot();
        var mainWindowXaml = AppXamlSources.LoadMainWindowAndPagesDocument(root);
        var diagnostics = mainWindowXaml.Descendants()
            .FirstOrDefault(el => (string?)el.Attribute(XamlNamespace + "Name") == "DiagnosticsSection");
        Assert.NotNull(diagnostics);

        var expectedControls = new[]
        {
            "PillReviewModeToggle",
            "PillReviewControlsPanel",
            "PillPreviewComboBox",
            "PillPreviewPlayButton",
            "PillPreviewNextButton",
            "PillPreviewDismissButton",
            "PillPreviewHoldOpenToggle",
            "PillPreviewStatusText",
            "PillPreviewActionEventText",
            "PillPreviewExclusionsText",
        };

        var descendantNames = diagnostics!.Descendants()
            .Select(el => (string?)el.Attribute(XamlNamespace + "Name"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(expectedControls, control => Assert.Contains(control, descendantNames));

        var overlayCode = File.ReadAllText(Path.Combine(root, "src", "Production", "EnviousWispr.App", "DictationOverlayWindow.xaml.cs"));
        Assert.Contains("if (_isPreviewSession)", overlayCode, StringComparison.Ordinal);
        Assert.Contains("(preview only)", overlayCode, StringComparison.Ordinal);

        var diagnosticsCode = File.ReadAllText(Path.Combine(root, "src", "Production", "EnviousWispr.App", "MainWindow.DiagnosticsAndUpdates.cs"));
        Assert.Contains("PillReviewModeToggle.IsOn = false;", diagnosticsCode, StringComparison.Ordinal);
        Assert.Contains("_overlayWindow.IsActiveNonPreview", diagnosticsCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldSuppressStatusForPreviewOnlyChecksVisiblePreview()
    {
        foreach (var state in Enum.GetValues<DictationOverlayState>())
        {
            Assert.False(PillPreviewCatalog.ShouldSuppressStatusForPreview(previewVisible: false, state));

            var expectedWhenPreviewVisible = state is not (DictationOverlayState.Recording or DictationOverlayState.Processing);
            Assert.Equal(expectedWhenPreviewVisible, PillPreviewCatalog.ShouldSuppressStatusForPreview(previewVisible: true, state));
        }

        var root = FindRepositoryRoot();
        var setSessionStatus = AppXamlSources.MainWindowShellPaths(root)
            .SelectMany(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path))
                .GetRoot()
                .DescendantNodes()
                .OfType<MethodDeclarationSyntax>())
            .Single(method => method.Identifier.Text == "SetSessionStatus");

        var identifiers = setSessionStatus
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Select(identifier => identifier.Identifier.Text)
            .ToArray();

        Assert.Contains(nameof(PillPreviewCatalog.ShouldSuppressStatusForPreview), identifiers);
        Assert.Contains("IsPreviewVisible", identifiers);
        Assert.DoesNotContain("PillReviewModeToggle", identifiers);
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
