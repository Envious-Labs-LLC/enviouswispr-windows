using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace EnviousWispr.Services.Input;

/// <summary>
/// Identifies Chromium browser address bars using a two-factor check: browser process allowlist
/// before any UI Automation walk, followed by UIA omnibox control verification without WebArea ancestry.
/// </summary>
internal static class WindowsBrowserAddressBarDetector
{
    internal const string ChromiumOmniboxClassSuffix = "OmniboxViewViews";
    internal const string RootWebAreaMarker = "RootWebArea";
    internal const int MaxAncestorWalkDepth = 64;

    private static readonly HashSet<string> RecognizedChromiumProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome",
        "msedge",
        "brave",
        "vivaldi",
        "opera",
        "chromium",
    };

    internal readonly record struct AncestorFacts(
        string? ControlTypeName,
        string? ClassName,
        string? AutomationId,
        string? AriaRole = null);

    /// <summary>
    /// Cheap process-level gate run before touching any UI Automation element.
    /// </summary>
    internal static bool IsRecognizedChromiumBrowserProcess(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        var normalized = processName.Trim();
        if (normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^4];
        }

        return RecognizedChromiumProcesses.Contains(normalized);
    }

    internal static bool IsRootWebAreaAncestor(AncestorFacts ancestor) =>
        string.Equals(ancestor.ClassName, RootWebAreaMarker, StringComparison.Ordinal) ||
        string.Equals(ancestor.AutomationId, RootWebAreaMarker, StringComparison.Ordinal) ||
        string.Equals(ancestor.ControlTypeName, "Document", StringComparison.Ordinal) ||
        string.Equals(ancestor.AriaRole, "document", StringComparison.OrdinalIgnoreCase);

    internal static bool HasRootWebAreaAncestor(
        IEnumerable<AncestorFacts> ancestors,
        int maxDepth = MaxAncestorWalkDepth)
    {
        var depth = 0;
        foreach (var ancestor in ancestors)
        {
            if (depth >= maxDepth)
            {
                return true;
            }

            if (IsRootWebAreaAncestor(ancestor))
            {
                return true;
            }

            depth++;
        }

        return false;
    }

    internal static bool MatchesChromiumOmniboxSignature(
        string? processName,
        bool isEditControl,
        string? className,
        bool hasRootWebAreaAncestor) =>
        IsRecognizedChromiumBrowserProcess(processName) &&
        isEditControl &&
        !string.IsNullOrEmpty(className) &&
        className.EndsWith(ChromiumOmniboxClassSuffix, StringComparison.Ordinal) &&
        !hasRootWebAreaAncestor;

    internal static bool MatchesChromiumOmniboxSignature(
        string? processName,
        bool isEditControl,
        string? className,
        IEnumerable<AncestorFacts> ancestors,
        int maxDepth = MaxAncestorWalkDepth)
    {
        if (!IsRecognizedChromiumBrowserProcess(processName) ||
            !isEditControl ||
            string.IsNullOrEmpty(className) ||
            !className.EndsWith(ChromiumOmniboxClassSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        return !HasRootWebAreaAncestor(ancestors, maxDepth);
    }

    /// <summary>
    /// Evaluates a live UIA element inside the adapter's UIA boundary.
    /// </summary>
    internal static bool IsChromiumAddressBar(AutomationElement? element, string? processName)
    {
        if (element is null || !IsRecognizedChromiumBrowserProcess(processName))
        {
            return false;
        }

        try
        {
            var current = element.Current;
            var isEdit = current.ControlType == ControlType.Edit;
            var className = current.ClassName ?? string.Empty;
            if (!isEdit || !className.EndsWith(ChromiumOmniboxClassSuffix, StringComparison.Ordinal))
            {
                return false;
            }

            var walker = TreeWalker.RawViewWalker;
            var root = AutomationElement.RootElement;
            var cursor = element;

            for (var depth = 0; depth < MaxAncestorWalkDepth; depth++)
            {
                var parent = walker.GetParent(cursor);
                if (parent is null || Automation.Compare(parent, root))
                {
                    return true;
                }

                var parentCurrent = parent.Current;
                var facts = new AncestorFacts(
                    parentCurrent.ControlType == ControlType.Document ? "Document" : null,
                    parentCurrent.ClassName,
                    parentCurrent.AutomationId);
                if (IsRootWebAreaAncestor(facts))
                {
                    return false;
                }

                cursor = parent;
            }

            // Fail closed when the ancestor chain exceeds the bounded depth limit.
            return false;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }
}
