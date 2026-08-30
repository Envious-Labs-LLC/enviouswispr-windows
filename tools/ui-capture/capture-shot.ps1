# Capture the interactive desktop to a PNG.
#
# THIS RUNS IN THE LOGGED-IN DESKTOP SESSION, NEVER OVER SSH, and the difference is not cosmetic.
# An SSH session on Windows lives in session 0 with no desktop attached. A capture taken there
# returns a black or 1x1 image with NO error, which is the silent-empty shape: a file that looks
# like a screenshot, is named like a screenshot, and shows nothing that was on screen.
# `start-capture.ps1` is what puts this code in the right session.
param(
    [Parameter(Mandatory = $true)][string] $Path,
    [int] $MinimumWidth = 1280,
    [int] $MinimumHeight = 720
)

$ErrorActionPreference = 'Stop'

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

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen
if ($bounds.Width -le 1 -or $bounds.Height -le 1) {
    throw "VirtualScreen is $($bounds.Width)x$($bounds.Height), so no desktop is attached to this session. Refusing to write a blank file that would read as a screenshot."
}

# AND REFUSE A DESKTOP THAT HAS COLLAPSED. A monitor that has gone to sleep drops the desktop to a
# fallback mode - measured at 1024x768 on a 3840x2160 panel at half past two in the morning. The
# capture SUCCEEDS: it is a real photograph of a real desktop, every other check passes, and every
# judgement made from it about padding or alignment is worthless. Anything this small is a sleeping
# display rather than a machine somebody works on. The floor is 1280x720 because that is what
# Windows 11 itself requires, so it refuses the measured fallback without excluding a supported PC.
if ($bounds.Width -lt $MinimumWidth -or $bounds.Height -lt $MinimumHeight) {
    throw "The desktop is $($bounds.Width)x$($bounds.Height), below the ${MinimumWidth}x${MinimumHeight} floor. A display that has gone to sleep reports a fallback mode this size, and a photograph of it looks like a screenshot while being useless for judging layout. Wake the screen, or lower the floor deliberately if this machine really is this size."
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
