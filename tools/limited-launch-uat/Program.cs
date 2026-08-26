using System.ComponentModel;
using System.Runtime.InteropServices;

var repository = FindRepository();
var deliveryExecutable = Path.Combine(
    repository,
    "tools",
    "delivery-uat",
    "bin",
    "Release",
    "net10.0-windows10.0.26100.0",
    "EnviousWispr.Delivery.Uat.exe");
if (!File.Exists(deliveryExecutable))
{
    throw new FileNotFoundException("Build the delivery UAT harness first.", deliveryExecutable);
}

if (!NativeMethods.OpenProcessToken(
        NativeMethods.GetCurrentProcess(),
        NativeMethods.TokenAssignPrimary |
            NativeMethods.TokenDuplicate |
            NativeMethods.TokenQuery |
            NativeMethods.TokenAdjustDefault,
        out var currentToken))
{
    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the current token.");
}

try
{
    if (!NativeMethods.DuplicateTokenEx(
            currentToken,
            NativeMethods.MaximumAllowed,
            0,
            NativeMethods.SecurityImpersonation,
            NativeMethods.TokenPrimary,
            out var mediumToken))
    {
        throw new Win32Exception(
            Marshal.GetLastWin32Error(),
            "Could not duplicate the current token.");
    }

    try
    {
        SetMediumIntegrity(mediumToken);
        _ = NativeMethods.CreateEnvironmentBlock(
            out var environment,
            mediumToken,
            inherit: false);
        try
        {
            var startup = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfo>(),
                Desktop = "winsta0\\default",
            };
            var commandLine = $"\"{deliveryExecutable}\"";
            if (!NativeMethods.CreateProcessWithTokenW(
                    mediumToken,
                    NativeMethods.LogonWithProfile,
                    deliveryExecutable,
                    commandLine,
                    NativeMethods.CreateNewConsole | NativeMethods.CreateUnicodeEnvironment,
                    environment,
                    repository,
                    ref startup,
                    out var processInformation))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not launch with the medium-integrity token.");
            }

            try
            {
                Console.WriteLine($"launched_pid={processInformation.ProcessId}");
            }
            finally
            {
                _ = NativeMethods.CloseHandle(processInformation.Thread);
                _ = NativeMethods.CloseHandle(processInformation.Process);
            }
        }
        finally
        {
            if (environment != 0)
            {
                _ = NativeMethods.DestroyEnvironmentBlock(environment);
            }
        }
    }
    finally
    {
        _ = NativeMethods.CloseHandle(mediumToken);
    }
}
finally
{
    _ = NativeMethods.CloseHandle(currentToken);
}

return 0;

static void SetMediumIntegrity(nint token)
{
    if (!NativeMethods.ConvertStringSidToSidW(
            "S-1-16-8192",
            out var mediumSid))
    {
        throw new Win32Exception(
            Marshal.GetLastWin32Error(),
            "Could not create the medium-integrity SID.");
    }

    try
    {
        var label = new TokenMandatoryLabel
        {
            Label = new SidAndAttributes
            {
                Sid = mediumSid,
                Attributes = NativeMethods.SeGroupIntegrity,
            },
        };
        var size = checked(
            Marshal.SizeOf<TokenMandatoryLabel>() +
            (int)NativeMethods.GetLengthSid(mediumSid));
        if (!NativeMethods.SetTokenInformation(
                token,
                NativeMethods.TokenIntegrityLevel,
                ref label,
                checked((uint)size)))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not lower the token integrity label.");
        }
    }
    finally
    {
        _ = NativeMethods.LocalFree(mediumSid);
    }
}

static string FindRepository()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CLAUDE.md")))
    {
        directory = directory.Parent;
    }

    return directory?.FullName ??
        throw new DirectoryNotFoundException("Could not locate the repository.");
}

[StructLayout(LayoutKind.Sequential)]
internal struct SidAndAttributes
{
    public nint Sid;
    public uint Attributes;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TokenMandatoryLabel
{
    public SidAndAttributes Label;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct StartupInfo
{
    public int Size;
    public string? Reserved;
    public string? Desktop;
    public string? Title;
    public int X;
    public int Y;
    public int XSize;
    public int YSize;
    public int XCountChars;
    public int YCountChars;
    public int FillAttribute;
    public int Flags;
    public short ShowWindow;
    public short Reserved2Size;
    public nint Reserved2;
    public nint StandardInput;
    public nint StandardOutput;
    public nint StandardError;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ProcessInformation
{
    public nint Process;
    public nint Thread;
    public int ProcessId;
    public int ThreadId;
}

internal static class NativeMethods
{
    internal const uint TokenAssignPrimary = 0x0001;
    internal const uint TokenDuplicate = 0x0002;
    internal const uint TokenQuery = 0x0008;
    internal const uint TokenAdjustDefault = 0x0080;
    internal const uint MaximumAllowed = 0x02000000;
    internal const int SecurityImpersonation = 2;
    internal const int TokenPrimary = 1;
    internal const int TokenIntegrityLevel = 25;
    internal const uint SeGroupIntegrity = 0x00000020;
    internal const uint LogonWithProfile = 0x00000001;
    internal const uint CreateNewConsole = 0x00000010;
    internal const uint CreateUnicodeEnvironment = 0x00000400;

    [DllImport("kernel32.dll")]
    internal static extern nint GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenProcessToken(
        nint process,
        uint desiredAccess,
        out nint token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DuplicateTokenEx(
        nint existingToken,
        uint desiredAccess,
        nint tokenAttributes,
        int impersonationLevel,
        int tokenType,
        out nint newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetTokenInformation(
        nint token,
        int tokenInformationClass,
        ref TokenMandatoryLabel tokenInformation,
        uint tokenInformationLength);

    [DllImport("advapi32.dll")]
    internal static extern uint GetLengthSid(nint sid);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ConvertStringSidToSidW(string sid, out nint sidPointer);

    [DllImport("kernel32.dll")]
    internal static extern nint LocalFree(nint memory);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateEnvironmentBlock(
        out nint environment,
        nint token,
        [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyEnvironmentBlock(nint environment);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateProcessWithTokenW(
        nint token,
        uint logonFlags,
        string applicationName,
        string commandLine,
        uint creationFlags,
        nint environment,
        string currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(nint handle);
}
