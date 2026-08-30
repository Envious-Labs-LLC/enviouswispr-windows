using EnviousWispr.Core.Dictation;
using System.Collections.Specialized;
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
    /// THE RESTORE IS GUARDED THE SAME WAY THE PASTE PATH GUARDS ITS OWN. The clipboard is only put
    /// back if the sequence number still matches what our Copy produced. If something else wrote to
    /// the clipboard in between - another app, the user, a paste - restoring would destroy THEIR
    /// write to undo ours, which is worse than leaving the borrowed content in place.
    ///
    /// A FAILED COPY LEAVES THE CLIPBOARD RESTORED AND RETURNS NOTHING. Every exit below either
    /// restores or never wrote, so there is no path where the user is left holding the selection we
    /// took and no word to show for it.
    ///
    /// Returns null when the selection could not be read for any reason. The caller cannot tell
    /// WHY, deliberately: every reason has the same remedy, which is to tell the user to try again,
    /// and a caller branching on the reason would be inventing distinctions it cannot act on.
    /// </remarks>
    public static Task<string?> TryReadSelectionAsync(CancellationToken cancellationToken) =>
        RunStaAsync<string?>(
            () => ReadSelectionOnSta(cancellationToken),
            onUnexpectedFailure: null,
            cancellationToken);

    private static string? ReadSelectionOnSta(CancellationToken cancellationToken)
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
        // something entirely unrelated.
        if (!TrySetClipboardText(string.Empty))
        {
            return null;
        }

        var beforeCopy = GetClipboardSequenceNumber();
        if (!SendCtrlC())
        {
            TryRestoreClipboard(snapshot);
            return null;
        }

        // The same settle the paste path uses. The Copy is asynchronous from our side: the app has
        // to receive the keystroke, act on it, and write to the clipboard.
        Thread.Sleep(200);

        var selection = GetClipboardSequenceNumber() != beforeCopy
            ? TryGetClipboardText()
            : null;

        TryRestoreClipboard(snapshot);
        return string.IsNullOrWhiteSpace(selection) ? null : selection;
    }

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
        string legacyText,
        bool restoreClipboard,
        Func<TextDeliveryRefusalReason> preflight,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        return RunStaAsync(
            () => PasteOnSta(
                text,
                legacyText,
                restoreClipboard,
                preflight,
                cancellationToken),
            cancellationToken);
    }

    private static TextCommitResult PasteOnSta(
        string text,
        string legacyText,
        bool restoreClipboard,
        Func<TextDeliveryRefusalReason> preflight,
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

        if (!TrySetClipboardText(text))
        {
            return new TextCommitResult(
                TextDeliveryRoute.None,
                Delivered: false,
                ClipboardFallback: false,
                ClipboardRestored: false,
                TextDeliveryRefusalReason.ClipboardUnavailable);
        }

        var ourSequence = GetClipboardSequenceNumber();
        var refusal = preflight();
        if (refusal != TextDeliveryRefusalReason.None)
        {
            var fallbackAvailable = TrySetClipboardText(legacyText);
            return new TextCommitResult(
                fallbackAvailable
                    ? TextDeliveryRoute.ClipboardOnly
                    : TextDeliveryRoute.None,
                Delivered: false,
                ClipboardFallback: fallbackAvailable,
                ClipboardRestored: false,
                fallbackAvailable
                    ? refusal
                    : TextDeliveryRefusalReason.ClipboardUnavailable);
        }

        if (!SendCtrlV())
        {
            return new TextCommitResult(
                TextDeliveryRoute.ClipboardOnly,
                Delivered: false,
                ClipboardFallback: true,
                ClipboardRestored: false,
                TextDeliveryRefusalReason.InputBlocked);
        }

        Thread.Sleep(200);
        var restored = snapshot is not null &&
            GetClipboardSequenceNumber() == ourSequence &&
            TryRestoreClipboard(snapshot);
        return new TextCommitResult(
            TextDeliveryRoute.ClipboardPaste,
            Delivered: true,
            ClipboardFallback: false,
            ClipboardRestored: restored);
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
            },
        },
    };

    private static bool TrySetClipboardText(string text)
    {
        try
        {
            Clipboard.SetDataObject(text, copy: true, retryTimes: 10, retryDelay: 50);
            return true;
        }
        catch (Exception exception) when (
            exception is ExternalException or ThreadStateException or ArgumentException)
        {
            return false;
        }
    }

    private static ClipboardSnapshot? TrySnapshotClipboard()
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

    internal static object? CloneClipboardValue(object value)
    {
        switch (value)
        {
            case byte[] bytes:
                return bytes.ToArray();
            case MemoryStream memory:
                return new MemoryStream(memory.ToArray(), writable: false);
            case Stream stream:
            {
                var originalPosition = stream.CanSeek ? stream.Position : 0;
                var copy = new MemoryStream();
                stream.CopyTo(copy);
                if (stream.CanSeek)
                {
                    stream.Position = originalPosition;
                }

                copy.Position = 0;
                return copy;
            }
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

    private static bool TryRestoreClipboard(ClipboardSnapshot snapshot)
    {
        try
        {
            if (snapshot.IsEmpty)
            {
                Clipboard.Clear();
            }
            else
            {
                Clipboard.SetDataObject(
                    snapshot.Data!,
                    copy: true,
                    retryTimes: 10,
                    retryDelay: 50);
            }

            return true;
        }
        catch (Exception exception) when (
            exception is ExternalException or ThreadStateException or ArgumentException)
        {
            return false;
        }
    }

    private static Task<TextCommitResult> RunStaAsync(
        Func<TextCommitResult> operation,
        CancellationToken cancellationToken) =>
        RunStaAsync(
            operation,
            new TextCommitResult(
                TextDeliveryRoute.None,
                Delivered: false,
                ClipboardFallback: false,
                ClipboardRestored: false,
                TextDeliveryRefusalReason.ClipboardUnavailable),
            cancellationToken);

    /// <summary>
    /// Runs one clipboard operation on a thread that can talk to the clipboard at all.
    /// </summary>
    /// <param name="onUnexpectedFailure">
    /// What to return when the operation throws something we did not anticipate. Passed in rather
    /// than defaulted, so each caller states its OWN safe answer: for a delivery that is a refusal
    /// the caller reports, and for a selection read it is simply nothing.
    /// </param>
    private static Task<T> RunStaAsync<T>(
        Func<T> operation,
        T onUnexpectedFailure,
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
                completion.TrySetResult(onUnexpectedFailure);
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

    private sealed record ClipboardSnapshot(bool IsEmpty, DataObject? Data);

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
}
