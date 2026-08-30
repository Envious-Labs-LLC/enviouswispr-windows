# Capture the interactive desktop to a PNG.
#
# THIS RUNS IN THE LOGGED-IN DESKTOP SESSION, NEVER OVER SSH, and the difference is not cosmetic.
# An SSH session on Windows lives in session 0 with no desktop attached. A capture taken there
# returns a black or 1x1 image with NO error, which is the silent-empty shape: a file that looks
# like a screenshot, is named like a screenshot, and shows nothing that was on screen.
# `start-capture.ps1` is what puts this code in the right session.
param(
    [Parameter(Mandatory = $true)][string] $Path
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

# TRUE PIXELS, NOT THE SCALED-DOWN VERSION WINDOWS OFFERS AN OLD PROGRAM. PowerShell is not
# per-monitor DPI aware, so Windows lies to it kindly: on this 3840x2160 display at 150% every
# measurement came back 2560x1440 and the capture was a DOWNSCALED image of the desktop. It looks
# like a screenshot and is one, of a screen that does not exist.
#
# THAT IS FATAL FOR THE JOB THIS TOOL DOES. The whole point is judging padding, a hairline border,
# an antialiased corner and a one-pixel misalignment - every one of which is destroyed by a 0.67x
# resample. It also made window rectangles read in the wrong units, and cost a wrong diagnosis:
# a window 1590 pixels tall on a 2160-pixel screen was measured as running off the bottom of a
# 1440-pixel one, and nearly got a fix for a bug that was not there.
Add-Type -Namespace UiCapture -Name Dpi -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
'@ -ErrorAction SilentlyContinue
# -4 is DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2. The older call is the fallback for a host that
# refuses it; either one stops the virtualisation, and both are no-ops once awareness is set.
try { [void][UiCapture.Dpi]::SetProcessDpiAwarenessContext([IntPtr] -4) } catch { }
try { [void][UiCapture.Dpi]::SetProcessDPIAware() } catch { }

# REFUSE RATHER THAN WRITE A BLANK. This is the check that turns the silent-empty failure into a
# loud one, and it is the whole reason this file is not three lines long.
$bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen
if ($bounds.Width -le 1 -or $bounds.Height -le 1) {
    throw "VirtualScreen is $($bounds.Width)x$($bounds.Height), so no desktop is attached to this session. Refusing to write a blank file that would read as a screenshot."
}

$bitmap = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
try {
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
    } finally {
        $graphics.Dispose()
    }

    $directory = Split-Path -Parent $Path
    if (-not (Test-Path $directory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
    $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
} finally {
    $bitmap.Dispose()
}

"$($bounds.Width)x$($bounds.Height) -> $Path"
