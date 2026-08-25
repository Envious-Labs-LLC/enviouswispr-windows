namespace EnviousWispr.Asr;

/// Parakeet TDT v3 vocabulary (vocab.txt, "token id" lines, 8193 entries).
/// U+2581 subword space marker is replaced with a plain space at load time,
/// exactly as the onnx-asr reference does.
public sealed class Vocab
{
    public string[] Tokens { get; }       // token id → decoded text piece
    public int VocabSize { get; }
    public int BlankIdx { get; }

    private Vocab(string[] tokens, int blankIdx)
    {
        Tokens = tokens;
        VocabSize = tokens.Length;
        BlankIdx = blankIdx;
    }

    public static Vocab Load(string path)
    {
        var maxId = -1;
        var lines = File.ReadAllLines(path);
        var byId = new Dictionary<int, string>();
        foreach (var line in lines)
        {
            if (line.Length == 0) continue;
            var sp = line.LastIndexOf(' ');
            if (sp < 0) continue;
            var token = line[..sp];
            if (!int.TryParse(line[(sp + 1)..], out var id)) continue;
            byId[id] = token.Replace("\u2581", " ");
            if (id > maxId) maxId = id;
        }

        var tokens = new string[maxId + 1];
        for (var i = 0; i <= maxId; i++) tokens[i] = byId.TryGetValue(i, out var t) ? t : "";

        var blankIdx = -1;
        foreach (var (id, t) in byId)
            if (t == "<blk>") { blankIdx = id; break; }
        if (blankIdx < 0) throw new InvalidDataException("vocab has no <blk> token");

        return new Vocab(tokens, blankIdx);
    }
}
