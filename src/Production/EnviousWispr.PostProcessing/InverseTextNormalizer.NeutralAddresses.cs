using System.Globalization;
using System.Text.RegularExpressions;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.PostProcessing;

public static partial class InverseTextNormalizer
{
    private sealed record SpokenUrlWords(
        string[] Dot,
        string[] Slash,
        string[] Colon,
        bool GlueSlash,
        string[] Query);

    private static readonly SpokenUrlWords[] NeutralSpokenUrlWords =
    [
        new(["point"], ["barre oblique", "bar oblique"], ["deux points", "deux-points"], false, []),
        new(["punto"], ["barra"], ["dos puntos"], false, []),
        new(["kropka"], ["ukośnik"], ["dwukropek"], false, []),
        new(["punt"], ["schuine streep"], ["dubbele punt"], true, []),
        new(["punkt"], ["schrägstrich"], ["doppelpunkt"], false, ["fragezeichen"]),
        new(["точка"], ["слэш", "слеш", "косая черта"], ["двоеточие"], false, ["вопросительный знак"]),
        new(["ponto"], ["barra"], ["dois pontos"], false, ["ponto de interrogação"]),
        new(["punto"], ["barra obliqua", "barra"], ["due punti"], false, ["punto interrogativo"]),
    ];

    private static readonly string[] NeutralCountryCodeTlds =
    [
        "de", "nl", "fr", "es", "pt", "pl", "se", "dk", "fi", "hu", "cz", "sk", "ru", "ua",
        "ch", "gr", "ie", "uk", "eu", "si", "hr", "lt", "lv", "ee", "bg", "ro",
    ];

    private static readonly string[] NeutralExtraTlds = ["br", "it"];

    private static readonly Dictionary<string, string> SpokenTldWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ру"] = "ru",
    };

    private static readonly Dictionary<string, HashSet<string>> NeutralAddressWordPairs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["at"] = new(["dot", "punkt"], StringComparer.OrdinalIgnoreCase),
        ["klammeraffe"] = new(["punkt"], StringComparer.OrdinalIgnoreCase),
        ["affenschwanz"] = new(["punkt"], StringComparer.OrdinalIgnoreCase),
        ["arroba"] = new(["punto", "ponto"], StringComparer.OrdinalIgnoreCase),
        ["chiocciola"] = new(["punto"], StringComparer.OrdinalIgnoreCase),
        ["apenstaartje"] = new(["punt"], StringComparer.OrdinalIgnoreCase),
        ["apestaartje"] = new(["punt"], StringComparer.OrdinalIgnoreCase),
        ["malpa"] = new(["kropka"], StringComparer.OrdinalIgnoreCase),
        ["małpa"] = new(["kropka"], StringComparer.OrdinalIgnoreCase),
        ["sobaka"] = new(["точка"], StringComparer.OrdinalIgnoreCase),
        ["собака"] = new(["точка"], StringComparer.OrdinalIgnoreCase),
        ["сабака"] = new(["кропка"], StringComparer.OrdinalIgnoreCase),
        ["малпа"] = new(["кропка"], StringComparer.OrdinalIgnoreCase),
        ["собачка"] = new(["крапка"], StringComparer.OrdinalIgnoreCase),
        ["равлик"] = new(["крапка"], StringComparer.OrdinalIgnoreCase),
        ["snabel-a"] = new(["punkt", "punktum"], StringComparer.OrdinalIgnoreCase),
        ["snabela"] = new(["punkt", "punktum"], StringComparer.OrdinalIgnoreCase),
        ["krøllalfa"] = new(["punktum", "prikk"], StringComparer.OrdinalIgnoreCase),
        ["kukac"] = new(["pont"], StringComparer.OrdinalIgnoreCase),
        ["arobase"] = new(["point"], StringComparer.OrdinalIgnoreCase),
        ["ät"] = new(["piste"], StringComparer.OrdinalIgnoreCase),
        ["miuku"] = new(["piste"], StringComparer.OrdinalIgnoreCase),
        ["miukumauku"] = new(["piste"], StringComparer.OrdinalIgnoreCase),
        ["kissanhäntä"] = new(["piste"], StringComparer.OrdinalIgnoreCase),
    };

    private static readonly HashSet<string> EnglishProseDomainWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "this", "that", "these", "those",
        "my", "your", "our", "his", "her", "its", "their",
        "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
    };

    private static readonly HashSet<string> NeutralNameRefusedWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "al", "de", "del", "en", "para", "por", "con", "y", "o", "e", "u", "es", "la", "el", "los",
        "las", "un", "una",
        "à", "au", "aux", "du", "des", "pour", "par", "avec", "et", "ou", "le", "les", "une", "chez",
        "do", "na", "w", "z", "i", "dla", "od", "to", "jest", "ze", "we",
        "naar", "aan", "voor", "van", "met", "of", "het", "een", "op", "in", "bij", "is",
        "an", "zu", "für", "und", "oder", "der", "die", "das", "ein", "eine", "mit", "bei",
    };

    private static readonly HashSet<string> RussianNameRefusedWords = new(["на", "и", "в", "это", "к", "для"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> PortugueseNameRefusedWords = new(["ao", "aos", "à", "que", "em", "o", "os"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ItalianNameRefusedWords = new(["ai", "alla", "allo", "alle", "il", "lo", "di", "della"], StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> DottedNameRefusedSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "js", "ts", "py", "rb", "go", "rs", "md", "txt", "html", "css", "json", "swift", "app",
        "pdf", "doc", "docx", "xls", "xlsx", "csv", "png", "jpg", "zip",
        "com", "net", "org", "io", "ai", "dev", "co",
    };

    private static readonly string[] NeutralOnlyDashWords =
    [
        "łącznik", "myślnik", "kreska",
        "streepje", "koppelteken", "koppel teken",
        "trait d'union",
        "дефис", "тире",
    ];

    private static readonly string[] NeutralEnglishDashWords = ["dash", "hyphen"];
    private static readonly string[] NeutralForeignDashWords =
    [
        "strich", "bindestrich", "tiret", "guion", "guión", "trattino", "traço", "hífen", "hifen",
    ];

    private static readonly HashSet<string> DutchDashWords = new(["streepje", "koppelteken", "koppel teken"], StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> DutchDigitWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nul"] = "0", ["één"] = "1", ["twee"] = "2", ["drie"] = "3", ["vier"] = "4",
        ["vijf"] = "5", ["zes"] = "6", ["zeven"] = "7", ["acht"] = "8", ["negen"] = "9",
    };

    private static readonly HashSet<string> NeutralGluedCueAtWords = new(["chiocciola", "arroba", "собака", "sobaka"], StringComparer.OrdinalIgnoreCase);

    private static readonly string[] SpokenQueryWords =
    [
        "point d'interrogation", "signo de interrogación", "signo de interrogacion",
        "znak zapytania", "vraagteken", "question mark",
    ];

    private static readonly string[] AllNeutralQueryWords = SpokenQueryWords
        .Concat(NeutralSpokenUrlWords.SelectMany(static w => w.Query))
        .ToArray();

    private static readonly string NeutralAddressAtAlt = BuildPhraseAlternation(NeutralAddressWordPairs.Keys);
    private static readonly string NeutralAddressDotAltRaw = BuildPhraseAlternation(NeutralAddressWordPairs.Values.SelectMany(static s => s));
    private static readonly string NeutralAddressDotAlt = BuildNeutralDotAlt(NeutralAddressWordPairs.Values.SelectMany(static s => s).Distinct(StringComparer.OrdinalIgnoreCase));
    private static readonly string NeutralDashWordAlt = BuildPhraseAlternation(NeutralForeignDashWords.Concat(NeutralOnlyDashWords).Concat(NeutralEnglishDashWords));
    private static readonly string NeutralSlashAllAlt = BuildPhraseAlternation(NeutralSpokenUrlWords.SelectMany(static w => w.Slash));

    private static readonly string[] NeutralBaseEmailTlds =
    [
        .. (new[] { "com", "org", "io", "co", "dev", "me", "net", "edu", "gov" }),
        .. NeutralCountryCodeTlds,
    ];

    private static readonly string EmailTldAlt = Alternation(NeutralBaseEmailTlds);
    private static readonly string NeutralEmailTldAlt = Alternation(NeutralBaseEmailTlds.Concat(NeutralExtraTlds).Concat(SpokenTldWords.Keys));
    private static readonly string NeutralHostTldAlt = Alternation(
        new[] { "com", "org", "io", "co", "dev", "me", "net", "edu", "gov", "ai", "app", "xyz" }
            .Concat(NeutralCountryCodeTlds)
            .Concat(NeutralExtraTlds)
            .Concat(SpokenTldWords.Keys));

    private const string ULabel = @"(?>[\p{L}\p{N}][\p{L}\p{M}\p{N}_-]*)(?<![_-])";
    private const string NeutralWwwAlias =
        @"(?:www|w\s+w\s+w|wu\s+wu\s+wu|uve\s+doble\s+uve\s+doble\s+uve\s+doble|triple\s+w|we\s+we\s+we|вэ\s+вэ\s+вэ|dáblio\s+dáblio\s+dáblio|vu\s+vu\s+vu)";
    private const string NeutralNumberDotWordAlt = "point|dot|punkt|punto|ponto|kropka|punt|точка";

    private const string NeutralAddressCuePattern =
        @"\p{L}*(?:mail|adres|correo)\p{L}*|(?:^|[^\p{L}])(?:napisz|wyślij|bericht)(?:[^\p{L}]|$)";
    private const string NeutralGluedAtWordCuePattern =
        @"\p{L}*(?:indirizzo|endereço|mensagem|адрес|письм|почт)\p{L}*|(?:^|[^\p{L}])(?:scrivi|scrivimi|manda)(?:[^\p{L}]|$)";
    private const string PortugueseAddressCuePattern = @"\p{L}*(?:endereço|mensagem)\p{L}*";

    private static readonly string[] NeutralAtWordList = NeutralAddressWordPairs.Keys.ToArray();
    private static readonly string[] NeutralGluedAtWordList =
    [
        "apenstaartje", "apestaartje", "chiocciola", "arroba", "собака", "małpa", "malpa", "sobaka",
    ];
    private static readonly string[] NeutralUrlSlashWords =
        NeutralSpokenUrlWords.SelectMany(static w => w.Slash).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private static readonly string[] NeutralDashWordList =
        NeutralForeignDashWords.Concat(NeutralOnlyDashWords).Concat(NeutralEnglishDashWords).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private static readonly string[] DutchDigitWordList = DutchDigitWords.Keys.ToArray();

    private static readonly Regex NeutralDomainDotRegex =
        Compiled($@"\s({NeutralAddressDotAltRaw})(?=\s)", ignoreCase: true);
    private static readonly Regex NeutralAsciiSepRegex =
        Compiled($@"(?:\.|\s+{NeutralAddressDotAlt}\s+)", ignoreCase: true);
    private static readonly Regex NeutralAsciiEmailCueRegex =
        Compiled(@"\be-?mail(?:\s+me)?(?:\s+at)?\s+$", ignoreCase: true);
    private static readonly Regex NeutralUnicodeDotSepRegex =
        Compiled($@"\s+{NeutralAddressDotAlt}\s+", ignoreCase: true);
    private static readonly Regex NeutralUnicodeTrailingDotRegex =
        Compiled($@"(?:{NeutralAddressDotAltRaw})\s+(?:{NeutralEmailTldAlt})$", ignoreCase: true);
    private static readonly Regex NeutralUnicodeDashRegex =
        Compiled($@"\s+(?:{NeutralDashWordAlt})\s+", ignoreCase: true);
    private static readonly Regex NeutralUnicodeSepRegex =
        Compiled($@"(?:\.|\s+{NeutralAddressDotAlt}\s+|\s+(?:{NeutralDashWordAlt})\s+)", ignoreCase: true);
    private static readonly Regex NeutralUnicodeNameCompoundSepRegex =
        Compiled($@"(?:\.|\s+{NeutralAddressDotAlt}\s+|\s+(?:{NeutralDashWordAlt})\s+|-)", ignoreCase: true);
    private static readonly Regex NeutralStartsAfterSpokenDotRegex =
        Compiled($@"(?:(?:^|\s)(?:{NeutralAddressDotAltRaw})\s+|\.)$", ignoreCase: true);
    private static readonly Regex NeutralStartsAfterSpokenDashRegex =
        Compiled($@"(?:^|\s)(?:{NeutralDashWordAlt})\s+$", ignoreCase: true);
    private static readonly Regex NeutralFurtherSpokenLabelRegex =
        Compiled($@"^\s+(?:{NeutralAddressDotAltRaw})\s+[\p{{L}}\p{{N}}]", ignoreCase: true);
    private static readonly Regex NeutralFollowedBySlashWordRegex =
        Compiled($@"^\s+(?:{NeutralSlashAllAlt})\s*[\p{{L}}\p{{N}}]", ignoreCase: true);
    private static readonly Regex NeutralFollowedByLiteralSlashRegex =
        Compiled(@"^\s*/\s*[\p{L}\p{N}]", ignoreCase: false);
    private static readonly Regex NeutralTrailingDotItRegex =
        Compiled(@"(?:^|\s)dot\s+$", ignoreCase: true);
    private static readonly Regex NeutralTrailingRuDotRegex =
        Compiled(@"(?:^|\s)точка\s+$", ignoreCase: true);
    private static readonly Regex NeutralAddressCueRegex =
        Compiled(NeutralAddressCuePattern, ignoreCase: true);
    private static readonly Regex NeutralGluedCueRegex =
        Compiled(NeutralGluedAtWordCuePattern, ignoreCase: true);
    private static readonly Regex PortugueseCueRegex =
        Compiled(PortugueseAddressCuePattern, ignoreCase: true);
    private static readonly Regex NeutralDottedCompoundRegex =
        Compiled($@"^{ULabel}(?:\.{ULabel}){{1,5}}$", ignoreCase: false);
    private static readonly Regex NeutralWwwAliasStartRegex =
        Compiled($@"^{NeutralWwwAlias}", ignoreCase: true);
    private static readonly Regex NeutralNonWwwAliasStartRegex =
        Compiled($@"^(?!www){NeutralWwwAlias}", ignoreCase: true);
    private static readonly Regex NeutralWwwAliasWordStartRegex =
        Compiled($@"^{NeutralWwwAlias}\s", ignoreCase: true);
    private static readonly Regex NeutralTrailingDigitsRegex =
        Compiled(@"\d+$", ignoreCase: false);
    private static readonly Regex NeutralHttpSpokenStartRegex =
        Compiled(@"^https?\s", ignoreCase: true);
    private static readonly Regex NeutralGluedSchuineRegex =
        Compiled(@"schuine\s+streep(?:je|jes|en|e|s)(?![\p{L}\p{M}\p{N}])", ignoreCase: true);
    private static readonly string NeutralAllUrlSyntaxAlt =
        BuildPhraseAlternation(NeutralSpokenUrlWords.SelectMany(static w => w.Dot.Concat(w.Slash).Concat(w.Colon)));
    private static readonly Regex NeutralWwwPrefilterRegex =
        Compiled(NeutralWwwAlias, ignoreCase: true);
    private static readonly Regex NeutralLinkStartsEarlierRegex =
        Compiled($@"(?:^|[^\p{{L}}])(?:https?|{NeutralAllUrlSyntaxAlt}|{NeutralAddressAtAlt}),?\s+$", ignoreCase: true);
    private static readonly Regex NeutralLinkStartsEarlierSlashRegex =
        Compiled(@"/\s*$", ignoreCase: false);
    private static readonly string NeutralLinkContinuesWithDotAlt =
        BuildPhraseAlternation(NeutralSpokenUrlWords.SelectMany(static w => w.Dot.Concat(w.Slash).Concat(w.Colon)).Concat(AllNeutralQueryWords));
    private static readonly string NeutralLinkContinuesWithoutDotAlt =
        BuildPhraseAlternation(NeutralSpokenUrlWords.SelectMany(static w => w.Slash.Concat(w.Colon)).Concat(AllNeutralQueryWords));
    private static readonly Regex NeutralLinkContinuesWithDotRegex =
        Compiled($@"^\s+(?:{NeutralLinkContinuesWithDotAlt}|{NeutralDashWordAlt})(?:\s+|$)", ignoreCase: true);
    private static readonly Regex NeutralLinkContinuesWithoutDotRegex =
        Compiled($@"^\s+(?:{NeutralLinkContinuesWithoutDotAlt}|{NeutralDashWordAlt})(?:\s+|$)", ignoreCase: true);
    private static readonly Regex NeutralLinkContinuesSlashRegex =
        Compiled(@"^\s*/", ignoreCase: false);
    private static readonly Regex NeutralLinkContinuesPunctRegex =
        Compiled(@"^[.:][\p{L}\p{N}]", ignoreCase: false);
    private static readonly Regex NeutralLinkContinuesWordRegex =
        Compiled(@"^[\p{L}\p{M}\p{N}_@-]", ignoreCase: false);
    private static readonly Regex NeutralDottedBeforeDigitRegex =
        Compiled(@"(?:^|\s)\d+\s+$", ignoreCase: false);
    private static readonly Regex NeutralDottedSepRegex =
        Compiled($@"[^\S\r\n]+(?:{NeutralNumberDotWordAlt})[^\S\r\n]+", ignoreCase: true);
    private static readonly Regex NeutralDashedArticleRegex =
        Compiled(@"(?:^|\s)[Aa]\s+$", ignoreCase: false);
    private static readonly Regex NeutralDashedTwoWordAlphaRegex =
        Compiled(@"(?:^|\s)[A-Za-z]\s+[Aa]\s+$", ignoreCase: false);
    private static readonly Regex NeutralDashedAlphaRegex =
        Compiled(@"(?:^|\s)[A-Za-z]\s+$", ignoreCase: false);
    private static readonly Regex NeutralNumberContBeforeRegex =
        Compiled($@"(?:^|\s)(?:{NeutralNumberDotWordAlt}|double|triple)\s+$", ignoreCase: true);
    private static readonly Regex NeutralNumberContAfterRegex =
        Compiled($@"^\s+(?:{NeutralNumberDotWordAlt}|double|triple)\s+\S", ignoreCase: true);
    private static readonly Regex NeutralDashContBeforeRegex =
        Compiled($@"(?:^|\s)(?:{NeutralDashWordAlt})\s+$", ignoreCase: true);
    private static readonly Regex NeutralDashContAfterRegex =
        Compiled($@"^\s+(?:{NeutralDashWordAlt})\s+\S", ignoreCase: true);
    private static readonly Regex NeutralEndsTwoPlusDigitsRegex =
        Compiled(@"\d{2,}$", ignoreCase: false);
    private static readonly Regex NeutralAfterSingleDigitRegex =
        Compiled(@"^\s+\d(?=\s|$)", ignoreCase: false);

    private sealed record CompiledUrlHelpers(
        Regex DotSplitRegex,
        Regex DotInSpacesRegex,
        Regex DotWordInSpacesRegex,
        Regex DotOrColonInSpacesRegex,
        Regex HostLocalhostRegex,
        Regex PathSplitRegex,
        string SchemePattern,
        string PathPattern,
        string WwwPattern);

    private static readonly Dictionary<SpokenUrlWords, CompiledUrlHelpers> NeutralUrlHelperCache =
        NeutralSpokenUrlWords.ToDictionary(
            static w => w,
            static w =>
            {
                var dotSplitPattern = $@"\.|\s+(?:{BuildPhraseAlternation(w.Dot)})\s+";
                var dotTokenPattern = $@"\s+{BuildNeutralDotAlt(w.Dot)}\s+";
                var dotWordPattern = $@"\s+(?:{BuildPhraseAlternation(w.Dot)})\s+";
                var dotColonPattern = $@"\s+(?:{BuildPhraseAlternation(w.Dot.Concat(w.Colon))})\s+";
                var localhostPattern = $@"^localhost(?:\s+(?:{BuildPhraseAlternation(w.Colon)})\s+\d{{1,5}})?$";
                var slash = NeutralSlashAlt(w);
                var pathPat = NeutralPathPat(w);
                var schemePattern =
                    $@"(?<![\p{{L}}\p{{N}}])(?<p>https?)(?:\s+(?:{BuildPhraseAlternation(w.Colon)})\s+{slash}\s+{slash}\s+|://\s*)(?<host>{NeutralHostOrLocalPat(w, portRequired: false)})(?<path>{pathPat.Pattern}*)(?![\p{{L}}\p{{M}}\p{{N}}_@-])";
                var urlPathsPattern =
                    $@"(?<![\p{{L}}\p{{M}}\p{{N}}_.@/:-])(?<host>{NeutralHostOrLocalPat(w, portRequired: true)})(?<path>{pathPat.Pattern}+)(?![\p{{L}}\p{{M}}\p{{N}}_@-])";
                var wwwPattern =
                    $@"(?<![\p{{L}}\p{{M}}\p{{N}}_.@/-])(?<host>{NeutralWwwAlias}(?:\.|{dotTokenPattern}){ULabel}(?:(?:\.|{dotTokenPattern}){ULabel}){{0,5}}(?:\.|{dotTokenPattern})(?:{NeutralHostTldAlt}))";
                return new CompiledUrlHelpers(
                    Compiled(dotSplitPattern, ignoreCase: true),
                    Compiled(dotTokenPattern, ignoreCase: true),
                    Compiled(dotWordPattern, ignoreCase: true),
                    Compiled(dotColonPattern, ignoreCase: true),
                    Compiled(localhostPattern, ignoreCase: true),
                    Compiled(pathPat.Split, ignoreCase: true),
                    schemePattern,
                    urlPathsPattern,
                    wwwPattern);
            });

    private static bool ContainsAnyIgnoreCase(string text, string[] needles)
    {
        foreach (var needle in needles)
        {
            if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsAsciiDigit(string text)
    {
        foreach (var ch in text)
        {
            if (ch is >= '0' and <= '9')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// #3210 / #3226 / #3233: Language-neutral ITN subset safe for non-English or unknown takes
    /// without invoking English number/time rules.
    /// </summary>
    public static string NormalizeLanguageNeutral(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var t = NeutralAsciiEmails(text);
        t = NeutralUnicodeEmails(t);
        t = NeutralGluedAtWordEmails(t);
        t = NeutralUrlSchemes(t);
        t = NeutralUrlPaths(t);
        t = NeutralWwwHosts(t);
        t = NeutralLocalhostPorts(t);
        t = NeutralDottedNumberChains(t);
        t = NeutralDashedCodes(t);
        return NeutralDashedDates(t);
    }

    public static string Normalize(string text, LanguageRuleSet ruleSet)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(ruleSet);
        return NormalizeLanguageNeutral(text);
    }

    private static string BuildPhraseAlternation(IEnumerable<string> phrases) => string.Join(
        '|',
        phrases.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(static p => p.Length)
            .ThenBy(static p => p, StringComparer.Ordinal)
            .Select(static p => string.Join(
                @"\s+",
                p.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(static part => Regex.Escape(part).Replace("'", @"['’]", StringComparison.Ordinal)))));

    private static string BuildNeutralDotAlt(IEnumerable<string> dots)
    {
        var dotList = dots.ToArray();
        var tails = AllNeutralQueryWords
            .Select(q =>
            {
                var d = dotList.FirstOrDefault(dot => q.StartsWith(dot + " ", StringComparison.OrdinalIgnoreCase));
                return d is null ? null : q[(d.Length + 1)..];
            })
            .Where(static t => !string.IsNullOrEmpty(t))
            .Select(static t => t!)
            .ToArray();
        var @base = $@"(?:{BuildPhraseAlternation(dotList)})";
        return tails.Length == 0
            ? @base
            : $@"{@base}(?!\s+(?:{BuildPhraseAlternation(tails)})(?![\p{{L}}\p{{M}}\p{{N}}]))";
    }

    private static bool IsPairedAddressWording(string atWord, string dotWord) =>
        NeutralAddressWordPairs.TryGetValue(atWord, out var dots) && dots.Contains(dotWord);

    private static bool NeutralTldSeparatorAllowed(string tld, string before) => tld.ToLowerInvariant() switch
    {
        "it" => !NeutralTrailingDotItRegex.IsMatch(before),
        "ру" => NeutralTrailingRuDotRegex.IsMatch(before),
        _ => true,
    };

    private static string WrittenTld(string tld) =>
        SpokenTldWords.TryGetValue(tld, out var mapped) ? mapped : tld;

    private static bool HasInnerSpokenTldWord(IEnumerable<string> labels) =>
        labels.Any(SpokenTldWords.ContainsKey);

    private static bool IsRefusedNeutralName(string[] labels, string atWord, string[] dots)
    {
        if (labels.Length != 1)
        {
            return false;
        }

        var name = labels[0].ToLowerInvariant();
        if (NeutralNameRefusedWords.Contains(name))
        {
            return true;
        }

        return atWord.ToLowerInvariant() switch
        {
            "собака" or "sobaka" => RussianNameRefusedWords.Contains(name),
            "chiocciola" => ItalianNameRefusedWords.Contains(name),
            "arroba" => dots.Contains("ponto", StringComparer.OrdinalIgnoreCase) && PortugueseNameRefusedWords.Contains(name),
            _ => false,
        };
    }

    private static string NeutralAsciiEmails(string t)
    {
        if (!ContainsAnyIgnoreCase(t, NeutralAtWordList))
        {
            return t;
        }

        var sep = $@"(?:\.|\s+{NeutralAddressDotAlt}\s+)";
        var pattern =
            $@"(?<![\w.@])(?<name>(?>[a-z][a-z0-9_]*)(?:{sep}(?>[a-z0-9_]+)){{0,5}})\s+(?<atw>{NeutralAddressAtAlt})\s+(?<dom>(?>[a-z][a-z0-9-]*)(?:{sep}(?>[a-z0-9][a-z0-9-]*)){{0,5}}){sep}(?<tld>{NeutralEmailTldAlt})\b(?!\.[a-z0-9])";

        return Replace(t, pattern, match =>
        {
            if (StartsAfterSpokenDot(t, match.Index))
            {
                return match.Value;
            }

            var atw = match.Groups["atw"].Value.ToLowerInvariant();
            var name = match.Groups["name"].Value;
            var dom = match.Groups["dom"].Value;
            var tld = match.Groups["tld"].Value;

            var atGroup = match.Groups["atw"];
            var domainHalf = " " + t[(atGroup.Index + atGroup.Length)..(match.Index + match.Length)] + " ";
            var domainDots = NeutralDomainDotRegex.Matches(domainHalf);
            if (domainDots.Count == 0)
            {
                return match.Value;
            }

            var allDots = NeutralDomainDotRegex.Matches(" " + match.Value + " ").Select(static m => m.Groups[1].Value).ToArray();
            if (!allDots.All(d => IsPairedAddressWording(atw, d)))
            {
                return match.Value;
            }

            var nameLabels = NeutralAsciiSepRegex.Split(name);
            var domLabels = NeutralAsciiSepRegex.Split(dom);
            if (IsRefusedNeutralName(nameLabels, atw, allDots))
            {
                return match.Value;
            }

            var beforeTld = t[match.Index..match.Groups["tld"].Index];
            if (!NeutralTldSeparatorAllowed(tld, beforeTld))
            {
                return match.Value;
            }

            var beforeName = t[Math.Max(0, match.Index - 24)..match.Index];
            var emailCue = NeutralAsciiEmailCueRegex.IsMatch(beforeName);
            if (atw == "at" && nameLabels.Length == 1 && domLabels.Length > 1 && !emailCue)
            {
                return match.Value;
            }

            if (atw == "at" && nameLabels.Length == 1 && allDots.Any(static d => d.Equals("dot", StringComparison.OrdinalIgnoreCase)))
            {
                return match.Value;
            }

            if (HasFurtherSpokenLabel(t, match.Index + match.Length) ||
                NeutralEmailFollowedBySlash(t, match.Index + match.Length) ||
                StartsAfterSpokenDash(t, match.Index))
            {
                return match.Value;
            }

            if (nameLabels.Length > 1 && DottedNameRefusedSuffixes.Contains(nameLabels[^1]))
            {
                return match.Value;
            }

            if (atw == "at" &&
                allDots.All(static d => d.Equals("dot", StringComparison.OrdinalIgnoreCase)) &&
                domLabels.Length > 0 &&
                EnglishProseDomainWords.Contains(domLabels[0]))
            {
                return match.Value;
            }

            return string.Join('.', nameLabels) + "@" + string.Join('.', domLabels.Append(WrittenTld(tld)));
        });
    }

    private static string NeutralUnicodeEmails(string t)
    {
        if (!ContainsAnyIgnoreCase(t, NeutralAtWordList))
        {
            return t;
        }

        var dot = $@"\s+{NeutralAddressDotAlt}\s+";
        var dash = $@"\s+(?:{NeutralDashWordAlt})\s+";
        var sep = $@"(?:\.|{dot}|{dash})";
        var pattern =
            $@"(?<![\p{{L}}\p{{M}}\p{{N}}_.@-])(?<name>{ULabel}(?:{sep}{ULabel}){{0,5}})\s+(?<atw>{NeutralAddressAtAlt})(?<comma>,)?\s+(?<dom>{ULabel}(?:{sep}{ULabel}){{0,5}}){sep}(?<tld>{NeutralEmailTldAlt})(?![\p{{L}}\p{{M}}\p{{N}}_-]|\.[\p{{L}}\p{{N}}])";

        return Replace(t, pattern, match =>
        {
            if (StartsAfterSpokenDot(t, match.Index) || StartsAfterSpokenDash(t, match.Index))
            {
                return match.Value;
            }

            var atw = match.Groups["atw"].Value.ToLowerInvariant();
            var dots = NeutralDomainDotRegex.Matches(" " + match.Value + " ").Select(static m => m.Groups[1].Value).ToArray();
            if (!dots.All(d => IsPairedAddressWording(atw, d)))
            {
                return match.Value;
            }

            var dom = match.Groups["dom"].Value;
            var tld = match.Groups["tld"].Value;
            var spokenDomainDot = NeutralUnicodeDotSepRegex.IsMatch(" " + dom + " ") ||
                NeutralUnicodeTrailingDotRegex.IsMatch(match.Value);
            if (atw == "at" && (dots.Length == 0 || !dots.All(static d => d.Equals("punkt", StringComparison.OrdinalIgnoreCase)) || !spokenDomainDot))
            {
                return match.Value;
            }

            var allAscii = (match.Value[..^tld.Length] + WrittenTld(tld)).All(char.IsAscii);
            var spokenDash = NeutralUnicodeDashRegex.IsMatch(match.Value);
            if (allAscii && spokenDomainDot && !spokenDash)
            {
                return match.Value;
            }

            var name = match.Groups["name"].Value;
            var sepRegex = NeutralUnicodeSepRegex;
            if (!spokenDomainDot)
            {
                var nameIsCompound = NeutralUnicodeNameCompoundSepRegex.Split(name).Length > 1;
                var newCue = NeutralGluedCueAtWords.Contains(atw) && HasCue(t, match.Index, NeutralGluedAtWordCuePattern);
                if (!nameIsCompound && !HasAddressCue(t, match.Index) && !newCue)
                {
                    return match.Value;
                }
            }

            if (match.Groups["comma"].Success && !((atw == "małpa" || atw == "malpa") && HasAddressCue(t, match.Index)))
            {
                return match.Value;
            }

            var end = match.Index + match.Length;
            if (HasFurtherSpokenLabel(t, end) || NeutralEmailFollowedBySlash(t, end))
            {
                return match.Value;
            }

            var nameLabels = sepRegex.Split(name);
            if (IsRefusedNeutralName(nameLabels, atw, dots))
            {
                return match.Value;
            }

            if (atw == "arroba" &&
                nameLabels.Length == 1 &&
                PortugueseNameRefusedWords.Contains(nameLabels[0]) &&
                HasCue(t, match.Index, PortugueseAddressCuePattern))
            {
                return match.Value;
            }

            var beforeTld = t[match.Index..match.Groups["tld"].Index];
            var domLabels = sepRegex.Split(dom);
            if (!NeutralTldSeparatorAllowed(tld, beforeTld) || HasInnerSpokenTldWord(domLabels))
            {
                return match.Value;
            }

            if (nameLabels.Length > 1 && DottedNameRefusedSuffixes.Contains(nameLabels[^1]))
            {
                return match.Value;
            }

            string Rewrite(string raw)
            {
                var rewritten = NeutralUnicodeDashRegex.Replace(raw, "-");
                return NeutralUnicodeDotSepRegex.Replace(rewritten, ".");
            }

            return Rewrite(name) + "@" + Rewrite(dom) + "." + WrittenTld(tld);
        });
    }

    private static string NeutralGluedAtWordEmails(string t)
    {
        if (!ContainsAnyIgnoreCase(t, NeutralGluedAtWordList))
        {
            return t;
        }

        var end = $@"\.(?<tld>{NeutralEmailTldAlt})(?![\p{{L}}\p{{M}}\p{{N}}_-]|\.[\p{{L}}\p{{N}}])";
        var dom = $@"(?<dom>{ULabel}(?:\.{ULabel}){{0,5}})";
        const string start = @"(?<![\p{L}\p{M}\p{N}_.@-])";
        var dotted = $@"{ULabel}(?:\.{ULabel}){{0,5}}";

        var outText = Replace(
            t,
            $@"{start}(?<name>[\p{{L}}\p{{N}}][\p{{L}}\p{{M}}\p{{N}}_.-]{{0,63}}?)(?<atw>apenstaartje|apestaartje)(?<gap>\s?){dom}\.(?<tld>{EmailTldAlt})(?![\p{{L}}\p{{M}}\p{{N}}_-]|\.[\p{{L}}\p{{N}}])",
            match =>
            {
                var name = match.Groups["name"].Value;
                var domain = match.Groups["dom"].Value;
                if (name.Length == 0 || name.EndsWith('.') || name.EndsWith('-') || NeutralNameRefusedWords.Contains(name))
                {
                    return match.Value;
                }

                if (match.Groups["gap"].Value.Length == 0 && domain.StartsWith("s", StringComparison.OrdinalIgnoreCase))
                {
                    return match.Value;
                }

                if (!HasAddressCue(t, match.Index) ||
                    NeutralEmailFollowedBySlash(t, match.Index + match.Length) ||
                    StartsAfterSpokenDot(t, match.Index) ||
                    StartsAfterSpokenDash(t, match.Index))
                {
                    return match.Value;
                }

                return $"{name}@{domain}.{match.Groups["tld"].Value}";
            });

        outText = ReplaceGluedAtWord(
            outText,
            $@"{start}(?<name>[\p{{L}}\p{{N}}][\p{{L}}\p{{M}}\p{{N}}_.-]{{0,63}}?)(?<![cC])[ \t]?(?<atw>chiocciola)\s?{dom}{end}",
            ItalianNameRefusedWords,
            pluralS: false,
            refusedOnlyAfter: null,
            requireCompoundName: false);

        outText = ReplaceGluedAtWord(
            outText,
            $@"{start}(?<name>{dotted})\s+(?<atw>arroba){dom}{end}",
            PortugueseNameRefusedWords,
            pluralS: true,
            refusedOnlyAfter: PortugueseAddressCuePattern,
            requireCompoundName: false);

        outText = ReplaceGluedAtWord(
            outText,
            $@"{start}(?<name>{dotted})\.(?<atw>собака)\.{dom}{end}",
            RussianNameRefusedWords,
            pluralS: false,
            refusedOnlyAfter: null,
            requireCompoundName: false);

        outText = ReplaceGluedAtWord(
            outText,
            $@"{start}(?<name>[\p{{L}}\p{{N}}][\p{{L}}\p{{M}}\p{{N}}_.-]{{0,63}}?)(?<atw>małpa|malpa)(?:\.|[ \t]+){dom}{end}",
            [],
            pluralS: false,
            refusedOnlyAfter: null,
            requireCompoundName: true);

        return ReplaceGluedAtWord(
            outText,
            $@"{start}(?<name>[\p{{L}}\p{{N}}][\p{{L}}\p{{M}}\p{{N}}_.-]{{0,63}}?)(?<atw>sobaka)(?:\.|[ \t]+){dom}{end}",
            RussianNameRefusedWords,
            pluralS: false,
            refusedOnlyAfter: null,
            requireCompoundName: true);
    }

    private static string ReplaceGluedAtWord(
        string t,
        string pattern,
        HashSet<string> refused,
        bool pluralS,
        string? refusedOnlyAfter,
        bool requireCompoundName)
    {
        return Replace(t, pattern, match =>
        {
            var name = match.Groups["name"].Value;
            var dom = match.Groups["dom"].Value;
            var tld = match.Groups["tld"].Value;

            if (requireCompoundName)
            {
                if (!NeutralDottedCompoundRegex.IsMatch(name))
                {
                    return match.Value;
                }

                var last = name.Split('.').LastOrDefault() ?? string.Empty;
                if (DottedNameRefusedSuffixes.Contains(last))
                {
                    return match.Value;
                }
            }

            if (name.Length == 0 || name.EndsWith('.') || name.EndsWith('-') || NeutralNameRefusedWords.Contains(name))
            {
                return match.Value;
            }

            if (refused.Contains(name) && (refusedOnlyAfter is null || HasCue(t, match.Index, refusedOnlyAfter)))
            {
                return match.Value;
            }

            if (pluralS && dom.StartsWith("s", StringComparison.OrdinalIgnoreCase))
            {
                return match.Value;
            }

            if (!NeutralTldSeparatorAllowed(tld, ".") || HasInnerSpokenTldWord(dom.Split('.')))
            {
                return match.Value;
            }

            var end = match.Index + match.Length;
            if (!(HasAddressCue(t, match.Index) || HasCue(t, match.Index, NeutralGluedAtWordCuePattern)) ||
                NeutralEmailFollowedBySlash(t, end) ||
                StartsAfterSpokenDot(t, match.Index) ||
                StartsAfterSpokenDash(t, match.Index) ||
                HasFurtherSpokenLabel(t, end))
            {
                return match.Value;
            }

            return $"{name}@{dom}.{WrittenTld(tld)}";
        });
    }

    private static bool StartsAfterSpokenDot(string text, int index)
    {
        var lead = Math.Min(index, 24);
        var before = text.Substring(index - lead, lead);
        return NeutralStartsAfterSpokenDotRegex.IsMatch(before);
    }

    private static bool StartsAfterSpokenDash(string text, int index)
    {
        var lead = Math.Min(index, 24);
        var before = text.Substring(index - lead, lead);
        return NeutralStartsAfterSpokenDashRegex.IsMatch(before);
    }

    private static bool HasCue(string text, int index, Regex cueRegex)
    {
        var lead = Math.Min(index, 48);
        return cueRegex.IsMatch(text.AsSpan(index - lead, lead));
    }

    private static bool HasCue(string text, int index, string pattern) => pattern switch
    {
        NeutralAddressCuePattern => HasCue(text, index, NeutralAddressCueRegex),
        NeutralGluedAtWordCuePattern => HasCue(text, index, NeutralGluedCueRegex),
        PortugueseAddressCuePattern => HasCue(text, index, PortugueseCueRegex),
        _ => HasCue(text, index, Compiled(pattern, ignoreCase: true)),
    };

    private static bool HasAddressCue(string text, int index) => HasCue(text, index, NeutralAddressCuePattern);

    private static bool HasFurtherSpokenLabel(string text, int end)
    {
        var tail = text.Substring(end, Math.Min(48, text.Length - end));
        return NeutralFurtherSpokenLabelRegex.IsMatch(tail);
    }

    private static bool NeutralEmailFollowedBySlash(string text, int end)
    {
        var rest = text.Substring(end, Math.Min(40, text.Length - end));
        return NeutralFollowedBySlashWordRegex.IsMatch(rest) ||
            NeutralFollowedByLiteralSlashRegex.IsMatch(rest);
    }

    private static string NeutralHostPat(SpokenUrlWords w)
    {
        var sep = $@"(?:\.|\s+{BuildNeutralDotAlt(w.Dot)}\s+)";
        return $@"(?:{NeutralWwwAlias}{sep})?{ULabel}(?:{sep}{ULabel}){{0,5}}{sep}(?:{NeutralHostTldAlt})";
    }

    private static string NeutralHostOrLocalPat(SpokenUrlWords w, bool portRequired)
    {
        var port = $@"\s+(?:{BuildPhraseAlternation(w.Colon)})\s+\d{{1,5}}";
        var ip = $@"\d{{1,3}}(?:(?:\.|\s+(?:{BuildPhraseAlternation(w.Dot)})\s+)\d{{1,3}}){{3}}";
        return $@"(?:{NeutralHostPat(w)}|{ip}|localhost(?:{port}{(portRequired ? ")" : ")?")})";
    }

    private static string[] NeutralHostLabels(string host, SpokenUrlWords w) =>
        NeutralUrlHelperCache[w].DotSplitRegex.Split(host);

    private static string? NeutralCanonicalHost(string raw, SpokenUrlWords w)
    {
        var helpers = NeutralUrlHelperCache[w];
        if (helpers.HostLocalhostRegex.IsMatch(raw))
        {
            var word = raw[..9];
            var portMatch = NeutralTrailingDigitsRegex.Match(raw);
            if (!portMatch.Success)
            {
                return word;
            }

            return int.TryParse(portMatch.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) &&
                   port is >= 1 and <= 65535
                ? $"{word}:{portMatch.Value}"
                : null;
        }

        var host = raw;
        var aliasMatch = NeutralWwwAliasStartRegex.Match(host);
        if (aliasMatch.Success && !aliasMatch.Value.Equals("www", StringComparison.OrdinalIgnoreCase))
        {
            host = "www" + host[aliasMatch.Length..];
        }

        var labels = NeutralHostLabels(host, w);
        if (labels.Length == 0 || HasInnerSpokenTldWord(labels.Take(labels.Length - 1)))
        {
            return null;
        }

        var tld = labels[^1];
        if (!NeutralTldSeparatorAllowed(tld, host[..^tld.Length]))
        {
            return null;
        }

        labels[^1] = WrittenTld(tld);
        return string.Join('.', labels);
    }

    private static string NeutralSlashAlt(SpokenUrlWords w)
    {
        var all = NeutralSpokenUrlWords.SelectMany(static row => row.Slash).ToArray();
        var branches = w.Slash
            .OrderByDescending(static p => p.Length)
            .Select(p =>
            {
                var tails = all
                    .Where(other => other.StartsWith(p + " ", StringComparison.OrdinalIgnoreCase))
                    .Select(other => other[(p.Length + 1)..])
                    .ToArray();
                var one = BuildPhraseAlternation([p]);
                return tails.Length == 0
                    ? one
                    : $@"{one}(?!\s+(?:{BuildPhraseAlternation(tails)})(?![\p{{L}}\p{{M}}\p{{N}}]))";
            });
        return $"(?:{string.Join('|', branches)})";
    }

    private static (string Pattern, string Split) NeutralPathPat(SpokenUrlWords w)
    {
        var slash = NeutralSlashAlt(w);
        var gap = w.GlueSlash ? @"\s*" : @"\s+";
        return ($@"(?:\s+{slash}{gap}{ULabel})", $@"\s+{slash}{gap}");
    }

    private static bool NeutralGluedSlashIsWord(string raw, SpokenUrlWords w) =>
        w.GlueSlash && NeutralGluedSchuineRegex.IsMatch(raw);

    private static string[] NeutralPathSegments(string raw, SpokenUrlWords w) =>
        NeutralUrlHelperCache[w].PathSplitRegex
            .Split(raw)
            .Select(static s => s.Trim())
            .Where(static s => s.Length > 0)
            .ToArray();

    private static bool NeutralLinkStartsEarlier(string text, int index)
    {
        var lead = Math.Min(index, 48);
        var before = text.Substring(index - lead, lead);
        return NeutralLinkStartsEarlierRegex.IsMatch(before) ||
            NeutralLinkStartsEarlierSlashRegex.IsMatch(before);
    }

    private static bool NeutralSpokenHostEndingAllowed(string host, SpokenUrlWords w)
    {
        var helpers = NeutralUrlHelperCache[w];
        var hasSpokenDot = helpers.DotWordInSpacesRegex.IsMatch(host);
        if (!hasSpokenDot ||
            NeutralWwwAliasStartRegex.IsMatch(host) ||
            helpers.HostLocalhostRegex.IsMatch(host))
        {
            return true;
        }

        var last = NeutralHostLabels(host, w).LastOrDefault();
        return last is null || !last.Equals("ai", StringComparison.OrdinalIgnoreCase) &&
            !last.Equals("app", StringComparison.OrdinalIgnoreCase) &&
            !last.Equals("xyz", StringComparison.OrdinalIgnoreCase);
    }

    private static bool NeutralLinkContinues(string rest, bool includeDotAndColon = true)
    {
        var syntaxRegex = includeDotAndColon ? NeutralLinkContinuesWithDotRegex : NeutralLinkContinuesWithoutDotRegex;
        return syntaxRegex.IsMatch(rest) ||
            NeutralLinkContinuesSlashRegex.IsMatch(rest) ||
            NeutralLinkContinuesPunctRegex.IsMatch(rest) ||
            NeutralLinkContinuesWordRegex.IsMatch(rest);
    }

    private static string NeutralUrlSchemes(string t)
    {
        if (!t.Contains("http", StringComparison.OrdinalIgnoreCase))
        {
            return t;
        }

        foreach (var w in NeutralSpokenUrlWords)
        {
            if (!t.Contains("://", StringComparison.Ordinal) && !ContainsAnyIgnoreCase(t, w.Slash))
            {
                continue;
            }

            var helpers = NeutralUrlHelperCache[w];
            t = Replace(t, helpers.SchemePattern, match =>
            {
                var rest = t[(match.Index + match.Length)..];
                if (NeutralLinkContinues(rest) ||
                    NeutralGluedSlashIsWord(match.Groups["path"].Value, w) ||
                    NeutralCanonicalHost(match.Groups["host"].Value, w) is not string host)
                {
                    return match.Value;
                }

                var spokenScheme = NeutralHttpSpokenStartRegex.IsMatch(match.Value);
                var rawHost = match.Groups["host"].Value;
                var spokenRest = helpers.DotOrColonInSpacesRegex.IsMatch(rawHost) ||
                    match.Groups["path"].Value.Length > 0 ||
                    NeutralWwwAliasWordStartRegex.IsMatch(rawHost);
                if (!spokenScheme && !spokenRest)
                {
                    return match.Value;
                }

                var segs = NeutralPathSegments(match.Groups["path"].Value, w);
                return $"{match.Groups["p"].Value}://{host}{string.Concat(segs.Select(static s => "/" + s))}";
            });
        }

        return t;
    }

    private static string NeutralUrlPaths(string t)
    {
        if (!ContainsAnyIgnoreCase(t, NeutralUrlSlashWords))
        {
            return t;
        }

        foreach (var w in NeutralSpokenUrlWords)
        {
            if (!ContainsAnyIgnoreCase(t, w.Slash))
            {
                continue;
            }

            var helpers = NeutralUrlHelperCache[w];
            t = Replace(t, helpers.PathPattern, match =>
            {
                var rest = t[(match.Index + match.Length)..];
                if (NeutralLinkContinues(rest) ||
                    NeutralLinkStartsEarlier(t, match.Index) ||
                    !NeutralSpokenHostEndingAllowed(match.Groups["host"].Value, w) ||
                    NeutralGluedSlashIsWord(match.Groups["path"].Value, w) ||
                    NeutralCanonicalHost(match.Groups["host"].Value, w) is not string host)
                {
                    return match.Value;
                }

                var segs = NeutralPathSegments(match.Groups["path"].Value, w);
                return segs.Length == 0
                    ? match.Value
                    : host + string.Concat(segs.Select(static s => "/" + s));
            });
        }

        return t;
    }

    private static string NeutralWwwHosts(string t)
    {
        if (!NeutralWwwPrefilterRegex.IsMatch(t))
        {
            return t;
        }

        foreach (var w in NeutralSpokenUrlWords)
        {
            if (!t.Contains('.') && !ContainsAnyIgnoreCase(t, w.Dot))
            {
                continue;
            }

            var helpers = NeutralUrlHelperCache[w];
            t = Replace(t, helpers.WwwPattern, match =>
            {
                var rawHost = match.Groups["host"].Value;
                var spoken = helpers.DotInSpacesRegex.IsMatch(rawHost) ||
                    NeutralNonWwwAliasStartRegex.IsMatch(rawHost);
                if (!spoken ||
                    NeutralLinkContinues(t[(match.Index + match.Length)..]) ||
                    NeutralLinkStartsEarlier(t, match.Index))
                {
                    return match.Value;
                }

                return NeutralCanonicalHost(rawHost, w) ?? match.Value;
            });
        }

        return t;
    }

    private static string NeutralLocalhostPorts(string t)
    {
        if (!t.Contains("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return t;
        }

        var colon = BuildPhraseAlternation(NeutralSpokenUrlWords.SelectMany(static w => w.Colon));
        var pattern = $@"(?<![\p{{L}}\p{{N}}])(?<h>localhost)\s+(?:{colon})\s+(?<p>\d+)(?![\p{{L}}\p{{N}}.,]\d|[\p{{L}}\p{{N}}])";

        return Replace(t, pattern, match =>
        {
            if (NeutralLinkContinues(t[(match.Index + match.Length)..]) ||
                NeutralLinkStartsEarlier(t, match.Index) ||
                !int.TryParse(match.Groups["p"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
                port is < 1 or > 65535)
            {
                return match.Value;
            }

            return $"{match.Groups["h"].Value}:{match.Groups["p"].Value}";
        });
    }

    private static string NeutralDottedNumberChains(string t)
    {
        if (!ContainsAsciiDigit(t))
        {
            return t;
        }

        const string gap = @"[^\S\r\n]+";
        const string sep = $@"{gap}(?:{NeutralNumberDotWordAlt}){gap}";
        const string pattern = $@"(?<![\w.])(?:\d+(?:\.\d+)+(?:{sep}\d+)+|\d+(?:{sep}\d+){{2,}})(?![\w]|\.\d)";

        return Replace(t, pattern, match =>
        {
            if (NeutralIdentifierContinues(t, match, NeutralNumberDotWordAlt + "|double|triple") ||
                NeutralLinkStartsEarlier(t, match.Index) ||
                NeutralLinkContinues(t[(match.Index + match.Length)..], includeDotAndColon: false))
            {
                return match.Value;
            }

            var before = t[Math.Max(0, match.Index - 24)..match.Index];
            if (NeutralDottedBeforeDigitRegex.IsMatch(before))
            {
                return match.Value;
            }

            var pieces = NeutralDottedSepRegex.Split(match.Value);
            return string.Join('.', pieces);
        });
    }

    private static string NeutralDashedCodes(string t)
    {
        if ((!ContainsAsciiDigit(t) && !ContainsAnyIgnoreCase(t, DutchDigitWordList)) ||
            (!t.Contains('-') && !ContainsAnyIgnoreCase(t, NeutralDashWordList)))
        {
            return t;
        }

        var number = $@"\d+|{BuildPhraseAlternation(DutchDigitWords.Keys)}";
        var glued = $@"|(?<gcode>[A-Z]{{1,5}}|[b-hj-z])-(?<gdw>(?i:{NeutralDashWordAlt}))\s+";
        var pattern =
            $@"(?<![\w/-])(?:(?<code>(?:[A-Z][ \t]+){{0,3}}[A-Z]{{1,5}}|[b-hj-z])\s+(?<dw>(?i:{NeutralDashWordAlt}))\s+|(?<hcode>[A-Z]{{1,5}}|[a-z])-[ \t]+{glued})(?<num>(?i:{number}))(?![\w-])";

        return Replace(t, pattern, match =>
        {
            var hcode = match.Groups["hcode"].Success ? match.Groups["hcode"].Value : null;
            var gcode = match.Groups["gcode"].Success ? match.Groups["gcode"].Value : null;
            var raw = hcode ?? gcode ?? new string(match.Groups["code"].Value.Where(static c => !char.IsWhiteSpace(c)).ToArray());

            if (raw.Length == 1 && char.IsLower(raw[0]))
            {
                var lead = Math.Min(match.Index, 12);
                var before = t.Substring(match.Index - lead, lead);
                var article = NeutralDashedArticleRegex.IsMatch(before) &&
                    !NeutralDashedTwoWordAlphaRegex.IsMatch(before);
                if (NeutralDashedAlphaRegex.IsMatch(before) && !article)
                {
                    return match.Value;
                }
            }

            var code = raw.Length == 1 ? raw.ToUpperInvariant() : raw;
            if (code == "I" || (hcode is not null && hcode.Equals("a", StringComparison.OrdinalIgnoreCase)) || hcode == "i")
            {
                return match.Value;
            }

            if (NeutralIdentifierContinues(t, match, NeutralDashWordAlt) || NeutralLinkStartsEarlier(t, match.Index))
            {
                return match.Value;
            }

            var dw = Regex.Replace(
                (match.Groups["dw"].Success ? match.Groups["dw"].Value : match.Groups["gdw"].Value).ToLowerInvariant(),
                @"\s+",
                " ",
                RegexOptions.CultureInvariant,
                RegexTimeout);
            var num = match.Groups["num"].Value.ToLowerInvariant();
            if (DutchDigitWords.TryGetValue(num, out var dutchDigit))
            {
                return DutchDashWords.Contains(dw) ? $"{code}-{dutchDigit}" : match.Value;
            }

            return num.All(char.IsDigit) && num.Length is >= 1 and <= 9 ? $"{code}-{num}" : match.Value;
        }, ignoreCase: false);
    }

    private static string NeutralDashedDates(string t)
    {
        if (!t.Contains('-') || !ContainsAsciiDigit(t))
        {
            return t;
        }

        return Replace(t, @"(?<![\w./=#?&:@-])(\d{4})-(\d{1,2})-(\d{1,2})(?![\w@/-]|\.\w)", match =>
        {
            if (NeutralIdentifierContinues(t, match, NeutralDashWordAlt))
            {
                return match.Value;
            }

            var mText = match.Groups[2].Value;
            var dText = match.Groups[3].Value;
            if ((mText.Length != 1 && dText.Length != 1) ||
                !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var year) ||
                !int.TryParse(mText, NumberStyles.None, CultureInfo.InvariantCulture, out var month) ||
                !int.TryParse(dText, NumberStyles.None, CultureInfo.InvariantCulture, out var day) ||
                !IsCalendarDate(year, month, day))
            {
                return match.Value;
            }

            return $"{year}-{month:00}-{day:00}";
        }, ignoreCase: false);
    }

    private static bool NeutralIdentifierContinues(string source, Match match, string connectorAlt)
    {
        var lead = Math.Min(match.Index, 24);
        var end = match.Index + match.Length;
        var before = source.Substring(match.Index - lead, lead);
        var after = source.Substring(end, Math.Min(source.Length - end, 24));
        var endsInDigit = match.Value.Length > 0 && char.IsDigit(match.Value[^1]);
        var (beforeRegex, afterRegex) = connectorAlt == NeutralDashWordAlt
            ? (NeutralDashContBeforeRegex, NeutralDashContAfterRegex)
            : (NeutralNumberContBeforeRegex, NeutralNumberContAfterRegex);
        return beforeRegex.IsMatch(before) ||
            afterRegex.IsMatch(after) ||
            (endsInDigit &&
             !NeutralEndsTwoPlusDigitsRegex.IsMatch(match.Value) &&
             NeutralAfterSingleDigitRegex.IsMatch(after));
    }

    private static bool IsCalendarDate(int year, int month, int day)
    {
        if (month is < 1 or > 12 || day < 1)
        {
            return false;
        }

        var leap = (year % 4 == 0 && year % 100 != 0) || year % 400 == 0;
        ReadOnlySpan<int> lengths = [31, leap ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
        return day <= lengths[month - 1];
    }
}
