using System.Security;
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
