param(
    [string]$AppExe = "C:\Users\saura\Apps\EnviousWispr-Windows-Test\EnviousWispr.exe",
    [string]$RepoRoot = "C:\Users\saura\agent-workspace\enviouswispr-windows",
    [string]$TaskName = "EnviousWispr-UAT"
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $AppExe)) { throw "Published app not found at $AppExe" }

$desktop = [Environment]::GetFolderPath("Desktop")
$shortcutPath = Join-Path $desktop "EnviousWispr Windows Test.lnk"
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $AppExe
$shortcut.WorkingDirectory = [System.IO.Path]::GetDirectoryName($AppExe)
$shortcut.Description = "EnviousWispr Windows test build. Hold F9 to dictate."
$shortcut.Save()

$uatScript = Join-Path $RepoRoot "tools\synth-test\run-interactive-uat.ps1"
$arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$uatScript`" -AppExe `"$AppExe`" -RepoRoot `"$RepoRoot`""
$action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument $arguments
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 5)
Register-ScheduledTask -TaskName $TaskName -Action $action -Principal $principal -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskName $TaskName

Write-Output "Shortcut: $shortcutPath"
Write-Output "Interactive UAT task started: $TaskName"
