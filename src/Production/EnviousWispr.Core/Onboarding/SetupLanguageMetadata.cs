using System.Collections.ObjectModel;
using System.Globalization;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Core.Onboarding;

/// <summary>OS system language resolution, 101-language native greeting catalog, and smart language search for setup.</summary>
public static class SetupLanguageMetadata
{
    public static IReadOnlyList<string> WorldGreetings { get; } =
    [
        "Hello!",
        "¡Hola!",
        "Bonjour !",
        "Hallo!",
        "Ciao!",
        "Olá!",
        "こんにちは！",
        "안녕하세요!",
        "你好！",
        "Привет!",
        "مرحباً!",
        "नमस्ते!",
        "Merhaba!",
        "Xin chào!",
    ];

    private static readonly ReadOnlyDictionary<string, string> GreetingsByCode =
        new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["af"] = "Hallo!",
            ["am"] = "ሰላም!",
            ["ar"] = "مرحباً!",
            ["as"] = "নমস্কাৰ!",
            ["az"] = "Salam!",
            ["ba"] = "Сәләм!",
            ["be"] = "Прывітанне!",
            ["bg"] = "Здравейте!",
            ["bn"] = "নমস্কার!",
            ["bo"] = "བཀྲ་ཤིས་བདེ་ལེགས།",
            ["br"] = "Demat!",
            ["bs"] = "Zdravo!",
            ["ca"] = "Hola!",
            ["cs"] = "Ahoj!",
            ["cy"] = "Helo!",
            ["da"] = "Hej!",
            ["de"] = "Hallo!",
            ["el"] = "Γεια σας!",
            ["en"] = "Hello!",
            ["en-gb"] = "Hello!",
            ["es"] = "¡Hola!",
            ["et"] = "Tere!",
            ["eu"] = "Kaixo!",
            ["fa"] = "سلام!",
            ["fi"] = "Hei!",
            ["fo"] = "Hey!",
            ["fr"] = "Bonjour !",
            ["gl"] = "Ola!",
            ["gu"] = "નમસ્તે!",
            ["ha"] = "Sannu!",
            ["haw"] = "Aloha!",
            ["he"] = "שלום!",
            ["hi"] = "नमस्ते!",
            ["hr"] = "Bok!",
            ["ht"] = "Bonjou!",
            ["hu"] = "Szia!",
            ["hy"] = "Բարև!",
            ["id"] = "Halo!",
            ["is"] = "Halló!",
            ["it"] = "Ciao!",
            ["ja"] = "こんにちは！",
            ["jw"] = "Halo!",
            ["ka"] = "გამარჯობა!",
            ["kk"] = "Сәлем!",
            ["km"] = "សួស្តី!",
            ["kn"] = "ನಮಸ್ಕಾರ!",
            ["ko"] = "안녕하세요!",
            ["la"] = "Salve!",
            ["lb"] = "Moien!",
            ["ln"] = "Mbote!",
            ["lo"] = "ສະບາຍດີ!",
            ["lt"] = "Labas!",
            ["lv"] = "Sveiki!",
            ["mg"] = "Salama!",
            ["mi"] = "Kia ora!",
            ["mk"] = "Здраво!",
            ["ml"] = "നമസ്കാരം!",
            ["mn"] = "Сайн байна уу!",
            ["mr"] = "नमस्कार!",
            ["ms"] = "Hai!",
            ["mt"] = "Bongu!",
            ["my"] = "မင်္ဂလာပါ!",
            ["ne"] = "नमस्ते!",
            ["nl"] = "Hallo!",
            ["nn"] = "Hei!",
            ["no"] = "Hei!",
            ["oc"] = "Adieu!",
            ["pa"] = "ਸਤ ਸ੍ਰੀ ਅਕਾਲ!",
            ["pl"] = "Cześć!",
            ["ps"] = "سلام!",
            ["pt"] = "Olá!",
            ["ro"] = "Salut!",
            ["ru"] = "Привет!",
            ["sa"] = "नमस्ते!",
            ["sd"] = "سلام!",
            ["si"] = "ආයුබෝවන්!",
            ["sk"] = "Ahoj!",
            ["sl"] = "Živjo!",
            ["sn"] = "Mhoro!",
            ["so"] = "Salaan!",
            ["sq"] = "Përshëndetje!",
            ["sr"] = "Здраво!",
            ["su"] = "Halo!",
            ["sv"] = "Hej!",
            ["sw"] = "Habari!",
            ["ta"] = "வணக்கம்!",
            ["te"] = "నమస్కారం!",
            ["tg"] = "Салом!",
            ["th"] = "สวัสดี!",
            ["tk"] = "Salam!",
            ["tl"] = "Kumusta!",
            ["tr"] = "Merhaba!",
            ["tt"] = "Сәлам!",
            ["uk"] = "Привіт!",
            ["ur"] = "سلام!",
            ["uz"] = "Salom!",
            ["vi"] = "Xin chào!",
            ["yi"] = "שלום־עליכם!",
            ["yo"] = "Báwo!",
            ["yue"] = "你好！",
            ["zh"] = "你好！",
        });

    private static readonly ReadOnlyDictionary<string, string[]> SearchAliasesByCode =
        new(new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["en"] = ["american", "usa", "us english"],
            ["en-gb"] = ["british", "uk", "united kingdom", "uk english", "great britain"],
            ["zh"] = ["mandarin", "putonghua", "chinese", "zhongwen"],
            ["yue"] = ["cantonese", "hong kong", "guangdonghua"],
            ["fa"] = ["farsi", "persian", "iran"],
            ["es"] = ["castilian", "castellano", "espanol"],
            ["fr"] = ["francais"],
            ["de"] = ["deutsch", "german"],
            ["pt"] = ["portugues", "brazil", "brazilian", "brasil"],
            ["no"] = ["bokmal", "nb", "norsk"],
            ["nn"] = ["nynorsk", "norwegian nynorsk"],
            ["he"] = ["iw", "ivrit"],
            ["id"] = ["in", "bahasa"],
            ["jw"] = ["jv", "javanese"],
            ["tl"] = ["filipino", "fil", "tagalog"],
            ["nl"] = ["flemish", "holland"],
            ["ja"] = ["nihongo"],
            ["ko"] = ["hangul", "hangugeo"],
            ["cs"] = ["cestina", "chech", "cz"],
            ["pl"] = ["polski"],
            ["uk"] = ["ukrainian", "ukranian"],
        });

    private static readonly CompareInfo InvariantCompare = CultureInfo.InvariantCulture.CompareInfo;

    public static string? ResolveSystemLanguageCode(CultureInfo? uiCulture = null)
    {
        var current = uiCulture ?? CultureInfo.CurrentUICulture;
        while (current is not null && !Equals(current, CultureInfo.InvariantCulture))
        {
            if (ResolveSystemLanguageCode(current.Name) is { } matched)
            {
                return matched;
            }

            current = current.Parent;
        }

        return null;
    }

    public static string? ResolveSystemLanguageCode(string? cultureOrLocaleName)
    {
        if (string.IsNullOrWhiteSpace(cultureOrLocaleName))
        {
            return null;
        }

        var normalized = cultureOrLocaleName.Trim().Replace('_', '-').ToLowerInvariant();
        if (normalized == LanguageCatalog.AutomaticCode)
        {
            return null;
        }

        if (normalized == LanguageCatalog.EnglishUkCode || normalized.StartsWith("en-gb-", StringComparison.Ordinal))
        {
            return LanguageCatalog.EnglishUkCode;
        }

        var mappedLegacy = normalized switch
        {
            "yue" or "zh-hk" or "zh-yue" or "zh-hant-hk" or "zh-mo" => "yue",
            "nb" => "no",
            "iw" => "he",
            "in" => "id",
            "jv" => "jw",
            "fil" => "tl",
            _ when normalized.StartsWith("nb-", StringComparison.Ordinal) => "no",
            _ when normalized.StartsWith("iw-", StringComparison.Ordinal) => "he",
            _ when normalized.StartsWith("in-", StringComparison.Ordinal) => "id",
            _ when normalized.StartsWith("jv-", StringComparison.Ordinal) => "jw",
            _ when normalized.StartsWith("fil-", StringComparison.Ordinal) => "tl",
            _ => null,
        };
        if (mappedLegacy is not null)
        {
            return mappedLegacy;
        }

        if (LanguageCatalog.TryGetEntry(normalized, out var entry))
        {
            return entry.Code;
        }

        return null;
    }

    public static string GetGreeting(string? languageCode, CultureInfo? fallbackUiCulture = null)
    {
        if (!string.IsNullOrWhiteSpace(languageCode) &&
            !string.Equals(languageCode.Trim(), LanguageCatalog.AutomaticCode, StringComparison.OrdinalIgnoreCase))
        {
            if (GreetingsByCode.TryGetValue(languageCode.Trim(), out var directGreeting))
            {
                return directGreeting;
            }

            if (LanguageCatalog.TryGetEntry(languageCode, out var entry) &&
                GreetingsByCode.TryGetValue(entry.Code, out var mappedGreeting))
            {
                return mappedGreeting;
            }
        }

        var resolvedSystem = ResolveSystemLanguageCode(fallbackUiCulture);
        if (resolvedSystem is not null && GreetingsByCode.TryGetValue(resolvedSystem, out var systemGreeting))
        {
            return systemGreeting;
        }

        return "Hello!";
    }

    public static bool HasGreetingFor(string code) =>
        !string.IsNullOrWhiteSpace(code) && GreetingsByCode.ContainsKey(code.Trim());

    public static IReadOnlyList<LanguageEntry> SearchPickerEntries(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return LanguageCatalog.PickerEntries;
        }

        var trimmed = query.Trim();
        return LanguageCatalog.PickerEntries
            .Where(entry => MatchesQuery(entry, trimmed))
            .ToArray();
    }

    public static bool MatchesQuery(LanguageEntry entry, string? query)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var trimmed = query.Trim();
        const CompareOptions options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
        if (InvariantCompare.IndexOf(entry.EnglishName, trimmed, options) >= 0 ||
            InvariantCompare.IndexOf(entry.NativeName, trimmed, options) >= 0 ||
            InvariantCompare.IndexOf(entry.Code, trimmed, options) >= 0)
        {
            return true;
        }

        if (SearchAliasesByCode.TryGetValue(entry.Code, out var aliases))
        {
            foreach (var alias in aliases)
            {
                if (InvariantCompare.IndexOf(alias, trimmed, options) >= 0)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
