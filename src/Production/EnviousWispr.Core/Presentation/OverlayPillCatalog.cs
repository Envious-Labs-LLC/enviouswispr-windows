using EnviousWispr.Core.Learning;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Core.Presentation;

/// <summary>Focused visual surface rendered by the dictation overlay window.</summary>
public enum OverlayPillSurfaceKind
{
    Hidden,
    Recording,
    Processing,
    Notice,
    EscapeRecovery,
    LearnedCorrection,
    BluetoothCard,
    PolishSetupCard,
}

/// <summary>Theme resource keys describing icon, border edge, and background wash styles.</summary>
public readonly record struct OverlaySeverityLook(
    string IconStyleKey,
    string EdgeStyleKey,
    string WashStyleKey);

/// <summary>Typed presentation metadata resolved from a <see cref="DictationStatus"/>.</summary>
public readonly record struct OverlayPillDefinition(
    OverlayPillSurfaceKind Surface,
    string Title,
    string IconGlyph,
    double DefaultDwellSeconds,
    bool AutoDwells,
    bool HoverPauses,
    OverlaySeverityLook Severity);

/// <summary>
/// Pure catalog mapping overlay states and statuses to surfaces, severity tokens, copy, and dwell policy.
/// </summary>
public static class OverlayPillCatalog
{
    public const double LearnedDwellSeconds = 12.0;
    public const double LearnedHoverResumeFloorSeconds = 2.0;
    public const double LearnedUndoPendingFloorSeconds = 3.0;
    public const double LearnedUndoneDwellSeconds = 1.5;
    public const double LearnedErrorDwellSeconds = 3.0;
    public const double EscapeRecoveryDwellSeconds = 3.0;

    public static readonly TimeSpan ApproachingCapElapsedThreshold = TimeSpan.FromMinutes(59);

    public const string MaxDurationTranscribingText = "60-minute limit reached. Transcribing...";

    public static OverlayPillSurfaceKind ResolveSurface(DictationOverlayState state) => state switch
    {
        DictationOverlayState.Hidden => OverlayPillSurfaceKind.Hidden,
        DictationOverlayState.Recording => OverlayPillSurfaceKind.Recording,
        DictationOverlayState.Processing => OverlayPillSurfaceKind.Processing,
        DictationOverlayState.EscapeRecovery => OverlayPillSurfaceKind.EscapeRecovery,
        DictationOverlayState.CorrectionLearned => OverlayPillSurfaceKind.LearnedCorrection,
        DictationOverlayState.BluetoothCard => OverlayPillSurfaceKind.BluetoothCard,
        DictationOverlayState.PolishSetupCard => OverlayPillSurfaceKind.PolishSetupCard,
        _ => OverlayPillSurfaceKind.Notice,
    };

    /// <summary>
    /// Maps every <see cref="DictationOverlayState"/> member explicitly to its Pill* theme style triple.
    /// </summary>
    public static OverlaySeverityLook ResolveSeverity(DictationOverlayState state)
    {
        var (icon, edge, wash) = state switch
        {
            DictationOverlayState.Success =>
                ("PillSuccessIconStyle", "PillSuccessEdgeStyle", "PillSuccessWashStyle"),
            DictationOverlayState.Advisory =>
                ("PillMutedIconStyle", "PillAdvisoryEdgeStyle", "PillAdvisoryWashStyle"),
            DictationOverlayState.Suggestion =>
                ("PillAdvisoryIconStyle", "PillAdvisoryEdgeStyle", "PillAdvisoryWashStyle"),
            DictationOverlayState.Warning or DictationOverlayState.Busy or DictationOverlayState.RetainedPaste =>
                ("PillWarningIconStyle", "PillWarningEdgeStyle", "PillWarningWashStyle"),
            DictationOverlayState.Distress =>
                ("PillDistressIconStyle", "PillDistressEdgeStyle", "PillDistressWashStyle"),
            DictationOverlayState.Error =>
                ("PillErrorIconStyle", "PillErrorEdgeStyle", "PillErrorWashStyle"),
            DictationOverlayState.Hidden
                or DictationOverlayState.Recording
                or DictationOverlayState.Processing
                or DictationOverlayState.Recovering
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

        return new OverlaySeverityLook(icon, edge, wash);
    }

    public static string ResolveFrameEdgeStyle(DictationOverlayState state, RecordingPillDesign activeDesign) =>
        state switch
        {
            DictationOverlayState.Recording => activeDesign == RecordingPillDesign.ReadingWell
                ? "PillWellEdgeStyle"
                : "PillCapsuleEdgeStyle",
            DictationOverlayState.EscapeRecovery => "PillEscapeEdgeStyle",
            DictationOverlayState.BluetoothCard or DictationOverlayState.PolishSetupCard => "PillCardEdgeStyle",
            DictationOverlayState.CorrectionLearned => "PillLearnedEdgeStyle",
            _ => "PillCapsuleEdgeStyle",
        };

    public static (string Title, string IconGlyph) ResolvePresentation(DictationStatus status)
    {
        var detail = status.Text;
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
            DictationOverlayState.RetainedPaste => (string.IsNullOrWhiteSpace(detail) ? "Paste did not land. Text kept on clipboard. Press Ctrl+V" : detail, string.Empty),
            DictationOverlayState.Recovering => (string.IsNullOrWhiteSpace(detail) ? "Recovering last recording..." : detail, string.Empty),
            DictationOverlayState.Busy => (fallback, string.Empty),
            DictationOverlayState.Distress => (string.IsNullOrWhiteSpace(detail) ? "Recording interrupted." : detail, string.Empty),
            DictationOverlayState.Error => (string.IsNullOrWhiteSpace(detail) ? "Something went wrong. Try again." : detail, "\uEA39"),
            DictationOverlayState.EscapeRecovery => (string.IsNullOrWhiteSpace(detail) ? "Dictation cancelled" : detail, string.Empty),
            DictationOverlayState.BluetoothCard => ("Bluetooth mic detected", "\uE7F6"),
            DictationOverlayState.PolishSetupCard => (string.IsNullOrWhiteSpace(detail) ? "Polish needs setup." : detail, "\uE82F"),
            _ => (fallback, "\uE720"),
        };
    }

    public static bool ShouldAutoDwell(DictationOverlayState state) => state is
        DictationOverlayState.Success
        or DictationOverlayState.EngineReady
        or DictationOverlayState.ImportStatus
        or DictationOverlayState.Advisory
        or DictationOverlayState.Suggestion
        or DictationOverlayState.Warning
        or DictationOverlayState.RetainedPaste
        or DictationOverlayState.Distress
        or DictationOverlayState.Error
        or DictationOverlayState.Busy;

    public static double DefaultDwellSeconds(DictationStatus status) => status.State switch
    {
        DictationOverlayState.EngineReady => 1.5d,
        DictationOverlayState.Distress => 2.0d,
        DictationOverlayState.Success or DictationOverlayState.Warning => 2.5d,
        DictationOverlayState.RetainedPaste => 4.5d,
        DictationOverlayState.ImportStatus => 3.0d,
        DictationOverlayState.Error => status.ErrorVariant switch
        {
            RichErrorVariant.MicNotResponding => 8.0d,
            RichErrorVariant.PermissionDenied => 6.0d,
            _ => 3.0d,
        },
        DictationOverlayState.Suggestion or DictationOverlayState.Busy => 6.0d,
        DictationOverlayState.Advisory => 8.0d,
        DictationOverlayState.EscapeRecovery => EscapeRecoveryDwellSeconds,
        _ => 3.0d,
    };

    public static double ResolveLearnedDwellSeconds(LearnedCorrectionPillPhase phase) => phase switch
    {
        LearnedCorrectionPillPhase.Learned => LearnedDwellSeconds,
        LearnedCorrectionPillPhase.UndoPending => LearnedUndoPendingFloorSeconds,
        LearnedCorrectionPillPhase.Undone => LearnedUndoneDwellSeconds,
        LearnedCorrectionPillPhase.UndoError => LearnedErrorDwellSeconds,
        _ => 0,
    };

    public static OverlayPillDefinition Resolve(DictationStatus status)
    {
        var surface = ResolveSurface(status.State);
        var (title, glyph) = ResolvePresentation(status);
        var autoDwells = ShouldAutoDwell(status.State);
        var defaultDwell = status.DwellSeconds ?? (autoDwells || status.State == DictationOverlayState.EscapeRecovery
            ? DefaultDwellSeconds(status)
            : 0d);
        var hoverPauses = status.State is DictationOverlayState.EscapeRecovery
            or DictationOverlayState.CorrectionLearned
            or DictationOverlayState.Suggestion;
        return new OverlayPillDefinition(
            Surface: surface,
            Title: title,
            IconGlyph: glyph,
            DefaultDwellSeconds: defaultDwell,
            AutoDwells: autoDwells,
            HoverPauses: hoverPauses,
            Severity: ResolveSeverity(status.State));
    }

    public static string ComputeSpokenName(
        DictationOverlayState state,
        DictationStatus status,
        string title,
        string? recordingSpokenName = null) => state switch
    {
        DictationOverlayState.Recording => recordingSpokenName ?? title,
        DictationOverlayState.Processing
            or DictationOverlayState.EngineReady
            or DictationOverlayState.ImportStatus
            or DictationOverlayState.Success => title,
        DictationOverlayState.ColdStart => string.IsNullOrWhiteSpace(status.Subtitle)
            ? title
            : $"{title}. {status.Subtitle}",
        DictationOverlayState.BluetoothCard =>
            "Bluetooth mic detected. Bluetooth microphones can take a moment on a cold start.",
        DictationOverlayState.PolishSetupCard =>
            $"{title}. {status.Subtitle ?? "Pasted without AI polish."}",
        _ => string.IsNullOrWhiteSpace(status.Subtitle)
            ? title
            : $"{title}. {status.Subtitle}",
    };

    public static string FormatRecordingNoticeText(RecordingNoticeKind notice) => notice switch
    {
        RecordingNoticeKind.ApproachingCap => "Recording auto-stops in under a minute (60-minute cap)",
        RecordingNoticeKind.AutoStopUnavailable => "Auto-stop unavailable right now",
        RecordingNoticeKind.MicSilentCalm => "We\u2019re not hearing anything from your microphone.",
        RecordingNoticeKind.MicSilentStrong => "Your microphone isn\u2019t sending sound. Stop and check it in Settings.",
        RecordingNoticeKind.MicMuted => "Your microphone is muted in Windows. Unmute it to dictate.",
        _ => string.Empty,
    };
}
