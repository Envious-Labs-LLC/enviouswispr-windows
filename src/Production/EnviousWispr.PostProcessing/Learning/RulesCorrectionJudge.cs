using System.Security.Cryptography;
using System.Text;
using EnviousWispr.Core.Learning;

namespace EnviousWispr.PostProcessing.Learning;

/// <summary>
/// Deterministic rules correction judge v2 (exact port of macOS <c>RulesCorrectionJudge.swift</c>).
/// Distinguishes spelling/name/acronym fixes from grammar, punctuation, formatting, or rewordings
/// with zero network or model cost.
/// </summary>
public sealed class RulesCorrectionJudge : ICorrectionJudge
{
    public sealed record JudgePolicy(
        double CorrectionSimilarity = 0.55,
        double NameSimilarity = 0.30,
        int MaxRunWords = 4,
        int MinAcronymLetters = 2,
        int MaxAcronymLetters = 6,
        int MaxInputUtf8Bytes = 256,
        int MaxComparisonCharacters = 64,
        int MinMorphStemCharacters = 4)
    {
        public static JudgePolicy V2 { get; } = new();
    }

    private sealed record V2Word(string Surface, string Key);

    private sealed class V2Family
    {
        public V2Family(string forms, bool functionWords)
        {
            Forms = forms.Split('|');
            Keys = Forms.Select(NormaliseKey).ToHashSet(StringComparer.Ordinal);
            FunctionWords = functionWords;
        }

        public string[] Forms { get; }
        public HashSet<string> Keys { get; }
        public bool FunctionWords { get; }
    }

    private static readonly Dictionary<string, string> LetterNames = new(StringComparer.Ordinal)
    {
        ["a"] = "a", ["ay"] = "a", ["b"] = "b", ["bee"] = "b", ["be"] = "b", ["c"] = "c", ["cee"] = "c", ["see"] = "c",
        ["sea"] = "c", ["d"] = "d", ["dee"] = "d", ["e"] = "e", ["ee"] = "e", ["f"] = "f", ["ef"] = "f", ["eff"] = "f",
        ["g"] = "g", ["gee"] = "g", ["h"] = "h", ["aitch"] = "h", ["i"] = "i", ["eye"] = "i", ["j"] = "j", ["jay"] = "j",
        ["k"] = "k", ["kay"] = "k", ["l"] = "l", ["el"] = "l", ["ell"] = "l", ["m"] = "m", ["em"] = "m", ["n"] = "n",
        ["en"] = "n", ["o"] = "o", ["oh"] = "o", ["p"] = "p", ["pee"] = "p", ["q"] = "q", ["queue"] = "q", ["cue"] = "q",
        ["r"] = "r", ["ar"] = "r", ["are"] = "r", ["s"] = "s", ["es"] = "s", ["ess"] = "s", ["t"] = "t", ["tee"] = "t",
        ["tea"] = "t", ["u"] = "u", ["you"] = "u", ["v"] = "v", ["vee"] = "v", ["w"] = "w", ["x"] = "x", ["ex"] = "x",
        ["y"] = "y", ["why"] = "y", ["z"] = "z", ["zee"] = "z", ["zed"] = "z",
        ["0"] = "0", ["1"] = "1", ["2"] = "2", ["3"] = "3", ["4"] = "4", ["5"] = "5", ["6"] = "6", ["7"] = "7", ["8"] = "8",
        ["9"] = "9",
    };

    private static readonly V2Family[] V2Families =
    [
        new("a|an|the|this|that|these|those|at|in|on|of|to|for|from|with|under|not|can|is|are|am|was|were|have|has|had|do|does|did|he|him|she|her|we|us", true),
        new("der|die|das|den|dem|des|ein|eine|einen|einem|mit|für|von|zu|in|an|auf|ich|du|bin|bist|ist|sind|war|waren|dir|dich", true),
        new("el|la|los|las|un|una|unos|unas|de|a|por|para|con|en|he|ha|han|había|yo|tu|tú", true),
        new("le|la|les|un|une|des|de|du|à|au|aux|dans|sur|nous|sommes|étions|est|sont", true),
        new("il|lo|la|i|gli|le|un|una|di|del|a|in|per|loro|hanno|avevano|è|sono", true),
        new("o|a|os|as|um|uma|de|do|da|em|no|na|por|para|eu|é|são|era", true),
        new("de|het|een|dit|dat|die|deze|in|op|aan|van|voor|wij|zijn|is|was|waren", true),
        new("en|ett|den|det|de|denna|detta|i|på|av|till|hon|är|var|har|hade", true),
        new("en|et|den|det|de|i|på|af|til|er|var|har|havde", true),
        new("en|ei|et|den|det|i|på|av|til|jeg|er|var|har|hadde", true),
        new("tämä|tuo|se|nämä|nuo|minä|sinä|hän|on|oli|ei", true),
        new("ten|ta|to|ci|te|w|na|do|z|od|on|jest|są|był", true),
        new("ten|ta|to|ti|ty|v|na|do|z|on|je|byl", true),
        new("un|o|niște|cel|cea|în|pe|de|la|cu|este|sunt|era", true),
        new("a|az|egy|ez|én|te|ő|van|volt|és|nem", true),
        new("bu|şu|o|ben|beni|benim|sen|senin|biz|ile|için|ve", true),
        new("di|ke|dari|pada|ini|itu|dan|yang", true),

        new("child|children", false),
        new("write|writes|wrote|written|writing", false),
        new("Haus|Hauses|Häuser|Häusern", false),
        new("heureux|heureuse|heureuses", false),
        new("stanco|stanca|stanchi|stanche", false),
        new("faço|faz|fazem|fiz|fez", false),
        new("bil|bilar|bilen|bilarna", false),
        new("stor|stora|stort|större", false),
        new("barn|barnet|børn|børnene", false),
        new("barn|barnet|barna|barnene", false),
        new("talo|talon|talot|talossa", false),
        new("kot|kota|koty|kotem", false),
        new("nowy|nowa|nowe|nowi", false),
        new("dům|domu|domy|domem", false),
        new("om|omul|oameni|oamenii", false),
        new("ház|házak|házban|házból", false),
        new("kitap|kitaplar|kitabı|kitabın", false),
        new("ev|evler|evde|evden|eve", false),
        new("hazır|hazırlar", false),
        new("geliyor|geldi|geliyordu", false),
        new("anak|anak-anak", false),
        new("buku|buku-buku", false),

        new("she is|she's", false),
        new("do not|don't", false),
        new("can not|cannot|can't", false),
        new("will not|won't", false),
        new("zu dem|zum", false),
        new("a el|al|de el|del", false),
        new("de le|du|à le|au|à les|aux", false),
        new("di il|del|a il|al|in il|nel", false),
        new("em o|no|em a|na|de o|do|de a|da", false),
        new("zo een|zo'n", false),
    ];

    private static readonly (string Left, string Right)[] V2MorphTails =
    [
        ("", "s"),
        ("", "es"),
        ("", "n"),
        ("", "en"),
        ("", "t"),
        ("a", "e"),
        ("o", "a"),
        ("o", "i"),
        ("e", "er"),
    ];

    private static readonly Dictionary<string, HashSet<int>> V2Membership = BuildMembership();
    private static readonly HashSet<string> V2FunctionWords = BuildFunctionWords();

    private readonly JudgePolicy _policy;

    public RulesCorrectionJudge(JudgePolicy? policy = null)
    {
        _policy = policy ?? JudgePolicy.V2;
    }

    public JudgePolicy CurrentPolicy => _policy;

    public ValueTask<CorrectionJudgeCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        var capabilities = new CorrectionJudgeCapabilities(
            CanRunOnThisDevice: true,
            ExecutionIdentity: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["arm"] = "rules",
                ["version"] = "rules-v2.0",
                ["os"] = Environment.OSVersion.VersionString,
            });
        return ValueTask.FromResult(capabilities);
    }

    public Task<CorrectionJudgeOutcome> JudgeAsync(
        CorrectionJudgeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult<CorrectionJudgeOutcome>(
                new CorrectionJudgeOutcome.Bypass(CorrectionJudgeBypass.Cancelled));
        }

        var decisions = request.Candidates
            .Select(c => new CorrectionJudgeDecision(
                c.Id,
                Classify(c.Original, c.Replacement)
                    ? CorrectionJudgeClass.CorrectionAndSafe
                    : CorrectionJudgeClass.NotCorrection))
            .ToArray();

        return Task.FromResult(CorrectionJudgeOutcome.Validated(decisions, request));
    }

    public bool Classify(string original, string replacement)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(replacement);

        if (Encoding.UTF8.GetByteCount(original) > _policy.MaxInputUtf8Bytes ||
            Encoding.UTF8.GetByteCount(replacement) > _policy.MaxInputUtf8Bytes)
        {
            return false;
        }

        var o = V2Words(original);
        var r = V2Words(replacement);
        if (o.Count == 0 || r.Count == 0)
        {
            return false;
        }

        if (ScoreText(o).Length > _policy.MaxComparisonCharacters ||
            ScoreText(r).Length > _policy.MaxComparisonCharacters)
        {
            return false;
        }

        if (o.Count <= _policy.MaxAcronymLetters &&
            ExtractAcronym(replacement, _policy) is { } acronym &&
            string.Equals(Spelled(Tokens(original)), acronym, StringComparison.Ordinal))
        {
            return true;
        }

        if (o.Count > _policy.MaxRunWords || r.Count > _policy.MaxRunWords)
        {
            return false;
        }

        var oJoined = Joined(o);
        var rJoined = Joined(r);
        if (string.Equals(oJoined, rJoined, StringComparison.Ordinal))
        {
            return IsCompoundJoin(original, o, r);
        }

        if (!oJoined.Any(char.IsLetter) || !rJoined.Any(char.IsLetter))
        {
            return false;
        }

        while (o.Count > 0 && r.Count > 0 && string.Equals(o[0].Key, r[0].Key, StringComparison.Ordinal))
        {
            o.RemoveAt(0);
            r.RemoveAt(0);
        }

        while (o.Count > 0 && r.Count > 0 && string.Equals(o[^1].Key, r[^1].Key, StringComparison.Ordinal))
        {
            o.RemoveAt(o.Count - 1);
            r.RemoveAt(r.Count - 1);
        }

        if (o.Count == 0 || r.Count == 0)
        {
            return false;
        }

        var residualO = Joined(o);
        var residualR = Joined(r);
        if (string.Equals(residualO, residualR, StringComparison.Ordinal))
        {
            return IsCompoundJoin(string.Join(' ', o.Select(w => w.Surface)), o, r);
        }

        if (!residualO.Any(char.IsLetter) || !residualR.Any(char.IsLetter))
        {
            return false;
        }

        if (o.Select(w => w.Key).OrderBy(k => k, StringComparer.Ordinal)
            .SequenceEqual(r.Select(w => w.Key).OrderBy(k => k, StringComparer.Ordinal), StringComparer.Ordinal))
        {
            return false;
        }

        if (IsOneFunctionInsertion(o, r))
        {
            return false;
        }

        if (IsGrammarRun(o, r))
        {
            return false;
        }

        if (o.Count == 1 || r.Count == 1)
        {
            return IsPositive(o, r);
        }

        if (o.Count != r.Count)
        {
            return false;
        }

        var sawChange = false;
        for (var i = 0; i < o.Count; i++)
        {
            var a = o[i];
            var b = r[i];
            if (string.Equals(a.Key, b.Key, StringComparison.Ordinal))
            {
                continue;
            }

            sawChange = true;
            if (IsGrammarAtom(a, b) || !IsPositive([a], [b]))
            {
                return false;
            }
        }

        return sawChange;
    }

    private bool IsPositive(IReadOnlyList<V2Word> o, IReadOnlyList<V2Word> r)
    {
        var spaced = TextSimilarity.CompositeWordScore(ScoreText(o), ScoreText(r));
        var joined = TextSimilarity.CompositeWordScore(Joined(o), Joined(r));
        var score = Math.Max(spaced, joined);

        if (score >= _policy.CorrectionSimilarity)
        {
            return true;
        }

        return r.Count == 1 && IsNameShaped(r[0].Surface) && score >= _policy.NameSimilarity;
    }

    private bool IsCompoundJoin(string original, IReadOnlyList<V2Word> o, IReadOnlyList<V2Word> r)
    {
        if (o.Count <= 1 || r.Count != 1)
        {
            return false;
        }

        if (!original.All(ch => char.IsLetter(ch) || char.IsWhiteSpace(ch)))
        {
            return false;
        }

        if (!IsStrongName(r[0].Surface, _policy))
        {
            return false;
        }

        return !o.Any(w => V2FunctionWords.Contains(w.Key));
    }

    private bool IsGrammarRun(IReadOnlyList<V2Word> o, IReadOnlyList<V2Word> r)
    {
        if (IsFamilyMatch(Joined(o), Joined(r)))
        {
            return true;
        }

        if (o.Count != r.Count)
        {
            return false;
        }

        for (var i = 0; i < o.Count; i++)
        {
            if (!string.Equals(o[i].Key, r[i].Key, StringComparison.Ordinal) && !IsGrammarAtom(o[i], r[i]))
            {
                return false;
            }
        }

        return true;
    }

    private bool IsGrammarAtom(V2Word a, V2Word b) =>
        IsFamilyMatch(a.Key, b.Key) || IsInflectionShape(a, b);

    private bool IsInflectionShape(V2Word a, V2Word b)
    {
        if (IsStrongName(a.Surface, _policy) || IsStrongName(b.Surface, _policy))
        {
            return false;
        }

        foreach (var (left, right) in V2MorphTails)
        {
            foreach (var (x, y) in new[] { (left, right), (right, left) })
            {
                if (!a.Key.EndsWith(x, StringComparison.Ordinal) ||
                    !b.Key.EndsWith(y, StringComparison.Ordinal))
                {
                    continue;
                }

                var aStem = a.Key[..^x.Length];
                var bStem = b.Key[..^y.Length];
                if (string.Equals(aStem, bStem, StringComparison.Ordinal) &&
                    aStem.Length >= _policy.MinMorphStemCharacters)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsOneFunctionInsertion(IReadOnlyList<V2Word> a, IReadOnlyList<V2Word> b)
    {
        IReadOnlyList<V2Word> longer;
        IReadOnlyList<V2Word> shorter;
        if (a.Count == b.Count + 1)
        {
            longer = a;
            shorter = b;
        }
        else if (b.Count == a.Count + 1)
        {
            longer = b;
            shorter = a;
        }
        else
        {
            return false;
        }

        var wanted = shorter.Select(w => w.Key).ToArray();
        for (var index = 0; index < longer.Count; index++)
        {
            if (!V2FunctionWords.Contains(longer[index].Key))
            {
                continue;
            }

            var removed = longer.Where((_, i) => i != index).Select(w => w.Key);
            if (removed.SequenceEqual(wanted, StringComparer.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsStrongName(string word, JudgePolicy policy)
    {
        if (!word.All(char.IsLetterOrDigit))
        {
            return false;
        }

        if (ExtractAcronym(word, policy) is not null)
        {
            return true;
        }

        var letters = word.Where(char.IsLetter).ToArray();
        return letters.Any(char.IsLower) && letters.Skip(1).Any(char.IsUpper);
    }

    public static bool IsNameShaped(string text)
    {
        foreach (var word in EditAlignment.SplitWords(text.Normalize(NormalizationForm.FormC)))
        {
            var letters = word.Where(char.IsLetter).ToArray();
            if (letters.Length < 2)
            {
                continue;
            }

            var upper = letters.Count(char.IsUpper);
            if (upper == letters.Length || char.IsUpper(letters[0]) || (upper > 0 && char.IsLower(letters[0])))
            {
                return true;
            }
        }

        return false;
    }

    public static string? ExtractAcronym(string text, JudgePolicy policy)
    {
        var word = EditAlignment.StripEdgePunctuation(text.Normalize(NormalizationForm.FormC).Trim());
        if (word.Length == 0 || word.Any(char.IsWhiteSpace))
        {
            return null;
        }

        var letters = word.Where(char.IsLetter).ToArray();
        if (word.Length < policy.MinAcronymLetters ||
            word.Length > policy.MaxAcronymLetters ||
            letters.Length == 0 ||
            !word.All(char.IsLetterOrDigit) ||
            !letters.All(char.IsUpper))
        {
            return null;
        }

        return word.ToLowerInvariant();
    }

    private static string? Spelled(IEnumerable<string> tokens)
    {
        var sb = new StringBuilder();
        foreach (var token in tokens)
        {
            if (!LetterNames.TryGetValue(token, out var letter))
            {
                return null;
            }

            sb.Append(letter);
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    private static IEnumerable<string> Tokens(string text) =>
        EditAlignment.SplitWords(text.Normalize(NormalizationForm.FormC))
            .Select(w => EditAlignment.StripEdgePunctuation(w).ToLowerInvariant())
            .Where(w => w.Length > 0);

    private static List<V2Word> V2Words(string text)
    {
        var result = new List<V2Word>();
        foreach (var raw in EditAlignment.SplitWords(text.Normalize(NormalizationForm.FormC)))
        {
            var surface = EditAlignment.StripEdgePunctuation(raw);
            var key = NormaliseKey(surface);
            if (key.Length > 0)
            {
                result.Add(new V2Word(surface, key));
            }
        }

        return result;
    }

    private static string NormaliseKey(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormC).ToLowerInvariant();
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }

    private static string Joined(IReadOnlyList<V2Word> words) =>
        string.Concat(words.Select(w => w.Key));

    private static string ScoreText(IReadOnlyList<V2Word> words) =>
        string.Join(' ', words.Select(w => w.Key));

    private static bool IsFamilyMatch(string a, string b) =>
        V2Membership.TryGetValue(a, out var left) &&
        V2Membership.TryGetValue(b, out var right) &&
        left.Overlaps(right);

    private static Dictionary<string, HashSet<int>> BuildMembership()
    {
        var map = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        for (var i = 0; i < V2Families.Length; i++)
        {
            foreach (var key in V2Families[i].Keys)
            {
                if (!map.TryGetValue(key, out var set))
                {
                    set = [];
                    map[key] = set;
                }

                set.Add(i);
            }
        }

        return map;
    }

    private static HashSet<string> BuildFunctionWords()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var family in V2Families)
        {
            if (family.FunctionWords)
            {
                set.UnionWith(family.Keys);
            }
        }

        return set;
    }
}
