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
# IT PUTS A WINDOW ON SOMEBODY'S SCREEN. Run it when nobody is using the machine. The app is closed
# again at the end of every shot, and it is pointed at a scratch data directory so it cannot touch
# the logged-in user's settings, history or custom words.
param(
    [string] $Shot = "main-window",
    [string] $OverlayState = "",
    [string] $AppExe = "",
    [string] $OutputDirectory = "",
    [string] $DataDirectory = "",
    [int] $SettleMilliseconds = 4000,
    [int] $TimeoutSeconds = 90
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

# A FILENAME LEAF, NEVER A PATH. $Shot is joined into output paths and one of those paths is handed
# to Remove-Item, so a value containing ..\ would delete matching files outside the capture
# directory. Refusing anything but a plain leaf is cheaper than sanitising and cannot be argued with.
if ($Shot -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$') {
    throw "-Shot must be a plain filename leaf: letters, digits, dot, dash and underscore, 1 to 64 characters. Got '$Shot'."
}
if ($OverlayState -and $OverlayState -notmatch '^[a-z]{1,20}$') {
    throw "-OverlayState must be a single lowercase word. Got '$OverlayState'."
}

if (-not $AppExe) {
    # FOUND RATHER THAN SPELLED OUT. The output path carries the platform, the target framework and
    # a runtime identifier, and a first draft that wrote all three by hand was wrong about the
    # runtime identifier and reported the app as unbuilt when it was one folder deeper. Newest wins,
    # so a fresh build is photographed rather than yesterday's.
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
if (-not (Test-Path -LiteralPath $OutputDirectory)) {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
}
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path

$shotPath = Join-Path $OutputDirectory "$Shot.png"
$logPath = Join-Path $OutputDirectory "$Shot.log"
$markerPath = Join-Path $OutputDirectory "$Shot.ok"
$pidPath = Join-Path $OutputDirectory "$Shot.pid"
Remove-Item -LiteralPath $shotPath, $logPath, $markerPath, $pidPath -ErrorAction SilentlyContinue

# A NAME NOBODY ELSE OWNS. A fixed name plus -Force replaces whatever task already had it, and the
# unregister at the end would then delete a task this script never created - including a concurrent
# run of itself.
$taskName = "EnviousWispr-UiCapture-$([guid]::NewGuid().ToString('N'))"

$runner = Join-Path $PSScriptRoot 'session-run.ps1'
$arguments = @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$runner`"",
    '-AppExe', "`"$AppExe`"",
    '-OutputDirectory', "`"$OutputDirectory`"",
    '-DataDirectory', "`"$DataDirectory`"",
    '-Shot', $Shot,
    '-SettleMilliseconds', $SettleMilliseconds
)
if ($OverlayState) { $arguments += @('-OverlayState', $OverlayState) }

$registered = $false
try {
    $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ($arguments -join ' ')
    $principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 5)
    Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Settings $settings | Out-Null
    $registered = $true
    Start-ScheduledTask -TaskName $taskName

    # WAIT ON THE MARKER, NOT ON THE TASK STATE OR THE PNG. A task reports Ready the moment
    # powershell.exe exits, which happens whether the capture succeeded, threw, or never found a
    # desktop; and a PNG exists even when it is a photograph of an empty desktop. The runner writes
    # the marker last, only after it saw the app on screen.
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline -and -not (Test-Path -LiteralPath $markerPath)) {
        Start-Sleep -Milliseconds 500
    }
} finally {
    # CLEANUP RUNS EVEN WHEN THE WAIT DID NOT. Deleting a task does NOT stop the program it started,
    # which Microsoft documents plainly, so the task is stopped first and the app is stopped by the
    # pid the runner wrote to disk - this session cannot see the logged-in session's processes.
    if ($registered) {
        try { Stop-ScheduledTask -TaskName $taskName -ErrorAction Stop } catch { "   cleanup: $($_.Exception.Message)" }
    }
    if (Test-Path -LiteralPath $pidPath) {
        $strayPid = (Get-Content -LiteralPath $pidPath -ErrorAction SilentlyContinue | Select-Object -First 1)
        if ($strayPid -match '^\d+$') {
            $stray = Get-Process -Id ([int] $strayPid) -ErrorAction SilentlyContinue
            if ($stray -and $stray.ProcessName -eq 'EnviousWispr.App') {
                $stray | Stop-Process -Force -ErrorAction SilentlyContinue
                "   cleanup: stopped stray app pid $strayPid"
            }
        }
    }
    if ($registered) {
        try { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction Stop }
        catch { "   cleanup: $($_.Exception.Message)" }
    }
}

if (Test-Path -LiteralPath $logPath) { Get-Content -LiteralPath $logPath | ForEach-Object { "   $_" } }

if (-not (Test-Path -LiteralPath $markerPath)) {
    throw "No capture was confirmed within $TimeoutSeconds seconds. The log above, if any, says how far it got. A PNG may exist and must not be trusted: the marker is written only after the app was seen on screen. Nobody logged in means no desktop to photograph."
}

$size = (Get-Item -LiteralPath $shotPath).Length
"CAPTURED $shotPath ($([math]::Round($size / 1KB)) KB)"
