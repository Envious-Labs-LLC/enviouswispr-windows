using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;

namespace EnviousWispr.Delivery.Target.Uat;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var mode = ArgumentValue(args, "--mode")?.ToLowerInvariant() ?? "edit";
        var refocusDelay = int.TryParse(
            ArgumentValue(args, "--refocus-delay-ms"),
            out var delay)
            ? Math.Clamp(delay, 0, 30_000)
            : 0;
        var holdFocus = int.TryParse(
            ArgumentValue(args, "--hold-focus-ms"),
            out var hold)
            ? Math.Clamp(hold, 0, 30_000)
            : 0;
        var resultPath = ValidateResultPath(ArgumentValue(args, "--result"));
        var expectedSubstring = ArgumentValue(args, "--expected-substring");
        var forbiddenSubstring = ArgumentValue(args, "--forbidden-substring");
        if (expectedSubstring is { Length: > 100 } ||
            expectedSubstring?.Any(char.IsControl) == true ||
            forbiddenSubstring is { Length: > 100 } ||
            forbiddenSubstring?.Any(char.IsControl) == true)
        {
            throw new ArgumentException("The expected or forbidden UAT substring is invalid.");
        }

        using var form = BuildForm(
            mode,
            refocusDelay,
            holdFocus,
            resultPath,
            expectedSubstring,
            forbiddenSubstring);
        Application.Run(form);
        if (form.SelfTestVerdict is { } verdict)
        {
            Console.WriteLine(verdict switch
            {
                0 => "settle-self-test: the settled receipt counted the paste queued ahead of the settle request.",
                3 => "settle-self-test: not run - the desk's clipboard could not be snapshotted whole, so it was not touched.",
                4 => "settle-self-test: the desk's clipboard could not be put back; the verdict is withdrawn.",
                _ => "settle-self-test: the settled receipt did NOT count the paste queued ahead of the settle request.",
            });
            Environment.ExitCode = verdict;
        }
    }

    private static TargetForm BuildForm(
        string mode,
        int refocusDelay,
        int holdFocus,
        string? resultPath,
        string? expectedSubstring,
        string? forbiddenSubstring)
    {
        var caretAtStart = mode == "caret-start";
        var form = new TargetForm
        {
            Name = "Phase13DeliveryTarget",
            Text = $"EnviousWispr delivery target - {mode} - {Environment.ProcessId}",
            StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new Size(760, 360),
            BackColor = Color.FromArgb(20, 24, 32),
            ForeColor = Color.White,
            TopMost = true,
        };

        Control focusTarget;
        if (mode == "game")
        {
            form.FormBorderStyle = FormBorderStyle.None;
            form.WindowState = FormWindowState.Maximized;
            focusTarget = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(10, 14, 22),
                TabStop = true,
                AccessibleName = "Non-editable full-screen game surface",
            };
            focusTarget.Controls.Add(new Label
            {
                AutoSize = true,
                Text = "Controlled non-editable full-screen target",
                Font = new Font(SystemFonts.DefaultFont.FontFamily, 24),
                ForeColor = Color.White,
                Location = new Point(80, 80),
            });
            form.Controls.Add(focusTarget);
        }
        else
        {
            var manualMicrophone = mode == "manual-microphone";
            var settleSelfTest = mode == "settle-self-test";
            var label = new Label
            {
                AutoSize = true,
                Text = manualMicrophone
                    ? "Hold F8 and say this sentence clearly:"
                    : mode == "password"
                        ? "Controlled protected field"
                        : "Controlled standard edit field",
                Font = new Font(SystemFonts.DefaultFont.FontFamily, manualMicrophone ? 15 : 18),
                Location = new Point(40, 40),
            };
            if (manualMicrophone)
            {
                label.AccessibleName = "Physical microphone acceptance instructions";
                form.Controls.Add(new Label
                {
                    AutoSize = true,
                    Text = "This is an Envious Wispr microphone test.\r\n" +
                        "The quick brown fox jumps over the lazy dog.\r\n" +
                        "Then release F8.",
                    Font = new Font(SystemFonts.DefaultFont.FontFamily, 15),
                    Location = new Point(40, 82),
                    AccessibleName = "Fixed public microphone acceptance phrase",
                });
            }
            var edit = new InstrumentedTextBox
            {
                // A FIELD THAT REWRITES WHAT IS SET INTO IT, for the route the adapter must refuse
                // to finish: its value write lands, the read-back differs, and the adapter must
                // report the insertion unverified and paste nothing after it.
                RewriteOnSetText = mode == "unverified-write",
                Name = mode == "password" ? "ProtectedField" : "StandardEditField",
                AccessibleName = manualMicrophone
                    ? "Physical microphone delivery target"
                    : mode == "password"
                        ? "Controlled protected field"
                        : "Controlled standard edit field",
                UseSystemPasswordChar = mode == "password",
                Text = mode == "password" ? string.Empty : SeedText,
                Font = new Font(SystemFonts.DefaultFont.FontFamily, 18),
                Location = new Point(40, manualMicrophone ? 180 : 100),
                Width = 660,
            };
            form.Controls.Add(label);
            form.Controls.Add(edit);
            focusTarget = edit;
            var settled = 0;
            void Publish()
            {
                if (resultPath is not null)
                {
                    WriteResult(
                        resultPath,
                        edit.Text,
                        expectedSubstring,
                        forbiddenSubstring,
                        edit.PasteMessages,
                        edit.SetTextMessages,
                        edit.Rewrites,
                        settled);
                }
            }

            // PUBLISHED ON EVERY TEXT CHANGE AND ON EVERY COUNTED MESSAGE. A paste that changes
            // nothing - empty, or refused by the control - raises no TextChanged, and a receipt
            // written only from there would still say "no WM_PASTE"; the counters publish
            // themselves.
            edit.TextChanged += (_, _) => Publish();
            edit.MessageCounted += (_, _) => Publish();
            Publish();

            // THE FINAL RECEIPT IS ACKNOWLEDGED, NOT ASSUMED. The sender's exit says nothing about
            // what is still queued for this window: a keystroke it sent sits in this thread's input
            // queue below every posted message, so a settle request posted after it would be handled
            // first. The request therefore starts a watch that answers only once this thread's queue
            // has held no input, no posted and no sent message for two consecutive looks; the receipt
            // written then carries the request's sequence, and that is the one the journey reads.
            form.SettleRequested += (_, sequence) =>
            {
                var quietLooks = 0;
                var watch = new System.Windows.Forms.Timer { Interval = 50 };
                watch.Tick += (_, _) =>
                {
                    quietLooks = NativeQueue.HasPendingInputOrMessages() ? 0 : quietLooks + 1;
                    if (quietLooks < 2)
                    {
                        return;
                    }

                    watch.Stop();
                    watch.Dispose();
                    settled = sequence;
                    Publish();
                    if (settleSelfTest)
                    {
                        form.SelfTestVerdict = edit.PasteMessages >= 1 ? 0 : 2;
                        form.Close();
                    }
                };
                watch.Start();
            };

            if (settleSelfTest)
            {
                // THE PROOF THAT A SETTLE CANNOT OVERTAKE A PASTE: a Ctrl+V sent to this window through
                // the input queue, with the clipboard emptied so the paste changes nothing, and a
                // settle request posted right behind it. The settled receipt must already count the
                // paste; a target that answered on the posted message alone would report none.
                form.Shown += (_, _) =>
                {
                    var arm = new System.Windows.Forms.Timer { Interval = 750 };
                    arm.Tick += (_, _) =>
                    {
                        arm.Stop();
                        arm.Dispose();
                        // THE DESK'S CLIPBOARD IS KEPT WHOLE: every format is snapshotted before it
                        // is emptied, the self-test refuses to run when any format cannot be copied,
                        // and the snapshot goes back when the window closes, whatever the verdict.
                        var kept = ClipboardKeeper.Capture();
                        if (kept is null)
                        {
                            form.SelfTestVerdict = 3;
                            form.Close();
                            return;
                        }

                        // RESTORED WHATEVER THE VERDICT, AND THE VERDICT DOES NOT SURVIVE A FAILED
                        // RESTORE: a self-test that passed and left the desk's clipboard changed has
                        // failed at the one thing it promised.
                        form.FormClosed += (_, _) =>
                        {
                            if (!kept.Restore())
                            {
                                form.SelfTestVerdict = 4;
                            }
                        };
                        Clipboard.Clear();
                        Focus(form, focusTarget);
                        SendKeys.Send("^v");
                        _ = NativeQueue.PostMessage(form.Handle, TargetForm.WmSettle, 1, 0);
                    };
                    arm.Start();
                };
            }
        }

        // THE CARET IS PART OF THE TARGET. At the end of the field's own text, the adapter's direct
        // value write applies (it appends); at the start, it does not, and the adapter pastes at the
        // caret instead. `caret-start` is the same field with the caret held at the start, so a journey
        // can make the production adapter take its paste route on purpose.
        void Focus(Form form, Control focusTarget)
        {
            NativeFocus.BringToForeground(form.Handle);
            form.Activate();
            form.BringToFront();
            focusTarget.Focus();
            if (focusTarget is TextBox textBox)
            {
                textBox.SelectionStart = caretAtStart ? 0 : textBox.TextLength;
                textBox.SelectionLength = 0;
            }
        }

        var closeTimer = new System.Windows.Forms.Timer { Interval = 90_000 };
        System.Windows.Forms.Timer? holdTimer = null;
        closeTimer.Tick += (_, _) => form.Close();
        form.Shown += (_, _) =>
        {
            Focus(form, focusTarget);

            closeTimer.Start();
            if (refocusDelay > 0)
            {
                var focusTimer = new System.Windows.Forms.Timer { Interval = refocusDelay };
                focusTimer.Tick += (_, _) =>
                {
                    focusTimer.Stop();
                    Focus(form, focusTarget);
                };
                focusTimer.Start();
            }

            if (holdFocus > 0)
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                holdTimer = new System.Windows.Forms.Timer { Interval = 100 };
                holdTimer.Tick += (_, _) =>
                {
                    if (timer.ElapsedMilliseconds >= holdFocus)
                    {
                        holdTimer.Stop();
                        return;
                    }

                    Focus(form, focusTarget);
                };
                holdTimer.Start();
            }
        };
        form.FormClosed += (_, _) =>
        {
            closeTimer.Dispose();
            holdTimer?.Dispose();
        };
        return form;
    }

    private static string? ValidateResultPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(path);
        var temporaryRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The UAT result must stay under the Windows temporary directory.");
        }

        return fullPath;
    }

    /// <summary>The words the standard field starts with, so where a delivery lands relative to them says which route it took.</summary>
    private const string SeedText = "hello";

    private static void WriteResult(
        string path,
        string text,
        string? expectedSubstring,
        string? forbiddenSubstring,
        int pasteMessages = 0,
        int setTextMessages = 0,
        int rewrites = 0,
        int settled = 0)
    {
        var result = JsonSerializer.Serialize(new
        {
            // THE SETTLE REQUEST THIS RECEIPT ANSWERS, or 0 for a receipt written on the way: a
            // journey reads a receipt whose settled sequence is its own request's.
            settled,
            // HOW THE WORDS ARRIVED, counted at the window: a paste reaches a Win32 edit as WM_PASTE,
            // a UI Automation value write as WM_SETTEXT. The journey reads the route off these.
            pasteMessages,
            setTextMessages,
            // HOW OFTEN THE FIELD REWROTE A VALUE SET INTO IT (the unverified-write mode), and whether
            // its text now ends with the mark it appends - the read-back the adapter must have seen.
            rewrites,
            rewritten = text.EndsWith(InstrumentedTextBox.RewriteMark, StringComparison.Ordinal),
            containsExpected = !string.IsNullOrWhiteSpace(expectedSubstring) &&
                text.Contains(expectedSubstring, StringComparison.OrdinalIgnoreCase),
            // WHERE THE WORDS LANDED tells the route apart: appended after the field's own seed text
            // by the direct value write, which leaves the seed at the start, or pasted at a caret held
            // at the start, which leaves the seed at the end.
            seedAtStart = text.StartsWith(SeedText, StringComparison.Ordinal) && text.Length > SeedText.Length,
            seedAtEnd = text.EndsWith(SeedText, StringComparison.Ordinal) && text.Length > SeedText.Length,
            containsForbidden = !string.IsNullOrWhiteSpace(forbiddenSubstring) &&
                text.Contains(forbiddenSubstring, StringComparison.OrdinalIgnoreCase),
            characterCount = text.Length,
        });
        File.WriteAllText(path, result);
    }

    /// <summary>The target window: a form that answers a settle request from the journey harness.</summary>
    private sealed class TargetForm : Form
    {
        /// <summary>The message a journey posts to ask for an acknowledged final receipt; wParam is the request's sequence.</summary>
        public const int WmSettle = 0x8000 + 0x0013;

        /// <summary>Raised on the UI thread with the request's sequence.</summary>
        public event EventHandler<int>? SettleRequested;

        /// <summary>The settle self-test's exit code, once it has run.</summary>
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int? SelfTestVerdict { get; set; }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmSettle)
            {
                SettleRequested?.Invoke(this, (int)m.WParam);
                return;
            }

            base.WndProc(ref m);
        }
    }

    /// <summary>The desk's clipboard, every format copied, so a self-test that empties it can put it back whole.</summary>
    private sealed class ClipboardKeeper
    {
        private readonly DataObject? _data;

        private ClipboardKeeper(DataObject? data)
        {
            _data = data;
        }

        /// <summary>A copy of every format on the clipboard, or null when any format cannot be copied - in which case nothing is touched.</summary>
        public static ClipboardKeeper? Capture()
        {
            try
            {
                var source = Clipboard.GetDataObject();
                if (source is null)
                {
                    return new ClipboardKeeper(null);
                }

                var copy = new DataObject();
                foreach (var format in source.GetFormats(autoConvert: false))
                {
                    var value = source.GetData(format, autoConvert: false);
                    var cloned = value is null ? null : Clone(value);
                    if (cloned is null)
                    {
                        return null;
                    }

                    copy.SetData(format, autoConvert: false, cloned);
                }

                return new ClipboardKeeper(copy);
            }
            catch (Exception exception) when (exception is ExternalException or ThreadStateException or InvalidOperationException or ArgumentException)
            {
                return null;
            }
        }

        /// <summary>Puts the copy back; false when the clipboard would not take it, which no verdict may survive.</summary>
        public bool Restore()
        {
            try
            {
                if (_data is null)
                {
                    Clipboard.Clear();
                }
                else
                {
                    Clipboard.SetDataObject(_data, copy: true, retryTimes: 10, retryDelay: 50);
                }

                return true;
            }
            catch (Exception exception) when (exception is ExternalException or ThreadStateException or ArgumentException)
            {
                return false;
            }
        }

        private static object? Clone(object value) => value switch
        {
            byte[] bytes => bytes.ToArray(),
            MemoryStream memory => new MemoryStream(memory.ToArray(), writable: false),
            Bitmap bitmap => bitmap.Clone(),
            System.Collections.Specialized.StringCollection strings => CloneStrings(strings),
            ICloneable cloneable => cloneable.Clone(),
            string or char or bool or byte or sbyte or short or ushort or int or uint or
                long or ulong or float or double or decimal or DateTime or DateTimeOffset or
                TimeSpan or Guid => value,
            Stream stream => CloneStream(stream),
            _ => null,
        };

        private static System.Collections.Specialized.StringCollection CloneStrings(System.Collections.Specialized.StringCollection strings)
        {
            var clone = new System.Collections.Specialized.StringCollection();
            clone.AddRange(strings.Cast<string>().ToArray());
            return clone;
        }

        /// <summary>The whole of a seekable stream from its start, its position put back; null for one that cannot seek or fails to read - the snapshot is refused then.</summary>
        private static MemoryStream? CloneStream(Stream stream)
        {
            if (!stream.CanSeek)
            {
                return null;
            }

            var originalPosition = stream.Position;
            try
            {
                stream.Position = 0;
                var copy = new MemoryStream();
                stream.CopyTo(copy);
                copy.Position = 0;
                return copy;
            }
            catch (Exception exception) when (exception is IOException or NotSupportedException or ObjectDisposedException or UnauthorizedAccessException)
            {
                return null;
            }
            finally
            {
                try
                {
                    stream.Position = originalPosition;
                }
                catch (Exception exception) when (exception is IOException or NotSupportedException or ObjectDisposedException)
                {
                    // The position could not be put back on a stream that already failed; the snapshot is refused above.
                }
            }
        }
    }

    /// <summary>What this thread's message queue still holds, asked of Windows.</summary>
    private static class NativeQueue
    {
        private const uint QsKey = 0x0001;
        private const uint QsMouseMove = 0x0002;
        private const uint QsMouseButton = 0x0004;
        private const uint QsPostMessage = 0x0008;
        private const uint QsSendMessage = 0x0040;
        private const uint QsHotkey = 0x0080;
        private const uint QsRawInput = 0x0400;
        private const uint Watched = QsKey | QsMouseMove | QsMouseButton | QsPostMessage | QsSendMessage | QsHotkey | QsRawInput;

        /// <summary>True while a keystroke, a mouse event, a posted or a sent message is still waiting for this thread.</summary>
        public static bool HasPendingInputOrMessages() => (GetQueueStatus(Watched) >> 16) != 0;

        [DllImport("user32.dll")]
        private static extern uint GetQueueStatus(uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    }

    /// <summary>A text box that counts how its text arrived: WM_PASTE for a paste, WM_SETTEXT for a UI Automation value write.</summary>
    private sealed class InstrumentedTextBox : TextBox
    {
        /// <summary>What the rewriting field appends to every value set into it, so the adapter's read-back never matches its write.</summary>
        public const string RewriteMark = " [rewritten by the target]";

        private const int WmSetText = 0x000C;
        private const int WmPaste = 0x0302;
        private bool _rewriting;

        public int PasteMessages { get; private set; }

        public int SetTextMessages { get; private set; }

        /// <summary>Raised after a counted message has been handled, whether or not the text changed.</summary>
        public event EventHandler? MessageCounted;

        /// <summary>Whether a value set into the field is changed the moment it lands - a control that does not keep what it was given.</summary>
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool RewriteOnSetText { get; init; }

        public int Rewrites { get; private set; }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmPaste)
            {
                PasteMessages++;
                base.WndProc(ref m);
                MessageCounted?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (m.Msg == WmSetText && IsHandleCreated && Visible && !_rewriting)
            {
                SetTextMessages++;
                base.WndProc(ref m);
                if (RewriteOnSetText)
                {
                    // THE VALUE LANDS, THEN CHANGES: the base has handled the set, and the field
                    // appends its mark through a second set of its own that is not counted or
                    // rewritten again. WM_GETTEXT from here on answers the marked text.
                    _rewriting = true;
                    try
                    {
                        Text = Text + RewriteMark;
                        Rewrites++;
                    }
                    finally
                    {
                        _rewriting = false;
                    }
                }

                MessageCounted?.Invoke(this, EventArgs.Empty);
                return;
            }

            base.WndProc(ref m);
        }
    }

    private static class NativeFocus
    {
        internal static void BringToForeground(nint window)
        {
            var foreground = GetForegroundWindow();
            var foregroundThread = foreground == 0
                ? 0
                : GetWindowThreadProcessId(foreground, out _);
            var currentThread = GetCurrentThreadId();
            var attached = foregroundThread != 0 &&
                foregroundThread != currentThread &&
                AttachThreadInput(currentThread, foregroundThread, attach: true);
            try
            {
                _ = BringWindowToTop(window);
                _ = SetForegroundWindow(window);
            }
            finally
            {
                if (attached)
                {
                    _ = AttachThreadInput(currentThread, foregroundThread, attach: false);
                }
            }
        }

        [DllImport("user32.dll")]
        private static extern nint GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AttachThreadInput(
            uint idAttach,
            uint idAttachTo,
            [MarshalAs(UnmanagedType.Bool)] bool attach);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BringWindowToTop(nint window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(nint window);
    }

    private static string? ArgumentValue(string[] arguments, string name)
    {
        for (var index = 0; index < arguments.Length - 1; index++)
        {
            if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return arguments[index + 1];
            }
        }

        return null;
    }
}
