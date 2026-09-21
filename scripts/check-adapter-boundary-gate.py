"""Mutation check for the UI Automation boundary gate.

The gate (WindowsTextDeliverySafetyTests.EveryUiAutomationCallInTheAdapterIsMadeThroughTheBoundary)
claims that every UI Automation call in WindowsTextTargetAdapter executes inside Automation(...).
A gate is worth exactly the escapes it catches, so this script applies each escape below to the
adapter in turn, runs the gate, requires it to fail for the right reason, and restores the file
whatever happens. Run from any directory:

    py scripts/check-adapter-boundary-gate.py

Exit code 0 when every mutation is caught; 1 otherwise. The adapter is left as it was found.
"""

from __future__ import annotations

import os
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
ADAPTER = ROOT / "src" / "Production" / "EnviousWispr.Services" / "Input" / "WindowsTextTargetAdapter.cs"
TESTS = ROOT / "src" / "Production" / "EnviousWispr.Architecture.Tests" / "EnviousWispr.Architecture.Tests.csproj"
GATE = "FullyQualifiedName~EveryUiAutomationCallInTheAdapterIsMadeThroughTheBoundary"
DOTNET = os.environ.get("DOTNET", os.path.expandvars(r"%USERPROFILE%\.dotnet\dotnet.exe"))
NL = "\n"

# Lines the mutations hang off; each must occur exactly once in the adapter.
EXISTING_READ = "            var existing = Automation(() => valuePattern.Current.Value) ?? string.Empty;" + NL
CARET_READ = "        var caret = Automation(() => ReadCaret(textPattern, options));" + NL
VERIFIED = "            var verified = string.Equals(readBack, replacement, StringComparison.Ordinal);" + NL
ELEMENT_READ = "        var element = Automation(static () => AutomationElement.FocusedElement);" + NL
FOCUS_READ = "        var focus = Automation(() => new FocusedElementFacts(" + NL
TEXT_USING = "using System.Windows.Automation.Text;" + NL

# Each mutation: a name, and the (anchor, replacement) edits that plant one escape.
MUTATIONS: dict[str, list[tuple[str, str]]] = {
    "query-expression-deferred": [(FOCUS_READ, "        var later = Automation(() => from item in Enumerable.Range(0, 1) select element.GetRuntimeId());" + NL + "        _ = later.ToArray();" + NL + FOCUS_READ)],
    "captured-method-group": [(FOCUS_READ, "        var getId = Automation(() => (Func<int[]>)element.GetRuntimeId);" + NL + "        _ = getId();" + NL + FOCUS_READ)],
    "nested-lambda-inherits": [(FOCUS_READ, "        var later = Automation(() => new Func<AutomationElement?>(() => AutomationElement.FocusedElement));" + NL + "        _ = later();" + NL + FOCUS_READ)],
    "local-function-inside": [(FOCUS_READ, "        _ = Automation(() => { AutomationElement? Read() => AutomationElement.FocusedElement; return Read(); });" + NL + FOCUS_READ)],
    "async-boundary": [(FOCUS_READ, "        _ = Automation(async () => { await Task.Yield(); return AutomationElement.FocusedElement; });" + NL + FOCUS_READ)],
    "helper-captured-inside": [(FOCUS_READ, "        var name = Automation(() => (Func<AutomationElement, string>)RuntimeId);" + NL + FOCUS_READ)],
    "qualified-helper-call": [(FOCUS_READ, "        _ = WindowsTextTargetAdapter.RuntimeId(element);" + NL + FOCUS_READ)],
    "helper-as-delegate": [(FOCUS_READ, "        Func<AutomationElement, string> identity = RuntimeId;" + NL + FOCUS_READ)],
    "using-static-identifier": [(TEXT_USING, TEXT_USING + "using static System.Windows.Automation.AutomationElement;" + NL), (FOCUS_READ, "        _ = FocusedElement;" + NL + FOCUS_READ)],
    "conditional-SetValue": [(VERIFIED, "            valuePattern?.SetValue(replacement);" + NL + VERIFIED)],
    "read-in-argument": [(EXISTING_READ, EXISTING_READ + "            var stamped = Automation(valuePattern.Current.Value.ToString);" + NL)],
    "split-read": [(EXISTING_READ, EXISTING_READ + "            var current = valuePattern.Current;" + NL + "            var leaked = current.Value;" + NL)],
    "inside-other-call": [(EXISTING_READ, EXISTING_READ + "            var leaked = string.IsNullOrEmpty(valuePattern.Current.Value);" + NL)],
    "range-out-of-ReadCaret": [(CARET_READ, "        var document = textPattern.DocumentRange;" + NL + CARET_READ)],
    "duplicate-SetValue": [(VERIFIED, "            valuePattern.SetValue(replacement);" + NL + VERIFIED)],
    "bare-FocusedElement": [(ELEMENT_READ, "        var probe = AutomationElement.FocusedElement;" + NL + ELEMENT_READ)],
}

# What the gate says when it catches an escape; any other failure is the wrong reason.
CAUGHT_MARKERS = ("outside the boundary", "captured instead of called", "Assert.Single() Failure")


def run_gate() -> str:
    completed = subprocess.run(
        [DOTNET, "test", str(TESTS), "-c", "Release", "--filter", GATE],
        capture_output=True,
        text=True,
        check=False,
    )
    return completed.stdout + completed.stderr


def main() -> int:
    original = ADAPTER.read_text(encoding="utf-8")
    failed = 0
    try:
        for name, edits in MUTATIONS.items():
            mutated = original
            for anchor, replacement in edits:
                if mutated.count(anchor) != 1:
                    print(f"{name}: anchor not found once - the adapter has moved on; update this script")
                    return 1
                mutated = mutated.replace(anchor, replacement)
            ADAPTER.write_text(mutated, encoding="utf-8", newline=NL)
            output = run_gate()
            caught = "Failed!" in output and any(marker in output for marker in CAUGHT_MARKERS)
            reason = next((line.strip()[:160] for line in output.splitlines() if any(marker in line for marker in CAUGHT_MARKERS) or "error CS" in line or "error CA" in line), "")
            print(f"{name}: {'caught' if caught else 'MISSED'} {reason}")
            failed += 0 if caught else 1
    finally:
        ADAPTER.write_text(original, encoding="utf-8", newline=NL)
    restored = ADAPTER.read_text(encoding="utf-8") == original
    print(f"adapter restored: {restored}")
    return 0 if failed == 0 and restored else 1


if __name__ == "__main__":
    sys.exit(main())
