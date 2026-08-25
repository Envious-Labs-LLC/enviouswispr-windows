param(
    [Parameter(Mandatory = $true)]
    [int]$TargetProcessId,
    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class EnviousWisprCaptureNative {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out Rect rect);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback, IntPtr data);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    public delegate bool EnumProc(IntPtr h, IntPtr data);
    public struct Rect { public int Left, Top, Right, Bottom; }
}
"@

$window = [IntPtr]::Zero
$callback = [EnviousWisprCaptureNative+EnumProc]{
    param($handle, $data)
    $owner = 0
    [void][EnviousWisprCaptureNative]::GetWindowThreadProcessId($handle, [ref]$owner)
    if ($owner -eq $TargetProcessId) {
        $length = [EnviousWisprCaptureNative]::GetWindowTextLength($handle)
        if ($length -gt 0) {
            $title = [Text.StringBuilder]::new($length + 1)
            [void][EnviousWisprCaptureNative]::GetWindowText($handle, $title, $title.Capacity)
            if ($title.ToString() -eq "EnviousWispr") {
                $script:window = $handle
                return $false
            }
        }
    }
    return $true
}
$deadline = [DateTime]::UtcNow.AddSeconds(5)
do {
    $window = [IntPtr]::Zero
    [void][EnviousWisprCaptureNative]::EnumWindows($callback, [IntPtr]::Zero)
    if ($window -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 250 }
} while ($window -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline)
if ($window -eq [IntPtr]::Zero) { throw "EnviousWispr overlay window was not found for PID $TargetProcessId" }

$rect = [EnviousWisprCaptureNative+Rect]::new()
[void][EnviousWisprCaptureNative]::GetWindowRect($window, [ref]$rect)
$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
if ($width -le 0 -or $height -le 0) { throw "Overlay window has an invalid size: ${width}x${height}" }

$bitmap = [Drawing.Bitmap]::new($width, $height)
try {
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $deviceContext = $graphics.GetHdc()
        try { $rendered = [EnviousWisprCaptureNative]::PrintWindow($window, $deviceContext, 2) }
        finally { $graphics.ReleaseHdc($deviceContext) }
    } finally { $graphics.Dispose() }
    if (-not $rendered) { throw "PrintWindow could not render the overlay" }
    $bitmap.Save($OutputPath)

    $colors = @{}
    for ($x = 0; $x -lt $width; $x += 8) {
        for ($y = 0; $y -lt $height; $y += 8) {
            $colors[$bitmap.GetPixel($x, $y).ToArgb()] = $true
        }
    }
    if ($colors.Count -le 1) { throw "Captured overlay is blank" }
    Write-Output "OVERLAY PASS: ${width}x${height}, $($colors.Count) sampled colors, $OutputPath"
}
finally { $bitmap.Dispose() }
