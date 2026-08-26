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
        if (expectedSubstring is { Length: > 100 } ||
            expectedSubstring?.Any(char.IsControl) == true)
        {
            throw new ArgumentException("The expected UAT substring is invalid.");
        }

        using var form = BuildForm(
            mode,
            refocusDelay,
            holdFocus,
            resultPath,
            expectedSubstring);
        Application.Run(form);
    }

    private static Form BuildForm(
        string mode,
        int refocusDelay,
        int holdFocus,
        string? resultPath,
        string? expectedSubstring)
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
            var edit = new TextBox
            {
                Name = mode == "password" ? "ProtectedField" : "StandardEditField",
                AccessibleName = manualMicrophone
                    ? "Physical microphone delivery target"
                    : mode == "password"
                        ? "Controlled protected field"
                        : "Controlled standard edit field",
                UseSystemPasswordChar = mode == "password",
                Text = mode == "password" ? string.Empty : "hello",
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
                    expectedSubstring);
                WriteResult(resultPath, edit.Text, expectedSubstring);
            }
        }

        static void Focus(Form form, Control focusTarget)
        {
            NativeFocus.BringToForeground(form.Handle);
            form.Activate();
            form.BringToFront();
            focusTarget.Focus();
            if (focusTarget is TextBox textBox)
            {
                textBox.SelectionStart = textBox.TextLength;
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

    private static void WriteResult(
        string path,
        string text,
        string? expectedSubstring)
    {
        var result = JsonSerializer.Serialize(new
        {
            containsExpected = !string.IsNullOrWhiteSpace(expectedSubstring) &&
                text.Contains(expectedSubstring, StringComparison.OrdinalIgnoreCase),
            characterCount = text.Length,
        });
        File.WriteAllText(path, result);
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
