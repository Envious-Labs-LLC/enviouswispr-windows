# Press one of the app's controls by name. RUNS IN THE LOGGED-IN SESSION.
#
# THROUGH THE ACCESSIBILITY LAYER, NOT THROUGH THE MOUSE. Clicking a coordinate read off a
# screenshot is how a check starts passing for the wrong reason: nothing verifies the pixel belonged
# to the control you meant, and a layout change moves it silently. InvokePattern presses the control
# the same way a screen reader user does, and it fails loudly when the control is absent, disabled,
# or offers no way to be pressed.
#
# IT IS ALSO THE ONLY WAY IN. An open issue asks whether the recording pill's button can be pressed
# at all, given the pill is shown WITHOUT taking focus. That is a question about the control, not
# about where it sits.
param(
    [Parameter(Mandatory = $true)][int] $ProcessId,
    [Parameter(Mandatory = $true)][string] $Name,
    [int] $TimeoutSeconds = 10
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$root = [System.Windows.Automation.AutomationElement]::RootElement
$byProcess = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
$byName = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
$condition = New-Object System.Windows.Automation.AndCondition($byProcess, $byName)

# THE CONTROL MAY NOT EXIST YET. A window that has just been shown takes a moment to publish its
# tree, so this waits rather than reporting a missing control that is merely late.
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$element = $null
while ((Get-Date) -lt $deadline -and -not $element) {
    $element = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if (-not $element) { Start-Sleep -Milliseconds 250 }
}
if (-not $element) {
    throw "No control named `"$Name`" appeared in process $ProcessId within $TimeoutSeconds seconds."
}
if (-not $element.Current.IsEnabled) {
    throw "The control named `"$Name`" is disabled, so pressing it would prove nothing."
}

# WHICHEVER WAY THE CONTROL SAYS IT WORKS. A button is invoked, a navigation destination is
# SELECTED, and a switch is toggled - they are three different patterns, and asking every control to
# be invoked failed on the navigation list with "offers no way to be pressed", which was true of
# Invoke and false of the control. The one that was used is reported, because "pressed Sounds" and
# "selected Sounds" are different claims about what happened.
$invoke = $null
if ($element.TryGetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern, [ref] $invoke)) {
    $invoke.Invoke()
    "invoked `"$Name`""
    return
}

$selection = $null
if ($element.TryGetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern, [ref] $selection)) {
    $selection.Select()
    "selected `"$Name`""
    return
}

$toggle = $null
if ($element.TryGetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern, [ref] $toggle)) {
    $was = $toggle.Current.ToggleState
    $toggle.Toggle()
    "toggled `"$Name`" from $was to $($toggle.Current.ToggleState)"
    return
}

throw "The control named `"$Name`" offers no way to be pressed, selected or toggled. That is the finding, not an error in this script."
