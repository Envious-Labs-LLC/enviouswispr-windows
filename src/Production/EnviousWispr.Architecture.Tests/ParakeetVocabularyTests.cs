using EnviousWispr.ASR;

namespace EnviousWispr.Architecture.Tests;

public sealed class ParakeetVocabularyTests
{
    [Fact]
    public void LoadRequiresContiguousIdentifiersAndBlankToken()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "vocab.txt");
            File.WriteAllLines(path, ["<blk> 0", "\u2581hello 1", "! 2"]);

            var vocabulary = ParakeetVocabulary.Load(path);

            Assert.Equal(3, vocabulary.Size);
            Assert.Equal(0, vocabulary.BlankIndex);
            Assert.Equal(" hello", vocabulary.Tokens[1]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("hello 1")]
    [InlineData("<blk> 0\nhello 2")]
    [InlineData("<blk> 0\nagain 0")]
    [InlineData("<blk> nope")]
    public void LoadRejectsMalformedVocabulary(string contents)
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "vocab.txt");
            File.WriteAllText(path, contents.Replace("\n", Environment.NewLine, StringComparison.Ordinal));

            Assert.Throws<InvalidDataException>(() => ParakeetVocabulary.Load(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FrenchBlocklistMatchesOnlyFrenchAndCoversExactFortyFourTokensWithAndWithoutBoundaryChar()
    {
        var allFortyFour = new[]
        {
            " the", " The", " and", " And", " they", " you", " You", " with",
            " that", " That", " this", " This", " have", " from", " was", " Was",
            " were", " are", " been", " would", " could", " will", " their", " there",
            " when", " what", " where", " which", " who", " not", " But", " but",
            " So", " so", " It", " we", " We", " our", " your", " my",
            " him", " her", " them", " these",
        };
        var rawTokens = allFortyFour
            .Select((token, index) => index % 2 == 0 ? token : "\u2581" + token[1..])
            .Concat(["\u2581le", "the", " themost", "<unk>", "<blk>"])
            .ToArray();
        var vocabulary = ParakeetVocabulary.FromTokens(rawTokens, blankIndex: rawTokens.Length - 1);

        var frenchMask = vocabulary.FrenchBlocklistMaskFor("fr");
        Assert.NotNull(frenchMask);
        for (var i = 0; i < allFortyFour.Length; i++)
        {
            Assert.True(frenchMask![i], $"Expected French English blocklist to include '{allFortyFour[i]}'.");
        }

        Assert.False(frenchMask![allFortyFour.Length]);     // "\u2581le"
        Assert.False(frenchMask[allFortyFour.Length + 1]);  // "the" without leading word boundary
        Assert.False(frenchMask[allFortyFour.Length + 2]);  // " themost"
        Assert.False(frenchMask[allFortyFour.Length + 3]);  // "<unk>"
        Assert.False(frenchMask[allFortyFour.Length + 4]);  // "<blk>"

        foreach (var other in new[] { null, "", "auto", "en", "de", "nl", "it", "es", "pl", "ru", "el" })
        {
            Assert.Null(vocabulary.FrenchBlocklistMaskFor(other));
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"EnviousWispr-Vocab-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
