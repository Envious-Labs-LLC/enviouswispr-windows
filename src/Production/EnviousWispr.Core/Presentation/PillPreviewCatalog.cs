using EnviousWispr.Core.Learning;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Core.Presentation;

/// <summary>Review sections organizing the Windows overlay gallery.</summary>
public enum PillPreviewGroup
{
    Recording,
    ProcessingAndReadiness,
    WarningsErrorsAndInterruptions,
    UndoAndLearning,
    CardsAndChips,
}

/// <summary>Every discrete overlay pill, notice, chip, and floating card Windows can render.</summary>
public enum PillPreviewKind
{
    RecordingCapsule,
    RecordingCapsuleHandsFree,
    RecordingCapsuleApproachingCap,
    RecordingLevelRail,
    RecordingLevelRailAutoStopUnavailable,
    RecordingReadingWell,
    RecordingReadingWellApproachingCap,
    ProcessingTranscribing,
    ProcessingPolishing,
    ColdStart,
    EngineReady,
    ImportStatus,
    ClipboardFallback,
    WarningPolishFailed,
    AdvisoryZeroSignal,
    ErrorAudioCapture,
    ErrorPermissionDenied,
    ErrorMicNotResponding,
    DistressDeviceDisconnected,
    DistressRecordingInterrupted,
    BusyFileTranscribing,
    EscapeRecovery,
    CorrectionLearned,
    CorrectionLearnedUndoPending,
    CorrectionLearnedUndone,
    CorrectionLearnedSaveError,
    LanguageChip,
    BluetoothAwarenessCard,
    PolishSetupCard,
}

/// <summary>Presentation descriptor for a single entry in the Diagnostics Pill Preview gallery.</summary>
public sealed record PillPreviewEntry(
    PillPreviewKind Kind,
    PillPreviewGroup Group,
    string GroupTitle,
    string Title,
    string Summary,
    RecordingPillDesign? PreviewDesign = null,
    string? PreviewTranscript = null,
    LearnedCorrectionPillModel? LearnedModel = null,
    string? SaveErrorWord = null,
    bool HoverPauses = false,
    bool PersistsUntilDismissed = false)
{
    /// <summary>Formatted header label shown inside categorized combo boxes.</summary>
    public string DisplayLabel => $"{GroupTitle}: {Title}";

    /// <summary>Returns the preview status via an ordinary method carrier verified by EW0003.</summary>
    public DictationStatus PreviewStatus() => PillPreviewCatalog.StatusFor(Kind);

    /// <summary>Human-readable dwell description for the preview gallery status line.</summary>
    public string DwellDescription => PersistsUntilDismissed
        ? "Stays open until dismissed"
        : PillPreviewCatalog.DwellDescriptionFor(Kind, HoverPauses);
}

/// <summary>macOS overlay pill deliberately excluded on Windows, with rationale.</summary>
public sealed record PillPreviewExclusion(string Title, string Reason);

/// <summary>
/// Canonical catalog of Windows overlay pills, cards, states, and documented macOS exclusions.
/// </summary>
public static class PillPreviewCatalog
{
    private static readonly Guid SampleLearnedPillId = new("11111111-2222-3333-4444-555555555555");

    /// <summary>Ordered gallery groups matching the review matrix.</summary>
    public static IReadOnlyList<PillPreviewGroup> OrderedGroups { get; } =
    [
        PillPreviewGroup.Recording,
        PillPreviewGroup.ProcessingAndReadiness,
        PillPreviewGroup.WarningsErrorsAndInterruptions,
        PillPreviewGroup.UndoAndLearning,
        PillPreviewGroup.CardsAndChips,
    ];

    /// <summary>All Windows overlay pill entries in canonical presentation order.</summary>
    public static IReadOnlyList<PillPreviewEntry> Entries { get; } = BuildEntries();

    /// <summary>macOS overlay surfaces intentionally omitted on Windows.</summary>
    public static IReadOnlyList<PillPreviewExclusion> ExcludedFromWindows { get; } =
    [
        new(
            "Accessibility Toast",
            "macOS-only Accessibility API permission nudge. Windows delivers text via UI Automation, SendInput, and clipboard without an Accessibility prompt."),
        new(
            "Meeting Summary Limit",
            "Belongs to macOS Meetings and Notes, which is not part of the Windows app."),
        new(
            "Call Pop-ups",
            "Floating call-detection banners are macOS-only."),
        new(
            "Crash Recovery (in-window card)",
            "Windows surfaces interrupted-session recovery via the main window Home banner rather than a floating card."),
    ];

    /// <summary>Returns display heading for a gallery group.</summary>
    public static string TitleFor(PillPreviewGroup group) => group switch
    {
        PillPreviewGroup.Recording => "Recording",
        PillPreviewGroup.ProcessingAndReadiness => "Processing & Readiness",
        PillPreviewGroup.WarningsErrorsAndInterruptions => "Warnings, Errors & Interruptions",
        PillPreviewGroup.UndoAndLearning => "Undo & Learning",
        PillPreviewGroup.CardsAndChips => "Cards & Chips",
        _ => "Other",
    };

    /// <summary>Returns the entry matching a strongly typed kind.</summary>
    public static PillPreviewEntry Find(PillPreviewKind kind) =>
        Entries.First(entry => entry.Kind == kind);

    /// <summary>Returns the entry immediately following the specified kind, wrapping around.</summary>
    public static PillPreviewEntry NextAfter(PillPreviewKind kind)
    {
        for (var index = 0; index < Entries.Count; index++)
        {
            if (Entries[index].Kind == kind)
            {
                return Entries[(index + 1) % Entries.Count];
            }
        }

        return Entries[0];
    }

    /// <summary>Resolves the strongly typed overlay status for any preview entry via DictationStatus factories.</summary>
    public static DictationStatus StatusFor(PillPreviewKind kind) => kind switch
    {
        PillPreviewKind.RecordingCapsule => DictationStatus.Recording("Listening"),
        PillPreviewKind.RecordingCapsuleHandsFree => DictationStatus.Recording("Hands-free recording", isHandsFree: true),
        PillPreviewKind.RecordingCapsuleApproachingCap => DictationStatus.Recording("Recording", isHandsFree: false, RecordingNoticeKind.ApproachingCap),
        PillPreviewKind.RecordingLevelRail => DictationStatus.Recording("Listening"),
        PillPreviewKind.RecordingLevelRailAutoStopUnavailable => DictationStatus.Recording("Recording", isHandsFree: true, RecordingNoticeKind.AutoStopUnavailable),
        PillPreviewKind.RecordingReadingWell => DictationStatus.Recording("Listening"),
        PillPreviewKind.RecordingReadingWellApproachingCap => DictationStatus.Recording("Recording", isHandsFree: true, RecordingNoticeKind.ApproachingCap),
        PillPreviewKind.ProcessingTranscribing => DictationStatus.Processing("Transcribing..."),
        PillPreviewKind.ProcessingPolishing => DictationStatus.Processing("Polishing..."),
        PillPreviewKind.ColdStart => DictationStatus.ColdStart("Getting dictation ready...", "Parakeet is warming up after a restart"),
        PillPreviewKind.EngineReady => DictationStatus.EngineReady("Ready, press to dictate"),
        PillPreviewKind.ImportStatus => DictationStatus.ImportStatus("Importing your words now. Check progress on the Dictionary page."),
        PillPreviewKind.ClipboardFallback => DictationStatus.Success("Copied. Press Ctrl+V to paste"),
        PillPreviewKind.WarningPolishFailed => DictationStatus.Warning("Pasted without AI polish."),
        PillPreviewKind.AdvisoryZeroSignal => DictationStatus.Advisory("Audio isn't capturing. Your mic may be muted or disconnected. Please check your microphone settings."),
        PillPreviewKind.ErrorAudioCapture => DictationStatus.Error("Audio capture error. Try again."),
        PillPreviewKind.ErrorPermissionDenied => DictationStatus.RichError(
            "Microphone access is blocked",
            "Allow EnviousWispr to use your microphone in Windows Settings.",
            new PillAction("Open Settings", PillActionKind.OpenMicrophonePrivacySettings, "Open Windows microphone privacy settings"),
            RichErrorVariant.PermissionDenied),
        PillPreviewKind.ErrorMicNotResponding => DictationStatus.RichError(
            "Microphone not responding",
            "No audio frames arrived from the selected device. Check mute switches or pick another mic.",
            new PillAction("How to fix", PillActionKind.OpenMicrophoneSettings, "Open microphone settings to troubleshoot"),
            RichErrorVariant.MicNotResponding),
        PillPreviewKind.DistressDeviceDisconnected => DictationStatus.Distress("Microphone disconnected."),
        PillPreviewKind.DistressRecordingInterrupted => DictationStatus.Distress("Recording interrupted."),
        PillPreviewKind.BusyFileTranscribing => DictationStatus.Busy(
            "Transcribing a file. Please wait.",
            new PillAction("View progress", PillActionKind.OpenFileTranscription, "Open Transcribe a File to watch progress")),
        PillPreviewKind.EscapeRecovery => DictationStatus.EscapeRecovery(
            "Dictation cancelled",
            new PillAction("Undo", PillActionKind.UndoEscapeRecovery, "Restore cancelled dictation")),
        PillPreviewKind.CorrectionLearned => DictationStatus.Quiet("Added to dictionary"),
        PillPreviewKind.CorrectionLearnedUndoPending => DictationStatus.Quiet("Undoing learned word"),
        PillPreviewKind.CorrectionLearnedUndone => DictationStatus.Quiet("Removed from dictionary"),
        PillPreviewKind.CorrectionLearnedSaveError => DictationStatus.Error("Couldn\u2019t save \u201CTuist\u201D"),
        PillPreviewKind.LanguageChip => DictationStatus.Suggestion(
            "Detected Spanish. Lock it?",
            new PillAction("Lock", PillActionKind.LockDetectedLanguage, "Lock Spanish for dictation")),
        PillPreviewKind.BluetoothAwarenessCard => DictationStatus.BluetoothCard(),
        PillPreviewKind.PolishSetupCard => DictationStatus.PolishSetupCard("Choose a model for Claude.", "Select a new cloud model"),
        _ => DictationStatus.Quiet("Unknown pill"),
    };

    /// <summary>Formats dwell duration description for a preview kind.</summary>
    public static string DwellDescriptionFor(PillPreviewKind kind, bool hoverPauses)
    {
        var status = StatusFor(kind);
        if (kind == PillPreviewKind.CorrectionLearned)
        {
            return "12s auto-dismiss (hover pauses)";
        }

        if (kind == PillPreviewKind.CorrectionLearnedUndoPending)
        {
            return "3s floor while undo runs";
        }

        if (kind == PillPreviewKind.CorrectionLearnedUndone)
        {
            return "1.5s auto-dismiss";
        }

        if (kind == PillPreviewKind.CorrectionLearnedSaveError)
        {
            return "3s auto-dismiss";
        }

        if (status.DwellSeconds is { } seconds)
        {
            return $"{seconds:0.#}s auto-dismiss{(hoverPauses ? " (hover pauses)" : string.Empty)}";
        }

        return status.State == DictationOverlayState.Recording
            ? "Active during recording"
            : "Active while stage runs";
    }

    private static IReadOnlyList<PillPreviewEntry> BuildEntries() =>
    [
        new(
            PillPreviewKind.RecordingCapsule,
            PillPreviewGroup.Recording,
            TitleFor(PillPreviewGroup.Recording),
            "Capsule (Hold-to-Talk)",
            "44px dark capsule with 18-bar audio-reactive Rainbow Lips, elapsed timer, and breathing hairline.",
            PreviewDesign: RecordingPillDesign.Classic),
        new(
            PillPreviewKind.RecordingCapsuleHandsFree,
            PillPreviewGroup.Recording,
            TitleFor(PillPreviewGroup.Recording),
            "Capsule (Hands-Free)",
            "Dark capsule in hands-free mode with pulsing aura around Rainbow Lips.",
            PreviewDesign: RecordingPillDesign.Classic),
        new(
            PillPreviewKind.RecordingCapsuleApproachingCap,
            PillPreviewGroup.Recording,
            TitleFor(PillPreviewGroup.Recording),
            "Capsule (Approaching Cap Notice)",
            "Capsule expanded cleanly with in-panel recording cap notice.",
            PreviewDesign: RecordingPillDesign.Classic),
        new(
            PillPreviewKind.RecordingLevelRail,
            PillPreviewGroup.Recording,
            TitleFor(PillPreviewGroup.Recording),
            "Level Rail",
            "232x44 dark capsule with monospaced timer and 24-bar scrolling level rail.",
            PreviewDesign: RecordingPillDesign.LevelRail),
        new(
            PillPreviewKind.RecordingLevelRailAutoStopUnavailable,
            PillPreviewGroup.Recording,
            TitleFor(PillPreviewGroup.Recording),
            "Level Rail (Auto-Stop Notice)",
            "Level Rail with compact in-panel notice when auto-stop cannot run.",
            PreviewDesign: RecordingPillDesign.LevelRail),
        new(
            PillPreviewKind.RecordingReadingWell,
            PillPreviewGroup.Recording,
            TitleFor(PillPreviewGroup.Recording),
            "Reading Well",
            "360px theme-aware card showing live transcription preview text above a soft top scroll fade.",
            PreviewDesign: RecordingPillDesign.ReadingWell,
            PreviewTranscript: "Draft words appear live here as you speak into your microphone."),
        new(
            PillPreviewKind.RecordingReadingWellApproachingCap,
            PillPreviewGroup.Recording,
            TitleFor(PillPreviewGroup.Recording),
            "Reading Well (Cap Notice)",
            "Reading Well displaying an in-panel notice footer beneath the live transcript.",
            PreviewDesign: RecordingPillDesign.ReadingWell,
            PreviewTranscript: "Approaching the five-minute recording limit for a single take."),

        new(
            PillPreviewKind.ProcessingTranscribing,
            PillPreviewGroup.ProcessingAndReadiness,
            TitleFor(PillPreviewGroup.ProcessingAndReadiness),
            "Processing (Transcribing)",
            "Dark capsule with rotating 12-spoke spectrum wheel and breathing rainbow hairline."),
        new(
            PillPreviewKind.ProcessingPolishing,
            PillPreviewGroup.ProcessingAndReadiness,
            TitleFor(PillPreviewGroup.ProcessingAndReadiness),
            "Processing (Polishing)",
            "Dark capsule shown while AI cleanup formats the transcript."),
        new(
            PillPreviewKind.ColdStart,
            PillPreviewGroup.ProcessingAndReadiness,
            TitleFor(PillPreviewGroup.ProcessingAndReadiness),
            "Cold Start (Warming Up)",
            "Spinning spectrum wheel with bold status line and subtle engine subtitle."),
        new(
            PillPreviewKind.EngineReady,
            PillPreviewGroup.ProcessingAndReadiness,
            TitleFor(PillPreviewGroup.ProcessingAndReadiness),
            "Engine Ready",
            "Green circular check badge with concise ready prompt."),
        new(
            PillPreviewKind.ImportStatus,
            PillPreviewGroup.ProcessingAndReadiness,
            TitleFor(PillPreviewGroup.ProcessingAndReadiness),
            "Import Status",
            "Sync arrows icon beside multi-line dictionary import status."),
        new(
            PillPreviewKind.ClipboardFallback,
            PillPreviewGroup.ProcessingAndReadiness,
            TitleFor(PillPreviewGroup.ProcessingAndReadiness),
            "Clipboard Fallback (Success)",
            "Spinning spectrum wheel displaying the exact clipboard status message rather than hardcoded text."),

        new(
            PillPreviewKind.WarningPolishFailed,
            PillPreviewGroup.WarningsErrorsAndInterruptions,
            TitleFor(PillPreviewGroup.WarningsErrorsAndInterruptions),
            "Warning (Standard)",
            "Orange warning glyph and crisp wrapping white copy inside dark capsule."),
        new(
            PillPreviewKind.AdvisoryZeroSignal,
            PillPreviewGroup.WarningsErrorsAndInterruptions,
            TitleFor(PillPreviewGroup.WarningsErrorsAndInterruptions),
            "Advisory (Zero Signal)",
            "360px dark capsule with muted mic-slash icon and multi-line wrapping guidance without clipping."),
        new(
            PillPreviewKind.ErrorAudioCapture,
            PillPreviewGroup.WarningsErrorsAndInterruptions,
            TitleFor(PillPreviewGroup.WarningsErrorsAndInterruptions),
            "Error (Standard)",
            "Red error circle badge with wrapping message inside dark capsule."),
        new(
            PillPreviewKind.ErrorPermissionDenied,
            PillPreviewGroup.WarningsErrorsAndInterruptions,
            TitleFor(PillPreviewGroup.WarningsErrorsAndInterruptions),
            "Error (Rich: Mic Permission)",
            "400px dark capsule with 4px left vertical rainbow stripe, 40x40 soft-red badge, subtitle, and Open Settings pill."),
        new(
            PillPreviewKind.ErrorMicNotResponding,
            PillPreviewGroup.WarningsErrorsAndInterruptions,
            TitleFor(PillPreviewGroup.WarningsErrorsAndInterruptions),
            "Error (Rich: Mic Not Responding)",
            "400px dark capsule with 4px left vertical rainbow stripe, 40x40 soft-red badge, subtitle, and How to fix pill."),
        new(
            PillPreviewKind.DistressDeviceDisconnected,
            PillPreviewGroup.WarningsErrorsAndInterruptions,
            TitleFor(PillPreviewGroup.WarningsErrorsAndInterruptions),
            "Interruption (Mic Disconnected)",
            "Dark capsule with pulsing red bottom hairline, pulsing red 18-bar LipsMark, and crisp orange message."),
        new(
            PillPreviewKind.DistressRecordingInterrupted,
            PillPreviewGroup.WarningsErrorsAndInterruptions,
            TitleFor(PillPreviewGroup.WarningsErrorsAndInterruptions),
            "Interruption (Recording Interrupted)",
            "Distress pulse treatment when Windows interrupts capture."),
        new(
            PillPreviewKind.BusyFileTranscribing,
            PillPreviewGroup.WarningsErrorsAndInterruptions,
            TitleFor(PillPreviewGroup.WarningsErrorsAndInterruptions),
            "Busy (File Transcribing)",
            "Dark capsule with warning badge and capsule action button when another job holds the session."),

        new(
            PillPreviewKind.EscapeRecovery,
            PillPreviewGroup.UndoAndLearning,
            TitleFor(PillPreviewGroup.UndoAndLearning),
            "Escape Recovery",
            "Theme-aware opaque pill with Undo button and animated 3s rainbow countdown rail (hover pauses and restarts).",
            HoverPauses: true),
        new(
            PillPreviewKind.CorrectionLearned,
            PillPreviewGroup.UndoAndLearning,
            TitleFor(PillPreviewGroup.UndoAndLearning),
            "Correction Learned",
            "Theme-aware learned dictionary pill with Undo, close button, and 12s countdown rail.",
            LearnedModel: new LearnedCorrectionPillModel(
                SampleLearnedPillId,
                "preview-word-1",
                "Tuist",
                LearnedCorrectionPillKind.Added,
                LearnedCorrectionPillPhase.Learned),
            HoverPauses: true),
        new(
            PillPreviewKind.CorrectionLearnedUndoPending,
            PillPreviewGroup.UndoAndLearning,
            TitleFor(PillPreviewGroup.UndoAndLearning),
            "Correction Learned (Undo Pending)",
            "UndoPending phase preserving at least 3s remaining countdown while undo completes.",
            LearnedModel: new LearnedCorrectionPillModel(
                SampleLearnedPillId,
                "preview-word-2",
                "Tuist",
                LearnedCorrectionPillKind.Added,
                LearnedCorrectionPillPhase.UndoPending)),
        new(
            PillPreviewKind.CorrectionLearnedUndone,
            PillPreviewGroup.UndoAndLearning,
            TitleFor(PillPreviewGroup.UndoAndLearning),
            "Correction Learned (Undone)",
            "Compact confirmation state after a learned word is removed.",
            LearnedModel: new LearnedCorrectionPillModel(
                SampleLearnedPillId,
                "preview-word-3",
                "Tuist",
                LearnedCorrectionPillKind.Added,
                LearnedCorrectionPillPhase.Undone)),
        new(
            PillPreviewKind.CorrectionLearnedSaveError,
            PillPreviewGroup.UndoAndLearning,
            TitleFor(PillPreviewGroup.UndoAndLearning),
            "Correction Save Error",
            "Compact dark capsule displaying a single crisp red failure line.",
            SaveErrorWord: "Tuist"),

        new(
            PillPreviewKind.LanguageChip,
            PillPreviewGroup.CardsAndChips,
            TitleFor(PillPreviewGroup.CardsAndChips),
            "Language Chip",
            "340px dark capsule with globe glyph, Lock accent button, and quiet Dismiss pill.",
            HoverPauses: true),
        new(
            PillPreviewKind.BluetoothAwarenessCard,
            PillPreviewGroup.CardsAndChips,
            TitleFor(PillPreviewGroup.CardsAndChips),
            "Bluetooth Awareness Card",
            "320px theme-aware card with close button, 3 tip rows, Got it CTA, Adjust settings link, and 2px rainbow bar.",
            PersistsUntilDismissed: true),
        new(
            PillPreviewKind.PolishSetupCard,
            PillPreviewGroup.CardsAndChips,
            TitleFor(PillPreviewGroup.CardsAndChips),
            "Polish Setup Card",
            "320px theme-aware card with bold heading, Pasted without AI polish subtitle, purple CTA, and Not now link.",
            PersistsUntilDismissed: true),
    ];
}
