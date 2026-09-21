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
RUNTIME_ID_HELPER = "    private static string RuntimeId(AutomationElement element) =>" + NL

# Each mutation: a name, and the (anchor, replacement) edits that plant one escape.
MUTATIONS: dict[str, list[tuple[str, str]]] = {
    "foreach-outside-boundary": [(FOCUS_READ, "        foreach (var child in Automation(() => element.FindAll(TreeScope.Children, Condition.TrueCondition)))" + NL + "        {" + NL + "            _ = child.GetHashCode();" + NL + "        }" + NL + NL + FOCUS_READ)],
    "foreach-untyped-inside-boundary": [(FOCUS_READ, "        _ = Automation(() => { var hashes = 0; foreach (var child in element.FindAll(TreeScope.Children, Condition.TrueCondition)) { hashes ^= child.GetHashCode(); } return hashes; });" + NL + FOCUS_READ)],
    "enumerator-escapes-boundary": [(FOCUS_READ, "        var walk = Automation(() => element.FindAll(TreeScope.Children, Condition.TrueCondition).GetEnumerator());" + NL + "        _ = walk.MoveNext() ? walk.Current?.GetHashCode() : null;" + NL + FOCUS_READ)],
    "untyped-property-read": [(FOCUS_READ, "        var label = Automation(() => element.GetCurrentPropertyValue(AutomationElement.LabeledByProperty));" + NL + "        _ = label?.GetHashCode();" + NL + FOCUS_READ)],
    "untyped-cached-property-read": [(FOCUS_READ, "        var label = Automation(() => element.GetCachedPropertyValue(AutomationElement.LabeledByProperty));" + NL + "        _ = label?.GetHashCode();" + NL + FOCUS_READ)],
    "untyped-out-result-escapes": [(FOCUS_READ, "        var raw = Automation(() => element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) ? pattern : null);" + NL + "        _ = raw?.GetHashCode();" + NL + FOCUS_READ)],
    "handle-in-inherited-record": [(FOCUS_READ, "        var cached = Automation(() => new Held { Element = element });" + NL + "        _ = cached.GetHashCode();" + NL + FOCUS_READ), (RUNTIME_ID_HELPER, "    private record HeldBase" + NL + "    {" + NL + "        public AutomationElement? Element { get; init; }" + NL + "    }" + NL + NL + "    private sealed record Held : HeldBase;" + NL + NL + RUNTIME_ID_HELPER)],
    "handle-in-generic-base": [(FOCUS_READ, "        var cached = Automation(() => new HeldElement { Value = element });" + NL + "        _ = cached.GetHashCode();" + NL + FOCUS_READ), (RUNTIME_ID_HELPER, "    private record HeldOf<T>" + NL + "    {" + NL + "        public T? Value { get; init; }" + NL + "    }" + NL + NL + "    private sealed record HeldElement : HeldOf<AutomationElement>;" + NL + NL + RUNTIME_ID_HELPER)],
    "handle-in-tuple": [(FOCUS_READ, "        var cached = Automation(() => (element, target.Value));" + NL + "        _ = cached.GetHashCode();" + NL + FOCUS_READ)],
    "handle-in-array": [(FOCUS_READ, "        var cached = Automation(() => new[] { element });" + NL + "        _ = cached[0].GetHashCode();" + NL + FOCUS_READ)],
    "handle-in-record": [(FOCUS_READ, "        var cached = Automation(() => new Held(element));" + NL + "        _ = cached.GetHashCode();" + NL + FOCUS_READ), (RUNTIME_ID_HELPER, "    private sealed record Held(AutomationElement Element);" + NL + NL + RUNTIME_ID_HELPER)],
    "erased-inside-boundary-cast": [(FOCUS_READ, "        var cached = Automation(() => (object)element);" + NL + "        _ = cached.GetHashCode();" + NL + FOCUS_READ)],
    "erased-inside-boundary-as": [(FOCUS_READ, "        var cached = Automation(() => element as object);" + NL + "        _ = cached?.GetHashCode();" + NL + FOCUS_READ)],
    "erased-inside-boundary-context": [(FOCUS_READ, "        var cached = Automation<object>(() => element);" + NL + "        _ = cached.GetHashCode();" + NL + FOCUS_READ)],
    "handle-into-object-parameter": [(FOCUS_READ, "        _ = ElementHash(element);" + NL + FOCUS_READ), (RUNTIME_ID_HELPER, "    private static int ElementHash(object value) => value.GetHashCode();" + NL + NL + RUNTIME_ID_HELPER)],
    "handle-into-generic-parameter": [(FOCUS_READ, "        _ = Hash(element);" + NL + FOCUS_READ), (RUNTIME_ID_HELPER, "    private static int Hash<T>(T value) where T : notnull => value.GetHashCode();" + NL + NL + RUNTIME_ID_HELPER)],
    "boundary-result-as-object": [(FOCUS_READ, "        object cached = Automation(() => element);" + NL + "        _ = cached.GetHashCode();" + NL + FOCUS_READ)],
    "upcast-virtual-dispatch": [(FOCUS_READ, "        _ = ((object)element).GetHashCode();" + NL + FOCUS_READ)],
    "expression-tree-call": [(FOCUS_READ, "        _ = System.Linq.Expressions.Expression.Lambda<Func<int[]>>(System.Linq.Expressions.Expression.Call(System.Linq.Expressions.Expression.Constant(element), \"GetRuntimeId\", null)).Compile()();" + NL + FOCUS_READ)],
    "handle-aliased": [(FOCUS_READ, "        var alias = element;" + NL + "        _ = alias;" + NL + FOCUS_READ)],
    "handle-to-external-api": [(FOCUS_READ, "        _ = string.Join(\",\", element);" + NL + FOCUS_READ)],
    "dynamic-dispatch": [(FOCUS_READ, "        _ = ((dynamic)element).GetRuntimeId();" + NL + FOCUS_READ)],
    "reflection-call": [(FOCUS_READ, "        _ = typeof(AutomationElement).GetMethod(nameof(AutomationElement.GetRuntimeId))!.Invoke(element, null);" + NL + FOCUS_READ)],
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
CAUGHT_MARKERS = ("outside the boundary", "captured instead of called", "Assert.Single() Failure", "dynamically dispatched call", "reflective call", "handle used outside the boundary", "handle erased", "untyped UI Automation result", "enumerated outside the boundary")


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
