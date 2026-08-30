using EnviousWispr.Core.Credentials;
using EnviousWispr.Core.Settings;
using EnviousWispr.Services.Credentials;

namespace EnviousWispr.Architecture.Tests;

public sealed class WindowsCredentialApiKeyStoreTests
{
    [Fact]
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
