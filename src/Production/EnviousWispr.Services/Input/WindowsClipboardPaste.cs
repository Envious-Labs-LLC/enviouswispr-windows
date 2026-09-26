using EnviousWispr.Core.Dictation;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace EnviousWispr.Services.Input;

internal static class WindowsClipboardPaste
{
    private const ushort VkControl = 0x11;
    private const ushort VkV = 0x56;
    private const ushort VkC = 0x43;
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;

    internal static int NativeInputSize => Marshal.SizeOf<NativeInput>();
    internal static int NativeKeyboardInputSize => Marshal.SizeOf<NativeKeyboardInput>();
    internal static int NativeKeyboardFlagsOffset =>
        Marshal.OffsetOf<NativeKeyboardInput>(nameof(NativeKeyboardInput.Flags)).ToInt32();

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
        TryReadSelectionAsync(SetClipboardTextOrThrow, SendCtrlC, cancellationToken);

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
        var snapshot = TrySnapshotClipboard();
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

    private static bool SendCtrlC()
    {
        var inputs = new[]
        {
            MakeInput(VkControl, keyUp: false),
            MakeInput(VkC, keyUp: false),
            MakeInput(VkC, keyUp: true),
            MakeInput(VkControl, keyUp: true),
        };
        return SendInput(
            checked((uint)inputs.Length),
            inputs,
            NativeInputSize) == inputs.Length;
    }

    private static string? TryGetClipboardText()
    {
        try
        {
            return Clipboard.ContainsText() ? Clipboard.GetText() : null;
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
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        return PasteAsync(text, fallbackText, restoreClipboard, preflight, SetClipboardTextOrThrow, cancellationToken);
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
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        ArgumentNullException.ThrowIfNull(writeText);
        ArgumentNullException.ThrowIfNull(sendPaste);
        return RunStaAsync(
            () => PasteOnSta(
                text,
                fallbackText,
                restoreClipboard,
                preflight,
                writeText,
                sendPaste,
                cancellationToken),
            cancellationToken);
    }

    private static TextCommitResult PasteOnSta(
        string text,
        string fallbackText,
        bool restoreClipboard,
        Func<TextDeliveryRefusalReason> preflight,
        Action<string> writeText,
        Func<bool> sendPaste,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ClipboardSnapshot? snapshot = null;
        if (restoreClipboard)
        {
            snapshot = TrySnapshotClipboard();
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
            var fallback = WriteClipboardText(fallbackText, writeText);
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
            return new TextCommitResult(
                TextDeliveryRoute.ClipboardOnly,
                Delivered: false,
                ClipboardFallback: true,
                ClipboardRestored: false,
                TextDeliveryRefusalReason.InputBlocked);
        }

        // PUMPING, BECAUSE THIS THREAD OWNS THE CLIPBOARD HERE: the target's paste needs nothing from us (the words were
        // flushed, so no format is rendered on request), but anybody who writes to the clipboard meanwhile - the person
        // copying - sends us WM_DESTROYCLIPBOARD and waits for the answer. A sleep that does not pump holds their copy
        // until it ends, and then the give-back below can see the clipboard still ours and restore over it (#247).
        PumpingWait(PasteSettle);
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
    private static (bool Restored, bool Uncertain) RestoreWhileOurs(ClipboardSnapshot snapshot, uint ourSequence)
    {
        var expected = ourSequence;
        for (var remaining = ClipboardWriteAttempts; ; remaining--)
        {
            try
            {
                if (snapshot.IsEmpty)
                {
                    ClearOnce();
                }
                else
                {
                    Clipboard.SetDataObject(snapshot.Data!, copy: true, retryTimes: 0, retryDelay: 0);
                }

                return (true, false);
            }
            catch (Exception exception) when (
                exception is ExternalException or ThreadStateException or ArgumentException)
            {
                if (exception is not ExternalException || remaining <= 1)
                {
                    return (false, true);
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

    private static bool SendCtrlV()
    {
        var inputs = new[]
        {
            MakeInput(VkControl, keyUp: false),
            MakeInput(VkV, keyUp: false),
            MakeInput(VkV, keyUp: true),
            MakeInput(VkControl, keyUp: true),
        };
        return SendInput(
            checked((uint)inputs.Length),
            inputs,
            NativeInputSize) == inputs.Length;
    }

    private static NativeInput MakeInput(ushort virtualKey, bool keyUp) => new()
    {
        Type = InputKeyboard,
        Union = new NativeInputUnion
        {
            Keyboard = new NativeKeyboardInput
            {
                VirtualKey = virtualKey,
                Flags = keyUp ? KeyEventKeyUp : 0,
                // THE APP'S OWN KEYSTROKE, SO ITS OWN HOOK STEPS ASIDE. With a last-dictation shortcut bound to
                // Ctrl+V or Ctrl+C, the hook would otherwise swallow the very paste or copy this sends. Ref: #206.
                ExtraInfo = MenuKeyMask.Tag,
            },
        },
    };

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
        WithPumpingRetry(() => Clipboard.SetDataObject(text, copy: true, retryTimes: 0, retryDelay: 0));

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

    /// <summary>Every format on the clipboard, copied; null when any format cannot be copied, in which case nothing that borrows the clipboard may proceed.</summary>
    internal static ClipboardSnapshot? TrySnapshotClipboard()
    {
        try
        {
            var source = Clipboard.GetDataObject();
            if (source is null)
            {
                return new ClipboardSnapshot(IsEmpty: true, Data: null);
            }

            var copy = new DataObject();
            foreach (var format in source.GetFormats(autoConvert: false))
            {
                var value = source.GetData(format, autoConvert: false);
                if (value is null)
                {
                    return null;
                }

                var cloned = CloneClipboardValue(value);
                if (cloned is null)
                {
                    return null;
                }

                copy.SetData(format, autoConvert: false, cloned);
            }

            return new ClipboardSnapshot(IsEmpty: false, copy);
        }
        catch (Exception exception) when (
            exception is ExternalException or ThreadStateException or
                InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The whole of a seekable stream, from its start, with its position put back; null for a stream whose whole contents cannot be read without consuming it, or that fails to read.</summary>
    /// <remarks>
    /// A SNAPSHOT IS THE WHOLE VALUE OR NOTHING. A stream copied from where it happened to stand
    /// lost its prefix, and a stream that cannot seek is consumed by the copy - the clipboard would
    /// then hold less than it did. Both answer null, and a null refuses the snapshot, so nothing that
    /// borrows the clipboard proceeds against a clipboard it could not put back whole.
    /// </remarks>
    internal static MemoryStream? CloneStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            return null;
        }

        var originalPosition = stream.Position;
        MemoryStream? copy = null;
        try
        {
            stream.Position = 0;
            copy = new MemoryStream();
            stream.CopyTo(copy);
            copy.Position = 0;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or ObjectDisposedException or UnauthorizedAccessException)
        {
            copy = null;
        }

        // THE COPY COUNTS ONLY WITH THE SOURCE PUT BACK. A copy that read everything and then could
        // not return the stream to where it stood has changed the clipboard's value; that is a
        // refusal too, not a success with a note.
        try
        {
            stream.Position = originalPosition;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or ObjectDisposedException)
        {
            copy = null;
        }

        return copy;
    }

    internal static object? CloneClipboardValue(object value)
    {
        switch (value)
        {
            case byte[] bytes:
                return bytes.ToArray();
            case MemoryStream memory:
                return new MemoryStream(memory.ToArray(), writable: false);
            case Stream stream:
                return CloneStream(stream);
            case Bitmap bitmap:
                return bitmap.Clone();
            case StringCollection strings:
            {
                var clone = new StringCollection();
                clone.AddRange(strings.Cast<string>().ToArray());
                return clone;
            }
            case ICloneable cloneable:
                return cloneable.Clone();
            case string or char or bool or byte or sbyte or short or ushort or int or uint or
                long or ulong or float or double or decimal or DateTime or DateTimeOffset or
                TimeSpan or Guid:
                return value;
            default:
                return null;
        }
    }

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
    /// </remarks>
    private static void WithPumpingRetry(Action attempt)
    {
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
            }
        }
    }

    /// <summary>Empties the clipboard once, as <c>Clipboard.Clear</c> does (<c>OleSetClipboard(null)</c>) but without its sleeping retry.</summary>
    private static void ClearOnce()
    {
        Application.OleRequired();
        Marshal.ThrowExceptionForHR(OleSetClipboard(IntPtr.Zero));
    }

    /// <summary>Puts a snapshot back with no guard at all: for the tests' own desk-clipboard guard, which restores what it took whatever happened since. Every restore the app makes goes through the guarded give-back instead.</summary>
    internal static bool TryRestoreClipboard(ClipboardSnapshot snapshot)
    {
        try
        {
            if (snapshot.IsEmpty)
            {
                WithPumpingRetry(ClearOnce);
            }
            else
            {
                WithPumpingRetry(() => Clipboard.SetDataObject(snapshot.Data!, copy: true, retryTimes: 0, retryDelay: 0));
            }

            return true;
        }
        catch (Exception exception) when (
            exception is ExternalException or ThreadStateException or ArgumentException)
        {
            return false;
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

    internal sealed record ClipboardSnapshot(bool IsEmpty, DataObject? Data);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint Type;
        public NativeInputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct NativeInputUnion
    {
        [FieldOffset(0)]
        public NativeKeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeKeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(
        uint inputCount,
        NativeInput[] inputs,
        int inputSize);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardOwner();

    [DllImport("ole32.dll")]
    private static extern int OleSetClipboard(IntPtr dataObject);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
