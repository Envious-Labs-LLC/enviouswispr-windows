using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Input;

namespace EnviousWispr.Services.Input;

internal static partial class WindowsClipboardPaste
{
    private const ushort VkShift = 0x10;
    private const ushort VkControl = 0x11;
    private const ushort VkInsert = 0x2D;
    private const ushort VkV = 0x56;
    private const ushort VkC = 0x43;
    private const uint InputKeyboard = 1;
    private const uint KeyEventExtendedKey = 0x0001;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint GmemMoveable = 0x0002;
    private const int ClipboardOpenErrorHr = unchecked((int)0x800401D0);
    private const int ClipboardSetErrorHr = unchecked((int)0x800401D2);
    private const int ClipboardEmptyErrorHr = unchecked((int)0x800401D4);
    private const uint CfText = 1;
    private const uint CfBitmap = 2;
    private const uint CfMetaFilePict = 3;
    private const uint CfOemText = 7;
    private const uint CfDib = 8;
    private const uint CfPalette = 9;
    private const uint CfUnicodeText = 13;
    private const uint CfEnhMetaFile = 14;
    private const uint CfLocale = 16;
    private const uint CfDibV5 = 17;
    private const uint CfOwnerDisplay = 0x0080;
    private const uint CfDspText = 0x0081;
    private const uint CfDspBitmap = 0x0082;
    private const uint CfDspMetaFilePict = 0x0083;
    private const uint CfDspEnhMetaFile = 0x008E;
    private const uint CfGdiObjFirst = 0x0300;
    private const uint CfGdiObjLast = 0x03FF;
    private const uint RegisteredFormatBase = 0xC000;
    internal const string ExcludeMonitorFormatName = "ExcludeClipboardContentFromMonitorProcessing";
    internal const string CanIncludeInClipboardHistoryFormatName = "CanIncludeInClipboardHistory";
    internal const string CanUploadToCloudClipboardFormatName = "CanUploadToCloudClipboard";

    private static readonly HashSet<string> TransientMarkerFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        ExcludeMonitorFormatName,
        CanIncludeInClipboardHistoryFormatName,
        CanUploadToCloudClipboardFormatName,
    };

    internal static bool IsTransientMarkerFormat(string? formatName) =>
        !string.IsNullOrWhiteSpace(formatName) && TransientMarkerFormats.Contains(formatName.Trim());

    /// <summary>Copies because somebody asked to, and reports it as the delivery it is.</summary>
    /// <remarks>
    /// SEPARATE FROM THE FALLBACK ON PURPOSE, and the difference is the whole point. CopyOnlyAsync
    /// answers a paste that could not happen: Delivered false, ClipboardFallback true, a refusal
    /// reason. Every reader downstream treats that shape as a failure that was caught - the log
    /// writes TextDeliveryRefused, an error code is assigned, a warning notice appears, and the
    /// history entry reads "held safely". Reusing it for a deliberate copy would report a fault
    /// every single time somebody used the setting exactly as intended.
    /// </remarks>
    public static Task<TextCommitResult> CopyRequestedAsync(
        string text,
        CancellationToken cancellationToken) =>
        RunStaAsync(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return TrySetClipboardText(text)
                    ? new TextCommitResult(
                        TextDeliveryRoute.ClipboardOnly,
                        Delivered: true,
                        ClipboardFallback: false,
                        ClipboardRestored: false,
                        TextDeliveryRefusalReason.None)
                    : new TextCommitResult(
                        TextDeliveryRoute.None,
                        Delivered: false,
                        ClipboardFallback: false,
                        ClipboardRestored: false,
                        TextDeliveryRefusalReason.ClipboardUnavailable);
            },
            cancellationToken);

    public static Task<TextCommitResult> CopyOnlyAsync(
        string text,
        TextDeliveryRefusalReason refusalReason,
        CancellationToken cancellationToken) =>
        RunStaAsync(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return TrySetClipboardText(text)
                    ? new TextCommitResult(
                        TextDeliveryRoute.ClipboardOnly,
                        Delivered: false,
                        ClipboardFallback: true,
                        ClipboardRestored: false,
                        refusalReason)
                    : new TextCommitResult(
                        TextDeliveryRoute.None,
                        Delivered: false,
                        ClipboardFallback: false,
                        ClipboardRestored: false,
                        TextDeliveryRefusalReason.ClipboardUnavailable);
            },
            cancellationToken);

    /// <summary>
    /// Asks the focused app for its selection with a synthetic Copy, and puts the clipboard back.
    /// </summary>
    /// <remarks>
    /// FOR APPS THAT PUBLISH NO SELECTION - most terminals, some editors, anything drawing its own
    /// text. Quick Add otherwise tells the user to select something and try again, which they
    /// cannot act on, because they DID select something and the app simply did not say so.
    ///
    /// THE RESTORE REPLACES ONLY WHAT THIS READ PUT THERE (#247): our own clearing write, or the
    /// target app's answer to our Copy. Anything newer - the person copying, another app writing -
    /// is left alone, because restoring would destroy THEIR write to undo ours.
    ///
    /// HOW THE APP'S ANSWER IS TOLD APART FROM A NEWER WRITE. The answer is the first change after our
    /// clearing write, taken once it has held still for a moment, and only while every change in it
    /// came from the clipboard owner that made the first one: an app writing its copy owns the
    /// clipboard through the same window for every format it offers, and the person copying in
    /// another app changes the owner. A change of owner is somebody else's write: nothing is read
    /// from it and nothing is restored over it. The race accepted: a write by somebody else that
    /// lands BEFORE the app answers (in the milliseconds between our Copy and its reply) is taken for
    /// the answer and restored over, as macOS accepts (catalog: macos.quickadd.writer-identity-limit);
    /// and two writers that both own the clipboard through no window are indistinguishable.
    ///
    /// A FAILED COPY LEAVES THE CLIPBOARD RESTORED AND RETURNS NOTHING. Every exit below either
    /// restores what is ours, or never wrote, or leaves a newer write alone; a clearing write that
    /// emptied the clipboard and then failed is ours and is put back.
    ///
    /// Returns null when the selection could not be read for any reason. The caller cannot tell
    /// WHY, deliberately: every reason has the same remedy, which is to tell the user to try again,
    /// and a caller branching on the reason would be inventing distinctions it cannot act on.
    /// </remarks>
    public static Task<string?> TryReadSelectionAsync(CancellationToken cancellationToken) =>
        TryReadSelectionAsync(SetTemporaryClipboardTextOrThrow, SendCtrlC, cancellationToken);

    /// <summary>The selection read with its clipboard writer and its Copy named: production passes the real ones; a test passes a writer that fails after changing the clipboard, and a Copy answered by a stand-in app.</summary>
    /// <remarks>
    /// THE WRITER AND THE KEYSTROKE, NOT THE GUARD. What decides whether the clipboard is ours to put
    /// back - the sequence number and the owner - is read here, never passed in.
    /// </remarks>
    internal static Task<string?> TryReadSelectionAsync(
        Action<string> writeText,
        Func<bool> sendCopy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writeText);
        ArgumentNullException.ThrowIfNull(sendCopy);
        return RunStaAsync<string?>(
            () => ReadSelectionOnSta(writeText, sendCopy, cancellationToken),
            onUnexpectedFailure: static _ => null,
            cancellationToken);
    }

    /// <summary>How long the app has to start answering the Copy; the macOS fallback's own wait.</summary>
    private static readonly TimeSpan CopyAnswerDeadline = TimeSpan.FromMilliseconds(400);

    /// <summary>How long an answer must hold still before it is read; the macOS fallback's own settle.</summary>
    private static readonly TimeSpan CopyAnswerSettle = TimeSpan.FromMilliseconds(20);

    /// <summary>How often the clipboard is looked at while the app's answer is awaited.</summary>
    private static readonly TimeSpan CopyAnswerPoll = TimeSpan.FromMilliseconds(5);

    /// <summary>How long the target has to read the words after the paste keystroke, before the clipboard is given back.</summary>
    private static readonly TimeSpan PasteSettle = TimeSpan.FromMilliseconds(200);

    private static string? ReadSelectionOnSta(
        Action<string> writeText,
        Func<bool> sendCopy,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Snapshot FIRST. A snapshot that fails means we cannot promise to give the clipboard back,
        // and taking it without that promise is the failure this whole method is shaped to avoid.
        var snapshot = TrySnapshotClipboard(cancellationToken);
        if (snapshot is null)
        {
            return null;
        }

        // Emptied rather than left as it was, so a Copy that silently does nothing - a focused app
        // with no selection at all - cannot hand back whatever the user had copied earlier as if it
        // were their selection. That is the plausible-value trap: the read would succeed and return
        // something entirely unrelated. A clearing write that fails can already have emptied the
        // clipboard; when it did so by our hand it is ours, and the clipboard is put back.
        var cleared = WriteClipboardText(string.Empty, writeText);
        if (!cleared.Written)
        {
            _ = GiveBack(snapshot, cleared.OwnedSequence);
            return null;
        }

        var ourClear = cleared.OwnedSequence;
        if (!sendCopy())
        {
            _ = GiveBack(snapshot, ourClear);
            return null;
        }

        var answer = AwaitCopyAnswer(ourClear!.Value);
        switch (answer.Kind)
        {
            case CopyAnswerKind.None:
                // The app did not answer: the clipboard still holds our clearing write, or has moved
                // since, and only the first is ours to put back.
                _ = GiveBack(snapshot, ourClear);
                return null;

            case CopyAnswerKind.Unsettled:
                _ = GiveBack(snapshot, answer.Sequence);
                return null;

            case CopyAnswerKind.SomebodyElse:
                // A write by another owner: not the selection, and not ours to undo.
                return null;
        }

        var selection = TryGetClipboardText();

        // READ AND RESTORED ONLY WHILE IT IS STILL THE ANSWER. A write that lands during the read is
        // the person's: what was read may be theirs, so it is not used, and the restore declines.
        if (GetClipboardSequenceNumber() != answer.Sequence)
        {
            return null;
        }

        _ = GiveBack(snapshot, answer.Sequence);
        return string.IsNullOrWhiteSpace(selection) ? null : selection;
    }

    /// <summary>Waits for the app's answer to the Copy: the first change after our clearing write, once it holds still, while one owner made all of it.</summary>
    private static CopyAnswer AwaitCopyAnswer(uint ourClear)
    {
        var started = Stopwatch.StartNew();
        var current = GetClipboardSequenceNumber();
        while (current == ourClear)
        {
            if (started.Elapsed >= CopyAnswerDeadline)
            {
                return new CopyAnswer(CopyAnswerKind.None, 0);
            }

            PumpingWait(CopyAnswerPoll);
            current = GetClipboardSequenceNumber();
        }

        var answeringOwner = GetClipboardOwner();
        var settleDeadline = started.Elapsed + CopyAnswerDeadline;
        while (true)
        {
            PumpingWait(CopyAnswerSettle);
            var next = GetClipboardSequenceNumber();
            if (GetClipboardOwner() != answeringOwner)
            {
                return new CopyAnswer(CopyAnswerKind.SomebodyElse, next);
            }

            if (next == current)
            {
                return new CopyAnswer(CopyAnswerKind.Answered, current);
            }

            if (started.Elapsed >= settleDeadline)
            {
                // Still moving under the same owner: the app never finished, so nothing is read, and
                // its half-written answer is ours to replace while it stays the last write.
                return new CopyAnswer(CopyAnswerKind.Unsettled, next);
            }

            current = next;
        }
    }

    private enum CopyAnswerKind
    {
        None,
        Answered,
        Unsettled,
        SomebodyElse,
    }

    private readonly record struct CopyAnswer(CopyAnswerKind Kind, uint Sequence);

    /// <summary>The clipboard's plain text, read WITHOUT CHANGING THE CLIPBOARD; null when it holds none or cannot be read.</summary>
    /// <remarks>
    /// FOR A SNIPPET THAT PASTES WHAT WAS COPIED, and the opposite of every other clipboard path in this
    /// class: nothing is snapshotted, emptied, written or restored, so the sequence number does not
    /// move and a delivery that borrows the clipboard afterwards borrows exactly what the person left
    /// there. A clipboard another app is holding reads as nothing rather than failing the dictation.
    /// </remarks>
    public static Task<string?> TryReadTextAsync(CancellationToken cancellationToken) =>
        RunStaAsync<string?>(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return TryGetClipboardText();
            },
            onUnexpectedFailure: static _ => null,
            cancellationToken);

    private static string? TryGetClipboardText()
    {
        try
        {
            lock (ProcessClipboardGate)
            {
                return Clipboard.ContainsText() ? Clipboard.GetText() : null;
            }
        }
        catch (Exception exception) when (
            exception is ExternalException or ThreadStateException or ArgumentException)
        {
            return null;
        }
    }

    public static Task<TextCommitResult> PasteAsync(
        string text,
        string fallbackText,
        bool restoreClipboard,
        Func<TextDeliveryRefusalReason> preflight,
        CancellationToken cancellationToken) =>
        PasteAsync(text, fallbackText, restoreClipboard, PasteKeystrokeKind.CtrlV, preflight, cancellationToken);

    public static Task<TextCommitResult> PasteAsync(
        string text,
        string fallbackText,
        bool restoreClipboard,
        PasteKeystrokeKind keystroke,
        Func<TextDeliveryRefusalReason> preflight,
        CancellationToken cancellationToken,
        TimeSpan? settleDelay = null,
        Action? onPasteDispatched = null)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        return PasteAsync(
            text,
            fallbackText,
            restoreClipboard,
            preflight,
            SetTemporaryClipboardTextOrThrow,
            keystroke == PasteKeystrokeKind.ShiftInsert ? SendShiftInsert : SendCtrlV,
            cancellationToken,
            settleDelay,
            writeFallbackText: SetClipboardTextOrThrow,
            onPasteDispatched: onPasteDispatched);
    }

    /// <summary>The paste with its clipboard writer named: production passes <see cref="SetClipboardTextOrThrow"/>; a test passes a writer that fails the way the clipboard can, after changing it.</summary>
    /// <remarks>
    /// THE WRITER, NOT THE GUARD. What decides whether the clipboard is ours to put back - the sequence
    /// number and the owner read after the write - is not a parameter; only the write that can fail is.
    /// </remarks>
    internal static Task<TextCommitResult> PasteAsync(
        string text,
        string fallbackText,
        bool restoreClipboard,
        Func<TextDeliveryRefusalReason> preflight,
        Action<string> writeText,
        CancellationToken cancellationToken) =>
        PasteAsync(text, fallbackText, restoreClipboard, preflight, writeText, SendCtrlV, cancellationToken);

    /// <summary>The paste with its writer and its paste keystroke named: a test answers the keystroke from a stand-in, as a target would, instead of sending one to whatever window is in front.</summary>
    internal static Task<TextCommitResult> PasteAsync(
        string text,
        string fallbackText,
        bool restoreClipboard,
        Func<TextDeliveryRefusalReason> preflight,
        Action<string> writeText,
        Func<bool> sendPaste,
        CancellationToken cancellationToken,
        TimeSpan? settleDelay = null,
        Action<string>? writeFallbackText = null,
        Action? onPasteDispatched = null)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        ArgumentNullException.ThrowIfNull(writeText);
        ArgumentNullException.ThrowIfNull(sendPaste);
        var fallbackWriter = writeFallbackText ?? writeText;
        return RunStaAsync(
            () => PasteOnSta(
                text,
                fallbackText,
                restoreClipboard,
                preflight,
                writeText,
                fallbackWriter,
                sendPaste,
                settleDelay ?? PasteSettle,
                onPasteDispatched,
                cancellationToken),
            cancellationToken);
    }

    private static TextCommitResult PasteOnSta(
        string text,
        string fallbackText,
        bool restoreClipboard,
        Func<TextDeliveryRefusalReason> preflight,
        Action<string> writeText,
        Action<string> writeFallbackText,
        Func<bool> sendPaste,
        TimeSpan settleDelay,
        Action? onPasteDispatched,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ClipboardSnapshot? snapshot = null;
        if (restoreClipboard)
        {
            snapshot = TrySnapshotClipboard(cancellationToken);
            if (snapshot is null)
            {
                return new TextCommitResult(
                    TextDeliveryRoute.None,
                    Delivered: false,
                    ClipboardFallback: false,
                    ClipboardRestored: false,
                    TextDeliveryRefusalReason.ClipboardUnavailable);
            }
        }

        var insertion = WriteClipboardText(text, writeText);
        if (!insertion.Written)
        {
            // A FAILED WRITE CAN STILL HAVE CHANGED THE CLIPBOARD (#242): the write empties it before it
            // sets anything, and can fail after that. When the clipboard moved by our own hand it is
            // ours to put back; otherwise nothing of ours is on it and nothing is restored.
            var (writeRestored, writeUncertain) = GiveBack(snapshot, insertion.OwnedSequence);
            return new TextCommitResult(
                TextDeliveryRoute.None,
                Delivered: false,
                ClipboardFallback: false,
                ClipboardRestored: writeRestored,
                TextDeliveryRefusalReason.ClipboardUnavailable,
                ClipboardUncertain: writeUncertain);
        }

        var ourSequence = insertion.OwnedSequence;
        TextDeliveryRefusalReason refusal;
        try
        {
            refusal = preflight();
        }
        catch (Exception exception) when (exception is not (StackOverflowException or OutOfMemoryException))
        {
            // A DEFECT IN THE PREFLIGHT IS NOT A CLIPBOARD FAILURE (plan-2 step 13, round three). The
            // words are already on the clipboard and nothing has been pasted; the clipboard is put
            // back if it was ours to put back, and the exception goes out to be named - never
            // answered "clipboard unavailable", which would send somebody to the wrong place.
            _ = GiveBack(snapshot, ourSequence);
            throw;
        }

        if (refusal != TextDeliveryRefusalReason.None)
        {
            var fallback = WriteClipboardText(fallbackText, writeFallbackText);
            if (fallback.Written)
            {
                return new TextCommitResult(
                    TextDeliveryRoute.ClipboardOnly,
                    Delivered: false,
                    ClipboardFallback: true,
                    ClipboardRestored: false,
                    refusal);
            }

            // A REFUSED PASTE WHOSE FALLBACK COULD NOT BE WRITTEN STILL GIVES THE CLIPBOARD BACK (#242).
            // The insertion is on the clipboard, nothing was pasted, and the fallback that would have
            // justified leaving words there never landed: the person would be left holding dictated
            // text they were never told about. Put back under the same guard as every restore here -
            // only while the sequence number is still ours, so a newer write (the person copying
            // something meanwhile) is never destroyed to undo ours. A restore that fails says so.
            // "Ours" is the failed fallback's own change when it made one (it emptied the clipboard
            // and then failed), and the insertion's otherwise.
            var (restored, uncertain) = GiveBack(snapshot, fallback.OwnedSequence ?? ourSequence);
            return new TextCommitResult(
                TextDeliveryRoute.None,
                Delivered: false,
                ClipboardFallback: false,
                ClipboardRestored: restored,
                TextDeliveryRefusalReason.ClipboardUnavailable,
                ClipboardUncertain: uncertain);
        }

        if (!sendPaste())
        {
            if (writeFallbackText != writeText)
            {
                var unmarkedFallback = WriteClipboardText(fallbackText, writeFallbackText);
                if (!unmarkedFallback.Written && unmarkedFallback.OwnedSequence is { } brokenSeq)
                {
                    var (restored, uncertain) = GiveBack(snapshot, brokenSeq);
                    return new TextCommitResult(
                        TextDeliveryRoute.None,
                        Delivered: false,
                        ClipboardFallback: false,
                        ClipboardRestored: restored,
                        TextDeliveryRefusalReason.ClipboardUnavailable,
                        ClipboardUncertain: uncertain);
                }
            }

            return new TextCommitResult(
                TextDeliveryRoute.ClipboardOnly,
                Delivered: false,
                ClipboardFallback: true,
                ClipboardRestored: false,
                TextDeliveryRefusalReason.InputBlocked);
        }

        onPasteDispatched?.Invoke();

        // PUMPING, BECAUSE THIS THREAD OWNS THE CLIPBOARD HERE: the target's paste needs nothing from us (the words were
        // flushed, so no format is rendered on request), but anybody who writes to the clipboard meanwhile - the person
        // copying - sends us WM_DESTROYCLIPBOARD and waits for the answer. A sleep that does not pump holds their copy
        // until it ends, and then the give-back below can see the clipboard still ours and restore over it (#247).
        PumpingWait(settleDelay > TimeSpan.Zero ? settleDelay : TimeSpan.Zero);
        var (pastedRestored, pastedUncertain) = GiveBack(snapshot, ourSequence);
        return new TextCommitResult(
            TextDeliveryRoute.ClipboardPaste,
            Delivered: true,
            ClipboardFallback: false,
            ClipboardRestored: pastedRestored,
            ClipboardUncertain: pastedUncertain);
    }

    /// <summary>Puts the snapshot back if the clipboard still holds our write; says whether it did, and whether a restore that was ours to make failed.</summary>
    /// <remarks>
    /// THREE ANSWERS, NOT TWO. Restored: the person has their clipboard back. Declined: the sequence
    /// number moved past our write, so somebody wrote after us and their write is left alone - not
    /// restored, and not uncertain either, because what is there is theirs. Failed: the clipboard was
    /// still ours and the restore did not land, so it may hold the dictated words (or nothing) in
    /// place of what the person had - uncertain, and the caller says so. No snapshot (the caller
    /// asked for no restore) is neither, and so is no sequence of ours (nothing of ours is on it).
    /// </remarks>
    private static (bool Restored, bool Uncertain) GiveBack(ClipboardSnapshot? snapshot, uint? ourSequence)
    {
        if (snapshot is null || ourSequence is null || GetClipboardSequenceNumber() != ourSequence.Value)
        {
            return (false, false);
        }

        return RestoreWhileOurs(snapshot, ourSequence.Value);
    }

    /// <summary>Puts the snapshot back, retrying while the clipboard is held, and gives up the moment somebody else's write lands.</summary>
    /// <remarks>
    /// THE GUARD IS RE-READ BEFORE EVERY RETRY (#247). A restore that cannot open the clipboard is most often
    /// waiting on somebody who is writing to it; a retry that did not look again would put the snapshot back
    /// OVER that write - measured: the person's copy made just after Quick Add's answer was overwritten this
    /// way. So after each failed try the sequence number is read again: moved by somebody else (the owner is not
    /// this thread) is their clipboard now, left alone - declined, not uncertain; moved by our own failed try
    /// is still ours, and the next try aims at it. Out of tries with the clipboard still ours is uncertain.
    /// </remarks>
    internal static (bool Restored, bool Uncertain) RestoreWhileOurs(ClipboardSnapshot snapshot, uint ourSequence)
    {
        using var owner = snapshot.NativeEntries is { Count: > 0 }
            ? new ClipboardMessageWindow()
            : null;
        var expected = ourSequence;
        for (var remaining = ClipboardWriteAttempts; ; remaining--)
        {
            try
            {
                RestoreSnapshotOnce(snapshot, owner?.Handle ?? IntPtr.Zero);
                return (true, false);
            }
            catch (Exception exception) when (
                exception is ExternalException or ThreadStateException or ArgumentException or InvalidOperationException)
            {
                var canRetryContention = exception is ExternalException && remaining > 1;
                if (!canRetryContention)
                {
                    return IsClipboardStillOurs(expected)
                        ? (false, true)
                        : (false, false);
                }

                PumpingWait(ClipboardRetryDelay);
                var current = GetClipboardSequenceNumber();
                if (current != expected)
                {
                    if (!ClipboardOwnedByThisThread())
                    {
                        return (false, false);
                    }

                    expected = current;
                }
            }
        }
    }

    private static bool IsClipboardStillOurs(uint expectedSequence) =>
        GetClipboardSequenceNumber() == expectedSequence || ClipboardOwnedByThisThread();

    private static bool TrySetClipboardText(string text)
    {
        try
        {
            SetClipboardTextOrThrow(text);
            return true;
        }
        catch (Exception exception) when (
            exception is ExternalException or ThreadStateException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The production writer: the text on the clipboard, flushed so it outlives this thread; throws what the clipboard refuses.</summary>
    internal static void SetClipboardTextOrThrow(string text) =>
        WithPumpingRetry(() =>
        {
            lock (ProcessClipboardGate)
            {
                Clipboard.SetDataObject(text, copy: true, retryTimes: 0, retryDelay: 0);
            }
        });

    /// <summary>
    /// Atomic temporary clipboard write for paste transfers and selection probes: publishes the text
    /// together with Windows history/monitor/cloud exclusion markers in one <see cref="DataObject"/>.
    /// </summary>
    internal static void SetTemporaryClipboardTextOrThrow(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        WithPumpingRetry(() =>
        {
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, autoConvert: true, text);
            AttachTransientMarkers(data);
            lock (ProcessClipboardGate)
            {
                Clipboard.SetDataObject(data, copy: true, retryTimes: 0, retryDelay: 0);
            }
        });
    }

    /// <summary>One write of a borrowing paste, and the sequence number that is ours after it.</summary>
    /// <remarks>
    /// A FAILED WRITE IS NOT A WRITE THAT CHANGED NOTHING (#242). <c>SetDataObject</c> refuses a null
    /// before it touches anything, and otherwise runs <c>OleSetClipboard</c> then <c>OleFlushClipboard</c>,
    /// each retried. <c>OleSetClipboard</c> opens the clipboard, EMPTIES it, then offers each format and
    /// closes it: a failure to open changes nothing, but a failure to offer a format or to close comes
    /// after the empty. A flush that fails comes after a set that succeeded, so the clipboard already
    /// holds our words. Either way the write reports failure with the person's clipboard already gone.
    ///
    /// SO "OURS" IS DECIDED BY WHO CHANGED IT, NOT BY WHETHER THE CALL SUCCEEDED. After a failure the
    /// sequence number is ours only when it moved AND the clipboard's owner is this thread - the window
    /// OLE opens the clipboard with belongs to the thread doing the write. A move made by anybody else
    /// (the person copying, or another app writing while it held the clipboard we were waiting for) is
    /// theirs, and nothing of ours is restored over it.
    /// </remarks>
    private static ClipboardWrite WriteClipboardText(string text, Action<string> writeText)
    {
        var before = GetClipboardSequenceNumber();
        try
        {
            writeText(text);
            return new ClipboardWrite(Written: true, OwnedSequence: GetClipboardSequenceNumber());
        }
        catch (Exception exception) when (
            exception is ExternalException or ThreadStateException or ArgumentException)
        {
            var after = GetClipboardSequenceNumber();
            return new ClipboardWrite(
                Written: false,
                OwnedSequence: after != before && ClipboardOwnedByThisThread() ? after : null);
        }
    }

    private static bool ClipboardOwnedByThisThread()
    {
        var owner = GetClipboardOwner();
        return owner != IntPtr.Zero &&
            GetWindowThreadProcessId(owner, out _) == GetCurrentThreadId();
    }

    /// <param name="OwnedSequence">The clipboard's sequence number when what is on it is our own change; null when nothing of ours is on it.</param>
    private readonly record struct ClipboardWrite(bool Written, uint? OwnedSequence);

    // Win32 OpenClipboard(IntPtr.Zero) can succeed across concurrent threads in the same process and allow
    // CloseClipboard() on one thread to unmap HGLOBAL clipboard handles while another is inside Marshal.Copy.
    private static readonly object ProcessClipboardGate = new();

    /// <summary>Waits on the clipboard thread WITHOUT stopping it from answering the messages other writers send it (#247).</summary>
    /// <remarks>
    /// THE CLIPBOARD'S OWNER IS SENT MESSAGES, AND THE SENDER WAITS FOR THE ANSWER. Whoever writes next calls
    /// EmptyClipboard, which SendMessages WM_DESTROYCLIPBOARD to the owner's window - after any write of ours, the
    /// OLE window of this thread - and does not return until it is handled. Thread.Sleep handles nothing: measured on
    /// the real app, the target's answer to Quick Add's Copy took 1,044 ms, held behind our wait, and landed after
    /// we had given up. Joining the current thread never completes, so it waits the full time, and on an STA thread
    /// the runtime's wait is a COM modal wait (CoWaitForMultipleHandles) that dispatches sent messages and incoming
    /// COM calls while it waits - documented for Thread.Join as "continues to perform standard COM and SendMessage
    /// pumping". Every thread this class runs on is STA (<see cref="RunStaAsync{T}"/>).
    /// </remarks>
    private static void PumpingWait(TimeSpan duration) => Thread.CurrentThread.Join(duration);

    /// <summary>WinForms' own retry count and a delay close to its own, spent in a pumping wait instead of its Thread.Sleep.</summary>
    private const int ClipboardWriteAttempts = 11;

    private static readonly TimeSpan ClipboardRetryDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>One clipboard write, retried while the clipboard is held, with the waits between tries pumping.</summary>
    /// <remarks>
    /// WINFORMS' RETRY SLEEPS WITHOUT PUMPING. The clipboard is most often held by a writer that is itself waiting
    /// for this thread to answer WM_DESTROYCLIPBOARD, and a retry that does not answer can only run out. So each write
    /// is asked of WinForms once (retryTimes 0: no sleep of its own) and retried here; the last failure is thrown.
    ///
    /// A RETRY NEVER WRITES OVER A NEWER WRITE BY SOMEBODY ELSE. The pumping wait is exactly what lets the writer we
    /// were waiting on finish, so by the next try their copy may be on the clipboard; trying again would replace it.
    /// Before each retry the sequence number is read again: moved, and the owner is not this thread, means somebody
    /// else wrote - the retries stop and the failure is thrown, so the caller's own give-back and decline apply and
    /// their copy is left alone. Moved by our own failed try (the owner is this thread) is still ours to retry.
    /// </remarks>
    private static void WithPumpingRetry(Action attempt)
    {
        var expected = GetClipboardSequenceNumber();
        for (var remaining = ClipboardWriteAttempts; ; remaining--)
        {
            try
            {
                attempt();
                return;
            }
            catch (ExternalException) when (remaining > 1)
            {
                PumpingWait(ClipboardRetryDelay);
                var current = GetClipboardSequenceNumber();
                if (current != expected)
                {
                    if (!ClipboardOwnedByThisThread())
                    {
                        throw;
                    }

                    expected = current;
                }
            }
        }
    }

    /// <summary>Empties the clipboard once, as <c>Clipboard.Clear</c> does (<c>OleSetClipboard(null)</c>) but without its sleeping retry.</summary>
    private static void ClearOnce()
    {
        Application.OleRequired();
        lock (ProcessClipboardGate)
        {
            Marshal.ThrowExceptionForHR(OleSetClipboard(IntPtr.Zero));
        }
    }

    /// <summary>A delivery on the clipboard thread: what the clipboard refuses is answered inside the operation; what still throws is a defect and comes out to be named.</summary>
    /// <remarks>
    /// NOT ANSWERED "CLIPBOARD UNAVAILABLE" ANY MORE (plan-2 step 13, round three). Every clipboard
    /// call a delivery makes catches the clipboard's own failures and answers them; an exception
    /// that reaches here is ours - a defect in the preflight, a null - and the delivery names it as
    /// one, with the words kept.
    /// </remarks>
    private static Task<TextCommitResult> RunStaAsync(
        Func<TextCommitResult> operation,
        CancellationToken cancellationToken) =>
        RunStaAsync<TextCommitResult>(operation, onUnexpectedFailure: null, cancellationToken);

    /// <summary>
    /// Runs one clipboard operation on a thread that can talk to the clipboard at all.
    /// </summary>
    /// <param name="onUnexpectedFailure">
    /// What to answer when the operation throws something we did not anticipate, or null to let the
    /// exception out. Passed in rather than defaulted, so each caller states its OWN answer: a
    /// selection read answers nothing; a delivery lets the defect out to be named.
    /// </param>
    private static Task<T> RunStaAsync<T>(
        Func<T> operation,
        Func<Exception, T>? onUnexpectedFailure,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(operation());
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception exception) when (
                exception is not (StackOverflowException or OutOfMemoryException))
            {
                if (onUnexpectedFailure is null)
                {
                    completion.TrySetException(exception);
                }
                else
                {
                    completion.TrySetResult(onUnexpectedFailure(exception));
                }
            }
        })
        {
            IsBackground = true,
            Name = "EnviousWispr clipboard delivery",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
