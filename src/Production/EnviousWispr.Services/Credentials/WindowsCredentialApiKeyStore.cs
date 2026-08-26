using System.ComponentModel;
using System.Runtime.InteropServices;
using EnviousWispr.Core.Credentials;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Services.Credentials;

public sealed class WindowsCredentialApiKeyStore : IApiKeyStore
{
    internal const string ProductionTargetPrefix = "EnviousLabs.EnviousWispr.ApiKey";
    private const int ErrorNotFound = 1168;
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int MaximumCredentialBlobBytes = 2_560;

    private readonly string _targetPrefix;

    public WindowsCredentialApiKeyStore()
        : this(ProductionTargetPrefix)
    {
    }

    internal WindowsCredentialApiKeyStore(string targetPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPrefix);
        _targetPrefix = targetPrefix;
    }

    public ApiKeyReadResult Read(PolishProvider provider)
    {
        var targetName = TargetName(provider);
        if (!CredRead(targetName, CredTypeGeneric, 0, out var nativeCredential))
        {
            return Marshal.GetLastWin32Error() == ErrorNotFound
                ? ApiKeyReadResult.Missing
                : ApiKeyReadResult.Unavailable;
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(nativeCredential);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return ApiKeyReadResult.Unavailable;
            }

            var characterCount = checked((int)credential.CredentialBlobSize / sizeof(char));
            var value = Marshal.PtrToStringUni(credential.CredentialBlob, characterCount);
            return string.IsNullOrWhiteSpace(value)
                ? ApiKeyReadResult.Unavailable
                : ApiKeyReadResult.Found(value);
        }
        finally
        {
            CredFree(nativeCredential);
        }
    }

    public void Store(PolishProvider provider, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var targetName = TargetName(provider);
        var blobSize = checked(value.Length * sizeof(char));
        if (blobSize > MaximumCredentialBlobBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                $"API keys must be at most {MaximumCredentialBlobBytes / sizeof(char)} characters.");
        }

        var secretBuffer = Marshal.StringToCoTaskMemUni(value);
        try
        {
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = targetName,
                CredentialBlobSize = checked((uint)blobSize),
                CredentialBlob = secretBuffer,
                Persist = CredPersistLocalMachine,
                UserName = "EnviousWispr",
            };
            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Credential Manager write failed.");
            }
        }
        finally
        {
            for (var index = 0; index < (value.Length + 1) * sizeof(char); index++)
            {
                Marshal.WriteByte(secretBuffer, index, 0);
            }
            Marshal.FreeCoTaskMem(secretBuffer);
        }
    }

    public void Delete(PolishProvider provider)
    {
        if (CredDelete(TargetName(provider), CredTypeGeneric, 0))
        {
            return;
        }

        var error = Marshal.GetLastWin32Error();
        if (error != ErrorNotFound)
        {
            throw new Win32Exception(error, "Credential Manager delete failed.");
        }
    }

    internal string TargetName(PolishProvider provider) =>
        $"{_targetPrefix}.{ProviderSuffix(provider)}";

    private static string ProviderSuffix(PolishProvider provider) => provider switch
    {
        PolishProvider.OpenAI => "OpenAI",
        PolishProvider.Anthropic => "Anthropic",
        PolishProvider.Gemini => "Gemini",
        _ => throw new ArgumentOutOfRangeException(
            nameof(provider),
            provider,
            "Only direct BYOK cloud polish providers have API-key credentials."),
    };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string UserName;
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string target,
        uint type,
        uint flags,
        out IntPtr credential);

    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("Advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

}
