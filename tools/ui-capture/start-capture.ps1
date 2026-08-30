# Take a screenshot of the app from an SSH session, by handing the work to the logged-in desktop.
#
#   powershell -ExecutionPolicy Bypass -File tools\ui-capture\start-capture.ps1 -Shot advisory-pill -OverlayState advisory
#
# THE SCHEDULED TASK IS THE ONLY DOOR BETWEEN THE TWO SESSIONS. An SSH session on Windows is session
# 0, which has no desktop: a WinUI app started there dies in Microsoft.UI.Input.dll with 0xc0000602
# before drawing, and a screen capture taken there returns a blank image with no error. A task
# registered with an INTERACTIVE principal runs as the logged-in user, on their desktop, where both
# of those work. This is the same mechanism tools\synth-test\setup-founder-test.ps1 already uses.
#
# IT PUTS A WINDOW ON SOMEBODY'S SCREEN. Run it when nobody is using the machine. The app is stopped
# again at the end of every shot, and it is pointed at a scratch data directory so it cannot touch
# the logged-in user's settings, history or custom words.
param(
    [string] $Shot = "main-window",
    [string] $OverlayState = "",
    [string] $AppExe = "",
    [string] $OutputDirectory = "",
    [string] $DataDirectory = "",
    [int] $SettleMilliseconds = 4000,
    [int] $TimeoutSeconds = 90,
    [string] $TaskName = "EnviousWispr-UiCapture"
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

if (-not $AppExe) {
    # FOUND RATHER THAN SPELLED OUT. The output path carries the platform, the target framework and
    # a runtime identifier, and a first draft that wrote all three by hand was wrong about the
    # runtime identifier and reported the app as unbuilt when it was sitting one folder deeper.
    # Newest wins, so a fresh build is photographed rather than yesterday's.
    $binRoot = Join-Path $repoRoot 'src\Production\EnviousWispr.App\bin'
    $AppExe = Get-ChildItem -LiteralPath $binRoot -Recurse -Filter 'EnviousWispr.App.exe' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $AppExe -or -not (Test-Path -LiteralPath $AppExe)) {
    throw "No built EnviousWispr.App.exe under src\Production\EnviousWispr.App\bin. Build it first: dotnet build src\Production\EnviousWispr.App\EnviousWispr.App.csproj -c Release -p:Platform=x64"
}
"using $AppExe"
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $env:TEMP 'enviouswispr-ui-capture' }
if (-not $DataDirectory) { $DataDirectory = Join-Path $env:TEMP 'enviouswispr-ui-capture-data' }

$shotPath = Join-Path $OutputDirectory "$Shot.png"
$logPath = Join-Path $OutputDirectory "$Shot.log"
Remove-Item -LiteralPath $shotPath, $logPath -ErrorAction SilentlyContinue

$runner = Join-Path $PSScriptRoot 'session-run.ps1'
$arguments = @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$runner`"",
    '-AppExe', "`"$AppExe`"",
    '-OutputDirectory', "`"$OutputDirectory`"",
    '-DataDirectory', "`"$DataDirectory`"",
    '-Shot', "`"$Shot`"",
    '-SettleMilliseconds', $SettleMilliseconds
)
if ($OverlayState) { $arguments += @('-OverlayState', "`"$OverlayState`"") }

$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ($arguments -join ' ')
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 5)
Register-ScheduledTask -TaskName $TaskName -Action $action -Principal $principal -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskName $TaskName

# WAIT ON THE FILE, NOT ON THE TASK STATE. A task reports Ready the moment powershell.exe exits,
# which happens whether the capture succeeded, threw, or never found a desktop. The PNG existing is
# the only evidence that a picture was actually taken.
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ((Get-Date) -lt $deadline -and -not (Test-Path -LiteralPath $shotPath)) {
    Start-Sleep -Milliseconds 500
}

Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue

if (Test-Path -LiteralPath $logPath) { Get-Content -LiteralPath $logPath | ForEach-Object { "   $_" } }

if (-not (Test-Path -LiteralPath $shotPath)) {
    throw "No capture appeared at $shotPath within $TimeoutSeconds seconds. The log above, if any, says how far it got. Nobody logged in means no desktop to photograph."
}

$size = (Get-Item -LiteralPath $shotPath).Length
"CAPTURED $shotPath ($([math]::Round($size / 1KB)) KB)"
