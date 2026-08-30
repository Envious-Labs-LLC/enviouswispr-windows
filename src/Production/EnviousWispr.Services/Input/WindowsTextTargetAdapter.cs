using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Input;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace EnviousWispr.Services.Input;

public sealed class WindowsTextTargetAdapter : ITextTargetAdapter, IDisposable
{
    private static readonly TimeSpan TargetActivationTimeout = TimeSpan.FromSeconds(1);
    private readonly SemaphoreSlim _automationGate = new(1, 1);

    public void Dispose() => _automationGate.Dispose();

    public Task<TextCommitResult> CopyOnlyAsync(
        ProcessedText text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        return WindowsClipboardPaste.CopyRequestedAsync(text.Text, cancellationToken);
    }

    public async Task<TargetContextResult> CaptureContextAsync(
        TargetWindowId target,
        TextDeliveryOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        try
        {
            await _automationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await Task.Run(
                    () => CaptureContext(target, options),
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _automationGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedAutomationFailure(exception))
        {
            return new TargetContextResult(
                TargetContextStatus.AccessibilityUnavailable,
                RefusalReason: TextDeliveryRefusalReason.AccessibilityUnavailable);
        }
    }

    /// <summary>
    /// Asks the focused app for its selection with a synthetic Copy, and puts the clipboard back.
    /// </summary>
    /// <remarks>
    /// For apps that publish no selection. Returns null when nothing could be read, for any reason:
    /// every reason has the same remedy, so a caller branching on them would be inventing
    /// distinctions it cannot act on.
    /// </remarks>
    public static Task<string?> TryReadSelectionWithCopyAsync(CancellationToken cancellationToken) =>
        WindowsClipboardPaste.TryReadSelectionAsync(cancellationToken);

    public async Task<TextCommitResult> CommitAsync(
        TextCommitRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Options);
        ValidateOptions(request.Options);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.ForcedRefusalReason != TextDeliveryRefusalReason.None)
        {
            return await WindowsClipboardPaste.CopyOnlyAsync(
                request.LegacyText.Text,
                request.ForcedRefusalReason,
                cancellationToken).ConfigureAwait(false);
        }

        if (request.ExpectedContext is null)
        {
            return await WindowsClipboardPaste.CopyOnlyAsync(
                request.LegacyText.Text,
                TextDeliveryRefusalReason.AccessibilityUnavailable,
                cancellationToken).ConfigureAwait(false);
        }

        var current = await CaptureContextAsync(
            request.Target,
            request.Options,
            cancellationToken).ConfigureAwait(false);
        if (current.Status != TargetContextStatus.Available ||
            current.Context is null ||
            !CaretUnchanged(request.ExpectedContext, current.Context))
        {
            var refusal = current.Status == TargetContextStatus.Elevated
                ? TextDeliveryRefusalReason.ElevatedTarget
                : current.Status == TargetContextStatus.Protected
                    ? TextDeliveryRefusalReason.ProtectedField
                    : TextDeliveryRefusalReason.TargetChanged;
            return await WindowsClipboardPaste.CopyOnlyAsync(
                request.LegacyText.Text,
                refusal,
                cancellationToken).ConfigureAwait(false);
        }

        if (CanUseDirectValueWrite(current.Context, request))
        {
            var direct = TryDirectValueWrite(request, current.Context);
            if (direct is not null)
            {
                if (direct.Delivered)
                {
                    return direct;
                }

                if (direct.RefusalReason == TextDeliveryRefusalReason.DirectWriteUnverified)
                {
                    return direct;
                }
            }
        }

        var policyRefusal = CompatibilityRefusal(request.TargetKind, request.Text.Text);
        if (policyRefusal != TextDeliveryRefusalReason.None)
        {
            return await WindowsClipboardPaste.CopyOnlyAsync(
                request.LegacyText.Text,
                policyRefusal,
                cancellationToken).ConfigureAwait(false);
        }

        return await WindowsClipboardPaste.PasteAsync(
            request.Text.Text,
            request.LegacyText.Text,
            request.Options.RestoreClipboardAfterPaste,
            () => PreflightInput(
                request.Target,
                request.ExpectedContext,
                request.Options),
            cancellationToken).ConfigureAwait(false);
    }

    internal static bool CaretUnchanged(CaretContext expected, CaretContext actual) =>
        expected.Target == actual.Target &&
        string.Equals(expected.FocusedElementId, actual.FocusedElementId, StringComparison.Ordinal) &&
        expected.TargetKind == actual.TargetKind &&
        expected.LeftReachedDocumentStart == actual.LeftReachedDocumentStart &&
        expected.RightReachedDocumentEnd == actual.RightReachedDocumentEnd &&
        expected.HasTextContext == actual.HasTextContext &&
        expected.IsScreenDerived == actual.IsScreenDerived &&
        expected.IsUrlBarField == actual.IsUrlBarField &&
        string.Equals(expected.Left, actual.Left, StringComparison.Ordinal) &&
        string.Equals(expected.Selection, actual.Selection, StringComparison.Ordinal) &&
        string.Equals(expected.Right, actual.Right, StringComparison.Ordinal);

    internal static TextDeliveryRefusalReason CompatibilityRefusal(
        TextTargetKind targetKind,
        string text) => targetKind switch
    {
        TextTargetKind.Terminal when text.IndexOfAny(['\r', '\n']) >= 0 =>
            TextDeliveryRefusalReason.UnsafeMultilineTarget,
        TextTargetKind.Game => TextDeliveryRefusalReason.UnsupportedTarget,
        _ => TextDeliveryRefusalReason.None,
    };

    private static bool CanUseDirectValueWrite(
        CaretContext context,
        TextCommitRequest request) =>
        context.TargetKind == TextTargetKind.StandardEdit &&
        context.SupportsDirectValueWrite &&
        context.DirectValueWriteAtEnd &&
        context.Selection.Length == 0 &&
        request.Text.Text.Length <= request.Options.MaximumDirectValueCharacters;

    private static TextCommitResult? TryDirectValueWrite(
        TextCommitRequest request,
        CaretContext expected)
    {
        var invoked = false;
        try
        {
            var preflight = PreflightInput(request.Target, expected, request.Options);
            if (preflight != TextDeliveryRefusalReason.None)
            {
                return null;
            }

            var element = AutomationElement.FocusedElement;
            if (element is null ||
                !element.TryGetCurrentPattern(ValuePattern.Pattern, out var patternObject) ||
                patternObject is not ValuePattern valuePattern ||
                valuePattern.Current.IsReadOnly)
            {
                return null;
            }

            var existing = valuePattern.Current.Value ?? string.Empty;
            if (existing.Length > request.Options.MaximumDirectValueCharacters ||
                !existing.EndsWith(expected.Left, StringComparison.Ordinal))
            {
                return null;
            }

            var replacement = existing + request.Text.Text;
            if (replacement.Length > request.Options.MaximumDirectValueCharacters)
            {
                return null;
            }

            invoked = true;
            valuePattern.SetValue(replacement);
            var verified = string.Equals(
                valuePattern.Current.Value,
                replacement,
                StringComparison.Ordinal);
            return new TextCommitResult(
                TextDeliveryRoute.UiAutomationValue,
                Delivered: verified,
                ClipboardFallback: false,
                ClipboardRestored: false,
                verified
                    ? TextDeliveryRefusalReason.None
                    : TextDeliveryRefusalReason.DirectWriteUnverified);
        }
        catch (Exception exception) when (IsExpectedAutomationFailure(exception))
        {
            return invoked
                ? new TextCommitResult(
                    TextDeliveryRoute.UiAutomationValue,
                    Delivered: false,
                    ClipboardFallback: false,
                    ClipboardRestored: false,
                    TextDeliveryRefusalReason.DirectWriteUnverified)
                : null;
        }
    }

    private static TargetContextResult CaptureContext(
        TargetWindowId target,
        TextDeliveryOptions options)
    {
        var windowStatus = ActivateAndValidateWindow(target);
        if (windowStatus != TargetContextStatus.Available)
        {
            return new TargetContextResult(
                windowStatus,
                RefusalReason: RefusalFor(windowStatus));
        }

        if (target.FocusedElementId is null)
        {
            return new TargetContextResult(
                TargetContextStatus.AccessibilityUnavailable,
                RefusalReason: TextDeliveryRefusalReason.AccessibilityUnavailable);
        }

        var element = AutomationElement.FocusedElement;
        if (element is null)
        {
            return new TargetContextResult(
                TargetContextStatus.AccessibilityUnavailable,
                RefusalReason: TextDeliveryRefusalReason.AccessibilityUnavailable);
        }

        var processId = checked((uint)element.Current.ProcessId);
        var focusedId = RuntimeId(element);
        if (processId != target.ProcessId ||
            !string.Equals(focusedId, target.FocusedElementId, StringComparison.Ordinal) ||
            !element.Current.HasKeyboardFocus)
        {
            return new TargetContextResult(
                TargetContextStatus.TargetChanged,
                RefusalReason: TextDeliveryRefusalReason.TargetChanged);
        }

        var targetKind = ClassifyTarget(target, element);
        if (element.Current.IsPassword)
        {
            return new TargetContextResult(
                TargetContextStatus.Protected,
                EmptyContext(target, focusedId, targetKind),
                TextDeliveryRefusalReason.ProtectedField);
        }

        if (!element.Current.IsEnabled || !element.Current.IsKeyboardFocusable)
        {
            return new TargetContextResult(
                TargetContextStatus.Available,
                EmptyContext(target, focusedId, targetKind));
        }

        if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject) ||
            patternObject is not TextPattern textPattern)
        {
            return new TargetContextResult(
                TargetContextStatus.Available,
                EmptyContext(target, focusedId, targetKind));
        }

        var selections = textPattern.GetSelection();
        if (selections.Length != 1)
        {
            return new TargetContextResult(
                TargetContextStatus.AccessibilityUnavailable,
                EmptyContext(target, focusedId, targetKind),
                TextDeliveryRefusalReason.UnsupportedTarget);
        }

        var selection = selections[0];
        var selected = selection.GetText(options.ContextWindowCharacters + 1);
        if (selected.Length > options.ContextWindowCharacters)
        {
            return new TargetContextResult(
                TargetContextStatus.AccessibilityUnavailable,
                EmptyContext(target, focusedId, targetKind),
                TextDeliveryRefusalReason.UnsupportedTarget);
        }

        var document = textPattern.DocumentRange;
        var leftRange = document.Clone();
        leftRange.MoveEndpointByRange(
            TextPatternRangeEndpoint.End,
            selection,
            TextPatternRangeEndpoint.Start);
        leftRange.MoveEndpointByUnit(
            TextPatternRangeEndpoint.Start,
            TextUnit.Character,
            -options.ContextWindowCharacters);
        var left = leftRange.GetText(options.ContextWindowCharacters);
        var leftAtStart = leftRange.CompareEndpoints(
            TextPatternRangeEndpoint.Start,
            document,
            TextPatternRangeEndpoint.Start) == 0;

        var rightRange = document.Clone();
        rightRange.MoveEndpointByRange(
            TextPatternRangeEndpoint.Start,
            selection,
            TextPatternRangeEndpoint.End);
        rightRange.MoveEndpointByUnit(
            TextPatternRangeEndpoint.End,
            TextUnit.Character,
            options.ContextWindowCharacters);
        var right = rightRange.GetText(options.ContextWindowCharacters);
        var rightAtEnd = rightRange.CompareEndpoints(
            TextPatternRangeEndpoint.End,
            document,
            TextPatternRangeEndpoint.End) == 0;

        var supportsValue = element.TryGetCurrentPattern(
            ValuePattern.Pattern,
            out var valuePatternObject) &&
            valuePatternObject is ValuePattern valuePattern &&
            !valuePattern.Current.IsReadOnly;
        return new TargetContextResult(
            TargetContextStatus.Available,
            new CaretContext(
                target,
                focusedId,
                targetKind,
                left,
                selected,
                right,
                leftAtStart,
                rightAtEnd,
                HasTextContext: true,
                SupportsDirectValueWrite: supportsValue,
                DirectValueWriteAtEnd: rightAtEnd && right.Length == 0,
                IsScreenDerived: targetKind == TextTargetKind.Terminal,
                IsUrlBarField: IsUrlBarField(element, targetKind)));
    }

    private static CaretContext EmptyContext(
        TargetWindowId target,
        string focusedId,
        TextTargetKind targetKind) => new(
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
        DirectValueWriteAtEnd: false);

    private static TextTargetKind ClassifyTarget(
        TargetWindowId target,
        AutomationElement element)
    {
        var processName = TryGetProcessName(target.ProcessId);
        if (TerminalProcesses.Contains(processName))
        {
            return TextTargetKind.Terminal;
        }

        if (OfficeProcesses.Contains(processName))
        {
            return TextTargetKind.Office;
        }

        if (ChatProcesses.Contains(processName))
        {
            return TextTargetKind.Chat;
        }

        if (BrowserProcesses.Contains(processName))
        {
            return TextTargetKind.Browser;
        }

        var controlType = element.Current.ControlType;
        if (controlType == ControlType.Edit)
        {
            return TextTargetKind.StandardEdit;
        }

        return IsFullscreen(target.Value)
            ? TextTargetKind.Game
            : TextTargetKind.Unknown;
    }

    private static bool IsUrlBarField(
        AutomationElement element,
        TextTargetKind targetKind)
    {
        if (targetKind != TextTargetKind.Browser)
        {
            return false;
        }

        var automationId = element.Current.AutomationId ?? string.Empty;
        var className = element.Current.ClassName ?? string.Empty;
        return automationId.Contains("address", StringComparison.OrdinalIgnoreCase) ||
            automationId.Contains("omnibox", StringComparison.OrdinalIgnoreCase) ||
            automationId.Contains("urlbar", StringComparison.OrdinalIgnoreCase) ||
            className.Contains("omnibox", StringComparison.OrdinalIgnoreCase) ||
            className.Contains("urlbar", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly HashSet<string> BrowserProcesses =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "chrome", "msedge", "firefox", "brave", "opera", "vivaldi",
        };

    private static readonly HashSet<string> OfficeProcesses =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "winword", "excel", "outlook", "onenote", "powerpnt",
        };

    private static readonly HashSet<string> ChatProcesses =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "discord", "slack", "ms-teams", "teams", "signal", "telegram", "whatsapp",
        };

    private static readonly HashSet<string> TerminalProcesses =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "windowsterminal", "cmd", "powershell", "pwsh", "conhost", "wezterm",
            "alacritty", "mintty", "tabby",
        };

    private static string TryGetProcessName(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            return process.ProcessName;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return string.Empty;
        }
    }

    private static TargetContextStatus ActivateAndValidateWindow(TargetWindowId target)
    {
        if (!target.IsValid || !IsWindow(target.Value))
        {
            return TargetContextStatus.TargetUnavailable;
        }

        _ = GetWindowThreadProcessId(target.Value, out var currentProcessId);
        if (target.ProcessId == 0 || currentProcessId != target.ProcessId)
        {
            return TargetContextStatus.TargetChanged;
        }

        var integrity = TargetHasHigherIntegrity(target.ProcessId);
        if (integrity is null or true)
        {
            return TargetContextStatus.Elevated;
        }

        if (GetForegroundWindow() != target.Value)
        {
            _ = SetForegroundWindow(target.Value);
            var timer = Stopwatch.StartNew();
            while (GetForegroundWindow() != target.Value &&
                timer.Elapsed < TargetActivationTimeout)
            {
                Thread.Sleep(50);
            }
        }

        return GetForegroundWindow() == target.Value
            ? TargetContextStatus.Available
            : TargetContextStatus.TargetChanged;
    }

    private static TextDeliveryRefusalReason PreflightInput(
        TargetWindowId target,
        CaretContext expected,
        TextDeliveryOptions options)
    {
        if (GetForegroundWindow() != target.Value)
        {
            return TextDeliveryRefusalReason.TargetChanged;
        }

        _ = GetWindowThreadProcessId(target.Value, out var processId);
        if (processId != target.ProcessId)
        {
            return TextDeliveryRefusalReason.TargetChanged;
        }

        if (AnyInputKeyHeld())
        {
            return TextDeliveryRefusalReason.InputStateUnsafe;
        }

        var current = CaptureContext(target, options);
        if (current.Status != TargetContextStatus.Available || current.Context is null)
        {
            return RefusalFor(current.Status);
        }

        return CaretUnchanged(expected, current.Context)
            ? TextDeliveryRefusalReason.None
            : TextDeliveryRefusalReason.TargetChanged;
    }

    private static void ValidateOptions(TextDeliveryOptions options)
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

    private static bool AnyInputKeyHeld() =>
        IsKeyHeld(VkControl) ||
        IsKeyHeld(VkMenu) ||
        IsKeyHeld(VkShift) ||
        IsKeyHeld(VkLeftWindows) ||
        IsKeyHeld(VkRightWindows) ||
        IsKeyHeld(VkV);

    private static bool IsKeyHeld(int virtualKey) =>
        (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static string RuntimeId(AutomationElement element) =>
        string.Join('.', element.GetRuntimeId());

    private static TextDeliveryRefusalReason RefusalFor(TargetContextStatus status) => status switch
    {
        TargetContextStatus.TargetUnavailable => TextDeliveryRefusalReason.TargetUnavailable,
        TargetContextStatus.TargetChanged => TextDeliveryRefusalReason.TargetChanged,
        TargetContextStatus.Protected => TextDeliveryRefusalReason.ProtectedField,
        TargetContextStatus.Elevated => TextDeliveryRefusalReason.ElevatedTarget,
        _ => TextDeliveryRefusalReason.AccessibilityUnavailable,
    };

    private static bool IsExpectedAutomationFailure(Exception exception) =>
        exception is COMException or ElementNotAvailableException or InvalidOperationException or
            UnauthorizedAccessException or Win32Exception;

    private static bool? TargetHasHigherIntegrity(uint targetProcessId)
    {
        var ours = GetIntegrityLevel(GetCurrentProcess());
        var targetProcess = OpenProcess(ProcessQueryLimitedInformation, false, targetProcessId);
        if (targetProcess == 0)
        {
            return null;
        }

        try
        {
            var target = GetIntegrityLevel(targetProcess);
            return ours is null || target is null ? null : target > ours;
        }
        finally
        {
            _ = CloseHandle(targetProcess);
        }
    }

    private static uint? GetIntegrityLevel(nint process)
    {
        if (!OpenProcessToken(process, TokenQuery, out var token))
        {
            return null;
        }

        try
        {
            _ = GetTokenInformation(token, TokenIntegrityLevel, 0, 0, out var needed);
            if (needed == 0)
            {
                return null;
            }

            var buffer = Marshal.AllocHGlobal(checked((int)needed));
            try
            {
                if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, needed, out _))
                {
                    return null;
                }

                var sid = Marshal.ReadIntPtr(buffer);
                var countPointer = GetSidSubAuthorityCount(sid);
                if (countPointer == 0)
                {
                    return null;
                }

                var count = Marshal.ReadByte(countPointer);
                if (count == 0)
                {
                    return null;
                }

                var authority = GetSidSubAuthority(sid, checked((uint)(count - 1)));
                return authority == 0 ? null : checked((uint)Marshal.ReadInt32(authority));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            _ = CloseHandle(token);
        }
    }

    private static bool IsFullscreen(nint window)
    {
        if (!GetWindowRect(window, out var windowRect))
        {
            return false;
        }

        var monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return monitor != 0 &&
            GetMonitorInfo(monitor, ref info) &&
            windowRect.Left <= info.Monitor.Left &&
            windowRect.Top <= info.Monitor.Top &&
            windowRect.Right >= info.Monitor.Right &&
            windowRect.Bottom >= info.Monitor.Bottom;
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;
    private const uint MonitorDefaultToNearest = 2;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkV = 0x56;
    private const int VkLeftWindows = 0x5B;
    private const int VkRightWindows = 0x5C;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint process, uint desiredAccess, out nint token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        nint token,
        int tokenInformationClass,
        nint tokenInformation,
        uint tokenInformationLength,
        out uint returnLength);

    [DllImport("advapi32.dll")]
    private static extern nint GetSidSubAuthorityCount(nint sid);

    [DllImport("advapi32.dll")]
    private static extern nint GetSidSubAuthority(nint sid, uint subAuthority);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
