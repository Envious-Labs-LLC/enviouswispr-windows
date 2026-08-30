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
    [ValidateRange(0, 28000)]
    [int] $SettleMilliseconds = 4000,
    [int] $MinimumWidth = 1280,
    [int] $MinimumHeight = 720,
    [string] $Press = '',
    [string] $Click = '',
    [switch] $Probe
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $OutputDirectory)) { New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null }
$log = Join-Path $OutputDirectory "$Shot.log"
$pidFile = Join-Path $OutputDirectory "$Shot.pid"
$marker = Join-Path $OutputDirectory "$Shot.ok"
$failMarker = Join-Path $OutputDirectory "$Shot.fail"
$final = Join-Path $OutputDirectory "$Shot.png"
$treePath = Join-Path $OutputDirectory "$Shot.tree.txt"
$staging = Join-Path $OutputDirectory "$Shot.png.partial"

function Publish($path, $content) {
    # WRITTEN ASIDE AND RENAMED INTO PLACE. The launcher polls for these files, so a verdict written
    # in two steps can be READ between them - an empty reason, or a marker that exists before the
    # thing it attests to is finished. A rename is the one operation that is either done or not.
    # NO -Force. The launcher holds an exclusive lock and verified both verdict files were absent
    # before starting, so a destination that already exists means something is wrong with that
    # assumption - and replacing it silently is how a wrong verdict becomes a confident one.
    $staging = "$path.writing"
    Set-Content -LiteralPath $staging -Value $content
    Move-Item -LiteralPath $staging -Destination $path
}

function Note($text) { Add-Content -LiteralPath $log -Value "$([DateTime]::Now.ToString('HH:mm:ss')) $text" }

Note "session $((Get-Process -Id $PID).SessionId), shot '$Shot', overlay '$OverlayState'"

$env:ENVIOUSWISPR_DATA_DIRECTORY = $DataDirectory
if ($OverlayState) { $env:ENVIOUSWISPR_UAT_OVERLAY_STATE = $OverlayState }

# THE APP CLOSES ITSELF, THROUGH ITS OWN TRAY EXIT. Asking the window to close does nothing here and
# should not: EnviousWispr lives in the notification area, so closing its window hides it and the
# process stays. Every capture therefore ended in a force-kill, and the app - correctly - reported
# "EnviousWispr did not close properly last time" on the next launch, ten times in a row, in a
# screenshot. ENVIOUSWISPR_UAT_EXIT_AFTER_MILLISECONDS runs the real shutdown path instead, so the
# harness stops manufacturing the defect it exists to photograph. It is clamped to the 500..30000
# window the app accepts. SettleMilliseconds is RANGE-CHECKED rather than clamped here: silently
# capping the exit timer while Start-Sleep honoured the larger settle would fire the app's shutdown
# BEFORE the shutter, and a harness that quietly changes the timing it was asked for is worse than
# one that refuses it.
$exitAfter = $SettleMilliseconds + 2000
$env:ENVIOUSWISPR_UAT_EXIT_AFTER_MILLISECONDS = $exitAfter

# NUDGE THE POINTER ONE PIXEL AND PUT IT BACK, TO WAKE A SLEEPING SCREEN. A blanked monitor drops the
# desktop to a small fallback mode, and a photograph of that passes every other check while being
# useless for judging layout. This is the smallest input that wakes a display: it moves the pointer
# by one pixel and returns it, so it cannot press anything or land on a control.
Add-Type -Namespace UiCapture -Name Cursor -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
public struct POINT { public int X, Y; }
'@
$where = New-Object UiCapture.Cursor+POINT
if ([UiCapture.Cursor]::GetCursorPos([ref] $where)) {
    [void][UiCapture.Cursor]::SetCursorPos($where.X + 1, $where.Y)
    [void][UiCapture.Cursor]::SetCursorPos($where.X, $where.Y)
}

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
$captured = $false
$failure = $null
try {
    # THE DPI CHECK IS INSIDE THE TRY, AND THE LOG ALREADY EXISTS. Thrown from the top of the file it
    # happened before there was anywhere to write it, and outside any catch - so the launcher simply
    # waited its ninety seconds and reported a timeout, with the actual reason gone. A refusal that
    # cannot say why it refused is only marginally better than the silent scaling it replaced.
    # BEFORE ANYTHING MEASURES A WINDOW, because awareness must be set before the first measurement.
    # TRUE PIXELS, AND IT REFUSES TO PROCEED WITHOUT THEM. PowerShell is not per-monitor DPI aware, so
    # Windows lies kindly: on a 3840x2160 display at 150% every measurement came back 2560x1440 and the
    # capture was a DOWNSCALED image of the desktop. It looks like a screenshot and is one, of a screen
    # that does not exist - and that is fatal for a tool whose whole job is judging padding, a hairline
    # border and an antialiased corner, all of which a 0.67x resample destroys.
    #
    # VERIFIED, NOT ATTEMPTED. Asking for awareness and ignoring the answer fails OPEN: the measurements
    # go quietly back to being wrong, which is the exact defect this is here to prevent. There is no
    # SetProcessDPIAware fallback either - it requests the older system awareness, so it would "succeed"
    # into precisely the wrong mode.
Add-Type -Namespace UiCapture -Name Dpi -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
[DllImport("user32.dll")] public static extern IntPtr GetThreadDpiAwarenessContext();
[DllImport("user32.dll")] public static extern bool AreDpiAwarenessContextsEqual(IntPtr a, IntPtr b);
'@
    $perMonitorV2 = [IntPtr] -4
    # The call fails when awareness is ALREADY set, which is a success for our purposes - so the verdict
    # comes from the effective context afterwards, never from this return value.
    [void][UiCapture.Dpi]::SetProcessDpiAwarenessContext($perMonitorV2)
    if (-not [UiCapture.Dpi]::AreDpiAwarenessContextsEqual(
            [UiCapture.Dpi]::GetThreadDpiAwarenessContext(), $perMonitorV2)) {
        throw "Could not put this process into per-monitor DPI awareness. Every measurement and the capture itself would be silently scaled, so this refuses to continue rather than produce a plausible picture of the wrong screen."
    }

    $app = Start-Process -FilePath $AppExe -PassThru
    # THE PID AND ITS START TIME GO TO DISK IMMEDIATELY. The launcher cannot see this session's
    # processes and must still be able to stop the app if this script is killed part-way through.
    # The start time is what makes the pid safe to act on: Windows reuses process ids, and a pid
    # alone can name a completely different program by the time anybody reads it.
    Set-Content -LiteralPath $pidFile -Value "$($app.Id)|$($app.StartTime.Ticks)"
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

    # PRESS WHAT WAS ASKED FOR, IN ORDER, BEFORE ANYTHING IS RECORDED. Each press settles before the
    # next, because a control that appears as a RESULT of the previous press does not exist yet.
    foreach ($control in @($Press -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })) {
        & (Join-Path $PSScriptRoot 'invoke-ui.ps1') -ProcessId $app.Id -Name $control |
            ForEach-Object { Note $_ }
        Start-Sleep -Milliseconds 1200
    }

    # A REAL CLICK, AFTER THE PRESSES AND BEFORE THE TREE IS READ. This is the only way to ask
    # whether a POINTER can reach something; invoking a control asks the control to act and skips
    # hit-testing entirely. Each entry is "<control name>@left|centre|right".
    foreach ($spot in @($Click -split ';' | ForEach-Object { $_.Trim() } | Where-Object { $_ })) {
        $parts = $spot -split '@'
        $where = if ($parts.Count -gt 1) { $parts[1] } else { 'centre' }
        & (Join-Path $PSScriptRoot 'click-ui.ps1') -ProcessId $app.Id -Name $parts[0] -Where $where |
            ForEach-Object { Note $_ }
        Start-Sleep -Milliseconds 700
    }

    # THE TREE BEFORE THE PICTURE, WHILE THE APP IS STILL UP. A screenshot cannot say whether a
    # control is enabled, what a toggle currently reads, or whether anything can be clicked at all.
    if ($Probe) {
        $tree = & (Join-Path $PSScriptRoot 'probe-ui.ps1') -ProcessId $app.Id
        if (-not $tree) {
            throw "The automation probe returned nothing. An empty tree beside a fresh screenshot reads as an app with no controls."
        }
        Publish $treePath ($tree -join "`n")
        Note "automation tree -> $treePath"
    }

    & (Join-Path $PSScriptRoot 'capture-shot.ps1') -Path $staging `
        -MinimumWidth $MinimumWidth -MinimumHeight $MinimumHeight | ForEach-Object { Note $_ }
    if (-not (Test-Path -LiteralPath $staging)) { throw "capture-shot.ps1 wrote no file." }

    # RENAMED ONLY ONCE IT IS WHOLE, so a reader never picks up a half-written PNG, and the marker
    # is written last so its presence means the app was on screen when the shutter fired.
    Move-Item -LiteralPath $staging -Destination $final -Force
    $captured = $true
    Note "captured $final"
} catch {
    $failure = $_.Exception.Message
    Note "FAILED: $failure"
} finally {
    Remove-Item -LiteralPath $staging -ErrorAction SilentlyContinue

    # THE APP IS STOPPED IN ITS OWN TRY, AND THE OUTCOME IS RECORDED. Stopping it with
    # SilentlyContinue meant a refused kill still read as a clean stop, and a terminating error here
    # skipped the verdict entirely - so the launcher waited out its timeout after a run that had
    # actually finished.
    $cleanupFailure = $null
    try {
        if ($app -and -not $app.HasExited) {
            # WAIT FOR THE APP'S OWN EXIT, and only insist if it overruns. The deadline it was given
            # is $exitAfter from launch; this allows that plus a margin for the shutdown itself.
            if ($app.WaitForExit($exitAfter + 6000)) {
                Note "pid $($app.Id) exited cleanly through its own tray exit"
            } else {
                $app | Stop-Process -Force -ErrorAction Stop
                if (-not $app.WaitForExit(5000)) {
                    throw "pid $($app.Id) is still running after being stopped."
                }
                Note "stopped pid $($app.Id) the hard way; it did not take its own exit"
            }
        }
    } catch {
        $cleanupFailure = $_.Exception.Message
        Note "CLEANUP FAILED: $cleanupFailure"
    }

    # EXACTLY ONE VERDICT, PUBLISHED LAST, AND SUCCESS MEANS BOTH HALVES SUCCEEDED. A capture that
    # left the app running is not a clean run, and saying so here is what stops the next capture
    # inheriting it. Signalling from the catch instead would let the launcher stop the task before
    # this cleanup finished, which is the race that stopped the polite shutdown from ever running.
    if ($captured -and -not $cleanupFailure) {
        Remove-Item -LiteralPath $pidFile -ErrorAction SilentlyContinue
        Publish $marker "captured"
    } else {
        $reason = @($failure, $cleanupFailure) | Where-Object { $_ }
        if (-not $reason) { $reason = @("the run ended without capturing") }
        Publish $failMarker ($reason -join '; ')
    }
}
