using System.Security;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Presentation;

/// <summary>Why a settings change was not kept, in the terms the window answers in.</summary>
/// <remarks>
/// THREE CAUSES, THREE ANSWERS, AND FOLDING THEM COST THE MOST USEFUL ONE. The store reports invalid
/// settings as an ArgumentException; collapsing that into "storage is unavailable" tells somebody
/// their disk is broken when a value they typed is out of range, which sends them looking in exactly
/// the wrong place. And THE APP CLOSING IS NOT A STORAGE FAILURE: a click that lands as the window
/// is going away is refused on purpose, and telling somebody their settings storage broke as they
/// quit is both alarming and untrue - so that case has a name of its own, and the window says nothing.
/// </remarks>
public enum SettingsSaveRefusal
{
    /// <summary>The window is closing and the writer has stopped taking changes. Not an error.</summary>
    Closing,

    /// <summary>One or more values are invalid; the previous settings remain active.</summary>
    InvalidValues,

    /// <summary>Windows refused the write; the previous settings remain active.</summary>
    StorageBlocked,

    /// <summary>The store could not be written for some other reason; the previous settings remain active.</summary>
    StorageUnavailable,
}

/// <summary>How a settings change went.</summary>
/// <param name="Refusal">Why it was not kept, or null when it was.</param>
/// <param name="Failure">The exception behind a refusal, for the log.</param>
public readonly record struct SettingsSaveResult(SettingsSaveRefusal? Refusal, Exception? Failure)
{
    public bool Saved => Refusal is null;

    public static SettingsSaveResult Kept => new(null, null);
}

/// <summary>How a settings change went, with what it worked out while it held the gate.</summary>
public readonly record struct SettingsSaveResult<T>(SettingsSaveRefusal? Refusal, Exception? Failure, T Value)
{
    public bool Saved => Refusal is null;
}

/// <summary>The Appearance page's choices, read off the controls on the UI thread before any wait.</summary>
/// <remarks>
/// A SNAPSHOT, NOT A CLOSURE OVER THE CONTROLS. The change is applied inside the writer's gate, after
/// a wait of unknown length on another save; a function that read the radio buttons at that moment
/// would read them off the wrong thread. The three values are captured now and applied later.
/// </remarks>
public readonly record struct AppearanceChoices(
    AppTheme Theme,
    OverlayPillPosition OverlayPosition,
    RecordingPillDesign PillDesignWithoutWords);

/// <summary>
/// The General page as its Save button reads it, on the UI thread, before any wait: every control's
/// value, raw - text as typed, choices as their index, numbers as the field holds them (NaN when
/// empty). What each means is the presenter's to decide.
/// </summary>
/// <remarks>
/// VALUES, NOT A TRANSFORMATION. The window used to read its controls and build the whole
/// preferences record itself - the parse of three shortcuts, the clash check between them, the
/// clamps that turn an index into an engine or a language, the defaults an empty field means, the
/// retention bounds, whether telemetry may be shared at all, and which fields a Save replaces. None
/// of that needs a window; all of it needed proving, and a WinUI handler cannot be run under a test.
/// The window reads the controls into this and gets back what to do; the meaning lives with the
/// presenter, where SettingsPresenterTests can reach it.
/// </remarks>
public sealed record GeneralSettingsInput(
    string RecordingShortcut,
    string CancelShortcut,
    string QuickAddShortcut,
    int FinalEngineIndex,
    bool WordCorrection,
    bool FillerRemoval,
    bool EmojiFormatter,
    bool SpokenPunctuation,
    int WhisperLanguageIndex,
    int RecordingModeIndex,
    bool EscapeRecovery,
    bool AutoStop,
    double AutoStopSeconds,
    int PolishProviderIndex,
    string PolishModel,
    string OllamaEndpoint,
    bool HistoryEnabled,
    double HistoryRetentionDays,
    int ThemeIndex,
    bool LivePreview,
    int OverlayPositionIndex,
    bool LevelRailPill,
    bool PlayRecordingSounds,
    RecordingSoundPairing RecordingSoundPairing,
    bool CopyInsteadOfPaste,
    bool LocalDiagnostics,
    double DiagnosticRetentionDays,
    bool ShareTelemetry,
    bool TelemetryAvailable,
    string? MicrophoneId);

/// <summary>Which shortcut field a General save found wanting, for the window to put the focus in.</summary>
public enum GeneralShortcutField
{
    None,
    Recording,
    Cancel,
    QuickAdd,
}

/// <summary>How a General save went.</summary>
public enum GeneralSaveStatus
{
    /// <summary>Every value was stored; the theme returned is the one now in force.</summary>
    Saved,

    /// <summary>A shortcut could not be read; the field named needs attention and nothing was stored.</summary>
    InvalidShortcut,

    /// <summary>Two or more shortcuts are the same gesture; nothing was stored.</summary>
    OverlappingShortcuts,

    /// <summary>The values were sound and the store refused or failed; the previous settings remain active.</summary>
    NotSaved,
}

/// <summary>What a General save established, in the terms the window answers in.</summary>
/// <param name="Status">Saved, or why not.</param>
/// <param name="InvalidField">The shortcut field to focus, when a shortcut could not be read.</param>
/// <param name="Overlap">The clashes, described, when shortcuts overlap.</param>
/// <param name="Theme">The theme to apply now, when saved.</param>
/// <param name="Save">The store's answer, when the values were sound.</param>
public sealed record GeneralSaveOutcome(
    GeneralSaveStatus Status,
    GeneralShortcutField InvalidField = GeneralShortcutField.None,
    string? Overlap = null,
    AppTheme? Theme = null,
    SettingsSaveResult? Save = null)
{
    public bool Saved => Status == GeneralSaveStatus.Saved;

    /// <summary>What the Save button tells the person: which choices apply now and which on the next launch.</summary>
    public const string SavedTitle = "Settings saved";

    public const string SavedMessage =
        "Theme, Live Preview, pill design, pill position, recording sounds, and local data choices apply now. "
        + "Engine, microphone, shortcut, and polish changes apply safely on the next launch.";

    public const string InvalidShortcutTitle = "Shortcut needs attention";

    public const string InvalidShortcutMessage = "Use supported keys such as F8, Escape, or Ctrl+Alt+W.";

    public const string OverlappingShortcutsTitle = "Shortcuts overlap";
}

/// <summary>The decisions the settings window makes about writing, without the window.</summary>
/// <remarks>
/// ONE AT A TIME, AND EACH CHANGE DERIVED FROM WHAT IS ACTUALLY STORED. Every writer in the window
/// built its record first and saved second, so two of them overlapping wrote two different
/// whole-settings snapshots and whichever finished last won - silently discarding the other person's
/// change. The fix is to derive inside the gate, not to pass a record across it, and the gate is
/// <see cref="SerialSettingsWriter"/>, which this owns for the life of the window.
///
/// THIS IS THE HEADLESS HALF. What the window keeps is reading its controls, drawing the outcome and
/// applying a theme; what moves here is the transaction, the classification of a failure into the
/// answer the window gives, and the rule that an Appearance click writes three fields and no more.
/// Two windows would each have one of these; nothing here is shared between instances.
/// </remarks>
public sealed class SettingsPresenter : IDisposable
{
    private readonly SerialSettingsWriter _writer;

    public SettingsPresenter(ISettingsStore store, AppSettings current)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(current);
        _writer = new SerialSettingsWriter(store, current);
    }

    /// <summary>The settings as last successfully written.</summary>
    public AppSettings Current => _writer.Current;

    /// <summary>Applies a change to the stored settings and says how it went.</summary>
    /// <remarks>
    /// A TRANSFORM, NOT A RECORD. A record built before the gate is stale by the time the gate opens,
    /// and writing it back discards whatever ran in between - a profile import preserves
    /// machine-local choices and app state, so even the "replace everything" callers are partial
    /// changes like every other.
    /// </remarks>
    public async Task<SettingsSaveResult> SaveAsync(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var failure = await _writer.UpdateAsync(change).ConfigureAwait(false);
        return failure is null ? SettingsSaveResult.Kept : new SettingsSaveResult(Classify(failure), failure);
    }

    /// <summary>Applies a change that also has something to say about what it did.</summary>
    /// <remarks>
    /// AN IMPORT DECIDES WHAT TO ADD BY LOOKING AT WHAT IS ALREADY THERE, so that decision is a
    /// question about the CURRENT words and has to be answered inside the gate. Answered outside, the
    /// plan describes a list that may have changed - and saving its result then overwrites whatever
    /// changed it. The value comes back so the message describes what was actually stored.
    /// </remarks>
    public async Task<SettingsSaveResult<T>> SaveAsync<T>(Func<AppSettings, (AppSettings Settings, T Value)> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var outcome = await _writer.UpdateAsync(change).ConfigureAwait(false);
        return outcome.Failure is null
            ? new SettingsSaveResult<T>(null, null, outcome.Value)
            : new SettingsSaveResult<T>(Classify(outcome.Failure), outcome.Failure, outcome.Value);
    }

    /// <summary>Writes the Appearance choices, and nothing else.</summary>
    /// <remarks>
    /// ONLY THESE THREE FIELDS, DELIBERATELY. The Save button builds a whole settings object out of
    /// every control in the window, which is right for a button the user pressed and wrong for a side
    /// effect of clicking a theme card: it would commit half-finished edits sitting on other pages
    /// that the user has not chosen to save yet. Derived inside the gate, so a click that overlaps
    /// another writer builds on what is actually stored rather than on a snapshot taken before the
    /// wait - and a change to any other field that landed meanwhile survives.
    ///
    /// The pill's look joined the Appearance page and had to join this write: Appearance is the one
    /// settings page with no Save button, so a card that only the Save button reads is a card that
    /// does nothing.
    /// </remarks>
    public Task<SettingsSaveResult> SaveAppearanceAsync(AppearanceChoices choices) =>
        SaveAsync(current => current with
        {
            Preferences = current.Preferences with
            {
                Theme = choices.Theme,
                OverlayPosition = choices.OverlayPosition,
                PillDesignWithoutWords = choices.PillDesignWithoutWords,
            },
        });

    /// <summary>The General page's Save: every value the page holds, checked, normalised and stored as one change.</summary>
    /// <remarks>
    /// THE REPLACEMENT SCOPE IS EXACTLY THREE FIELDS OF THE STORED SETTINGS: the preferred microphone,
    /// the preferences, and the observability choices. Everything else - the vocabulary and the rest of
    /// the reusable data, the launch count, onboarding, the release notes seen, the language offers - is
    /// whatever is stored when the gate opens, so a save that waited behind an import or a pinned
    /// language keeps what they wrote. Within the preferences, the pill design with words is always
    /// Reading Well: the page offers no other, and a save must not lose the one it has.
    ///
    /// AN EMPTY NUMBER MEANS THE DEFAULT, NOT ZERO. The auto-stop threshold and the two retentions come
    /// off number boxes that read NaN when cleared; a zero stored there would be clamped up by the
    /// policy anyway, but would show a threshold nobody chose. Telemetry is shared only when the build
    /// can share it at all, whatever the toggle says.
    ///
    /// THE SHORTCUTS ARE CHECKED BEFORE ANYTHING IS STORED: a shortcut that cannot be read names its
    /// field and stores nothing; two that are the same gesture are described and store nothing - the
    /// same detector the page's live warning asks, so the warning and the refusal never disagree.
    /// </remarks>
    public async Task<GeneralSaveOutcome> SaveGeneralAsync(GeneralSettingsInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var recording = HotkeyGestureParser.Parse(input.RecordingShortcut);
        var cancel = HotkeyGestureParser.Parse(input.CancelShortcut);
        var quickAdd = HotkeyGestureParser.Parse(input.QuickAddShortcut);
        if (!recording.Succeeded || !cancel.Succeeded || !quickAdd.Succeeded)
        {
            return new GeneralSaveOutcome(
                GeneralSaveStatus.InvalidShortcut,
                InvalidField: !recording.Succeeded
                    ? GeneralShortcutField.Recording
                    : !cancel.Succeeded
                        ? GeneralShortcutField.Cancel
                        : GeneralShortcutField.QuickAdd);
        }

        var clashes = HotkeyConflictDetector.Find(ShortcutRoles(input));
        if (clashes.Count > 0)
        {
            return new GeneralSaveOutcome(GeneralSaveStatus.OverlappingShortcuts, Overlap: HotkeyConflictDetector.Describe(clashes));
        }

        var dictation = new DictationPreferences(
            (FinalAsrEngine)Math.Clamp(input.FinalEngineIndex, 0, 2),
            recording.Gesture!.Value.ToString(),
            input.WordCorrection,
            input.FillerRemoval,
            input.EmojiFormatter,
            input.SpokenPunctuation,
            (WhisperLanguagePreference)Math.Clamp(input.WhisperLanguageIndex, 0, 4),
            (DictationRecordingMode)Math.Clamp(input.RecordingModeIndex, 0, 1),
            cancel.Gesture!.Value.ToString(),
            input.EscapeRecovery,
            quickAdd.Gesture!.Value.ToString(),
            input.AutoStop,
            double.IsNaN(input.AutoStopSeconds) ? DictationPreferences.Default.AutoStopSilenceSeconds : input.AutoStopSeconds);
        var polish = new PolishPreferences(
            PolishProviderFromIndex(input.PolishProviderIndex),
            NullIfBlank(input.PolishModel),
            NullIfBlank(input.OllamaEndpoint));
        var history = new HistoryPreferences(
            input.HistoryEnabled,
            RetentionDays.FromField(input.HistoryRetentionDays, fallback: 30, RetentionDays.HistoryMinimum, RetentionDays.HistoryMaximum));
        var theme = ThemeFromIndex(input.ThemeIndex);
        var observability = new ObservabilityPreferences(
            input.LocalDiagnostics,
            RetentionDays.FromField(
                input.DiagnosticRetentionDays,
                ObservabilityPreferences.Default.DiagnosticRetentionDays,
                RetentionDays.DiagnosticMinimum,
                RetentionDays.DiagnosticMaximum),
            input.TelemetryAvailable && input.ShareTelemetry);
        var preferences = new UserPreferences(
            dictation,
            polish,
            history,
            theme,
            input.LivePreview,
            OverlayPositionFromIndex(input.OverlayPositionIndex),
            input.LevelRailPill ? RecordingPillDesign.LevelRail : RecordingPillDesign.Classic,
            RecordingPillDesign.ReadingWell,
            input.PlayRecordingSounds,
            input.RecordingSoundPairing,
            input.CopyInsteadOfPaste);

        // THE VALUES WERE READ ON THE UI THREAD AND ARE APPLIED INSIDE THE GATE: the record above is
        // built now, from what was read; the transform below touches the three fields it replaces and
        // nothing else, so whatever else moved while this waited is kept.
        var save = await SaveAsync(current => current with
        {
            PreferredMicrophoneId = input.MicrophoneId,
            Preferences = preferences,
            Observability = observability,
        }).ConfigureAwait(false);
        return save.Saved
            ? new GeneralSaveOutcome(GeneralSaveStatus.Saved, Theme: theme, Save: save)
            : new GeneralSaveOutcome(GeneralSaveStatus.NotSaved, Save: save);
    }

    /// <summary>The three shortcut fields as the conflict detector reads them; the page's live warning asks with the same roles.</summary>
    public static (string Role, string Text)[] ShortcutRoles(GeneralSettingsInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return ShortcutRoles(input.RecordingShortcut, input.CancelShortcut, input.QuickAddShortcut);
    }

    public static (string Role, string Text)[] ShortcutRoles(string recording, string cancel, string quickAdd) =>
    [
        ("Recording", recording),
        ("Cancel", cancel),
        ("Add-a-word", quickAdd),
    ];

    /// <summary>The theme a choice index means; the window applies a theme the moment it is chosen and needs the same map.</summary>
    public static AppTheme ThemeFromIndex(int index) => index switch
    {
        1 => AppTheme.Light,
        2 => AppTheme.Dark,
        _ => AppTheme.System,
    };

    public static int ThemeIndex(AppTheme theme) => theme switch
    {
        AppTheme.Light => 1,
        AppTheme.Dark => 2,
        _ => 0,
    };

    public static OverlayPillPosition OverlayPositionFromIndex(int index) =>
        index == 1 ? OverlayPillPosition.Bottom : OverlayPillPosition.Top;

    public static int OverlayPositionIndex(OverlayPillPosition position) =>
        position == OverlayPillPosition.Bottom ? 1 : 0;

    public static PolishProvider PolishProviderFromIndex(int index) => index switch
    {
        1 => PolishProvider.EgOne,
        2 => PolishProvider.Ollama,
        3 => PolishProvider.OpenAI,
        4 => PolishProvider.Anthropic,
        5 => PolishProvider.Gemini,
        _ => PolishProvider.None,
    };

    public static int PolishProviderIndex(PolishProvider provider) => provider switch
    {
        PolishProvider.EgOne => 1,
        PolishProvider.Ollama => 2,
        PolishProvider.OpenAI => 3,
        PolishProvider.Anthropic => 4,
        PolishProvider.Gemini => 5,
        _ => 0,
    };

    private static string? NullIfBlank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>Waits for any settings write to finish, then stops accepting new ones.</summary>
    /// <remarks>
    /// AWAITED AT EXIT, BECAUSE ABANDONING THE WRITER LETS THE PROCESS END MID-WRITE. Synchronous
    /// teardown cannot wait, so it does not try; this is the asynchronous half that can. A change
    /// that arrives afterwards is refused as <see cref="SettingsSaveRefusal.Closing"/>.
    /// </remarks>
    public Task DrainAsync() => _writer.DrainAsync();

    /// <summary>Closes the writer without waiting. Prefer <see cref="DrainAsync"/>; the window's teardown is synchronous and cannot.</summary>
    public void Dispose() => _writer.Dispose();

    /// <summary>The answer the window gives for a failure the writer reported.</summary>
    public static SettingsSaveRefusal Classify(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return failure switch
        {
            ObjectDisposedException => SettingsSaveRefusal.Closing,
            ArgumentException => SettingsSaveRefusal.InvalidValues,
            UnauthorizedAccessException or SecurityException => SettingsSaveRefusal.StorageBlocked,
            _ => SettingsSaveRefusal.StorageUnavailable,
        };
    }
}
