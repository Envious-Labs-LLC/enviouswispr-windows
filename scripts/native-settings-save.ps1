<#
.SYNOPSIS
  GeneralSavePersistsAndRendersCommittedValues (plan-2 step 12): the actual Save button, pressed in the
  running app through the accessibility layer, on a logged-on Windows desk.

.DESCRIPTION
  The portable proofs run SettingsPresenter.SaveGeneralAsync directly; a WinUI click handler cannot run
  under xunit. This launches the production app on a fresh, temporary data directory, opens the Clipboard
  settings page, flips "Copy to the clipboard instead of pasting" through its TogglePattern, presses "Save
  settings" through its InvokePattern, and then reads two things back: settings.json on disk
  (Preferences.CopyInsteadOfPaste must be true) and the toggle as the window re-rendered it after the
  save (its TogglePattern state must be On, and the operation bar must say "Settings saved"). The app is
  then told to exit through its UAT exit switch and must exit cleanly with ApplicationCleanShutdown in
  its log.

  Everything goes through UI Automation - no coordinates, no mouse - so a control that moved or renamed
  fails loudly rather than passing for the wrong reason. Record the verdict line in
  docs/reliability/native-journey-evidence.md with the build it was taken on.

.PARAMETER Root
  The checkout whose Release x64 app build to run. Defaults to the parent of this script's folder.
#>
param(
    [string] $Root = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$app = Join-Path $Root 'src\Production\EnviousWispr.App\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\EnviousWispr.App.exe'
if (-not (Test-Path $app)) { throw "Build the app (Release, x64) first: $app" }
if (Get-Process EnviousWispr.App -ErrorAction SilentlyContinue) {
    throw 'EnviousWispr.App is running. Close it from its tray menu first; this script does not stop it.'
}

$data = Join-Path ([System.IO.Path]::GetTempPath()) "EnviousWispr-settings-save-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force $data | Out-Null

function Start-App {
    param([string] $ExitAfterMilliseconds)
    $psi = New-Object System.Diagnostics.ProcessStartInfo($app)
    $psi.UseShellExecute = $false
    $psi.Environment['ENVIOUSWISPR_DATA_DIRECTORY'] = $data
    $psi.Environment['ENVIOUSWISPR_MODEL_DIRECTORY'] = (Join-Path $Root 'models')
    $psi.Environment['ENVIOUSWISPR_UAT_EXIT_AFTER_MILLISECONDS'] = $ExitAfterMilliseconds
    return [System.Diagnostics.Process]::Start($psi)
}

# A FRESH PROFILE OPENS ON ONBOARDING, NOT ON THE NAVIGATION. The app is run once to write its default
# settings file, that file is marked as onboarded - the one field a first run leaves false - and the
# app is run again on it, as a second launch would be.
$seed = Start-App -ExitAfterMilliseconds '3000'
if (-not $seed.WaitForExit(30000)) { $seed.Kill($true); throw 'The seeding launch did not exit.' }
$settingsPath = Join-Path $data 'settings.json'
$seeded = Get-Content $settingsPath -Raw | ConvertFrom-Json
$seeded.hasCompletedOnboarding = $true
$seeded | ConvertTo-Json -Depth 12 | Set-Content $settingsPath -Encoding utf8

$process = Start-App -ExitAfterMilliseconds '30000'

function Find-Element {
    param([string] $Name, [int] $TimeoutSeconds = 20)
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $byProcess = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
    $byName = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    $condition = New-Object System.Windows.Automation.AndCondition($byProcess, $byName)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $element = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($element) { return $element }
        Start-Sleep -Milliseconds 250
    }
    throw "No control named `"$Name`" appeared in the app within $TimeoutSeconds seconds."
}

function Invoke-Element {
    param([System.Windows.Automation.AutomationElement] $Element)
    $pattern = $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

function Select-Element {
    param([System.Windows.Automation.AutomationElement] $Element)
    $pattern = $Element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $pattern.Select()
}

function Get-ToggleState {
    param([System.Windows.Automation.AutomationElement] $Element)
    $pattern = $Element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    return $pattern.Current.ToggleState
}

$verdict = [ordered]@{ case = 'GeneralSavePersistsAndRendersCommittedValues'; passed = $false }
try {
    # THE WINDOW, THEN THE CLIPBOARD PAGE: the navigation item is selected through its own pattern.
    $null = Find-Element -Name 'EnviousWispr navigation' -TimeoutSeconds 30
    Select-Element (Find-Element -Name 'Clipboard')
    $toggle = Find-Element -Name 'Copy to the clipboard instead of pasting'
    $before = Get-ToggleState $toggle
    if ("$before" -ne 'Off') { throw "The fresh profile's toggle should start Off; it reads $before." }
    ($toggle.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)).Toggle()
    Start-Sleep -Milliseconds 300
    if ("$(Get-ToggleState $toggle)" -ne 'On') { throw 'The toggle did not flip to On.' }

    Invoke-Element (Find-Element -Name 'Save settings')

    # PERSISTED: the file a launch would read.
    $deadline = (Get-Date).AddSeconds(10)
    $persisted = $false
    while ((Get-Date) -lt $deadline -and -not $persisted) {
        if (Test-Path $settingsPath) {
            $json = Get-Content $settingsPath -Raw | ConvertFrom-Json
            $persisted = $json.preferences.copyInsteadOfPaste -eq $true
        }
        if (-not $persisted) { Start-Sleep -Milliseconds 250 }
    }
    if (-not $persisted) { throw 'settings.json did not record copyInsteadOfPaste = true within 10 seconds of Save.' }

    # RENDERED: the window re-applies the stored settings to its controls after a save; the toggle
    # must still read On from the tree, and the operation bar must carry the saved title.
    $rendered = "$(Get-ToggleState (Find-Element -Name 'Copy to the clipboard instead of pasting'))"
    if ($rendered -ne 'On') { throw "After the save the toggle renders as $rendered, not On." }
    $null = Find-Element -Name 'Settings saved' -TimeoutSeconds 10

    $verdict.persisted = $true
    $verdict.rendered = $rendered
    $verdict.message = 'Settings saved'
}
finally {
    $exited = $process.WaitForExit(45000)
    $verdict.exitedCleanly = $exited -and $process.ExitCode -eq 0
    $log = Join-Path $data 'diagnostics\app.jsonl'
    $verdict.cleanShutdownLogged = (Test-Path $log) -and ((Get-Content $log -Raw) -match 'ApplicationCleanShutdown')
    if (-not $exited) { $process.Kill($true) }
    $verdict.passed = $verdict.persisted -and $verdict.exitedCleanly -and $verdict.cleanShutdownLogged
    try { Remove-Item -Recurse -Force $data -ErrorAction Stop } catch { }
    ($verdict | ConvertTo-Json -Compress)
}

if (-not $verdict.passed) { exit 2 }
