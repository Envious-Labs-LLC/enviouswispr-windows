using System.Globalization;
using System.Text;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Input;

namespace EnviousWispr.Services.Input;

internal static class WindowsTextDeliveryPolicy
{
    internal static PasteArrivalStatus EvaluateArrival(
        string insertedText,
        string? observedLeft,
        string? observedValue,
        string? previousLeft,
        string? previousValue = null)
    {
        var normalizedExpected = NormalizeArrivalText(insertedText).Trim();
        if (string.IsNullOrEmpty(normalizedExpected))
        {
            return PasteArrivalStatus.Unreadable;
        }

        if (observedLeft is null && observedValue is null)
        {
            return PasteArrivalStatus.Unreadable;
        }

        if (observedLeft is not null)
        {
            if (previousLeft is null && previousValue is null)
            {
                return PasteArrivalStatus.Unreadable;
            }

            var normalizedLeft = NormalizeArrivalText(observedLeft).TrimEnd();
            if (previousLeft is not null)
            {
                var normalizedPreviousLeft = NormalizeArrivalText(previousLeft).TrimEnd();
                var previousAlreadyEndedWithExpected = !string.IsNullOrEmpty(normalizedPreviousLeft) &&
                    normalizedPreviousLeft.EndsWith(normalizedExpected, StringComparison.Ordinal);
                if (normalizedLeft.EndsWith(normalizedExpected, StringComparison.Ordinal) &&
                    (!previousAlreadyEndedWithExpected ||
                     normalizedLeft.Length >= normalizedPreviousLeft.Length + normalizedExpected.Length))
                {
                    return PasteArrivalStatus.Confirmed;
                }
            }
            else if (previousValue is not null)
            {
                var normalizedPrevValue = NormalizeArrivalText(previousValue).TrimEnd();
                if (normalizedLeft.EndsWith(normalizedExpected, StringComparison.Ordinal) &&
                    CountOccurrences(normalizedLeft, normalizedExpected) > CountOccurrences(normalizedPrevValue, normalizedExpected))
                {
                    return PasteArrivalStatus.Confirmed;
                }
            }

            return PasteArrivalStatus.NotObserved;
        }

        // ValuePattern-only controls have no TextPattern caret, so compare against a pre-paste value
        // baseline rather than confirming stale text that was already in the box before the paste.
        if (previousValue is null)
        {
            return PasteArrivalStatus.Unreadable;
        }

        var normalizedValue = NormalizeArrivalText(observedValue!).TrimEnd();
        var normalizedPreviousValue = NormalizeArrivalText(previousValue).TrimEnd();
        return CountOccurrences(normalizedValue, normalizedExpected) > CountOccurrences(normalizedPreviousValue, normalizedExpected)
            ? PasteArrivalStatus.Confirmed
            : PasteArrivalStatus.NotObserved;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle) || string.IsNullOrEmpty(haystack))
        {
            return 0;
        }

        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    internal static string NormalizeArrivalText(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var hasFormatChars = false;
        foreach (var rune in normalized.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format)
            {
                hasFormatChars = true;
                break;
            }
        }

        if (!hasFormatChars)
        {
            return normalized;
        }

        var builder = new StringBuilder(normalized.Length);
        foreach (var rune in normalized.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) != UnicodeCategory.Format)
            {
                builder.Append(rune.ToString());
            }
        }

        return builder.ToString();
    }

    internal static PasteKeystrokeKind ResolveCommitKeystroke(
        CaretContext context,
        WindowsPasteRule commitRule)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(commitRule);
        return context.TargetKind == TextTargetKind.Terminal
            ? PasteKeystrokeKind.ShiftInsert
            : commitRule.Keystroke;
    }

    internal static TextDeliveryRefusalReason RevalidationRefusal(TargetContextResult current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return current.Status switch
        {
            TargetContextStatus.Elevated => TextDeliveryRefusalReason.ElevatedTarget,
            TargetContextStatus.Protected => TextDeliveryRefusalReason.ProtectedField,
            TargetContextStatus.AccessibilityUnavailable => current.RefusalReason != TextDeliveryRefusalReason.None
                ? current.RefusalReason
                : TextDeliveryRefusalReason.AccessibilityUnavailable,
            _ => TextDeliveryRefusalReason.TargetChanged,
        };
    }

    internal static TextDeliveryRefusalReason GuardedPreflight(Func<TextDeliveryRefusalReason> preflight)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        try
        {
            return preflight();
        }
        catch (AutomationRefusalException)
        {
            return TextDeliveryRefusalReason.AccessibilityUnavailable;
        }
    }

    internal static bool ShouldCheckCaretTextUnchanged(
        CaretContext expected,
        bool candidateDeletesDictatedText)
    {
        ArgumentNullException.ThrowIfNull(expected);
        // Honest editable targets always revalidate caret text so mid-commit cursor moves fail closed;
        // screen-derived terminals allow non-deleting insertions to tolerate UIA cell-grid redraws.
        return !expected.IsScreenDerived || candidateDeletesDictatedText;
    }

    internal static bool CaretUnchanged(
        CaretContext expected,
        CaretContext actual,
        bool checkCaretTextUnchanged = true) =>
        expected.Target == actual.Target &&
        string.Equals(expected.FocusedElementId, actual.FocusedElementId, StringComparison.Ordinal) &&
        expected.TargetKind == actual.TargetKind &&
        expected.HasTextContext == actual.HasTextContext &&
        expected.IsScreenDerived == actual.IsScreenDerived &&
        expected.IsUrlBarField == actual.IsUrlBarField &&
        (!checkCaretTextUnchanged || (
            expected.LeftReachedDocumentStart == actual.LeftReachedDocumentStart &&
            expected.RightReachedDocumentEnd == actual.RightReachedDocumentEnd &&
            string.Equals(expected.Left, actual.Left, StringComparison.Ordinal) &&
            string.Equals(expected.Selection, actual.Selection, StringComparison.Ordinal) &&
            string.Equals(expected.Right, actual.Right, StringComparison.Ordinal)));

    internal static bool ShouldRetryColdChromiumFocus(WindowsTextTargetAdapter.FocusedElementFacts? focus) =>
        focus is not null &&
        !focus.HasKeyboardFocus &&
        focus.ClassName.StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal);

    internal static TextDeliveryRefusalReason CompatibilityRefusal(
        TextTargetKind targetKind,
        string text) => targetKind switch
    {
        TextTargetKind.Terminal when text.IndexOfAny(['\r', '\n']) >= 0 =>
            TextDeliveryRefusalReason.UnsafeMultilineTarget,
        TextTargetKind.Game => TextDeliveryRefusalReason.UnsupportedTarget,
        _ => TextDeliveryRefusalReason.None,
    };

    internal static bool CanUseDirectValueWrite(
        CaretContext context,
        TextCommitRequest request,
        WindowsPasteRule rule) =>
        !rule.SkipDirectValueWrite &&
        context.TargetKind == TextTargetKind.StandardEdit &&
        context.SupportsDirectValueWrite &&
        context.DirectValueWriteAtEnd &&
        context.Selection.Length == 0 &&
        request.Text.Text.Length <= request.Options.MaximumDirectValueCharacters;

    internal static bool IsEditableReplacementCandidate(WindowsTextTargetAdapter.FocusedElementFacts focus) =>
        !focus.IsPassword &&
        focus.IsEnabled &&
        focus.IsKeyboardFocusable &&
        (focus.IsControlEdit || focus.IsControlDocument);

    internal static WindowsTextTargetAdapter.FocusEvaluationResult EvaluateFocusedElement(
        TargetWindowId target,
        WindowsTextTargetAdapter.FocusedElementFacts? focus,
        string? win32ChildFocusId,
        bool isExplicitHistoryPaste,
        Func<string?, bool, WindowsPasteRule> resolvePolicy,
        bool allowSameWindowEditableReplacement = true,
        bool? focusProcessMatchesTarget = null)
    {
        ArgumentNullException.ThrowIfNull(resolvePolicy);
        if (focus is null)
        {
            return new WindowsTextTargetAdapter.FocusEvaluationResult(
                new TargetContextResult(
                    TargetContextStatus.AccessibilityUnavailable,
                    RefusalReason: TextDeliveryRefusalReason.AccessibilityUnavailable),
                WindowFallbackOnly: false,
                EffectiveFocusId: string.Empty,
                Rule: null);
        }

        var processMatches = focusProcessMatchesTarget ?? (focus.ProcessId == target.ProcessId);

        if (target.FocusedElementId is null)
        {
            if (!isExplicitHistoryPaste)
            {
                return new WindowsTextTargetAdapter.FocusEvaluationResult(
                    new TargetContextResult(
                        TargetContextStatus.AccessibilityUnavailable,
                        RefusalReason: TextDeliveryRefusalReason.AccessibilityUnavailable),
                    WindowFallbackOnly: false,
                    EffectiveFocusId: string.Empty,
                    Rule: null);
            }

            if (!processMatches)
            {
                return new WindowsTextTargetAdapter.FocusEvaluationResult(
                    new TargetContextResult(
                        TargetContextStatus.AccessibilityUnavailable,
                        RefusalReason: TextDeliveryRefusalReason.AccessibilityUnavailable),
                    WindowFallbackOnly: false,
                    EffectiveFocusId: string.Empty,
                    Rule: null);
            }

            if (focus.IsPassword)
            {
                return new WindowsTextTargetAdapter.FocusEvaluationResult(
                    new TargetContextResult(
                        TargetContextStatus.Protected,
                        EmptyContext(target, string.Empty, TextTargetKind.Unknown),
                        TextDeliveryRefusalReason.ProtectedField),
                    WindowFallbackOnly: false,
                    EffectiveFocusId: string.Empty,
                    Rule: null);
            }

            if (!focus.HasKeyboardFocus)
            {
                return new WindowsTextTargetAdapter.FocusEvaluationResult(
                    new TargetContextResult(
                        TargetContextStatus.TargetChanged,
                        RefusalReason: TextDeliveryRefusalReason.TargetChanged),
                    WindowFallbackOnly: false,
                    EffectiveFocusId: string.Empty,
                    Rule: null);
            }

            var historyRule = resolvePolicy(focus.ClassName, focus.IsControlEdit);
            if (!historyRule.AllowsWindowFallbackWithoutFocusedElement)
            {
                return new WindowsTextTargetAdapter.FocusEvaluationResult(
                    new TargetContextResult(
                        TargetContextStatus.AccessibilityUnavailable,
                        EmptyContext(target, string.Empty, historyRule.TargetKind),
                        TextDeliveryRefusalReason.UnsupportedTarget),
                    WindowFallbackOnly: false,
                    EffectiveFocusId: string.Empty,
                    Rule: null);
            }

            var liveHistoryId = !string.IsNullOrEmpty(focus.RuntimeId)
                ? focus.RuntimeId
                : (win32ChildFocusId ?? string.Empty);
            return new WindowsTextTargetAdapter.FocusEvaluationResult(
                Failure: null,
                WindowFallbackOnly: true,
                EffectiveFocusId: liveHistoryId,
                Rule: historyRule);
        }

        if (target.FocusedElementId.StartsWith(WindowsWindowIdentity.Win32FocusPrefix, StringComparison.Ordinal))
        {
            if (!focus.HasKeyboardFocus)
            {
                return new WindowsTextTargetAdapter.FocusEvaluationResult(
                    new TargetContextResult(
                        TargetContextStatus.TargetChanged,
                        RefusalReason: TextDeliveryRefusalReason.TargetChanged),
                    WindowFallbackOnly: false,
                    EffectiveFocusId: string.Empty,
                    Rule: null);
            }

            if (!processMatches)
            {
                return new WindowsTextTargetAdapter.FocusEvaluationResult(
                    new TargetContextResult(
                        TargetContextStatus.AccessibilityUnavailable,
                        RefusalReason: TextDeliveryRefusalReason.AccessibilityUnavailable),
                    WindowFallbackOnly: false,
                    EffectiveFocusId: string.Empty,
                    Rule: null);
            }

            if (focus.IsPassword)
            {
                var protectedRule = resolvePolicy(focus.ClassName, focus.IsControlEdit);
                var protectedFocusId = !string.IsNullOrEmpty(focus.RuntimeId)
                    ? WindowsWindowIdentity.CombineFocusIdentity(win32ChildFocusId, focus.RuntimeId)
                    : (win32ChildFocusId ?? target.FocusedElementId);
                return new WindowsTextTargetAdapter.FocusEvaluationResult(
                    new TargetContextResult(
                        TargetContextStatus.Protected,
                        EmptyContext(target, protectedFocusId, protectedRule.TargetKind),
                        TextDeliveryRefusalReason.ProtectedField),
                    WindowFallbackOnly: false,
                    EffectiveFocusId: protectedFocusId,
                    Rule: protectedRule);
            }

            var exactWin32Match = string.Equals(win32ChildFocusId, target.FocusedElementId, StringComparison.Ordinal);
            var resolvedWin32Id = !string.IsNullOrEmpty(focus.RuntimeId)
                ? WindowsWindowIdentity.CombineFocusIdentity(win32ChildFocusId, focus.RuntimeId)
                : win32ChildFocusId;
            if (!exactWin32Match &&
                (!allowSameWindowEditableReplacement ||
                 !IsEditableReplacementCandidate(focus) ||
                 string.IsNullOrEmpty(resolvedWin32Id)))
            {
                return new WindowsTextTargetAdapter.FocusEvaluationResult(
                    new TargetContextResult(
                        TargetContextStatus.TargetChanged,
                        RefusalReason: TextDeliveryRefusalReason.TargetChanged),
                    WindowFallbackOnly: false,
                    EffectiveFocusId: string.Empty,
                    Rule: null);
            }

            var effectiveWin32Id = resolvedWin32Id ?? target.FocusedElementId;
            var win32Rule = resolvePolicy(focus.ClassName, focus.IsControlEdit);
            return new WindowsTextTargetAdapter.FocusEvaluationResult(
                Failure: null,
                WindowFallbackOnly: false,
                EffectiveFocusId: effectiveWin32Id,
                Rule: win32Rule);
        }

        if (!processMatches || !focus.HasKeyboardFocus)
        {
            return new WindowsTextTargetAdapter.FocusEvaluationResult(
                new TargetContextResult(
                    TargetContextStatus.TargetChanged,
                    RefusalReason: TextDeliveryRefusalReason.TargetChanged),
                WindowFallbackOnly: false,
                EffectiveFocusId: string.Empty,
                Rule: null);
        }

        var rule = resolvePolicy(focus.ClassName, focus.IsControlEdit);
        if (focus.IsPassword)
        {
            return new WindowsTextTargetAdapter.FocusEvaluationResult(
                new TargetContextResult(
                    TargetContextStatus.Protected,
                    EmptyContext(target, focus.RuntimeId, rule.TargetKind),
                    TextDeliveryRefusalReason.ProtectedField),
                WindowFallbackOnly: false,
                EffectiveFocusId: focus.RuntimeId,
                Rule: rule);
        }

        if (!string.Equals(focus.RuntimeId, target.FocusedElementId, StringComparison.Ordinal) &&
            (!allowSameWindowEditableReplacement ||
             !IsEditableReplacementCandidate(focus) ||
             string.IsNullOrEmpty(focus.RuntimeId)))
        {
            return new WindowsTextTargetAdapter.FocusEvaluationResult(
                new TargetContextResult(
                    TargetContextStatus.TargetChanged,
                    RefusalReason: TextDeliveryRefusalReason.TargetChanged),
                WindowFallbackOnly: false,
                EffectiveFocusId: string.Empty,
                Rule: null);
        }

        return new WindowsTextTargetAdapter.FocusEvaluationResult(
            Failure: null,
            WindowFallbackOnly: false,
            EffectiveFocusId: focus.RuntimeId,
            Rule: rule);
    }

    internal static CaretContext EmptyContext(
        TargetWindowId target,
        string focusedId,
        TextTargetKind targetKind,
        string? baselineValue = null) => new(
        target,
        focusedId,
        targetKind,
        Left: string.Empty,
        Selection: string.Empty,
        Right: string.Empty,
        LeftReachedDocumentStart: false,
        RightReachedDocumentEnd: false,
        HasTextContext: false,
        SupportsDirectValueWrite: false,
        DirectValueWriteAtEnd: false,
        IsScreenDerived: targetKind == TextTargetKind.Terminal,
        BaselineValue: baselineValue);

    internal static WindowsPasteRule ResolveTargetPolicy(
        TargetWindowId target,
        string? elementClassName,
        bool isControlEdit)
    {
        var processName = !string.IsNullOrWhiteSpace(target.ProcessName)
            ? target.ProcessName
            : WindowsWindowIdentity.TryGetProcessName(target.ProcessId);
        var windowClassName = WindowsWindowIdentity.TryGetClassName(target.Value);
        var fullscreen = WindowsTargetWindowNative.IsFullscreen(target.Value);
        return WindowsPastePolicy.Resolve(
            processName,
            windowClassName,
            elementClassName,
            isControlEdit,
            fullscreen);
    }

    internal static TextDeliveryRefusalReason PreflightRefusal(
        TargetContextResult current,
        CaretContext expected,
        bool checkCaretTextUnchanged = true)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(expected);
        if (current.Status != TargetContextStatus.Available || current.Context is null)
        {
            return current.RefusalReason != TextDeliveryRefusalReason.None
                ? current.RefusalReason
                : RefusalFor(current.Status);
        }

        return CaretUnchanged(expected, current.Context, checkCaretTextUnchanged)
            ? TextDeliveryRefusalReason.None
            : TextDeliveryRefusalReason.TargetChanged;
    }

    internal static void ValidateOptions(TextDeliveryOptions options)
    {
        if (options.ContextWindowCharacters is < 1 or > 4_096)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The context window must be between 1 and 4096 characters.");
        }

        if (options.MaximumDirectValueCharacters is < 1 or > 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The direct-value limit must be between 1 and 1048576 characters.");
        }
    }

    internal static TextDeliveryRefusalReason RefusalFor(TargetContextStatus status) => status switch
    {
        TargetContextStatus.TargetUnavailable => TextDeliveryRefusalReason.TargetUnavailable,
        TargetContextStatus.TargetChanged => TextDeliveryRefusalReason.TargetChanged,
        TargetContextStatus.Protected => TextDeliveryRefusalReason.ProtectedField,
        TargetContextStatus.Elevated => TextDeliveryRefusalReason.ElevatedTarget,
        _ => TextDeliveryRefusalReason.AccessibilityUnavailable,
    };
}
