<#
.SYNOPSIS
  The two native cases of plan-2 step 10, run on a logged-on Windows desk: NativeExitClosesMicrophoneAndWorkers
  and NativeDeliveryExercisesThreeRoutes.

.DESCRIPTION
  Portable tests cannot establish native behaviour. These runs use the production app, the production
  WASAPI capture, the production runtime worker supervisors and the production WindowsTextTargetAdapter,
  against the controlled target windows in tools/delivery-target-uat and the reviewed public fixtures.
  Nothing is downloaded: the model packs must already be under models/.

  NativeExitClosesMicrophoneAndWorkers: the four standard journeys (Parakeet and Whisper through the
  virtual cable, the synthetic quick tap with live preview, escape recovery), each required to end with
  the app exited cleanly, ApplicationCleanShutdown in its log, and no runtime worker it started still
  running.

  NativeDeliveryExercisesThreeRoutes: the synthetic quick tap delivered into the controlled target in
  each of its three modes, the harness reading the route the adapter took from where the words landed
  and from the log: edit -> UiAutomationValue, caret-start -> ClipboardPaste, password -> ClipboardOnly.

  The verdicts are printed as one line per run; record them in docs/reliability/native-journey-evidence.md
  with the build they were taken on. Close the desk app first; this script refuses to run beside one.

.PARAMETER Root
  The checkout whose build to run. Defaults to the parent of this script's folder.

.PARAMETER SkipVirtualCable
  Leave out the two VB-Audio cable journeys on a machine without the cable.
#>
param(
    [string] $Root = (Split-Path -Parent $PSScriptRoot),
    [switch] $SkipVirtualCable
)

$ErrorActionPreference = 'Continue'
$harness = Join-Path $Root 'tools\app-journey-uat\bin\x64\Release\net10.0-windows10.0.26100.0\EnviousWispr.AppJourney.Uat.exe'
if (-not (Test-Path $harness)) {
    throw "Build tools/app-journey-uat (Release, x64) and tools/delivery-target-uat (Release) first: $harness"
}

$runs = @()
if (-not $SkipVirtualCable) {
    $runs += @{ case = 'NativeExitClosesMicrophoneAndWorkers'; name = 'live-cable-parakeet'; args = @('--live-microphone', '--english-parakeet', '--virtual-cable') }
    $runs += @{ case = 'NativeExitClosesMicrophoneAndWorkers'; name = 'live-cable-whisper'; args = @('--live-microphone', '--virtual-cable') }
}
$runs += @{ case = 'NativeExitClosesMicrophoneAndWorkers'; name = 'synthetic-quick-tap-preview'; args = @('--synthetic-hotkey', '--quick-tap', '--live-preview') }
$runs += @{ case = 'NativeExitClosesMicrophoneAndWorkers'; name = 'escape-recovery'; args = @('--escape-recovery') }
$runs += @{ case = 'NativeDeliveryExercisesThreeRoutes'; name = 'route-edit'; args = @('--synthetic-hotkey', '--quick-tap', '--english-parakeet', '--target-mode', 'edit') }
$runs += @{ case = 'NativeDeliveryExercisesThreeRoutes'; name = 'route-caret-start'; args = @('--synthetic-hotkey', '--quick-tap', '--english-parakeet', '--target-mode', 'caret-start') }
$runs += @{ case = 'NativeDeliveryExercisesThreeRoutes'; name = 'route-password'; args = @('--synthetic-hotkey', '--quick-tap', '--english-parakeet', '--target-mode', 'password') }
$runs += @{ case = 'UnverifiedDirectWriteNeverPastes'; name = 'route-unverified-write'; args = @('--synthetic-hotkey', '--quick-tap', '--english-parakeet', '--target-mode', 'unverified-write') }

# NEVER FORCE-STOPPED. A running EnviousWispr.App may be the user's, mid-dictation or mid-write; the
# journeys under test are exactly the protocol that ends one properly, and killing one from here would
# bypass it. Close it yourself first (its tray menu), then run this.
if (Get-Process EnviousWispr.App -ErrorAction SilentlyContinue) {
    throw 'EnviousWispr.App is running. Close it from its tray menu first; this script does not stop it.'
}
$failed = 0
Set-Location $Root

# THE TARGET'S RECEIPT INSTRUMENT IS PROVED BEFORE IT IS TRUSTED: the controlled target's own settle
# self-test sends itself a Ctrl+V with the clipboard emptied (a paste that changes nothing) and posts a
# settle request right behind it; the settled receipt must already count the paste.
$target = Join-Path $Root 'tools\delivery-target-uat\bin\Release\net10.0-windows10.0.26100.0\EnviousWispr.Delivery.Target.Uat.exe'
$selfTestReceipt = Join-Path ([System.IO.Path]::GetTempPath()) "EnviousWispr-settle-self-test-$([guid]::NewGuid().ToString('N')).json"
$selfTest = Start-Process -FilePath $target -ArgumentList @('--mode', 'settle-self-test', '--result', $selfTestReceipt) -PassThru -Wait
if ($selfTest.ExitCode -eq 0) {
    "SettledReceiptCountsAQueuedPaste settle-self-test: passed=True receipt=$((Get-Content $selfTestReceipt -Raw).Trim())"
} else {
    $failed++
    "SettledReceiptCountsAQueuedPaste settle-self-test: FAILED exit=$($selfTest.ExitCode)"
}
Remove-Item $selfTestReceipt -ErrorAction SilentlyContinue

foreach ($run in $runs) {
    $lines = & $harness @($run.args) 2>&1
    $code = $LASTEXITCODE
    $verdict = $lines | Where-Object { "$_" -match '^\s*\{' } | Select-Object -Last 1
    if ($code -eq 0 -and $verdict) {
        $j = "$verdict".Trim() | ConvertFrom-Json
        "$($run.case) $($run.name): passed=$($j.passed) exitedCleanly=$($j.appExitedCleanly) strayWorkers=$($j.strayWorkerCount) route=$($j.deliveryRoute) build=$($j.appVersion)"
    } else {
        $failed++
        "$($run.case) $($run.name): FAILED exit=$code"
        $lines | Select-Object -Last 4 | ForEach-Object { "   $_" }
    }
}

exit $failed
