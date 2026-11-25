namespace EnviousWispr.ASR;

internal sealed class ParakeetVocabulary
{
    private readonly bool[] _latinMask;
    private readonly bool[] _cyrillicMask;
    private readonly bool[] _greekMask;

    private ParakeetVocabulary(string[] tokens, int blankIndex)
    {
        Tokens = tokens;
        BlankIndex = blankIndex;
        _latinMask = BuildScriptMask(tokens, blankIndex, ParakeetScript.Latin);
        _cyrillicMask = BuildScriptMask(tokens, blankIndex, ParakeetScript.Cyrillic);
        _greekMask = BuildScriptMask(tokens, blankIndex, ParakeetScript.Greek);
    }

    public string[] Tokens { get; }

    public int Size => Tokens.Length;

    public int BlankIndex { get; }

    public bool[]? MaskFor(ParakeetScript? script) => script switch
    {
        ParakeetScript.Latin => _latinMask,
        ParakeetScript.Cyrillic => _cyrillicMask,
        ParakeetScript.Greek => _greekMask,
        _ => null,
    };

    internal static ParakeetVocabulary FromTokens(string[] tokens, int blankIndex)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentOutOfRangeException.ThrowIfNegative(blankIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(blankIndex, tokens.Length);
        return new ParakeetVocabulary(tokens, blankIndex);
    }

    internal static int SelectToken(
        ReadOnlySpan<float> logits,
        int blankIndex,
        bool[]? allowedTokenMask)
    {
        var top1 = IndexOfMaximum(logits);
        if (allowedTokenMask is null ||
            top1 == blankIndex ||
            (uint)top1 >= (uint)allowedTokenMask.Length ||
            allowedTokenMask[top1])
        {
            return top1;
        }

        var limit = Math.Min(logits.Length, allowedTokenMask.Length);
        var bestIndex = -1;
        var bestValue = float.NegativeInfinity;
        for (var index = 0; index < limit; index++)
        {
            if (index == blankIndex || !allowedTokenMask[index])
            {
                continue;
            }

            if (logits[index] > bestValue)
            {
                bestValue = logits[index];
                bestIndex = index;
            }
        }

        return bestIndex >= 0 ? bestIndex : top1;
    }

    internal static bool IsControlToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var trimmed = token.AsSpan().Trim();
        return trimmed.Length >= 2 && trimmed[0] == '<' && trimmed[^1] == '>';
    }

    private static bool[] BuildScriptMask(string[] tokens, int blankIndex, ParakeetScript script)
    {
        var mask = new bool[tokens.Length];
        for (var index = 0; index < tokens.Length; index++)
        {
            if (index == blankIndex || IsControlToken(tokens[index]))
            {
                continue;
            }

            mask[index] = TokenLanguageFilter.Matches(tokens[index], script);
        }

        return mask;
    }

    private static int IndexOfMaximum(ReadOnlySpan<float> values)
    {
        var bestIndex = 0;
        var bestValue = float.NegativeInfinity;
        for (var index = 0; index < values.Length; index++)
        {
            if (values[index] > bestValue)
            {
                bestValue = values[index];
                bestIndex = index;
            }
        }

        return bestIndex;
    }

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
