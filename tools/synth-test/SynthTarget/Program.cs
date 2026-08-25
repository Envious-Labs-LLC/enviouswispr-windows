// Deterministic paste target for the synthetic E2E test (tools/synth-test).
// A plain Win32 (WinForms) text box; dumps its content to a file every 500 ms
// so the test can read exactly what the app pasted, without UI Automation.
//
// Usage: SynthTarget.exe <dump-file-path>

var dumpPath = args.Length > 0 ? args[0] : "synthtarget-dump.txt";
File.WriteAllText(dumpPath, "");

var box = new TextBox
{
    Multiline = true,
    Dock = DockStyle.Fill,
    AcceptsReturn = true,
    Font = new Font("Consolas", 14f),
};

var form = new Form
{
    Text = "EnviousWispr SynthTarget",
    Width = 700,
    Height = 300,
    StartPosition = FormStartPosition.CenterScreen,
    Controls = { box },
};

var timer = new System.Windows.Forms.Timer { Interval = 500 };
timer.Tick += (_, _) =>
{
    try { File.WriteAllText(dumpPath, box.Text); } catch { /* disk hiccup — retry next tick */ }
};
timer.Start();

Application.Run(form);
