using System.Text;
using EnviousWispr.Core.Learning;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.PostProcessing.Learning;

/// <summary>
/// Pure candidate filter assigning each aligned edit run a disposition before judging.
/// Mirrors macOS <c>CorrectionCandidateFilter</c>.
/// </summary>
public static class CorrectionCandidateFilter
{
    public const int MaxSimilarityChars = 200;

    private static readonly string[] ContractionSuffixes = ["'s", "'t", "'re", "'ll", "'ve", "'d", "'m"];

    private static readonly HashSet<string> Stopwords = new(
        [
            "a", "an", "and", "are", "as", "at", "be", "but", "by", "for", "from",
            "he", "her", "his", "i", "in", "is", "it", "me", "my", "of", "on", "or",
            "our", "she", "so", "that", "the", "their", "them", "they", "this", "to",
            "up", "was", "we", "were", "with", "you", "your",
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, int> CardinalUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zero"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4,
        ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9,
        ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13,
        ["fourteen"] = 14, ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17,
        ["eighteen"] = 18, ["nineteen"] = 19,
    };

    private static readonly Dictionary<string, int> CardinalTens = new(StringComparer.OrdinalIgnoreCase)
    {
        ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50,
        ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90,
    };

    private static readonly Dictionary<string, int> OrdinalUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["first"] = 1, ["second"] = 2, ["third"] = 3, ["fourth"] = 4,
        ["fifth"] = 5, ["sixth"] = 6, ["seventh"] = 7, ["eighth"] = 8, ["ninth"] = 9,
    };

    private static readonly Dictionary<string, int> OrdinalStandalone = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tenth"] = 10, ["eleventh"] = 11, ["twelfth"] = 12, ["thirteenth"] = 13,
        ["fourteenth"] = 14, ["fifteenth"] = 15, ["sixteenth"] = 16, ["seventeenth"] = 17,
        ["eighteenth"] = 18, ["nineteenth"] = 19, ["twentieth"] = 20, ["thirtieth"] = 30,
        ["fortieth"] = 40, ["fiftieth"] = 50, ["sixtieth"] = 60, ["seventieth"] = 70,
        ["eightieth"] = 80, ["ninetieth"] = 90,
    };

    public enum IneligibleReason
    {
        StopwordPhrase,
        ContractionEnding,
        UnfinishedEdit,
        NumberFormatting,
        AliasOwnedElsewhere,
        BlockedByUser,
    }

    public abstract record Disposition
    {
        private Disposition() { }

        public sealed record Ineligible(IneligibleReason Reason) : Disposition;

        public sealed record AlreadyCovered : Disposition;

        public sealed record Candidate(LearnTargetState Target) : Disposition;
    }

    public sealed record Inputs(
        IReadOnlyList<CustomWordEntry> UserWords,
        IReadOnlyList<CustomWordEntry> PackTerms,
        IReadOnlySet<string> BlockedPairKeys);

    public sealed record Filtered(
        EditAlignment.Run Run,
        string PairKey,
        Disposition Disposition,
        double? Similarity);

    public sealed record Prepared(
        IReadOnlyList<CorrectionCandidate> Candidates,
        IReadOnlyDictionary<int, Filtered> ById,
        IReadOnlyList<Filtered> Overflow);

    private readonly record struct NumberForm(int Value, bool IsOrdinal);

    public static IReadOnlyList<Filtered> Filter(IReadOnlyList<EditAlignment.Run> runs, Inputs inputs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(inputs);

        var owners = BuildTriggerOwners(inputs.UserWords, inputs.PackTerms);
        var results = new List<Filtered>(runs.Count);
        foreach (var run in runs)
        {
            var pairKey = CorrectionPairKey.Make(run.CoreOriginal, run.CoreReplacement);
            var disposition = Dispose(run, pairKey, inputs, owners);
            var evidence = disposition is Disposition.Candidate ? ComputeSimilarity(run) : null;
            results.Add(new Filtered(run, pairKey, disposition, evidence));
        }

        return results;
    }

    private static Disposition Dispose(
        EditAlignment.Run run,
        string pairKey,
        Inputs inputs,
        Dictionary<string, string> owners)
    {
        var editedTokens = EditAlignment.SplitWords(run.CoreReplacement)
            .Select(NormalisedToken)
            .ToArray();
        if (editedTokens.Length > 0 && editedTokens.All(Stopwords.Contains))
        {
            return new Disposition.Ineligible(IneligibleReason.StopwordPhrase);
        }

        if (HasContractionEnding(run.CoreReplacement))
        {
            return new Disposition.Ineligible(IneligibleReason.ContractionEnding);
        }

        if (IsDeletionOnly(run.CoreOriginal, run.CoreReplacement))
        {
            return new Disposition.Ineligible(IneligibleReason.UnfinishedEdit);
        }

        if (IsNumberFormatting(run))
        {
            return new Disposition.Ineligible(IneligibleReason.NumberFormatting);
        }

        if (inputs.BlockedPairKeys.Contains(pairKey))
        {
            return new Disposition.Ineligible(IneligibleReason.BlockedByUser);
        }

        var target = ResolveTarget(run.CoreReplacement, inputs);
        if (target is not null && Covers(target, run.CoreOriginal))
        {
            return new Disposition.AlreadyCovered();
        }

        if (IsOwnedElsewhere(run, target, owners))
        {
            return new Disposition.Ineligible(IneligibleReason.AliasOwnedElsewhere);
        }

        return target is not null
            ? new Disposition.Candidate(new LearnTargetState.ExistingWord(target.Id))
            : new Disposition.Candidate(LearnTargetState.New);
    }

    public static bool IsDeletionOnly(string original, string corrected)
    {
        var o = original.ToLowerInvariant().ToCharArray();
        var c = corrected.ToLowerInvariant().ToCharArray();
        if (c.Length >= o.Length)
        {
            return false;
        }

        var removedLetters = o.Count(char.IsLetter) - c.Count(char.IsLetter);
        if (removedLetters < 2)
        {
            return false;
        }

        var ci = 0;
        foreach (var ch in o)
        {
            if (ci < c.Length && ch == c[ci])
            {
                ci++;
            }
        }

        if (ci != c.Length)
        {
            return false;
        }

        if (o.Length > MaxSimilarityChars)
        {
            return true;
        }

        return !CanRemoveOnlySeparateSingleLetters(o, c);
    }

    public static bool CanRemoveOnlySeparateSingleLetters(char[] original, char[] corrected)
    {
        var reach = new bool[corrected.Length + 1, 2];
        reach[0, 0] = true;

        foreach (var ch in original)
        {
            var next = new bool[corrected.Length + 1, 2];
            for (var j = 0; j <= corrected.Length; j++)
            {
                for (var k = 0; k <= 1; k++)
                {
                    if (!reach[j, k])
                    {
                        continue;
                    }

                    if (j < corrected.Length && ch == corrected[j])
                    {
                        next[j + 1, 0] = true;
                    }

                    var letters = k + (char.IsLetter(ch) ? 1 : 0);
                    if (letters < 2)
                    {
                        next[j, letters] = true;
                    }
                }
            }

            reach = next;
        }

        return reach[corrected.Length, 0] || reach[corrected.Length, 1];
    }

    public static bool IsNumberFormatting(EditAlignment.Run run)
    {
        var original = WithoutSentenceDecoration(run.Original);
        var replacement = WithoutSentenceDecoration(run.Replacement);
        if (!IsLettersOnlyPhrase(original) || ParseDigitNumber(replacement) is not { } digits)
        {
            return false;
        }

        return NumberWordReadings(original).Contains(digits);
    }

    public static CustomWordEntry? ResolveTarget(string corrected, Inputs inputs)
    {
        var key = CorrectionPairKey.Normalise(corrected);
        var user = inputs.UserWords.FirstOrDefault(w =>
            string.Equals(CorrectionPairKey.Normalise(w.Replacement), key, StringComparison.Ordinal));
        if (user is not null)
        {
            return user;
        }

        return inputs.PackTerms.FirstOrDefault(w =>
            string.Equals(CorrectionPairKey.Normalise(w.Replacement), key, StringComparison.Ordinal));
    }

    public static bool Covers(CustomWordEntry entry, string original)
    {
        var key = CorrectionPairKey.Normalise(original);
        if (string.Equals(CorrectionPairKey.Normalise(entry.Replacement), key, StringComparison.Ordinal))
        {
            return true;
        }

        return entry.Aliases.Any(alias =>
            string.Equals(CorrectionPairKey.Normalise(alias), key, StringComparison.Ordinal));
    }

    public static bool HasContractionEnding(string text)
    {
        var words = EditAlignment.SplitWords(text);
        if (words.Length == 0)
        {
            return false;
        }

        var word = words[^1].Replace('\u2019', '\'').ToLowerInvariant();
        word = TrimPunctuationExceptApostrophe(word);
        return ContractionSuffixes.Any(suffix =>
            word.EndsWith(suffix, StringComparison.Ordinal) && word.Length > suffix.Length);
    }

    public static Prepared Prepare(IReadOnlyList<Filtered> filtered)
    {
        var eligible = filtered
            .Select((item, index) => (Item: item, Index: index))
            .Where(pair => pair.Item.Disposition is Disposition.Candidate)
            .OrderByDescending(pair => StartsCapitalised(pair.Item.Run.CoreReplacement))
            .ThenBy(pair => pair.Index)
            .Select(pair => pair.Item)
            .ToArray();

        var kept = eligible.Take(CorrectionJudgeRequest.MaxCandidates).ToArray();
        var overflow = eligible.Skip(CorrectionJudgeRequest.MaxCandidates).ToArray();
        var byId = new Dictionary<int, Filtered>(kept.Length);
        var candidates = new List<CorrectionCandidate>(kept.Length);

        for (var i = 0; i < kept.Length; i++)
        {
            var id = i + 1;
            var item = kept[i];
            byId[id] = item;
            candidates.Add(new CorrectionCandidate(id, item.Run.CoreOriginal, item.Run.CoreReplacement));
        }

        return new Prepared(candidates, byId, overflow);
    }

    public static bool StartsCapitalised(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsLetter(rune))
            {
                return Rune.IsUpper(rune);
            }
        }

        return false;
    }

    public static Dictionary<string, string> BuildTriggerOwners(
        IReadOnlyList<CustomWordEntry> userWords,
        IReadOnlyList<CustomWordEntry> packTerms)
    {
        // Mirrors CustomWordCorrector ownership: aliases first (last user word wins),
        // then pack aliases (never overwriting user), then single-word/aliasless canonicals.
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        RegisterOwners(userWords, owners, overwrite: true);
        RegisterOwners(packTerms, owners, overwrite: false);
        return owners;
    }

    public static bool IsSurfaceOwnedElsewhere(
        string surface,
        string? allowedOwnerId,
        IReadOnlyDictionary<string, string> owners)
    {
        var key = CorrectionPairKey.Normalise(surface);
        return owners.TryGetValue(key, out var ownerId) &&
               !string.Equals(ownerId, allowedOwnerId, StringComparison.Ordinal);
    }

    private static void RegisterOwners(
        IReadOnlyList<CustomWordEntry> entries,
        Dictionary<string, string> owners,
        bool overwrite)
    {
        foreach (var entry in entries)
        {
            foreach (var alias in entry.Aliases)
            {
                var key = CorrectionPairKey.Normalise(alias);
                if (key.Length > 0 && (overwrite || !owners.ContainsKey(key)))
                {
                    owners[key] = entry.Id;
                }
            }
        }

        foreach (var entry in entries)
        {
            var key = CorrectionPairKey.Normalise(entry.Replacement);
            if (key.Length > 0 &&
                (!key.Contains(' ', StringComparison.Ordinal) || entry.Aliases.Count == 0) &&
                !owners.ContainsKey(key))
            {
                owners[key] = entry.Id;
            }
        }
    }

    private static bool IsOwnedElsewhere(
        EditAlignment.Run run,
        CustomWordEntry? target,
        Dictionary<string, string> owners) =>
        IsSurfaceOwnedElsewhere(run.CoreOriginal, target?.Id, owners) ||
        IsSurfaceOwnedElsewhere(run.CoreReplacement, target?.Id, owners);

    private static double? ComputeSimilarity(EditAlignment.Run run)
    {
        var o = run.CoreOriginal;
        var r = run.CoreReplacement;
        if (o.Length > MaxSimilarityChars || r.Length > MaxSimilarityChars)
        {
            return null;
        }

        if (!IsLatin(o) || !IsLatin(r))
        {
            return null;
        }

        return TextSimilarity.CompositeWordScore(o.ToLowerInvariant(), r.ToLowerInvariant());
    }

    private static bool IsLatin(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (!Rune.IsLetter(rune))
            {
                continue;
            }

            var v = rune.Value;
            var latin = (v is >= 0x0041 and <= 0x005A) ||
                        (v is >= 0x0061 and <= 0x007A) ||
                        v is 0x00AA or 0x00BA ||
                        (v is >= 0x00C0 and <= 0x024F) ||
                        (v is >= 0x1E00 and <= 0x1EFF);
            if (!latin)
            {
                return false;
            }
        }

        return true;
    }

    private static string WithoutSentenceDecoration(string text)
    {
        var s = text.Trim();
        var changed = true;
        while (changed && s.Length > 0)
        {
            changed = false;
            var last = s[^1];
            if (last is '.' or ',' or ';' or ':' or '!' or '?' or '"' or '\'' or '\u201C' or '\u201D' or '\u2018' or '\u2019' or '\u00AB' or '\u00BB')
            {
                s = s[..^1];
                changed = true;
                continue;
            }

            var first = s[0];
            if (first is '"' or '\'' or '\u201C' or '\u201D' or '\u2018' or '\u2019' or '\u00AB' or '\u00BB')
            {
                s = s[1..];
                changed = true;
            }
        }

        return s;
    }

    private static bool IsLettersOnlyPhrase(string text)
    {
        if (text.Length == 0 || !char.IsLetter(text[0]) || !char.IsLetter(text[^1]))
        {
            return false;
        }

        var previous = text[0];
        foreach (var ch in text)
        {
            if (ch is ' ' or '-')
            {
                if (!char.IsLetter(previous))
                {
                    return false;
                }
            }
            else if (!char.IsLetter(ch))
            {
                return false;
            }

            previous = ch;
        }

        return true;
    }

    private static NumberForm? ParseDigitNumber(string text)
    {
        var digitLen = 0;
        while (digitLen < text.Length && char.IsAsciiDigit(text[digitLen]))
        {
            digitLen++;
        }

        if (digitLen == 0 || digitLen > 9 || !int.TryParse(text[..digitLen], out var value))
        {
            return null;
        }

        var suffix = text[digitLen..];
        if (suffix.Length == 0)
        {
            return new NumberForm(value, IsOrdinal: false);
        }

        return string.Equals(suffix, EnglishOrdinalSuffix(value), StringComparison.Ordinal)
            ? new NumberForm(value, IsOrdinal: true)
            : null;
    }

    private static string EnglishOrdinalSuffix(int n)
    {
        if (n % 100 is >= 11 and <= 13)
        {
            return "th";
        }

        return (n % 10) switch
        {
            1 => "st",
            2 => "nd",
            3 => "rd",
            _ => "th",
        };
    }

    private static HashSet<NumberForm> NumberWordReadings(string word)
    {
        if (word.Contains(' ', StringComparison.Ordinal))
        {
            return [];
        }

        var readings = new HashSet<NumberForm>();
        var parts = word.ToLowerInvariant().Split('-');
        if (parts.Length == 1)
        {
            var w = parts[0];
            if (CardinalUnits.TryGetValue(w, out var cu))
            {
                readings.Add(new NumberForm(cu, false));
            }
            else if (CardinalTens.TryGetValue(w, out var ct))
            {
                readings.Add(new NumberForm(ct, false));
            }

            if (OrdinalUnits.TryGetValue(w, out var ou))
            {
                readings.Add(new NumberForm(ou, true));
            }
            else if (OrdinalStandalone.TryGetValue(w, out var os))
            {
                readings.Add(new NumberForm(os, true));
            }
        }
        else if (parts.Length == 2 && CardinalTens.TryGetValue(parts[0], out var tens))
        {
            if (CardinalUnits.TryGetValue(parts[1], out var u) && u is >= 1 and <= 9)
            {
                readings.Add(new NumberForm(tens + u, false));
            }

            if (OrdinalUnits.TryGetValue(parts[1], out var ou))
            {
                readings.Add(new NumberForm(tens + ou, true));
            }
        }

        return readings;
    }

    private static string TrimPunctuationExceptApostrophe(string text)
    {
        var start = 0;
        var end = text.Length;
        while (start < end && (char.IsPunctuation(text[start]) || char.IsSymbol(text[start])) && text[start] != '\'')
        {
            start++;
        }

        while (end > start && (char.IsPunctuation(text[end - 1]) || char.IsSymbol(text[end - 1])) && text[end - 1] != '\'')
        {
            end--;
        }

        return text.Substring(start, end - start);
    }

    private static string NormalisedToken(string token)
    {
        var sb = new StringBuilder(token.Length);
        foreach (var ch in token.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }
}
