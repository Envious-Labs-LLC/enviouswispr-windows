param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$AppDir = "",
    [int]$TimeoutSeconds = 20
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
if ([string]::IsNullOrWhiteSpace($AppDir)) {
    $AppDir = Join-Path $repoRoot "src\EnviousWispr\bin\$Configuration\net8.0-windows"
}
$log = Join-Path $AppDir "enviouswispr.log"
$targetExe = Join-Path $PSScriptRoot "SynthTarget\bin\$Configuration\net8.0-windows\SynthTarget.exe"
$dump = Join-Path $PSScriptRoot "synthtarget-dump.txt"
$phrase = "The quick brown fox jumps over the lazy dog."

if (-not (Test-Path $log)) { throw "App log not found at $log. Start the app first." }
if (-not (Test-Path $targetExe)) { throw "SynthTarget not built at $targetExe" }
$logBefore = (Get-Item $log).Length

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class EnviousWisprTestKeys {
  [DllImport("user32.dll")] public static extern IntPtr SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(uint dwProcessId);
  [DllImport("user32.dll", SetLastError=true)] static extern uint SendInput(uint count, INPUT[] inputs, int size);
  [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public INPUTUNION data; }
  [StructLayout(LayoutKind.Explicit, Size=32)] struct INPUTUNION { [FieldOffset(0)] public KEYBDINPUT keyboard; }
  [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT {
    public ushort virtualKey; public ushort scanCode; public uint flags; public uint time; public UIntPtr extraInfo;
  }
  public static void SendF9(bool keyUp) {
    var keyboard = new KEYBDINPUT { virtualKey = 0x78, flags = keyUp ? 2u : 0u };
    var input = new INPUT { type = 1, data = new INPUTUNION { keyboard = keyboard } };
    var size = Marshal.SizeOf(typeof(INPUT));
    if (SendInput(1, new [] { input }, size) != 1) {
      var error = Marshal.GetLastWin32Error();
      throw new InvalidOperationException("SendInput F9 failed (INPUT size=" + size + ", Win32=" + error + ")");
    }
  }
}
"@

$target = $null
$keyIsDown = $false
try {
    Remove-Item $dump -Force -ErrorAction SilentlyContinue
    $target = Start-Process $targetExe -ArgumentList $dump -PassThru
    $tries = 0
    while ($target.MainWindowHandle -eq [IntPtr]::Zero -and $tries -lt 25) {
        Start-Sleep -Milliseconds 400
        if ($target.HasExited) { throw "SynthTarget exited early" }
        $tries++
    }
    if ($target.MainWindowHandle -eq [IntPtr]::Zero) { throw "SynthTarget window handle never appeared" }

    [void][EnviousWisprTestKeys]::AllowSetForegroundWindow($target.Id)
    [void][EnviousWisprTestKeys]::SetForegroundWindow($target.MainWindowHandle)
    Start-Sleep -Milliseconds 500
    Write-Output ("synthtarget pid: {0}" -f $target.Id)

    [EnviousWisprTestKeys]::SendF9($false)
    $keyIsDown = $true
    Write-Output "F9 down (capture started)"
    Start-Sleep -Milliseconds 800

    $voice = New-Object -ComObject SAPI.SpVoice
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $voice.Speak($phrase) | Out-Null
    Write-Output ("speech playback took {0:N1} s" -f $sw.Elapsed.TotalSeconds)
    Start-Sleep -Milliseconds 600

    [EnviousWisprTestKeys]::SendF9($true)
    $keyIsDown = $false
    Write-Output "F9 up (capture stopped)"

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $text = ""
    do {
        Start-Sleep -Milliseconds 500
        if (Test-Path $dump) {
            $text = Get-Content $dump -Raw
            if ($null -eq $text) { $text = "" }
        }
    } while ((-not $text.ToLowerInvariant().Contains("quick brown fox") -or
              -not $text.ToLowerInvariant().Contains("lazy dog")) -and
             [DateTime]::UtcNow -lt $deadline)

    Write-Output "=== PASTE TARGET CONTENT ==="
    Write-Output $text
    Write-Output "=== END PASTE TARGET ==="

    $targetText = $text.ToLowerInvariant()
    $quickFox = $targetText.Contains("quick brown fox")
    $lazyDog = $targetText.Contains("lazy dog")
    Write-Output ("{0}: paste target contains 'quick brown fox'" -f $(if ($quickFox) { "PASS" } else { "FAIL" }))
    Write-Output ("{0}: paste target contains 'lazy dog'" -f $(if ($lazyDog) { "PASS" } else { "FAIL" }))

    Write-Output "=== APP LOG FOR THIS RUN ==="
    $fs = [System.IO.File]::OpenRead($log)
    try {
        [void]$fs.Seek($logBefore, [System.IO.SeekOrigin]::Begin)
        $sr = [System.IO.StreamReader]::new($fs)
        try { Write-Output $sr.ReadToEnd() } finally { $sr.Dispose() }
    } finally { $fs.Dispose() }
    Write-Output "=== END APP LOG ==="

    if (-not ($quickFox -and $lazyDog)) {
        throw "End-to-end paste failed: the expected phrase did not reach the target text box."
    }
    Write-Output "E2E PASS: speech reached the focused target through F9, ASR, polish, and paste."
}
finally {
    if ($keyIsDown) {
        [EnviousWisprTestKeys]::SendF9($true)
    }
    if ($target -and -not $target.HasExited) {
        try { $target.Kill(); $target.WaitForExit(3000) | Out-Null } catch { }
    }
}
