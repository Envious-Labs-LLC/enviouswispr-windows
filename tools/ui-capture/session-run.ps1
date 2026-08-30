# Show the app on the real desktop and photograph it. RUNS IN THE LOGGED-IN SESSION.
#
# WHY THIS EXISTS. Every gate in this repository reads markup or tokens. Not one of them can see a
# rendered pixel, so padding, contrast, a clipped glyph and a control nobody can click are all
# invisible to a green suite. Three open issues say "checked on a real screen" and could not be
# closed, because the only way in was SSH, and SSH lands in session 0 where a WinUI app dies in
# Microsoft.UI.Input.dll with 0xc0000602 before it draws anything.
#
# ISOLATED DATA, BECAUSE THIS RUNS ON SOMEBODY'S ACTUAL MACHINE. ENVIOUSWISPR_DATA_DIRECTORY points
# the app at a scratch folder, so a capture run cannot touch the settings, history or custom words
# of whoever is logged in.
param(
    [Parameter(Mandatory = $true)][string] $AppExe,
    [Parameter(Mandatory = $true)][string] $OutputDirectory,
    [Parameter(Mandatory = $true)][string] $DataDirectory,
    [string] $OverlayState = "",
    [string] $Shot = "shot",
    [int] $SettleMilliseconds = 4000
)

$ErrorActionPreference = 'Stop'
$log = Join-Path $OutputDirectory "$Shot.log"
if (-not (Test-Path $OutputDirectory)) { New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null }

function Note($text) { Add-Content -LiteralPath $log -Value "$([DateTime]::Now.ToString('HH:mm:ss')) $text" }

Note "session $((Get-Process -Id $PID).SessionId), shot '$Shot', overlay '$OverlayState'"

$env:ENVIOUSWISPR_DATA_DIRECTORY = $DataDirectory
if ($OverlayState) { $env:ENVIOUSWISPR_UAT_OVERLAY_STATE = $OverlayState }

$app = $null
try {
    $app = Start-Process -FilePath $AppExe -PassThru
    Note "started pid $($app.Id)"

    # A FIXED WAIT, AND IT IS HONEST ABOUT BEING ONE. The ready event the app signals is a named
    # event in this session, and waiting on it correctly is more machinery than a screenshot needs.
    # The log records the wait, so a capture taken too early is visible as a capture taken too early
    # rather than mistaken for the app looking wrong.
    Start-Sleep -Milliseconds $SettleMilliseconds

    if ($app.HasExited) {
        Note "THE APP EXITED BEFORE THE CAPTURE, exit code $($app.ExitCode). The image below is the desktop without it."
    }

    # EVERY WINDOW THE APP OWNS, BECAUSE A CROPPED SCREENSHOT IS NOT A MEASUREMENT. Reading a
    # corner radius off a photograph is guesswork; these numbers say exactly how big each window is
    # and where, which is what a layout bug has to be argued from. MainWindowHandle is not enough -
    # the pill is a second top-level window and is the one usually being measured.
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class UiCaptureWindows {
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int n);
    delegate bool EnumProc(IntPtr h, IntPtr p);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public static List<string> For(uint target) {
        var found = new List<string>();
        EnumWindows((h, p) => {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid == target && IsWindowVisible(h)) {
                RECT r; GetWindowRect(h, out r);
                var title = new System.Text.StringBuilder(200);
                GetWindowTextW(h, title, 200);
                found.Add(string.Format("{0}x{1} at {2},{3}  \"{4}\"",
                    r.Right - r.Left, r.Bottom - r.Top, r.Left, r.Top, title.ToString()));
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@ -ErrorAction SilentlyContinue
    foreach ($line in [UiCaptureWindows]::For([uint32] $app.Id)) { Note "window $line" }

    & (Join-Path $PSScriptRoot 'capture-shot.ps1') -Path (Join-Path $OutputDirectory "$Shot.png") |
        ForEach-Object { Note $_ }
} catch {
    Note "FAILED: $($_.Exception.Message)"
    throw
} finally {
    if ($app -and -not $app.HasExited) {
        $app | Stop-Process -Force -ErrorAction SilentlyContinue
        Note "stopped pid $($app.Id)"
    }
}
