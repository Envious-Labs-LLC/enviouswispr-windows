using EnviousWispr.Asr;

namespace EnviousWispr.Tests;

/// Vocab.txt contract ("token id" lines, 8193 entries in the real file):
/// id-ordering, <blk> discovery, U+2581 subword-space → plain space, and
/// tolerant handling of blank/malformed lines.
public class VocabTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("evw-vocab-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* test cleanup is best-effort */ }
    }

    private string WriteVocab(string content)
    {
        var path = Path.Combine(_dir, "vocab.txt");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Load_Parses_Tokens_Blank_And_Gaps()
    {
        var v = Vocab.Load(WriteVocab("<blk> 0\nhello 1\nworld 3\n"));
        Assert.Equal(4, v.VocabSize); // ids 0..3, array sized to maxId+1
        Assert.Equal(0, v.BlankIdx);
        Assert.Equal("hello", v.Tokens[1]);
        Assert.Equal("", v.Tokens[2]); // gap ids decode to empty
        Assert.Equal("world", v.Tokens[3]);
    }

    [Fact]
    public void Load_Replaces_Subword_Space_Marker()
    {
        var v = Vocab.Load(WriteVocab("<blk> 0\nhe\u2581llo 7\n"));
        Assert.Equal("he llo", v.Tokens[7]); // U+2581 → plain space, in place
    }

    [Fact]
    public void Load_Skips_Blank_And_Malformed_Lines()
    {
        var v = Vocab.Load(WriteVocab("<blk> 0\n\nno-id-here  \nfoo bar\nhi 2\n"));
        Assert.Equal(3, v.VocabSize);
        Assert.Equal("hi", v.Tokens[2]);
    }

    [Fact]
    public void Load_Without_Blank_Throws()
    {
        Assert.Throws<InvalidDataException>(() => Vocab.Load(WriteVocab("hello 0\nworld 1\n")));
    }
}
