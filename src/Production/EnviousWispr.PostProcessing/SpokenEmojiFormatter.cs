using System.Text.Json;
using System.Text.RegularExpressions;

namespace EnviousWispr.PostProcessing;

public sealed record EmojiDictionaryEntry(string Phrase, string Emoji, IReadOnlyList<string> Synonyms);

public sealed partial class SpokenEmojiFormatter
{
    public const double AmbiguityMargin = 0.05;
    public const int PhoneticMaximumLevenshtein = 2;
    public const int PhoneticMaximumLookbackTokens = 4;

    private static readonly HashSet<string> LiteralDiscussionNouns = new(
        [
            "category", "categories", "feature", "features", "name", "names", "symbol",
            "symbols", "word", "words", "button", "buttons", "glyph", "glyphs", "icon",
            "icons", "character", "characters", "version", "format", "library", "set",
            "picker", "keyboard", "meaning", "description", "usage", "shortcode", "unicode",
            "code",
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions DictionarySerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly Dictionary<string, EmojiDictionaryEntry> _surfaceEntries;
    private readonly Dictionary<string, IReadOnlyList<SurfaceEntry>> _phoneticIndex;
    private readonly int _maximumSurfaceWords;
    private readonly bool _phoneticEnabled;

    public SpokenEmojiFormatter(
        IReadOnlyList<EmojiDictionaryEntry> entries,
        bool phoneticEnabled = true)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Validate(entries);
        _phoneticEnabled = phoneticEnabled;

        var surfaces = new Dictionary<string, EmojiDictionaryEntry>(StringComparer.OrdinalIgnoreCase);
        var phonetic = new Dictionary<string, List<SurfaceEntry>>(StringComparer.Ordinal);
        var maximumWords = 1;
        foreach (var entry in entries)
        {
            foreach (var surface in new[] { entry.Phrase }.Concat(entry.Synonyms))
            {
                var normalized = NormalizeSurface(surface);
                surfaces.Add(normalized, entry);
                maximumWords = Math.Max(maximumWords, WordCount(normalized));
                AddPhoneticSurface(phonetic, normalized, entry, isWholeSurface: true);
                foreach (var token in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    AddPhoneticSurface(phonetic, token, entry, isWholeSurface: false);
                }
            }
        }

        _surfaceEntries = surfaces;
        _phoneticIndex = phonetic.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<SurfaceEntry>)pair.Value,
            StringComparer.Ordinal);
        _maximumSurfaceWords = maximumWords;
    }

    public static SpokenEmojiFormatter LoadBundled(bool phoneticEnabled = true)
    {
        try
        {
            const string resourceName =
                "EnviousWispr.PostProcessing.Resources.emoji-dictionary.json";
            using var stream = typeof(SpokenEmojiFormatter).Assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException("The bundled emoji dictionary is missing.");
            var entries = JsonSerializer.Deserialize<List<EmojiDictionaryEntry>>(
                stream,
                DictionarySerializerOptions)
                ?? throw new InvalidOperationException("The bundled emoji dictionary is invalid.");
            return new SpokenEmojiFormatter(entries, phoneticEnabled);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new InvalidOperationException("The bundled emoji dictionary is invalid.", exception);
        }
    }

    public string Format(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0 || !TriggerRegex().IsMatch(text))
        {
            return text;
        }

        var wordTokens = WordRegex().Matches(text)
            .Select(match => new WordToken(match.Value, match.Index, match.Length))
            .ToArray();
        var replacements = new List<Replacement>();
        for (var triggerIndex = 0; triggerIndex < wordTokens.Length; triggerIndex++)
        {
            var trigger = wordTokens[triggerIndex];
            if (!IsTrigger(trigger.Text) || IsLiteralDiscussion(text, wordTokens, triggerIndex))
            {
                continue;
            }

            var match = FindExact(text, wordTokens, triggerIndex) ??
                (_phoneticEnabled ? FindPhonetic(text, wordTokens, triggerIndex) : null);
            if (match is null || replacements.Any(existing => existing.Overlaps(match)))
            {
                continue;
            }

            replacements.Add(match);
        }

        if (replacements.Count == 0)
        {
            return text;
        }

        var result = text;
        foreach (var replacement in replacements.OrderByDescending(item => item.Start))
        {
            result = string.Concat(
                result.AsSpan(0, replacement.Start),
                replacement.Emoji,
                result.AsSpan(replacement.End));
        }

        return result;
    }

    public static void Validate(IReadOnlyList<EmojiDictionaryEntry> entries)
    {
        var surfaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Phrase) || string.IsNullOrWhiteSpace(entry.Emoji))
            {
                throw new ArgumentException("Emoji dictionary entries require a phrase and glyph.", nameof(entries));
            }

            if (entry.Synonyms is null)
            {
                throw new ArgumentException("Emoji dictionary synonyms cannot be null.", nameof(entries));
            }

            foreach (var surface in new[] { entry.Phrase }.Concat(entry.Synonyms))
            {
                var normalized = NormalizeSurface(surface);
                if (normalized.Length == 0 || ContainsTrigger(normalized))
                {
                    throw new ArgumentException("Emoji dictionary surfaces cannot be empty or contain trigger words.", nameof(entries));
                }

                if (!surfaces.Add(normalized))
                {
                    throw new ArgumentException("Emoji dictionary surfaces must be unique.", nameof(entries));
                }
            }

            if (ContainsTrigger(entry.Emoji))
            {
                throw new ArgumentException("Emoji glyphs cannot contain trigger words.", nameof(entries));
            }
        }
    }

    private Replacement? FindExact(string text, WordToken[] tokens, int triggerIndex)
    {
        var maximum = Math.Min(_maximumSurfaceWords, triggerIndex);
        for (var length = maximum; length >= 1; length--)
        {
            var start = triggerIndex - length;
            if (!HasAllowedSeparators(text, tokens, start, triggerIndex))
            {
                continue;
            }

            var surface = string.Join(' ', tokens[start..triggerIndex].Select(token => token.Text));
            if (_surfaceEntries.TryGetValue(surface, out var entry))
            {
                return new Replacement(tokens[start].Start, tokens[triggerIndex].End, entry.Emoji);
            }
        }

        return null;
    }

    private Replacement? FindPhonetic(string text, WordToken[] tokens, int triggerIndex)
    {
        var maximum = Math.Min(PhoneticMaximumLookbackTokens, triggerIndex);
        for (var length = maximum; length >= 1; length--)
        {
            var start = triggerIndex - length;
            if (!HasAllowedSeparators(text, tokens, start, triggerIndex))
            {
                continue;
            }

            var phrase = NormalizeSurface(string.Join(' ', tokens[start..triggerIndex].Select(token => token.Text)));
            if (!_phoneticIndex.TryGetValue(TextSimilarity.Soundex(phrase), out var candidates))
            {
                continue;
            }

            var scored = candidates
                .Select(candidate => new
                {
                    Candidate = candidate,
                    Score = TextSimilarity.LevenshteinSimilarity(phrase, candidate.Surface),
                })
                .ToArray();
            var preferred = scored.Any(item =>
                item.Candidate.IsWholeSurface &&
                (1 - item.Score) * Math.Max(phrase.Length, item.Candidate.Surface.Length) <=
                    PhoneticMaximumLevenshtein)
                ? scored.Where(item => item.Candidate.IsWholeSurface)
                : scored.AsEnumerable();
            var ranked = preferred
                .OrderByDescending(item => item.Score)
                .ToArray();
            if (ranked.Length == 0)
            {
                continue;
            }

            var best = ranked[0];
            var second = ranked.Skip(1).FirstOrDefault(item => !string.Equals(
                item.Candidate.Entry.Phrase,
                best.Candidate.Entry.Phrase,
                StringComparison.OrdinalIgnoreCase));
            var maximumLength = Math.Max(phrase.Length, best.Candidate.Surface.Length);
            var distance = (int)((1 - best.Score) * maximumLength);
            if (distance > PhoneticMaximumLevenshtein ||
                second is not null && best.Score - second.Score < AmbiguityMargin)
            {
                continue;
            }

            return new Replacement(
                tokens[start].Start,
                tokens[triggerIndex].End,
                best.Candidate.Entry.Emoji);
        }

        return null;
    }

    private static bool IsLiteralDiscussion(string text, WordToken[] tokens, int triggerIndex)
    {
        if (triggerIndex + 1 >= tokens.Length ||
            !LiteralDiscussionNouns.Contains(tokens[triggerIndex + 1].Text))
        {
            return false;
        }

        var trigger = tokens[triggerIndex];
        var next = tokens[triggerIndex + 1];
        return text.AsSpan(trigger.End, next.Start - trigger.End).Trim().Length == 0;
    }

    private static bool HasAllowedSeparators(
        string text,
        WordToken[] tokens,
        int start,
        int triggerIndex)
    {
        for (var index = start; index < triggerIndex; index++)
        {
            var separator = text.AsSpan(
                tokens[index].End,
                tokens[index + 1].Start - tokens[index].End);
            if (separator.Length == 0 ||
                separator.IndexOfAnyExcept(" \t\r\n,.!?\u2014\u2013-".AsSpan()) >= 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsTrigger(string value) =>
        value.Equals("emoji", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("emoticon", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsTrigger(string value) =>
        value.Contains("emoji", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("emoticon", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeSurface(string value) =>
        WhitespaceRegex().Replace(value.Trim(), " ").ToLowerInvariant();

    private static int WordCount(string value) =>
        value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    private static void AddPhoneticSurface(
        Dictionary<string, List<SurfaceEntry>> index,
        string surface,
        EmojiDictionaryEntry entry,
        bool isWholeSurface)
    {
        var code = TextSimilarity.Soundex(surface);
        if (!index.TryGetValue(code, out var values))
        {
            values = [];
            index.Add(code, values);
        }

        if (!values.Any(value =>
            value.Surface.Equals(surface, StringComparison.OrdinalIgnoreCase) &&
            value.Entry.Phrase.Equals(entry.Phrase, StringComparison.OrdinalIgnoreCase) &&
            value.IsWholeSurface == isWholeSurface))
        {
            values.Add(new SurfaceEntry(surface, entry, isWholeSurface));
        }
    }

    private sealed record SurfaceEntry(
        string Surface,
        EmojiDictionaryEntry Entry,
        bool IsWholeSurface);

    private sealed record WordToken(string Text, int Start, int Length)
    {
        public int End => Start + Length;
    }

    private sealed record Replacement(int Start, int End, string Emoji)
    {
        public bool Overlaps(Replacement other) => Start < other.End && other.Start < End;
    }

    [GeneratedRegex(@"\b(?:emoji|emoticon)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TriggerRegex();

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}'\u2019-]*", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
