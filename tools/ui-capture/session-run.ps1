# Show the app on the real desktop and photograph it. RUNS IN THE LOGGED-IN SESSION.
#
# WHY THIS EXISTS. Every gate in this repository reads markup or tokens. Not one of them can see a
# rendered pixel, so padding, contrast, a clipped glyph and a control nobody can click are all
# invisible to a green suite. The only way in was SSH, and SSH lands in session 0, where a WinUI app
# dies in Microsoft.UI.Input.dll with 0xc0000602 before it draws anything and a screen capture
# returns a blank image with no error.
#
# ISOLATED DATA, BECAUSE THIS RUNS ON SOMEBODY'S ACTUAL MACHINE. ENVIOUSWISPR_DATA_DIRECTORY points
# the app at a scratch folder, so a capture run cannot touch the settings, history or custom words
# of whoever is logged in.
#
# A MARKER FILE, NOT A PNG, IS THE SUCCESS SIGNAL. A PNG exists whether the app was on screen or
# not: a capture of an empty desktop is a valid image of the wrong thing, which is the silent-empty
# failure this whole file is built to refuse. The marker is written only after the app was seen with
# a visible window, so the launcher can tell a photograph of the app from a photograph of nothing.
param(
    [Parameter(Mandatory = $true)][string] $AppExe,
    [Parameter(Mandatory = $true)][string] $OutputDirectory,
    [Parameter(Mandatory = $true)][string] $DataDirectory,
    [string] $OverlayState = "",
    [string] $Shot = "shot",
    [int] $SettleMilliseconds = 4000
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $OutputDirectory)) { New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null }
$log = Join-Path $OutputDirectory "$Shot.log"
$pidFile = Join-Path $OutputDirectory "$Shot.pid"
$marker = Join-Path $OutputDirectory "$Shot.ok"
$final = Join-Path $OutputDirectory "$Shot.png"
$staging = Join-Path $OutputDirectory "$Shot.png.partial"

function Note($text) { Add-Content -LiteralPath $log -Value "$([DateTime]::Now.ToString('HH:mm:ss')) $text" }

Note "session $((Get-Process -Id $PID).SessionId), shot '$Shot', overlay '$OverlayState'"

$env:ENVIOUSWISPR_DATA_DIRECTORY = $DataDirectory
if ($OverlayState) { $env:ENVIOUSWISPR_UAT_OVERLAY_STATE = $OverlayState }

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

$app = $null
try {
    $app = Start-Process -FilePath $AppExe -PassThru
    # THE PID GOES TO DISK IMMEDIATELY. The launcher cannot see this session's processes and must
    # still be able to stop the app if this script is killed part-way through.
    Set-Content -LiteralPath $pidFile -Value $app.Id
    Note "started pid $($app.Id)"

    Start-Sleep -Milliseconds $SettleMilliseconds

    if ($app.HasExited) {
        throw "The app exited before the capture with code $($app.ExitCode). A photograph now would be of an empty desktop."
    }

    # EVERY WINDOW THE APP OWNS, BECAUSE A CROPPED SCREENSHOT IS NOT A MEASUREMENT. A corner radius
    # read off a photograph is a guess; these numbers are what a layout bug has to be argued from.
    # MainWindowHandle is not enough - the pill is a second top-level window and is usually the one
    # being measured.
    $windows = [UiCaptureWindows]::For([uint32] $app.Id)
    if (-not $windows -or $windows.Count -eq 0) {
        throw "The app is running but has no visible window. A photograph now would show the desktop and read as a photograph of the app."
    }
    foreach ($line in $windows) { Note "window $line" }

    & (Join-Path $PSScriptRoot 'capture-shot.ps1') -Path $staging | ForEach-Object { Note $_ }
    if (-not (Test-Path -LiteralPath $staging)) { throw "capture-shot.ps1 wrote no file." }

    # RENAMED ONLY ONCE IT IS WHOLE, so a reader never picks up a half-written PNG, and the marker
    # is written last so its presence means the app was on screen when the shutter fired.
    Move-Item -LiteralPath $staging -Destination $final -Force
    Set-Content -LiteralPath $marker -Value "captured"
    Note "captured $final"
} catch {
    Note "FAILED: $($_.Exception.Message)"
    throw
} finally {
    Remove-Item -LiteralPath $staging -ErrorAction SilentlyContinue
    if ($app -and -not $app.HasExited) {
        # ASK BEFORE INSISTING. Force-killing every run made the app report "did not close properly"
        # on the next launch, ten times in a row - the capture harness manufacturing the very defect
        # somebody might photograph.
        $app.CloseMainWindow() | Out-Null
        if (-not $app.WaitForExit(4000)) {
            $app | Stop-Process -Force -ErrorAction SilentlyContinue
            Note "stopped pid $($app.Id) the hard way; it ignored the close request"
        } else {
            Note "closed pid $($app.Id)"
        }
    }
}
