param(
    [string]$AppExe = "",
    [string]$RepoRoot = ""
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
}
if ([string]::IsNullOrWhiteSpace($AppExe)) {
    $AppExe = Join-Path $RepoRoot "src\EnviousWispr\bin\Release\net8.0-windows\EnviousWispr.exe"
}
$result = Join-Path $RepoRoot "tools\synth-test\uat-result.txt"
$appLog = [System.IO.Path]::Combine([System.IO.Path]::GetDirectoryName($AppExe), "enviouswispr.log")
$transcript = Join-Path $RepoRoot "tools\synth-test\uat-transcript.txt"
Remove-Item $result, $transcript -Force -ErrorAction SilentlyContinue

try {
    if (-not [Environment]::UserInteractive) { throw "UAT task is not running in an interactive Windows session" }
    if (-not (Test-Path $AppExe)) { throw "Published app not found at $AppExe" }

    $existing = Get-CimInstance Win32_Process -Filter "Name = 'EnviousWispr.exe'" |
        Where-Object { $_.ExecutablePath -eq $AppExe }
    if ($existing) {
        $app = Get-Process -Id $existing.ProcessId -ErrorAction Stop
        $logBefore = 0
    } else {
        $logBefore = if (Test-Path $appLog) { (Get-Item $appLog).Length } else { 0 }
        $app = Start-Process $AppExe -PassThru
    }

    $session = (Get-Process -Id $app.Id -ErrorAction Stop).SessionId
    if ($session -eq 0) { throw "App launched in invisible Windows session 0" }

    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 500
        $logText = ""
        if (Test-Path $appLog) {
            $allLogText = Get-Content $appLog -Raw
            if ($null -ne $allLogText) {
                $logText = if ($allLogText.Length -ge $logBefore) {
                    $allLogText.Substring($logBefore)
                } else {
                    $allLogText
                }
            }
        }
    } while ((-not $logText.Contains("hotkey F8") -or -not $logText.Contains("EG-1 probe: GREEN")) -and
             [DateTime]::UtcNow -lt $deadline)
    if (-not $logText.Contains("hotkey F8")) { throw "App did not register F8 before timeout" }
    if (-not $logText.Contains("EG-1 probe: GREEN")) { throw "EG-1 did not become green before timeout" }

    $overlayPath = Join-Path $RepoRoot "tools\synth-test\overlay.png"
    & (Join-Path $RepoRoot "tools\synth-test\capture-overlay.ps1") `
        -TargetProcessId $app.Id -OutputPath $overlayPath *>&1 |
        Tee-Object -FilePath $transcript

    $lockScreen = Get-Process LogonUI -ErrorAction SilentlyContinue |
        Where-Object { $_.SessionId -eq $session }
    if ($lockScreen) {
        @(
            "UAT BLOCKED"
            "timestamp=$([DateTimeOffset]::Now.ToString('o'))"
            "reason=Windows is locked, so Windows correctly blocks synthetic keyboard input"
            "interactive=$([Environment]::UserInteractive)"
            "appPid=$($app.Id)"
            "sessionId=$session"
            "overlay=$overlayPath"
        ) | Set-Content $result
        return
    }

    & (Join-Path $RepoRoot "tools\synth-test\e2e-synthetic.ps1") -Configuration Release `
        -AppDir ([System.IO.Path]::GetDirectoryName($AppExe)) *>&1 |
        Tee-Object -FilePath $transcript -Append

    @(
        "UAT PASS"
        "timestamp=$([DateTimeOffset]::Now.ToString('o'))"
        "interactive=$([Environment]::UserInteractive)"
        "appPid=$($app.Id)"
        "sessionId=$session"
        "appExe=$AppExe"
        "transcript=$transcript"
    ) | Set-Content $result
}
catch {
    @(
        "UAT FAIL"
        "timestamp=$([DateTimeOffset]::Now.ToString('o'))"
        "error=$($_.Exception.Message)"
        "transcript=$transcript"
    ) | Set-Content $result
    throw
}
