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
    }

    private static Form BuildForm(
        string mode,
        int refocusDelay,
        int holdFocus,
        string? resultPath,
        string? expectedSubstring,
        string? forbiddenSubstring)
    {
        var form = new Form
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
            if (resultPath is not null)
            {
                edit.TextChanged += (_, _) => WriteResult(
                    resultPath,
                    edit.Text,
                    expectedSubstring,
                    forbiddenSubstring,
                    edit.PasteMessages,
                    edit.SetTextMessages,
                    edit.Rewrites);
                WriteResult(resultPath, edit.Text, expectedSubstring, forbiddenSubstring, edit.PasteMessages, edit.SetTextMessages, edit.Rewrites);
            }
        }

        // THE CARET IS PART OF THE TARGET. At the end of the field's own text, the adapter's direct
        // value write applies (it appends); at the start, it does not, and the adapter pastes at the
        // caret instead. `caret-start` is the same field with the caret held at the start, so a journey
        // can make the production adapter take its paste route on purpose.
        var caretAtStart = mode == "caret-start";
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
        int rewrites = 0)
    {
        var result = JsonSerializer.Serialize(new
        {
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

        /// <summary>Whether a value set into the field is changed the moment it lands - a control that does not keep what it was given.</summary>
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool RewriteOnSetText { get; init; }

        public int Rewrites { get; private set; }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmPaste)
            {
                PasteMessages++;
            }
            else if (m.Msg == WmSetText && IsHandleCreated && Visible && !_rewriting)
            {
                SetTextMessages++;
                if (RewriteOnSetText)
                {
                    // THE VALUE LANDS, THEN CHANGES: the base handles the set, and the field appends
                    // its mark through a second set of its own that is not counted or rewritten
                    // again. WM_GETTEXT from here on answers the marked text.
                    base.WndProc(ref m);
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

                    return;
                }
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
