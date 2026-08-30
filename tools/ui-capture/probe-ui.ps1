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
Add-Type -AssemblyName WindowsBase
Add-Type -AssemblyName System.Windows.Forms

$root = [System.Windows.Automation.AutomationElement]::RootElement
$condition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
$windows = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $condition)

function Describe($element, $depth, $clip) {
    $info = $element.Current
    $rect = $info.BoundingRectangle
    $pad = ' ' * ($depth * 2)

    # THE WORD SAYS WHAT WAS MEASURED, WHICH IS NOT THE SAME AS VISIBLE. This reported "visible",
    # and it cannot know that: it measures the screen, the window and any scrollable ancestor, and
    # it does NOT see an ordinary Grid or Border clip, another window sitting on top, or a monitor
    # that is switched off. A control behind a dialog would have read as visible. The name now
    # carries its own limit, and every line ends with what was not measured.
    #
    # VISIBILITY IS MEASURED, NOT ASKED FOR. IsOffscreen answers a narrower question than it sounds
    # like: UI Automation calls a control onscreen when any part of it is, so a button with two
    # pixels showing below a scroll viewport reports exactly the same as one sitting in the middle
    # of the window. Trusting it turned a partially clipped control into proof of visibility. The
    # rectangle is intersected with the window and the virtual screen instead, and a control that
    # only partly survives says so.
    if ($rect.IsEmpty -or $rect.Width -le 0 -or $info.IsOffscreen) {
        $bounds = 'offscreen'
        $state = 'OFFSCREEN'
    } else {
        $bounds = "$([int]$rect.X),$([int]$rect.Y) $([int]$rect.Width)x$([int]$rect.Height)"
        $visible = $rect
        $visible.Intersect($clip)
        if ($visible.IsEmpty -or $visible.Width -le 0 -or $visible.Height -le 0) {
            $state = 'OFFSCREEN'
        } elseif ([int]$visible.Width -lt [int]$rect.Width -or [int]$visible.Height -lt [int]$rect.Height) {
            $state = "PARTIALLY_OUTSIDE_KNOWN_CLIP_BOUNDS, $([int]$visible.Width)x$([int]$visible.Height) survives"
        } else {
            $state = 'WITHIN_KNOWN_CLIP_BOUNDS'
        }
    }

    # WHAT THE CONTROL OFFERS, WHICH IS THE PART A PICTURE CANNOT SHOW. A clickable point is
    # reported as UNAVAILABLE rather than NONE, because failing to produce one often means the
    # window is obscured rather than that the control cannot be pressed - the title bar's own
    # Minimize and Close report the same thing.
    $extra = ''
    $toggle = $null
    if ($element.TryGetCurrentPattern(
            [System.Windows.Automation.TogglePattern]::Pattern, [ref] $toggle)) {
        $extra += " toggle=$($toggle.Current.ToggleState)"
    }
    $patterns = @()
    foreach ($pair in @(
            @('invoke', [System.Windows.Automation.InvokePattern]::Pattern),
            @('toggle', [System.Windows.Automation.TogglePattern]::Pattern),
            @('selection-item', [System.Windows.Automation.SelectionItemPattern]::Pattern),
            @('scroll', [System.Windows.Automation.ScrollPattern]::Pattern))) {
        $found = $null
        if ($element.TryGetCurrentPattern($pair[1], [ref] $found)) { $patterns += $pair[0] }
    }
    if ($patterns) { $extra += " does=$($patterns -join ',')" }
    if ($info.IsKeyboardFocusable) { $extra += ' focusable' }

    $point = New-Object System.Windows.Point
    if ($element.TryGetClickablePoint([ref] $point)) {
        $extra += " clickPoint=$([int]$point.X),$([int]$point.Y)"
    } else {
        $extra += ' clickPoint=UNAVAILABLE'
    }
    if (-not $info.IsEnabled) { $extra += ' DISABLED' }

    "$pad$($info.ControlType.ProgrammaticName -replace 'ControlType\.', '') `"$($info.Name)`" [$bounds] $state$extra"

    $children = $element.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.Condition]::TrueCondition)
    if ($depth -ge $MaxDepth) {
        # SAID OUT LOUD, because a tree that simply stops looks like a tree that ended.
        if ($children.Count -gt 0) { "$pad  ... TRUNCATED at depth $MaxDepth, $($children.Count) child element(s) not shown" }
        return
    }

    # A SCROLL VIEWPORT CLIPS WHAT IS INSIDE IT, and that is the clipping that hides a first-run
    # button. Anything scrollable narrows the box its descendants are judged against.
    $childClip = $clip
    $scroll = $null
    if (-not $rect.IsEmpty -and $element.TryGetCurrentPattern(
            [System.Windows.Automation.ScrollPattern]::Pattern, [ref] $scroll)) {
        $childClip = $clip
        $childClip.Intersect($rect)
    }
    foreach ($child in $children) { Describe $child ($depth + 1) $childClip }
}

$screen = New-Object System.Windows.Rect(
    [System.Windows.Forms.SystemInformation]::VirtualScreen.X,
    [System.Windows.Forms.SystemInformation]::VirtualScreen.Y,
    [System.Windows.Forms.SystemInformation]::VirtualScreen.Width,
    [System.Windows.Forms.SystemInformation]::VirtualScreen.Height)

"clipBasis=screen,window,scroll-ancestors; occlusion-and-other-clips=UNMEASURED"

foreach ($window in $windows) {
    # EACH WINDOW CLIPS ITS OWN CONTENTS, and the screen clips the window.
    $clip = $screen
    $windowRect = $window.Current.BoundingRectangle
    if (-not $windowRect.IsEmpty) { $clip.Intersect($windowRect) }
    Describe $window 0 $clip
}
