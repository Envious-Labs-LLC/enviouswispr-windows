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

    public static WindowsCredentialApiKeyStore CreateForIsolatedUat(string suffix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suffix);
        if (suffix.Length > 64 || suffix.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new ArgumentException(
                "The isolated UAT credential suffix must contain only ASCII letters, digits, or hyphens.",
                nameof(suffix));
        }

        return new WindowsCredentialApiKeyStore($"{ProductionTargetPrefix}.Uat.{suffix}");
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

    public ApiKeyReadStatus GetStatus(PolishProvider provider)
    {
        var targetName = TargetName(provider);
        if (!CredRead(targetName, CredTypeGeneric, 0, out var nativeCredential))
        {
            return Marshal.GetLastWin32Error() == ErrorNotFound
                ? ApiKeyReadStatus.Missing
                : ApiKeyReadStatus.Unavailable;
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(nativeCredential);
            return credential.CredentialBlob != IntPtr.Zero && credential.CredentialBlobSize > 0
                ? ApiKeyReadStatus.Found
                : ApiKeyReadStatus.Unavailable;
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

    /// <summary>
    /// Deletes every credential this store's namespace owns, for "Delete all EnviousWispr data", and
    /// returns how many could not be deleted. Throws when Windows cannot list them at all.
    /// </summary>
    /// <remarks>
    /// ENUMERATED, NOT RECITED. A key stored under a provider this build no longer lists is still the
    /// person's key and still this app's, so the store asks Windows for every generic credential under
    /// its own prefix rather than deleting the three names it knows today.
    ///
    /// ONE LEVEL, EXACTLY THIS NAMESPACE. Windows matches the filter as "prefix.*", which would also
    /// match a deeper namespace sharing the prefix - the production prefix is the stem of every
    /// isolated UAT one. So a target is deleted only when what follows the prefix is a single name
    /// with no further dot, which is the shape <see cref="TargetName"/> writes. Anything outside the
    /// prefix is never returned by the filter and never touched.
    /// </remarks>
    public int DeleteAll()
    {
        var failed = 0;
        foreach (var target in OwnedTargets())
        {
            if (!CredDelete(target, CredTypeGeneric, 0) && Marshal.GetLastWin32Error() != ErrorNotFound)
            {
                failed++;
            }
        }

        return failed;
    }

    /// <summary>The generic credentials directly under this store's prefix: the targets it wrote, and any it wrote for a provider since retired.</summary>
    internal IReadOnlyList<string> OwnedTargets()
    {
        var namespacePrefix = _targetPrefix + ".";
        if (!CredEnumerate(namespacePrefix + "*", 0, out var count, out var credentials))
        {
            var error = Marshal.GetLastWin32Error();
            return error == ErrorNotFound
                ? []
                : throw new Win32Exception(error, "Credential Manager enumeration failed.");
        }

        try
        {
            var owned = new List<string>();
            for (var index = 0; index < count; index++)
            {
                var pointer = Marshal.ReadIntPtr(credentials, index * IntPtr.Size);
                var credential = Marshal.PtrToStructure<Credential>(pointer);
                if (credential.Type != CredTypeGeneric ||
                    credential.TargetName is not { } target ||
                    !target.StartsWith(namespacePrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var name = target[namespacePrefix.Length..];
                if (name.Length > 0 && !name.Contains('.', StringComparison.Ordinal))
                {
                    owned.Add(target);
                }
            }

            return owned;
        }
        finally
        {
            CredFree(credentials);
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

    [DllImport("Advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredEnumerate(
        string filter,
        uint flags,
        out int count,
        out IntPtr credentials);

    [DllImport("Advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

}
