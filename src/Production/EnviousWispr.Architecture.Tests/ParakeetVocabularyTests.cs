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

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"EnviousWispr-Vocab-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
