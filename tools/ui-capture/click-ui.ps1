# Click a real point inside a named control. RUNS IN THE LOGGED-IN SESSION.
#
# THE ACCESSIBILITY LAYER CANNOT ANSWER "DOES A CLICK LAND HERE". Invoking a control through UI
# Automation asks the control to act; it does not go through hit-testing, so it succeeds on an area
# no pointer can reach. That difference is not academic: a toggle stretched to fill its row reported
# a 447-unit rectangle while the clickable area was still 154, because WinUI does not hit-test space
# whose background is null. The tree said fixed and it was not.
#
# THE RECTANGLE IS RESOLVED AT CLICK TIME, NOT PASSED IN. The window lands in a different place on
# every launch, so a coordinate taken from a previous run misses - measured, by six pixels, which is
# the most misleading kind of miss because the click still happens and nothing changes.
param(
    [Parameter(Mandatory = $true)][int] $ProcessId,
    [Parameter(Mandatory = $true)][string] $Name,
    [ValidateSet('left', 'centre', 'right')][string] $Where = 'centre',
    [int] $Inset = 16,
    # PAST THE CONTROL ON PURPOSE. Asking whether the ROW around a control is clickable means
    # clicking beside it, which is by definition outside its own rectangle.
    [int] $OffsetX = 0,
    # AND DOWN THE ROW AS WELL AS ACROSS IT. A ToggleSwitch is two stacked bands: its HEADER, which
    # is the sentence a person reads and aims at, and the switch below it. Both live inside the one
    # rectangle the automation tree reports, so a click at the vertical centre cannot say which band
    # it landed in. Measured from the TOP edge when given, rather than from the centre.
    [Nullable[int]] $FromTop = $null,
    # WHEN THE LABEL AND THE CONTROL SHARE A NAME, WHICH IS THE POINT OF A LABELLED ROW. A settings
    # row now carries its label as its own Text element and gives the switch the same words as its
    # accessible name, so a search by name alone matches two elements and takes whichever comes
    # first. Naming the control type is what makes the target unambiguous.
    [string] $OfType = '',
    [int] $TimeoutSeconds = 10
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -Namespace UiCapture -Name Click -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
'@

$root = [System.Windows.Automation.AutomationElement]::RootElement
$clauses = @(
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)),
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)))
if ($OfType) {
    $kind = [System.Windows.Automation.ControlType]::$OfType
    if (-not $kind) { throw "There is no control type named `"$OfType`"." }
    $clauses += New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $kind)
}
$condition = New-Object System.Windows.Automation.AndCondition($clauses)

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$element = $null
while ((Get-Date) -lt $deadline -and -not $element) {
    $element = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if (-not $element) { Start-Sleep -Milliseconds 250 }
}
if (-not $element) { throw "No control named `"$Name`" to click in process $ProcessId." }

$rect = $element.Current.BoundingRectangle
if ($rect.IsEmpty -or $rect.Width -le 0) { throw "The control named `"$Name`" has no on-screen rectangle." }

$y = if ($null -ne $FromTop) { [int] ($rect.Y + $FromTop) } else { [int] ($rect.Y + ($rect.Height / 2)) }
$x = switch ($Where) {
    'left'   { [int] ($rect.X + $Inset) }
    'right'  { [int] ($rect.X + $rect.Width - $Inset) }
    default  { [int] ($rect.X + ($rect.Width / 2)) }
}

$x += $OffsetX
[void][UiCapture.Click]::SetCursorPos($x, $y)
Start-Sleep -Milliseconds 120
[UiCapture.Click]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 60
[UiCapture.Click]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
"clicked the $Where of `"$Name`" at $x,$y (rect $([int]$rect.X),$([int]$rect.Y) $([int]$rect.Width)x$([int]$rect.Height))"
