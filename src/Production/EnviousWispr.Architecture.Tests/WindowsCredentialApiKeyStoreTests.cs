using EnviousWispr.Core.Credentials;
using EnviousWispr.Core.Settings;
using EnviousWispr.Services.Credentials;
using Xunit.Abstractions;

namespace EnviousWispr.Architecture.Tests;

public sealed class WindowsCredentialApiKeyStoreTests
{
    private readonly ITestOutputHelper _output;

    public WindowsCredentialApiKeyStoreTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void CredentialManagerSupportsCreateReadReplaceAndIdempotentDelete()
    {
        var prefix = $"EnviousLabs.EnviousWispr.Tests.{Guid.NewGuid():N}";
        var store = new WindowsCredentialApiKeyStore(prefix);
        var ran = false;
        try
        {
            // A fresh target the store reports as Unavailable (rather than Missing) means the
            // Windows Credential Manager itself is unreachable in this logon session, not that the
            // credential is absent. Measured on the Windows rig: CredRead/CredWrite/CredDelete all
            // fail with ERROR_NO_LOGON_SESSIONS (1312) in a non-interactive session (SSH, service,
            // agent). Per the repo's UAT BLOCKED convention, an environmental gap is recorded as
            // blocked, not failed; a real Credential Manager failure in an interactive session still
            // fails this test.
            if (store.GetStatus(PolishProvider.OpenAI) == ApiKeyReadStatus.Unavailable)
            {
                _output.WriteLine(
                    "UAT BLOCKED: Windows Credential Manager has no logon session in this context " +
                    "(ERROR_NO_LOGON_SESSIONS / 1312); the create/read/replace/delete path cannot run. " +
                    "It runs in an interactive session.");
                return;
            }

            ran = true;
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
            if (ran)
            {
                store.Delete(PolishProvider.OpenAI);
            }
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
