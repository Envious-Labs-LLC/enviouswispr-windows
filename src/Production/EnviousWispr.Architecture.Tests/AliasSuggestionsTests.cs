using EnviousWispr.Core.Settings;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// A model asked for a list returns a list most of the time. Every other shape has to become good
/// candidates or none - never a plausible alias the user did not ask for.
/// </summary>
public sealed class AliasSuggestionsTests
{
    private static IReadOnlyList<string> Parse(string? reply, string term = "Kubernetes") =>
        AliasSuggestions.Parse(reply, term, []);

    /// <summary>
    /// The control for the whole file. A plain list must produce candidates, or a parser that
    /// returned nothing would pass every rejection test below and the feature would be dead.
    /// </summary>
    [Fact]
    public void APlainListBecomesCandidates()
    {
        Assert.Equal(
            ["cuban eddies", "cooper netties"],
            Parse("cuban eddies\ncooper netties"));
    }

    /// <summary>
    /// The decorations nest: a line can be numbered AND quoted AND trailing-punctuated, and every
    /// layer has to come off for the alias underneath to match what a recogniser produces.
    /// </summary>
    [Theory]
    [InlineData("1. cuban eddies")]
    [InlineData("1) cuban eddies")]
    [InlineData("- cuban eddies")]
    [InlineData("* cuban eddies")]
    [InlineData("\"cuban eddies\"")]
    [InlineData("cuban eddies,")]
    [InlineData("2. \"cuban eddies\",")]
    public void ListDecorationIsStripped(string line)
    {
        Assert.Equal(["cuban eddies"], Parse(line));
    }

    /// <summary>
    /// A model suggesting the term back would create a correction from a word to itself, which
    /// fires forever and does nothing.
    /// </summary>
    [Fact]
    public void TheTermItselfIsNeverSuggestedBack()
    {
        Assert.Equal(["cuban eddies"], Parse("Kubernetes\ncuban eddies"));
    }

    [Fact]
    public void TheTermIsRejectedWhateverItsCase()
    {
        Assert.Empty(Parse("KUBERNETES\nkubernetes"));
    }

    [Fact]
    public void AnAliasTheUserAlreadyHasIsNotOfferedAgain()
    {
        Assert.Equal(
            ["cooper netties"],
            AliasSuggestions.Parse("cuban eddies\ncooper netties", "Kubernetes", ["cuban eddies"]));
    }

    [Fact]
    public void TheSameCandidateTwiceIsOfferedOnce()
    {
        Assert.Equal(["cuban eddies"], Parse("cuban eddies\nCuban Eddies"));
    }

    /// <summary>
    /// A mishearing of a word is about as long as the word. This rejects a model that returned a
    /// sentence, which would become an alias nobody could ever trigger.
    /// </summary>
    [Fact]
    public void ASentenceIsRejectedRatherThanBecomingAnAlias()
    {
        var sentence = new string('x', AliasSuggestions.MaximumLength + 1);

        Assert.Empty(Parse(sentence));
    }

    /// <summary>
    /// A line with no letters is a separator or a stray bullet. Accepting it would offer the user
    /// an alias made of punctuation.
    /// </summary>
    [Theory]
    [InlineData("---")]
    [InlineData("***")]
    [InlineData("123")]
    public void ALineWithNoLettersIsNotACandidate(string line)
    {
        Assert.Empty(Parse(line));
    }

    [Fact]
    public void TheListIsCappedSoTheTailOfItIsNotShown()
    {
        var many = string.Join("\n", Enumerable.Range(0, 20).Select(i => $"alias{i}"));

        Assert.Equal(AliasSuggestions.MaximumSuggestions, Parse(many).Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoReplyIsNoSuggestionsRatherThanACrash(string? reply)
    {
        Assert.Empty(Parse(reply));
    }

    /// <summary>
    /// A model that apologises or explains before listing still yields its list. The preamble is
    /// long prose and falls to the length check; the candidates survive.
    /// </summary>
    [Fact]
    public void APreambleDoesNotCostTheCandidatesBehindIt()
    {
        const string reply =
            "Sure! Here are some common ways speech recognition might mishear that term, based on "
            + "phonetic similarity and typical recogniser behaviour:\ncuban eddies\ncooper netties";

        Assert.Equal(["cuban eddies", "cooper netties"], Parse(reply));
    }
}
