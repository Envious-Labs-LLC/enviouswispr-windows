namespace EnviousWispr.ASR;

internal sealed class ParakeetVocabulary
{
    private static readonly HashSet<string> FrenchEnglishBlocklistTokens = new(StringComparer.Ordinal)
    {
        " the", " The",
        " and", " And",
        " they",
        " you", " You",
        " with",
        " that", " That",
        " this", " This",
        " have",
        " from",
        " was", " Was",
        " were",
        " are",
        " been",
        " would",
        " could",
        " will",
        " their",
        " there",
        " when",
        " what",
        " where",
        " which",
        " who",
        " not",
        " But", " but",
        " So", " so",
        " It",
        " we", " We",
        " our",
        " your",
        " my",
        " him",
        " her",
        " them",
        " these",
    };

    private readonly bool[] _latinMask;
    private readonly bool[] _cyrillicMask;
    private readonly bool[] _greekMask;
    private readonly bool[] _frenchEnglishBlocklistMask;

    private ParakeetVocabulary(string[] tokens, int blankIndex)
    {
        Tokens = tokens;
        BlankIndex = blankIndex;
        _latinMask = BuildScriptMask(tokens, blankIndex, ParakeetScript.Latin);
        _cyrillicMask = BuildScriptMask(tokens, blankIndex, ParakeetScript.Cyrillic);
        _greekMask = BuildScriptMask(tokens, blankIndex, ParakeetScript.Greek);
        _frenchEnglishBlocklistMask = BuildFrenchEnglishBlocklistMask(tokens, blankIndex);
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

    /// <summary>
    /// Returns the French-only English-exclusive word blocklist mask matching macOS <c>TdtDecoderV3</c> (#630/#840/#1678),
    /// or null for every other language so non-French Latin languages remain untouched.
    /// </summary>
    public bool[]? FrenchBlocklistMaskFor(string? effectiveLanguage) =>
        string.Equals(effectiveLanguage, "fr", StringComparison.OrdinalIgnoreCase)
            ? _frenchEnglishBlocklistMask
            : null;

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
        bool[]? allowedTokenMask) =>
        SelectToken(logits, blankIndex, allowedTokenMask, blockedTokenMask: null);

    internal static int SelectToken(
        ReadOnlySpan<float> logits,
        int blankIndex,
        bool[]? allowedTokenMask,
        bool[]? blockedTokenMask)
    {
        var selected = IndexOfMaximum(logits);
        if (selected == blankIndex)
        {
            return selected;
        }

        if (allowedTokenMask is not null &&
            ((uint)selected >= (uint)allowedTokenMask.Length || !allowedTokenMask[selected]))
        {
            var scriptFallback = FindBestAllowedToken(
                logits,
                blankIndex,
                allowedTokenMask,
                blockedTokenMask: null);
            if (scriptFallback >= 0)
            {
                selected = scriptFallback;
            }
        }

        if (blockedTokenMask is not null &&
            (uint)selected < (uint)blockedTokenMask.Length &&
            blockedTokenMask[selected])
        {
            var blocklistFallback = FindBestAllowedToken(
                logits,
                blankIndex,
                allowedTokenMask,
                blockedTokenMask);
            if (blocklistFallback >= 0)
            {
                selected = blocklistFallback;
            }
        }

        return selected;
    }

    private static int FindBestAllowedToken(
        ReadOnlySpan<float> logits,
        int blankIndex,
        bool[]? allowedTokenMask,
        bool[]? blockedTokenMask)
    {
        var limit = allowedTokenMask is not null
            ? Math.Min(logits.Length, allowedTokenMask.Length)
            : logits.Length;
        var bestIndex = -1;
        var bestValue = float.NegativeInfinity;
        for (var index = 0; index < limit; index++)
        {
            if (index == blankIndex)
            {
                continue;
            }

            if (allowedTokenMask is not null && !allowedTokenMask[index])
            {
                continue;
            }

            if (blockedTokenMask is not null &&
                (uint)index < (uint)blockedTokenMask.Length &&
                blockedTokenMask[index])
            {
                continue;
            }

            if (logits[index] > bestValue)
            {
                bestValue = logits[index];
                bestIndex = index;
            }
        }

        return bestIndex;
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

    private static bool[] BuildFrenchEnglishBlocklistMask(string[] tokens, int blankIndex)
    {
        var mask = new bool[tokens.Length];
        for (var index = 0; index < tokens.Length; index++)
        {
            if (index == blankIndex || IsControlToken(tokens[index]) || string.IsNullOrEmpty(tokens[index]))
            {
                continue;
            }

            var normalized = tokens[index].Replace('\u2581', ' ');
            mask[index] = FrenchEnglishBlocklistTokens.Contains(normalized);
        }

        return mask;
    }

    internal static int IndexOfMaximum(ReadOnlySpan<float> values)
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
