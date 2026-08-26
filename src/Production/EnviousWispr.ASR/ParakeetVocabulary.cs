namespace EnviousWispr.ASR;

internal sealed class ParakeetVocabulary
{
    private ParakeetVocabulary(string[] tokens, int blankIndex)
    {
        Tokens = tokens;
        BlankIndex = blankIndex;
    }

    public string[] Tokens { get; }

    public int Size => Tokens.Length;

    public int BlankIndex { get; }

    public static ParakeetVocabulary Load(string path)
    {
        var byId = new Dictionary<int, string>();
        var maximumId = -1;
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0)
            {
                continue;
            }

            var separator = line.LastIndexOf(' ');
            if (separator < 0 ||
                !int.TryParse(
                    line.AsSpan(separator + 1),
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var id) ||
                id < 0)
            {
                throw new InvalidDataException("The Parakeet vocabulary is malformed.");
            }

            if (!byId.TryAdd(id, line[..separator].Replace('\u2581', ' ')))
            {
                throw new InvalidDataException("The Parakeet vocabulary contains a duplicate identifier.");
            }

            maximumId = Math.Max(maximumId, id);
        }

        if (maximumId < 0 || byId.Count != maximumId + 1)
        {
            throw new InvalidDataException("The Parakeet vocabulary is incomplete.");
        }

        var tokens = new string[maximumId + 1];
        foreach (var (id, token) in byId)
        {
            tokens[id] = token;
        }

        var blankIndex = Array.IndexOf(tokens, "<blk>");
        if (blankIndex < 0)
        {
            throw new InvalidDataException("The Parakeet vocabulary has no blank token.");
        }

        return new ParakeetVocabulary(tokens, blankIndex);
    }
}
