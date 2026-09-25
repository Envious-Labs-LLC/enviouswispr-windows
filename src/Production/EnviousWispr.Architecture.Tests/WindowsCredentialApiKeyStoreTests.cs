using System.Runtime.InteropServices;
using EnviousWispr.Core.Credentials;
using EnviousWispr.Core.Settings;
using EnviousWispr.Services.Credentials;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// A fact that needs Windows Credential Manager to be reachable from this logon session; skipped
/// where there is no logon session for it (ERROR_NO_LOGON_SESSIONS / 1312), as in a non-interactive
/// session (SSH, service, agent). A real Credential Manager failure in a reachable session is not
/// skipped: the test runs and fails.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class CredentialManagerFactAttribute : FactAttribute
{
    private const uint CredTypeGeneric = 1;
    private const int ErrorNoLogonSessions = 1312;

    public CredentialManagerFactAttribute()
    {
        if (!CredentialManagerReachable())
        {
            Skip = "Windows Credential Manager has no logon session in this context " +
                   "(ERROR_NO_LOGON_SESSIONS / 1312); the create/read/replace/delete path did not run. " +
                   "It runs in an interactive session.";
        }
    }

    /// <summary>
    /// Whether this logon session can reach Windows Credential Manager. A read of a throwaway target
    /// answers the session, not the target: in a non-interactive session the whole store is
    /// unreachable and even a read of a missing credential returns 1312 instead of 1168 (not found).
    /// Any other answer - a hit, 1168, or another failure - means the session reaches Credential
    /// Manager, so the test runs and a real failure fails it.
    /// </summary>
    private static bool CredentialManagerReachable()
    {
        if (CredReadW($"EnviousWispr.Tests.Probe.{Guid.NewGuid():N}", CredTypeGeneric, 0, out var credential))
        {
            if (credential != IntPtr.Zero)
            {
                CredFree(credential);
            }

            return true;
        }

        return Marshal.GetLastWin32Error() != ErrorNoLogonSessions;
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredReadW(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("Advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}

public sealed class WindowsCredentialApiKeyStoreTests
{
    [CredentialManagerFact]
    public void CredentialManagerSupportsCreateReadReplaceAndIdempotentDelete()
    {
        var prefix = $"EnviousLabs.EnviousWispr.Tests.{Guid.NewGuid():N}";
        var store = new WindowsCredentialApiKeyStore(prefix);
        try
        {
            Assert.Equal(ApiKeyReadStatus.Missing, store.Read(PolishProvider.OpenAI).Status);
            Assert.Equal(ApiKeyReadStatus.Missing, store.GetStatus(PolishProvider.OpenAI));

            store.Store(PolishProvider.OpenAI, "first-test-value");
            Assert.Equal(ApiKeyReadStatus.Found, store.GetStatus(PolishProvider.OpenAI));
            var first = store.Read(PolishProvider.OpenAI);
            Assert.Equal(ApiKeyReadStatus.Found, first.Status);
            Assert.Equal("first-test-value", first.Value);

            store.Store(PolishProvider.OpenAI, "replacement-test-value");
            var replacement = store.Read(PolishProvider.OpenAI);
            Assert.Equal(ApiKeyReadStatus.Found, replacement.Status);
            Assert.Equal("replacement-test-value", replacement.Value);

            store.Delete(PolishProvider.OpenAI);
            store.Delete(PolishProvider.OpenAI);
            Assert.Equal(ApiKeyReadStatus.Missing, store.Read(PolishProvider.OpenAI).Status);
            Assert.Equal(ApiKeyReadStatus.Missing, store.GetStatus(PolishProvider.OpenAI));
        }
        finally
        {
            store.Delete(PolishProvider.OpenAI);
        }
    }

    /// <summary>"Delete all EnviousWispr data" removes this store's keys and nothing outside its namespace. Ref: #42.</summary>
    /// <remarks>
    /// THE PRODUCTION PREFIX IS THE STEM OF EVERY UAT ONE, and Windows matches a filter as "prefix.*", so a
    /// deeper namespace comes back from the same enumeration. It must survive, as must a target that merely
    /// starts with the same characters without the separating dot.
    /// </remarks>
    [CredentialManagerFact]
    public void DeleteAllRemovesThisNamespaceOnlyAndLeavesDeeperAndLookalikeTargets()
    {
        var prefix = $"EnviousLabs.EnviousWispr.Tests.{Guid.NewGuid():N}";
        var store = new WindowsCredentialApiKeyStore(prefix);
        var deeper = new WindowsCredentialApiKeyStore($"{prefix}.Uat.other");
        var lookalike = new WindowsCredentialApiKeyStore($"{prefix}X");
        try
        {
            store.Store(PolishProvider.OpenAI, "store-openai");
            store.Store(PolishProvider.Gemini, "store-gemini");
            deeper.Store(PolishProvider.OpenAI, "deeper-openai");
            lookalike.Store(PolishProvider.Anthropic, "lookalike-anthropic");
            Assert.Equal(
                [$"{prefix}.Gemini", $"{prefix}.OpenAI"],
                store.OwnedTargets().Order(StringComparer.Ordinal).ToArray());

            Assert.Equal(0, store.DeleteAll());

            Assert.Equal(ApiKeyReadStatus.Missing, store.GetStatus(PolishProvider.OpenAI));
            Assert.Equal(ApiKeyReadStatus.Missing, store.GetStatus(PolishProvider.Gemini));
            Assert.Empty(store.OwnedTargets());
            Assert.Equal("deeper-openai", deeper.Read(PolishProvider.OpenAI).Value);
            Assert.Equal("lookalike-anthropic", lookalike.Read(PolishProvider.Anthropic).Value);
            Assert.Equal(0, store.DeleteAll());
        }
        finally
        {
            store.Delete(PolishProvider.OpenAI);
            store.Delete(PolishProvider.Gemini);
            deeper.Delete(PolishProvider.OpenAI);
            lookalike.Delete(PolishProvider.Anthropic);
        }
    }

    [Fact]
    public void NonCloudProvidersAreRejectedBeforeCallingWindows()
    {
        var store = new WindowsCredentialApiKeyStore(
            $"EnviousLabs.EnviousWispr.Tests.{Guid.NewGuid():N}");

        Assert.Throws<ArgumentOutOfRangeException>(() => store.Read(PolishProvider.EgOne));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.GetStatus(PolishProvider.EgOne));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Store(PolishProvider.None, "value"));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Delete(PolishProvider.Ollama));
    }

    [Theory]
    [InlineData("")]
    [InlineData("contains spaces")]
    [InlineData("contains.period")]
    [InlineData("contains/slash")]
    public void IsolatedUatScopeRejectsUnsafeSuffixes(string suffix)
    {
        Assert.Throws<ArgumentException>(() =>
            WindowsCredentialApiKeyStore.CreateForIsolatedUat(suffix));
    }
}
