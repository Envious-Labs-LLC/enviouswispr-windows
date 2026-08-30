using EnviousWispr.Core.Sessions;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Architecture.Tests;

public sealed class CoreContractTests
{
    [Fact]
    public void SessionSnapshotContainsStateAndTypedFailureButNoContentFields()
    {
        var snapshot = DictationSessionSnapshot.Start(DateTimeOffset.UnixEpoch);
        var propertyNames = typeof(DictationSessionSnapshot).GetProperties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(DictationSessionState.Recording, snapshot.State);
        Assert.DoesNotContain("text", propertyNames);
        Assert.DoesNotContain("transcript", propertyNames);
        Assert.DoesNotContain("audio", propertyNames);
        Assert.DoesNotContain("clipboard", propertyNames);
    }

    [Fact]
    public void ProviderPreferencesHaveNoCredentialOrSecretMember()
    {
        var propertyNames = typeof(PolishPreferences).GetProperties()
            .Where(property => property.GetMethod?.IsStatic == false)
            .Select(property => property.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(["ModelId", "OllamaEndpoint", "Provider"], propertyNames.Order());
        Assert.DoesNotContain("apiKey", propertyNames);
        Assert.DoesNotContain("credential", propertyNames);
        Assert.DoesNotContain("secret", propertyNames);
    }

    [Fact]
    public void ReusableUserDataDefensivelyCopiesCallerCollections()
    {
        var customWords = new List<CustomWordEntry>
        {
            new("envy wisper", "EnviousWispr"),
        };
        var snippets = new List<SnippetEntry>
        {
            new("signoff", "Kind regards"),
        };
        var userData = new ReusableUserData(customWords, snippets);

        customWords.Clear();
        snippets.Clear();

        Assert.Single(userData.CustomWords);
        Assert.Single(userData.Snippets);
    }
}
