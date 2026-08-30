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
    [ValidateRange(0, 28000)]
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
$failPath = Join-Path $OutputDirectory "$Shot.fail"
# CLEARED LOUDLY, AND VERIFIED GONE. A silent clear leaves a locked file in place, and the next run
# then reads the PREVIOUS run's verdict as its own - the worst possible failure for a tool whose
# whole job is telling you what it saw.
foreach ($stale in @($shotPath, $logPath, $markerPath, $pidPath, $failPath)) {
    if (Test-Path -LiteralPath $stale) { Remove-Item -LiteralPath $stale -ErrorAction Stop }
}
foreach ($control in @($markerPath, $failPath)) {
    if (Test-Path -LiteralPath $control) {
        throw "Could not clear $control before starting. A stale verdict would be read as this run's."
    }
}

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

# DECLARED BEFORE THE TRY, because finally runs and THEN the script terminates on an unhandled
# error - so a check written after the try/finally never executes on that path, and every cleanup
# failure vanished exactly when something had already gone wrong.
$registered = $false
$cleanupFailures = @()
$runFailure = $null
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
    # EITHER MARKER ENDS THE WAIT. Watching only for success meant a run that had already failed,
    # and knew why within a second, still cost the full timeout before reporting one.
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline -and
           -not (Test-Path -LiteralPath $markerPath) -and
           -not (Test-Path -LiteralPath $failPath)) {
        Start-Sleep -Milliseconds 500
    }
} catch {
    $runFailure = $_.Exception.Message
} finally {
    # THREE STAGES, EACH PROTECTED ON ITS OWN, AND UNREGISTER ALWAYS ATTEMPTED. A single try around
    # all three meant one failure skipped the rest, and a swallowed unregister failure let the run
    # print CAPTURED while its task was still registered. Deleting a task does NOT stop the program
    # it started, which Microsoft documents plainly, so the order is: stop the task, stop the app,
    # then remove the task - and any failure is carried out and reported rather than discarded.
    # ONLY ON A RUN THAT DID NOT SIGNAL SUCCESS. The runner writes its marker AFTER closing the app
    # politely; stopping its task on a successful run killed it mid-shutdown, so the polite close
    # never happened and the app was force-killed anyway - the harness defeating its own fix.
    if ($registered -and -not (Test-Path -LiteralPath $markerPath)) {
        try { Stop-ScheduledTask -TaskName $taskName -ErrorAction Stop }
        catch { $cleanupFailures += "stop task: $($_.Exception.Message)" }

        try {
            if (Test-Path -LiteralPath $pidPath) {
                # PID AND START TIME BOTH, because Windows reuses process ids and ProcessName alone
                # would also match a SECOND copy of this app that somebody else is using.
                $recorded = (Get-Content -LiteralPath $pidPath -ErrorAction SilentlyContinue |
                    Select-Object -First 1) -split '\|'
                if ($recorded.Count -eq 2 -and $recorded[0] -match '^\d+$' -and $recorded[1] -match '^\d+$') {
                    $stray = Get-Process -Id ([int] $recorded[0]) -ErrorAction SilentlyContinue
                    if ($stray -and $stray.StartTime.Ticks -eq [long] $recorded[1]) {
                        $stray | Stop-Process -Force -ErrorAction Stop
                        "   cleanup: stopped stray app pid $($recorded[0])"
                    }
                }
            }
        } catch { $cleanupFailures += "stop app: $($_.Exception.Message)" }
    }

    if ($registered) {
        try { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction Stop }
        catch { $cleanupFailures += "unregister: $($_.Exception.Message)" }

        # VERIFIED GONE, NOT ASSUMED GONE. An unregister that reports nothing and leaves the task
        # behind is how a machine accumulates one scheduled task per screenshot.
        # -ErrorAction Stop, so "the task is gone" cannot be confused with "the check itself
        # failed". SilentlyContinue reports both as absence, which is the reassuring answer.
        try {
            if (Get-ScheduledTask -TaskName $taskName -ErrorAction Stop) {
                $cleanupFailures += "the task $taskName is still registered"
            }
        } catch {
            # MATCHED ON THE ERROR ID, NOT THE EXCEPTION TYPE. "No such task" arrives as
            # CimJobException, which derives from SystemException rather than CimException - so a
            # typed catch for CimException never fires and every SUCCESSFUL run reported a cleanup
            # failure, suppressed CAPTURED and exited non-zero. The error id is the stable name for
            # this outcome.
            if ($_.FullyQualifiedErrorId -ne 'CmdletizationQuery_NotFound_TaskName,Get-ScheduledTask') {
                $cleanupFailures += "could not verify the task was removed: $($_.Exception.Message)"
            }
        }
    }
}

if (Test-Path -LiteralPath $logPath) { Get-Content -LiteralPath $logPath | ForEach-Object { "   $_" } }

# EVERY REASON IS COLLECTED AND THROWN ONCE. Throwing at the first one hid the rest: a run that
# failed in the desktop session reported that and never mentioned the scheduled task it had also
# failed to remove, so the leftovers went unnoticed until somebody opened Task Scheduler.
$parts = @()
$sawSuccess = Test-Path -LiteralPath $markerPath
$sawFailure = Test-Path -LiteralPath $failPath

if ($sawSuccess -and $sawFailure) {
    $parts += "both a success and a failure verdict are present, so neither can be trusted"
} elseif ($sawFailure) {
    $reason = Get-Content -LiteralPath $failPath -Raw -ErrorAction SilentlyContinue
    if (-not $reason) { $reason = 'no reason was recorded' }
    $parts += "the capture failed in the desktop session: $($reason.Trim())"
} elseif (-not $sawSuccess -and -not $runFailure) {
    $parts += "no capture was confirmed within $TimeoutSeconds seconds. The log above, if any, says how far it got. A PNG may exist and must not be trusted: the marker is written only after the app was seen on screen and cleanly closed. Nobody logged in means no desktop to photograph."
}

if ($runFailure) { $parts += $runFailure }
# A LEFTOVER TASK IS A FAILED RUN, however good the photograph is.
$parts += $cleanupFailures

if ($parts.Count -gt 0) { throw ($parts -join '; ') }

$size = (Get-Item -LiteralPath $shotPath).Length
"CAPTURED $shotPath ($([math]::Round($size / 1KB)) KB)"
