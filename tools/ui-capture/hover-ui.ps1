# Does resting a pointer on the pill keep it there? RUNS IN THE LOGGED-IN SESSION. READS ONLY.
#
# TWO ARMS, BECAUSE ONE NUMBER PROVES NOTHING. "The pill was still there after fifteen seconds" says
# nothing until you know what it does when nobody touches it, so this measures the untouched dwell in
# a run of its own and only then the hovered one. Same build, same state, one difference.
#
# IT MOVES WITH SendInput, AND THAT IS THE WHOLE REASON THIS FILE EXISTS. The first version used
# SetCursorPos and measured 2.64 s against 2.13 s - no hover effect - on a feature that works. A
# warped cursor puts nothing on the input queue, and WinUI raises PointerEntered from that queue, so
# the pointer sat inside the window and the window was never told a pointer had arrived. Driven with
# SendInput the same build gives 2.91 s against never-within-fifteen-seconds. A defect was filed
# against a healthy product on the first reading and withdrawn on the second.
#
# THE CHECK THAT WOULD HAVE CAUGHT IT IS NOT ABOUT THE API. The overlay already supports being dragged
# with a real mouse, so pointer events demonstrably reach it, and a result implying they never arrive
# contradicted a shipping feature. A reading that disagrees with something already known to work is
# the reading to distrust first.
#
# IT MOVES SOMEBODY'S POINTER. The position is read before and put back afterwards, and the pointer
# only ever travels to the target and home again.
param(
    [Parameter(Mandatory = $true)][string] $AppExe,
    [Parameter(Mandatory = $true)][string] $DataDirectory,
    [string] $OverlayState = 'warning',
    [string] $WindowTitleMatch = 'dictation status',
    [int] $SettleMilliseconds = 900,
    [int] $CeilingSeconds = 15
)

$ErrorActionPreference = 'Stop'
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class RealMouse {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
  [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, INPUT[] i, int size);
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT {
    public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
  }
  [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public MOUSEINPUT mi; }

  // MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE, in the 0..65535 virtual-desktop space SendInput uses.
  public static void MoveTo(int x, int y) {
    int w = GetSystemMetrics(78); // SM_CXVIRTUALSCREEN
    int h = GetSystemMetrics(79); // SM_CYVIRTUALSCREEN
    var input = new INPUT[1];
    input[0].type = 0;
    input[0].mi.dx = (int)((x * 65535.0) / (w - 1));
    input[0].mi.dy = (int)((y * 65535.0) / (h - 1));
    input[0].mi.dwFlags = 0x0001 | 0x8000;
    SendInput(1, input, Marshal.SizeOf(typeof(INPUT)));
  }
}
"@

if (-not [RealMouse]::SetProcessDpiAwarenessContext([IntPtr](-4))) {
    throw "Could not take per-monitor DPI awareness."
}

function Find-Pill([int] $ownerPid) {
    $script:found = [IntPtr]::Zero
    $callback = [RealMouse+EnumProc] {
        param($h, $p)
        $owner = 0
        $null = [RealMouse]::GetWindowThreadProcessId($h, [ref] $owner)
        if ($owner -ne $ownerPid -or -not [RealMouse]::IsWindowVisible($h)) { return $true }
        $title = New-Object System.Text.StringBuilder 200
        $null = [RealMouse]::GetWindowTextW($h, $title, $title.Capacity)
        if ($title.ToString() -notmatch $WindowTitleMatch) { return $true }
        $script:found = $h
        return $false
    }
    $null = [RealMouse]::EnumWindows($callback, [IntPtr]::Zero)
    return $script:found
}

function Measure-Dwell([bool] $hover) {
    Get-Process -Name 'EnviousWispr.App' -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2
    $env:ENVIOUSWISPR_DATA_DIRECTORY = $DataDirectory
    $env:ENVIOUSWISPR_UAT_OVERLAY_STATE = $OverlayState
    $env:ENVIOUSWISPR_UAT_EXIT_AFTER_MILLISECONDS = ($CeilingSeconds + 12) * 1000
    $app = Start-Process -FilePath $AppExe -PassThru
    Start-Sleep -Milliseconds $SettleMilliseconds

    $pill = Find-Pill $app.Id
    if ($pill -eq [IntPtr]::Zero) {
        Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
        throw "No window matching '$WindowTitleMatch' at settle time, so there is no dwell to measure. A notice with no action dismisses itself quickly - shorten the settle."
    }

    $rect = New-Object RealMouse+RECT
    $null = [RealMouse]::GetWindowRect($pill, [ref] $rect)
    $x = [int](($rect.Left + $rect.Right) / 2)
    $y = [int](($rect.Top + $rect.Bottom) / 2)

    if ($hover) {
        for ($i = 1; $i -le 12; $i++) {
            [RealMouse]::MoveTo(
                [int]($rect.Left - 60 + (($x - $rect.Left + 60) * $i / 12)),
                [int]($rect.Top - 60 + (($y - $rect.Top + 60) * $i / 12)))
            Start-Sleep -Milliseconds 30
        }
    }

    $clock = [Diagnostics.Stopwatch]::StartNew()
    $step = 0
    while ($clock.Elapsed.TotalSeconds -lt $CeilingSeconds) {
        Start-Sleep -Milliseconds 200
        if ($hover) {
            $step = ($step + 1) % 2
            [RealMouse]::MoveTo($x + ($step * 5), $y + ($step * 3))
        }

        if ((Find-Pill $app.Id) -eq [IntPtr]::Zero) {
            $clock.Stop()
            Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
            return [Math]::Round($clock.Elapsed.TotalSeconds, 2)
        }
    }

    $clock.Stop()
    Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
    return -1
}

$startPoint = New-Object RealMouse+POINT
$null = [RealMouse]::GetCursorPos([ref] $startPoint)
try {
    $plain = Measure-Dwell $false
    Write-Host ("control  : {0}" -f $(if ($plain -lt 0) { "never within $CeilingSeconds s" } else { "$plain s" }))
    $hovered = Measure-Dwell $true
    Write-Host ("hovered  : {0}" -f $(if ($hovered -lt 0) { "never within $CeilingSeconds s" } else { "$hovered s" }))
    Write-Host ""
    if ($hovered -lt 0 -and $plain -ge 0) {
        Write-Host "HOVER PAUSES IT."
    } else {
        Write-Host "NO HOVER EFFECT. Check the pointer is landing on the target before believing this."
    }
}
finally {
    [RealMouse]::MoveTo($startPoint.X, $startPoint.Y)
    Get-Process -Name 'EnviousWispr.App' -ErrorAction SilentlyContinue | Stop-Process -Force
}
