# Read the app's own accessibility tree. RUNS IN THE LOGGED-IN SESSION.
#
# A PHOTOGRAPH SAYS WHAT SOMETHING LOOKS LIKE, NOT WHAT IT IS. Two open issues turn on questions a
# picture cannot answer: whether the pill's action button can actually be reached, and whether a
# settings row commits the toggle or only the switch does. Both are properties of the automation
# tree - the same tree a screen reader walks - so this asks it directly.
#
# IT IS ALSO THE HONEST WAY TO FIND A CLICK TARGET. Reading coordinates off a screenshot by eye and
# clicking them is how a test starts passing for the wrong reason; a name and a rectangle from the
# tree is what the control actually claims about itself.
param(
    [Parameter(Mandatory = $true)][int] $ProcessId,
    [int] $MaxDepth = 14
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$root = [System.Windows.Automation.AutomationElement]::RootElement
$condition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
$windows = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $condition)

function Describe($element, $depth) {
    $info = $element.Current
    $rect = $info.BoundingRectangle
    $pad = ' ' * ($depth * 2)
    $bounds = if ($rect.IsEmpty -or $rect.Width -le 0) { 'offscreen' }
              else { "$([int]$rect.X),$([int]$rect.Y) $([int]$rect.Width)x$([int]$rect.Height)" }

    # THE TOGGLE STATE AND THE CLICKABLE POINT ARE THE WHOLE REASON THIS EXISTS. A control that
    # offers no clickable point cannot be pressed, whatever it looks like.
    $extra = ''
    $toggle = $null
    if ($element.TryGetCurrentPattern(
            [System.Windows.Automation.TogglePattern]::Pattern, [ref] $toggle)) {
        $extra += " toggle=$($toggle.Current.ToggleState)"
    }
    $point = New-Object System.Windows.Point
    if ($element.TryGetClickablePoint([ref] $point)) {
        $extra += " click=$([int]$point.X),$([int]$point.Y)"
    } else {
        $extra += ' click=NONE'
    }
    if (-not $info.IsEnabled) { $extra += ' DISABLED' }
    if ($info.IsOffscreen) { $extra += ' OFFSCREEN' }

    "$pad$($info.ControlType.ProgrammaticName -replace 'ControlType\.', '') `"$($info.Name)`" [$bounds]$extra"

    if ($depth -lt $MaxDepth) {
        foreach ($child in $element.FindAll(
                [System.Windows.Automation.TreeScope]::Children,
                [System.Windows.Automation.Condition]::TrueCondition)) {
            Describe $child ($depth + 1)
        }
    }
}

foreach ($window in $windows) {
    Describe $window 0
}
