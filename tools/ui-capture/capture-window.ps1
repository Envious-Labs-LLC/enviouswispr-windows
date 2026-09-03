# Photograph a window's OWN pixels, not the screen it happens to be on. RUNS IN THE LOGGED-IN SESSION.
#
# WHY THIS EXISTS BESIDE session-run.ps1. That script grabs the SCREEN, so an overlay shot is a
# photograph of whatever is in front of the overlay. The first attempt to photograph the recording
# pill returned a music player, because somebody's music player was on top of it - and nothing in the
# capture could say so, which is the occlusion limit probe-ui.ps1 already declares. PrintWindow asks
# the window to draw ITSELF into a bitmap, so the result is the pill whatever the z-order, and it
# needs nobody's desktop cleared.
#
# ITS OWN LIMIT, STATED: this is what the window RENDERS, not what the screen SHOWS. It cannot tell
# you the pill was legible over somebody's document, because the document is not in the picture. Use
# it to judge the pill itself; use a screen grab to judge the pill in company.
#
# THE TRANSIENT SEVERITIES NEED A SHORT SETTLE. A notice with no action dismisses itself on its dwell,
# so a six-second settle photographs nothing and reports no window at all. The advisory keeps its pill
# because it carries a button. 1500 ms catches the rest.
param(
    [Parameter(Mandatory = $true)][string] $AppExe,
    [Parameter(Mandatory = $true)][string] $OverlayState,
    [Parameter(Mandatory = $true)][string] $OutputPath,
    [Parameter(Mandatory = $true)][string] $DataDirectory,
    [string] $WindowTitleMatch = 'dictation status',
    [int] $SettleMilliseconds = 1500
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class Shot {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
}
"@

# WITHOUT THIS THE PICTURE IS A CROP THAT LOOKS LIKE A LAYOUT FAULT. Windows PowerShell is not
# per-monitor DPI aware, so GetWindowRect returns virtualised DIP - 380x150 for a window rendering
# at 570x225 - and PrintWindow then draws the real pixels into a bitmap two thirds the size. What
# came back was the top-left of the pill with the body text running off the right edge and the
# button below the frame, which reads exactly like clipped content and was the instrument.
# -4 is DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2.
if (-not [Shot]::SetProcessDpiAwarenessContext([IntPtr](-4))) {
    throw "Could not take per-monitor DPI awareness, so every measurement here would be silently scaled."
}

$env:ENVIOUSWISPR_DATA_DIRECTORY = $DataDirectory
$env:ENVIOUSWISPR_UAT_OVERLAY_STATE = $OverlayState
$env:ENVIOUSWISPR_UAT_EXIT_AFTER_MILLISECONDS = $SettleMilliseconds + 6000

$app = Start-Process -FilePath $AppExe -PassThru
Start-Sleep -Milliseconds $SettleMilliseconds

$target = [IntPtr]::Zero
$rect = New-Object Shot+RECT
$callback = [Shot+EnumProc] {
    param($h, $p)
    $owner = 0
    $null = [Shot]::GetWindowThreadProcessId($h, [ref] $owner)
    if ($owner -ne $app.Id -or -not [Shot]::IsWindowVisible($h)) { return $true }
    $title = New-Object System.Text.StringBuilder 200
    $null = [Shot]::GetWindowTextW($h, $title, $title.Capacity)
    if ($title.ToString() -notmatch $WindowTitleMatch) { return $true }
    $script:target = $h
    return $false
}
$null = [Shot]::EnumWindows($callback, [IntPtr]::Zero)

if ($script:target -eq [IntPtr]::Zero) {
    Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
    throw "No window matching '$WindowTitleMatch' for overlay state '$OverlayState'. A transient notice may have dismissed itself already - try a shorter settle."
}

$null = [Shot]::GetWindowRect($script:target, [ref] $rect)
$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
Write-Host "window ${width}x${height} at $($rect.Left),$($rect.Top)"

$bitmap = New-Object System.Drawing.Bitmap $width, $height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$dc = $graphics.GetHdc()
# 2 = PW_RENDERFULLCONTENT, which is what makes this work for a composited window.
$ok = [Shot]::PrintWindow($script:target, $dc, 2)
$graphics.ReleaseHdc($dc)
$graphics.Dispose()
$bitmap.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()
Write-Host "PrintWindow returned $ok -> $OutputPath"

Start-Sleep -Seconds 8
if (-not $app.HasExited) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
