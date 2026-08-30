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
