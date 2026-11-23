using System.Runtime.InteropServices;
using System.Security;
using EnviousWispr.Core.Distribution;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace EnviousWispr.Services.Distribution;

/// <summary>The seam over <see cref="StartupTask"/> so every state transition can be proven without package identity.</summary>
public interface IPackagedStartupTask
{
    StartupTaskState State { get; }

    Task<StartupTaskState> RequestEnableAsync(CancellationToken cancellationToken = default);

    void Disable();
}

/// <summary>The seam over the per-user Windows <c>Run</c> registry key used only by unpackaged builds.</summary>
public interface IStartupRegistryStore
{
    string? ReadCommand();

    bool IsApproved() => true;

    void WriteCommand(string command);

    void DeleteCommand();
}

/// <summary>
/// Reads and toggles Windows sign-in startup through <see cref="StartupTask"/> for packaged installs
/// and the per-user <c>Run</c> registry key for unpackaged builds. Ref #42.
/// </summary>
/// <remarks>
/// TWO EXECUTION MODELS, NEVER MIXED. A packaged copy declares <c>EnviousWisprStartup</c> in
/// <c>Package.appxmanifest</c> and talks only to <see cref="StartupTask"/>: it cannot write its own
/// <c>Run</c> key, and when the user disables the task in Windows Settings or Task Manager
/// (<see cref="StartupTaskState.DisabledByUser"/>) the app honours that state without calling
/// <c>RequestEnableAsync</c> or falling back to the registry. Only an unpackaged copy without package
/// identity uses the per-user <c>Run</c> key fallback.
/// </remarks>
public sealed class WindowsStartupRegistration
{
    public const string StartupTaskId = "EnviousWisprStartup";
    public const string RunRegistrySubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string StartupApprovedRunRegistrySubKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public const string RunRegistryValueName = "EnviousWispr.App";

    private readonly Func<bool> _isPackaged;
    private readonly Func<string, CancellationToken, Task<IPackagedStartupTask>> _getStartupTask;
    private readonly IStartupRegistryStore _registry;
    private readonly Func<string?> _executablePath;

    public WindowsStartupRegistration()
        : this(
            HasPackageIdentity,
            GetWinRtStartupTaskAsync,
            new CurrentUserRunRegistryStore(),
            () => Environment.ProcessPath)
    {
    }

    public WindowsStartupRegistration(
        Func<bool> isPackaged,
        Func<string, CancellationToken, Task<IPackagedStartupTask>> getStartupTask,
        IStartupRegistryStore registry,
        Func<string?> executablePath)
    {
        ArgumentNullException.ThrowIfNull(isPackaged);
        ArgumentNullException.ThrowIfNull(getStartupTask);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(executablePath);

        _isPackaged = isPackaged;
        _getStartupTask = getStartupTask;
        _registry = registry;
        _executablePath = executablePath;
    }

    /// <summary>Maps every <see cref="StartupTaskState"/> Windows can report to the status the window shows.</summary>
    public static StartupRegistrationStatus FromTaskState(StartupTaskState state) => state switch
    {
        StartupTaskState.Disabled => StartupRegistrationStatus.Disabled,
        StartupTaskState.DisabledByUser => StartupRegistrationStatus.DisabledByUser,
        StartupTaskState.Enabled => StartupRegistrationStatus.Enabled,
        StartupTaskState.DisabledByPolicy => StartupRegistrationStatus.DisabledByPolicy,
        StartupTaskState.EnabledByPolicy => StartupRegistrationStatus.EnabledByPolicy,
        _ => StartupRegistrationStatus.Failed,
    };

    /// <summary>Formats the quoted executable command stored in the per-user <c>Run</c> key for an unpackaged install.</summary>
    public static string FormatRunCommand(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("A startup command needs the executable path.", nameof(executablePath));
        }

        var trimmed = executablePath.Trim().Trim('"');
        if (!Path.IsPathFullyQualified(trimmed))
        {
            throw new ArgumentException("A startup command needs a fully qualified executable path.", nameof(executablePath));
        }

        return $"\"{trimmed}\"";
    }

    /// <summary>
    /// Returns <see langword="true"/> when the command stored in the per-user <c>Run</c> key launches
    /// <paramref name="executablePath"/> rather than a different installation or the preserved WPF proof executable.
    /// </summary>
    public static bool MatchesExecutable(string? storedCommand, string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(storedCommand) || string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        var candidate = storedCommand.Trim();
        if (candidate.StartsWith('"'))
        {
            var closingQuote = candidate.IndexOf('"', 1);
            if (closingQuote <= 1 || closingQuote != candidate.Length - 1)
            {
                return false;
            }

            candidate = candidate[1..closingQuote].Trim();
        }

        var target = executablePath.Trim().Trim('"');
        if (!Path.IsPathFullyQualified(candidate) || !Path.IsPathFullyQualified(target))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(candidate),
                Path.GetFullPath(target),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public async Task<StartupRegistrationResult> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var packaged = false;
        try
        {
            packaged = _isPackaged();
            if (packaged)
            {
                var task = await _getStartupTask(StartupTaskId, cancellationToken).ConfigureAwait(false);
                return new StartupRegistrationResult(FromTaskState(task.State), IsPackaged: true);
            }

            var currentExecutable = _executablePath();
            _ = FormatRunCommand(currentExecutable);
            var command = _registry.ReadCommand();
            if (!MatchesExecutable(command, currentExecutable))
            {
                return new StartupRegistrationResult(StartupRegistrationStatus.Disabled, IsPackaged: false);
            }

            var status = _registry.IsApproved()
                ? StartupRegistrationStatus.Enabled
                : StartupRegistrationStatus.DisabledByUser;
            return new StartupRegistrationResult(status, IsPackaged: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            return new StartupRegistrationResult(StartupRegistrationStatus.Failed, IsPackaged: packaged);
        }
    }

    public async Task<StartupRegistrationResult> SetEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var packaged = false;
        try
        {
            packaged = _isPackaged();
            if (packaged)
            {
                var task = await _getStartupTask(StartupTaskId, cancellationToken).ConfigureAwait(false);
                var current = task.State;

                // HONOUR USER AND POLICY OVERRIDES: Windows refuses RequestEnableAsync once the user turns
                // the startup task off in Windows Settings or Task Manager, and policy states are fixed by
                // the administrator. Never attempt to re-enable or touch the registry from a packaged copy.
                if (current is StartupTaskState.DisabledByUser
                    or StartupTaskState.DisabledByPolicy
                    or StartupTaskState.EnabledByPolicy)
                {
                    return new StartupRegistrationResult(FromTaskState(current), IsPackaged: true);
                }

                if (enabled)
                {
                    if (current == StartupTaskState.Enabled)
                    {
                        return new StartupRegistrationResult(StartupRegistrationStatus.Enabled, IsPackaged: true);
                    }

                    var next = await task.RequestEnableAsync(cancellationToken).ConfigureAwait(false);
                    return new StartupRegistrationResult(FromTaskState(next), IsPackaged: true);
                }

                if (current == StartupTaskState.Disabled)
                {
                    return new StartupRegistrationResult(StartupRegistrationStatus.Disabled, IsPackaged: true);
                }

                task.Disable();
                return new StartupRegistrationResult(FromTaskState(task.State), IsPackaged: true);
            }

            var currentExecutable = _executablePath();
            var command = FormatRunCommand(currentExecutable);
            if (enabled)
            {
                if (MatchesExecutable(_registry.ReadCommand(), currentExecutable) && !_registry.IsApproved())
                {
                    return new StartupRegistrationResult(StartupRegistrationStatus.DisabledByUser, IsPackaged: false);
                }

                _registry.WriteCommand(command);
                var verified = MatchesExecutable(_registry.ReadCommand(), currentExecutable);
                if (!verified)
                {
                    return new StartupRegistrationResult(StartupRegistrationStatus.Failed, IsPackaged: false);
                }

                return new StartupRegistrationResult(
                    _registry.IsApproved() ? StartupRegistrationStatus.Enabled : StartupRegistrationStatus.DisabledByUser,
                    IsPackaged: false);
            }

            if (MatchesExecutable(_registry.ReadCommand(), currentExecutable))
            {
                _registry.DeleteCommand();
            }

            var removed = !MatchesExecutable(_registry.ReadCommand(), currentExecutable);
            return new StartupRegistrationResult(
                removed ? StartupRegistrationStatus.Disabled : StartupRegistrationStatus.Failed,
                IsPackaged: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            return new StartupRegistrationResult(StartupRegistrationStatus.Failed, IsPackaged: packaged);
        }
    }

    /// <summary>
    /// Interprets the binary record Windows Startup Apps and Task Manager write under
    /// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run</c>.
    /// </summary>
    public static bool IsStartupApprovedValue(byte[]? approvalBytes)
    {
        if (approvalBytes is null or { Length: 0 })
        {
            return true;
        }

        if (approvalBytes.Length < 4)
        {
            return false;
        }

        // Windows stores a 4-byte little-endian status value followed by an 8-byte FILETIME when
        // disabled by the user. Low bit 1 (0x01, 0x03, 0x05, 0x07) or any non-zero disable timestamp
        // marks the Run entry as disabled in Windows Startup Apps / Task Manager.
        if ((approvalBytes[0] & 0x01) != 0 ||
            approvalBytes[0] is not (0x00 or 0x02 or 0x04 or 0x06) ||
            approvalBytes[1] != 0 ||
            approvalBytes[2] != 0 ||
            approvalBytes[3] != 0)
        {
            return false;
        }

        for (var index = 4; index < approvalBytes.Length; index++)
        {
            if (approvalBytes[index] != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasPackageIdentity()
    {
        try
        {
            _ = Package.Current.Id;
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException)
        {
            return false;
        }
    }

    private static async Task<IPackagedStartupTask> GetWinRtStartupTaskAsync(
        string taskId,
        CancellationToken cancellationToken)
    {
        var task = await StartupTask.GetAsync(taskId).AsTask(cancellationToken).ConfigureAwait(false);
        return new WinRtPackagedStartupTask(task);
    }

    private sealed class WinRtPackagedStartupTask(StartupTask task) : IPackagedStartupTask
    {
        public StartupTaskState State => task.State;

        public Task<StartupTaskState> RequestEnableAsync(CancellationToken cancellationToken = default) =>
            task.RequestEnableAsync().AsTask(cancellationToken);

        public void Disable() => task.Disable();
    }

    private sealed class CurrentUserRunRegistryStore : IStartupRegistryStore
    {
        public string? ReadCommand()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistrySubKey, writable: false);
            return key?.GetValue(RunRegistryValueName) as string;
        }

        public bool IsApproved()
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupApprovedRunRegistrySubKey, writable: false);
            return IsStartupApprovedValue(key?.GetValue(RunRegistryValueName) as byte[]);
        }

        public void WriteCommand(string command)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunRegistrySubKey, writable: true)
                ?? throw new IOException("The Windows Run registry key could not be opened for writing.");
            key.SetValue(RunRegistryValueName, command, RegistryValueKind.String);
        }

        public void DeleteCommand()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistrySubKey, writable: true);
            key?.DeleteValue(RunRegistryValueName, throwOnMissingValue: false);
        }
    }
}
