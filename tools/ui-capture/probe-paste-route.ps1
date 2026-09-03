# Ask another application what a fourth delivery route would have to work with. READS ONLY.
#
# WHY A PROBE AND NOT A BRANCH. #77 wants macOS's `menuPaste` tier on Windows: invoke the app's own
# Paste command where the aimed write and the synthetic keystroke both fail. Whether that is reachable
# at all is a fact about Word, Excel and OneNote rather than about our design, and getting it wrong
# presses an unknown control inside somebody's document. So this INVOKES NOTHING. It reports:
#   - what the focused element claims to be, and whether the aimed write could ever apply to it
#   - every Document element and every command named Paste in that window, with its patterns
#   - whether the app's Paste button claims to be enabled with an empty clipboard and with a full one
#
# RUNS IN THE LOGGED-IN SESSION, against an app that is already open. Point it at a process id.
param(
    [Parameter(Mandatory = $true)][int] $ProcessId,
    [int] $MaxDepth = 12,
    [switch] $SkipClipboardCheck
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName WindowsBase
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class ForegroundInterop {
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr window, out int processId);
  [DllImport("kernel32.dll")] public static extern int GetCurrentThreadId();
  [DllImport("user32.dll")] public static extern bool AttachThreadInput(int from, int to, bool attach);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr window);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
}
"@

# THE FOREGROUND HAS TO BE TAKEN, NOT ASKED FOR, AND THE FAILURE IS SILENT. Windows refuses
# SetForegroundWindow from a process that does not already own the foreground: it returns, nothing
# moves, and every probe afterwards faithfully describes whatever DOES own it. Three separate readings
# here confidently described a WinUI navigation item while claiming to describe Word - our own app,
# MINIMISED and still the foreground window, which is a state a minimised window is allowed to be in
# when nothing else was activated. SwitchToThisWindow is refused the same way. Attaching to the
# current foreground thread's input queue for the length of the call is what actually works.
$target = (Get-Process -Id $ProcessId).MainWindowHandle
if ($target -eq 0) {
    throw "Process $ProcessId has no main window."
}

$foreground = [ForegroundInterop]::GetForegroundWindow()
$ignoredProcess = 0
$foregroundThread = [ForegroundInterop]::GetWindowThreadProcessId($foreground, [ref] $ignoredProcess)
$thisThread = [ForegroundInterop]::GetCurrentThreadId()
$null = [ForegroundInterop]::AttachThreadInput($thisThread, $foregroundThread, $true)
try {
    $null = [ForegroundInterop]::ShowWindow($target, 9)
    $null = [ForegroundInterop]::BringWindowToTop($target)
    $null = [ForegroundInterop]::SetForegroundWindow($target)
}
finally {
    $null = [ForegroundInterop]::AttachThreadInput($thisThread, $foregroundThread, $false)
}

Start-Sleep -Seconds 2
$landed = [ForegroundInterop]::GetForegroundWindow()
$landedProcess = 0
$null = [ForegroundInterop]::GetWindowThreadProcessId($landed, [ref] $landedProcess)
if ($landedProcess -ne $ProcessId) {
    # SAID OUT LOUD RATHER THAN MEASURED ANYWAY. A reading taken against the wrong window looks exactly
    # like a reading taken against the right one.
    Write-Host "THE FOREGROUND DID NOT MOVE. It belongs to process $landedProcess, not $ProcessId."
    Write-Host 'Nothing below would describe the app you asked about. Stopping.'
    exit 3
}

function Get-PatternNames($element) {
    $names = @()
    foreach ($pattern in $element.GetSupportedPatterns()) {
        $names += ($pattern.ProgrammaticName -replace 'PatternIdentifiers\.Pattern$', '')
    }

    return ($names | Sort-Object)
}

$focused = [System.Windows.Automation.AutomationElement]::FocusedElement
$info = $focused.Current
Write-Host '=== THE FOCUSED ELEMENT ==='
Write-Host "  name         : $($info.Name)"
Write-Host "  control type : $($info.ControlType.ProgrammaticName)"
Write-Host "  class        : $($info.ClassName)"
Write-Host "  framework    : $($info.FrameworkId)"
Write-Host "  process      : $($info.ProcessId)"
Write-Host "  patterns     : $((Get-PatternNames $focused) -join ', ')"

# THE SAME QUESTION `CanUseDirectValueWrite` ASKS. If this says no, the aimed write was never going to
# serve this target and the only thing between a dictation and ClipboardOnly is the synthetic keystroke.
$valuePattern = $null
if ($focused.TryGetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern, [ref] $valuePattern)) {
    Write-Host ("  aimed write  : ValuePattern present, read-only={0}{1}" -f
        $valuePattern.Current.IsReadOnly,
        $(if ($valuePattern.Current.IsReadOnly) { ' - NOT ELIGIBLE' } else { ' - ELIGIBLE' }))
} else {
    Write-Host '  aimed write  : NOT ELIGIBLE - no ValuePattern on the focused element'
}

$root = [System.Windows.Automation.AutomationElement]::RootElement
$condition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
$window = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
$walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker

$script:documentCount = 0
$script:pasteCount = 0
function Search($element, $depth) {
    if ($depth -gt $script:MaxDepth) { return }
    $child = $walker.GetFirstChild($element)
    while ($null -ne $child) {
        $current = $child.Current
        $rectangle = $current.BoundingRectangle
        $where = if ($current.IsOffscreen -or $rectangle.IsEmpty) {
            'OFFSCREEN'
        } else {
            "$([int]$rectangle.X),$([int]$rectangle.Y) $([int]$rectangle.Width)x$([int]$rectangle.Height)"
        }

        if ($current.ControlType.ProgrammaticName -eq 'ControlType.Document') {
            $script:documentCount++
            Write-Host ("  DOCUMENT [{0}] '{1}' class={2} | {3} | patterns: {4}" -f
                $depth, $current.Name, $current.ClassName, $where,
                ((Get-PatternNames $child) -join ', '))
        }

        if ($current.Name -match '(?i)paste') {
            $script:pasteCount++
            Write-Host ("  PASTE [{0}] '{1}' | {2} | id={3} | {4} | patterns: {5}" -f
                $depth, $current.Name, $current.ControlType.ProgrammaticName,
                $current.AutomationId, $where, ((Get-PatternNames $child) -join ', '))
        }

        Search $child ($depth + 1)
        $child = $walker.GetNextSibling($child)
    }
}

Write-Host ''
Write-Host "=== DOCUMENTS AND PASTE COMMANDS IN '$($window.Current.Name)' ==="
Search $window 0
Write-Host ''
Write-Host "$($script:documentCount) document element(s), $($script:pasteCount) paste command(s)."

function Find-PasteButton($element, $depth) {
    if ($depth -gt $script:MaxDepth) { return $null }
    $child = $walker.GetFirstChild($element)
    while ($null -ne $child) {
        if ($child.Current.AutomationId -eq 'Paste' -and
            $child.Current.ControlType.ProgrammaticName -eq 'ControlType.Button') {
            return $child
        }

        $found = Find-PasteButton $child ($depth + 1)
        if ($null -ne $found) { return $found }
        $child = $walker.GetNextSibling($child)
    }

    return $null
}

if ($SkipClipboardCheck) {
    return
}

# WHETHER THE BUTTON CAN BE BELIEVED, WHICH DECIDES THE WHOLE DESIGN. A route that invokes a command
# has to know beforehand whether it is available and afterwards whether it fired. Word and Excel both
# report their Paste button ENABLED with the clipboard cleared, so IsEnabled is neither. Measured here
# rather than assumed, because the answer decides whether delivery can ever be confirmed from the
# control - and if it cannot, an invoke must be confirmed from the DOCUMENT or fall through, the way
# `DirectWriteUnverified` already does.
Write-Host ''
Write-Host '=== DOES ITS PASTE BUTTON KNOW WHETHER IT CAN PASTE? ==='
$restore = $null
if ([System.Windows.Forms.Clipboard]::ContainsText()) {
    $restore = [System.Windows.Forms.Clipboard]::GetText()
}

try {
    [System.Windows.Forms.Clipboard]::Clear()
    Start-Sleep -Seconds 2
    $button = Find-PasteButton $window 0
    if ($null -eq $button) {
        Write-Host '  no button with AutomationId=Paste in this window'
        return
    }

    Write-Host ("  clipboard empty    : enabled={0} offscreen={1}" -f
        $button.Current.IsEnabled, $button.Current.IsOffscreen)

    [System.Windows.Forms.Clipboard]::SetText('EnviousWispr paste-route probe')
    Start-Sleep -Seconds 2
    $button = Find-PasteButton $window 0
    Write-Host ("  clipboard has text : enabled={0} offscreen={1}" -f
        $button.Current.IsEnabled, $button.Current.IsOffscreen)
}
finally {
    # PUT BACK WHAT WAS BORROWED. The first version of this left the founder's clipboard holding the
    # probe's own string. Text only: this cannot restore an image or a file list, and says so rather
    # than pretending the clipboard is unchanged.
    if ($null -ne $restore) {
        [System.Windows.Forms.Clipboard]::SetText($restore)
        Write-Host '  the clipboard text that was there before has been put back'
    } else {
        [System.Windows.Forms.Clipboard]::Clear()
        Write-Host '  the clipboard held no text before this ran, and is now empty'
        Write-Host '  ANYTHING THAT WAS NOT TEXT - an image, a file list - IS GONE'
    }
}
