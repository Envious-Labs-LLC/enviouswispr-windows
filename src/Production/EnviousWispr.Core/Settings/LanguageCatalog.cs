using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using EnviousWispr.Core.Dictation;

namespace EnviousWispr.Core.Settings;

/// <summary>A selectable dictation language in the picker and engine configuration.</summary>
public sealed record LanguageEntry(
    string Code,
    string NativeName,
    string EnglishName,
    string LockCode,
    EnglishSpelling? Spelling = null)
{
    public bool IsAutomatic => string.Equals(Code, LanguageCatalog.AutomaticCode, StringComparison.Ordinal);

    public string DisplayName => NativeName == EnglishName
        ? EnglishName
        : $"{NativeName} ({EnglishName})";

    public string PickerSubtitle => Code switch
    {
        LanguageCatalog.EnglishUkCode => "British spelling: colour, organise, centre",
        "en" => "American spelling: color, organize, center",
        _ => $"{EnglishName} \u00B7 {Code}",
    };

    public override string ToString() => DisplayName;
}

/// <summary>Full cross-platform language catalog matching the macOS Whisper (100 entries) and Parakeet (25 European languages) catalog.</summary>
public static class LanguageCatalog
{
    public const string AutomaticCode = "auto";
    public const string AutomaticTitle = "Automatic detection";
    public const string EnglishUkCode = "en-gb";
    public const int MaxRecentLanguages = 5;

    public static readonly LanguageEntry EnglishUK = new(
        EnglishUkCode,
        "English (UK)",
        "English (UK)",
        "en",
        EnglishSpelling.British);

    public static IReadOnlyList<LanguageEntry> All { get; } = CreateAllEntries();

    public static IReadOnlyList<LanguageEntry> PickerEntries { get; } = CreatePickerEntries();

    /// <summary>Exact 25 European ISO language codes supported by Parakeet TDT v3 script/alphabet pinning.</summary>
    public static IReadOnlySet<string> ParakeetSupportedCodes { get; } = new ReadOnlySet<string>(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "en", "es", "fr", "de", "it", "pt", "ro", "nl", "da", "sv", "fi", "hu",
            "et", "lv", "lt", "mt", "pl", "cs", "sk", "sl", "hr",
            "ru", "uk", "bg", "el",
        });

    private static readonly Dictionary<string, LanguageEntry> ByCode = CreateByCodeMap();
    private static readonly HashSet<string> ValidStoredCodes = CreateValidStoredCodeSet();

    /// <summary>Normalizes a stored/configured language value ("auto" or a canonical ISO code). Accepts legacy enum names for migration.</summary>
    public static bool TryNormalizeStoredCode(string? raw, out string code)
    {
        code = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var trimmed = raw.Trim();
        var legacy = trimmed.ToLowerInvariant() switch
        {
            "automatic" => AutomaticCode,
            "english" => "en",
            "french" => "fr",
            "german" => "de",
            "spanish" => "es",
            _ => null,
        };
        if (legacy is not null)
        {
            code = legacy;
            return true;
        }

        return WhisperLanguageCodes.TryNormalize(trimmed, out code);
    }

    /// <summary>Returns true when <paramref name="code"/> is already in canonical stored form ("auto" or lowercase ISO code from <see cref="All"/>).</summary>
    public static bool IsCanonicalStoredCode(string? code) =>
        !string.IsNullOrWhiteSpace(code) && ValidStoredCodes.Contains(code);

    /// <summary>Returns true when <paramref name="code"/> is a valid recent-language picker entry ("en-gb" or an ISO code from <see cref="All"/>).</summary>
    public static bool IsCanonicalPickerCode(string? code) =>
        !string.IsNullOrWhiteSpace(code) &&
        !string.Equals(code, AutomaticCode, StringComparison.Ordinal) &&
        (string.Equals(code, EnglishUkCode, StringComparison.Ordinal) || ValidStoredCodes.Contains(code));

    /// <summary>Normalizes any stored/user input, returning <see cref="AutomaticCode"/> if null, empty, or invalid.</summary>
    public static string NormalizeOrDefault(string? raw) =>
        TryNormalizeStoredCode(raw, out var normalized) ? normalized : AutomaticCode;

    /// <summary>Finds a picker entry ("en-gb" or any catalog entry), handling regional subtags like en-us or es-419.</summary>
    public static bool TryGetEntry(string? code, out LanguageEntry entry)
    {
        entry = default!;
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        var normalized = code.Trim().Replace('_', '-').ToLowerInvariant();
        if (normalized == EnglishUkCode)
        {
            entry = EnglishUK;
            return true;
        }

        if (ByCode.TryGetValue(normalized, out var direct))
        {
            entry = direct;
            return true;
        }

        var separator = normalized.IndexOf('-');
        if (separator > 0)
        {
            var suffix = normalized[(separator + 1)..];
            if (suffix.Length > 0 &&
                suffix.All(char.IsAsciiLetterOrDigit) &&
                ByCode.TryGetValue(normalized[..separator], out var baseEntry))
            {
                entry = baseEntry;
                return true;
            }
        }

        return false;
    }

    public static bool IsSupported(FinalAsrEngine engine, string? storedCode)
    {
        var normalized = NormalizeOrDefault(storedCode);
        if (normalized == AutomaticCode)
        {
            return true;
        }

        return engine switch
        {
            FinalAsrEngine.Whisper => IsCanonicalStoredCode(normalized),
            FinalAsrEngine.Parakeet => ParakeetSupportedCodes.Contains(normalized),
            _ => false,
        };
    }

    public static string EffectiveRecognitionCode(FinalAsrEngine engine, string? storedCode)
    {
        var normalized = NormalizeOrDefault(storedCode);
        if (normalized == AutomaticCode)
        {
            return AutomaticCode;
        }

        return IsSupported(engine, normalized) ? normalized : AutomaticCode;
    }

    /// <summary>Resolves the active picker entry from stored <see cref="DictationPreferences.WhisperLanguage"/> and <see cref="DictationPreferences.EnglishSpelling"/>, or null for Automatic.</summary>
    public static LanguageEntry? ResolveSelection(string? storedCode, EnglishSpelling spelling)
    {
        var normalized = NormalizeOrDefault(storedCode);
        if (normalized == AutomaticCode)
        {
            return null;
        }

        if (normalized == "en" && spelling == EnglishSpelling.British)
        {
            return EnglishUK;
        }

        return ByCode.TryGetValue(normalized, out var entry) ? entry : null;
    }

    public static (string WhisperLanguage, EnglishSpelling EnglishSpelling) ApplyPickerSelection(
        string? selectedCode,
        EnglishSpelling currentSpelling)
    {
        if (string.IsNullOrWhiteSpace(selectedCode) ||
            string.Equals(selectedCode.Trim(), AutomaticCode, StringComparison.OrdinalIgnoreCase))
        {
            return (AutomaticCode, currentSpelling);
        }

        if (!TryGetEntry(selectedCode, out var entry))
        {
            return (AutomaticCode, currentSpelling);
        }

        return (entry.LockCode, entry.Spelling ?? currentSpelling);
    }

    public static string DisplayNameFor(string? storedCode, EnglishSpelling spelling = EnglishSpelling.American)
    {
        var entry = ResolveSelection(storedCode, spelling);
        return entry is null ? AutomaticTitle : entry.DisplayName;
    }

    public static string EnglishNameFor(string? code) =>
        TryGetEntry(code, out var entry) ? entry.EnglishName : AutomaticTitle;

    public static IReadOnlyList<LanguageEntry> EntriesForEngine(FinalAsrEngine engine) =>
        engine == FinalAsrEngine.Parakeet
            ? PickerEntries.Where(entry => ParakeetSupportedCodes.Contains(entry.LockCode)).ToArray()
            : PickerEntries;

    public static IReadOnlyList<LanguageEntry> FilterEntries(FinalAsrEngine engine, string? query)
    {
        var available = EntriesForEngine(engine);
        if (string.IsNullOrWhiteSpace(query))
        {
            return available;
        }

        var trimmed = query.Trim();
        return available
            .Where(entry => MatchesQuery(entry, trimmed))
            .ToArray();
    }

    public static bool MatchesQuery(LanguageEntry entry, string query)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        return entry.EnglishName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               entry.NativeName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               entry.Code.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<string> RecordRecentLanguage(
        IReadOnlyList<string>? existing,
        string? pickedCode)
    {
        var updated = new List<string>(MaxRecentLanguages);
        if (!string.IsNullOrWhiteSpace(pickedCode) &&
            !string.Equals(pickedCode.Trim(), AutomaticCode, StringComparison.OrdinalIgnoreCase) &&
            TryGetEntry(pickedCode, out var entry))
        {
            updated.Add(entry.Code);
        }

        if (existing is not null)
        {
            foreach (var code in existing)
            {
                if (updated.Count >= MaxRecentLanguages)
                {
                    break;
                }

                if (TryGetEntry(code, out var normalizedEntry) &&
                    !updated.Contains(normalizedEntry.Code, StringComparer.Ordinal))
                {
                    updated.Add(normalizedEntry.Code);
                }
            }
        }

        return updated;
    }

    public static IReadOnlyList<LanguageEntry> ResolveRecentEntries(
        IReadOnlyList<string>? recentCodes,
        FinalAsrEngine engine)
    {
        if (recentCodes is null || recentCodes.Count == 0)
        {
            return [];
        }

        var entries = new List<LanguageEntry>(recentCodes.Count);
        foreach (var code in recentCodes)
        {
            if (TryGetEntry(code, out var entry) &&
                IsSupported(engine, entry.LockCode) &&
                !entries.Any(existing => string.Equals(existing.Code, entry.Code, StringComparison.Ordinal)))
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    private static LanguageEntry[] CreatePickerEntries()
    {
        var sorted = All
            .OrderBy(entry => entry.EnglishName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var englishIndex = sorted.FindIndex(entry => entry.Code == "en");
        if (englishIndex >= 0)
        {
            sorted.Insert(englishIndex + 1, EnglishUK);
        }
        else
        {
            sorted.Add(EnglishUK);
        }

        return [.. sorted];
    }

    private static Dictionary<string, LanguageEntry> CreateByCodeMap()
    {
        var map = new Dictionary<string, LanguageEntry>(All.Count, StringComparer.Ordinal);
        foreach (var entry in All)
        {
            map[entry.Code] = entry;
        }

        return map;
    }

    private static HashSet<string> CreateValidStoredCodeSet()
    {
        var set = new HashSet<string>(All.Count + 1, StringComparer.Ordinal)
        {
            AutomaticCode,
        };
        foreach (var entry in All)
        {
            set.Add(entry.Code);
        }

        return set;
    }

    private static IReadOnlyList<LanguageEntry> CreateAllEntries() =>
    [
        new("af", "Afrikaans", "Afrikaans", "af"),
        new("am", "አማርኛ", "Amharic", "am"),
        new("ar", "العربية", "Arabic", "ar"),
        new("as", "অসমীয়া", "Assamese", "as"),
        new("az", "Azərbaycanca", "Azerbaijani", "az"),
        new("ba", "Башҡортса", "Bashkir", "ba"),
        new("be", "Беларуская", "Belarusian", "be"),
        new("bg", "Български", "Bulgarian", "bg"),
        new("bn", "বাংলা", "Bengali", "bn"),
        new("bo", "བོད་སྐད་", "Tibetan", "bo"),
        new("br", "Brezhoneg", "Breton", "br"),
        new("bs", "Bosanski", "Bosnian", "bs"),
        new("ca", "Català", "Catalan", "ca"),
        new("cs", "Čeština", "Czech", "cs"),
        new("cy", "Cymraeg", "Welsh", "cy"),
        new("da", "Dansk", "Danish", "da"),
        new("de", "Deutsch", "German", "de"),
        new("el", "Ελληνικά", "Greek", "el"),
        new("en", "English", "English", "en", EnglishSpelling.American),
        new("es", "Español", "Spanish", "es"),
        new("et", "Eesti", "Estonian", "et"),
        new("eu", "Euskara", "Basque", "eu"),
        new("fa", "فارسی", "Persian", "fa"),
        new("fi", "Suomi", "Finnish", "fi"),
        new("fo", "Føroyskt", "Faroese", "fo"),
        new("fr", "Français", "French", "fr"),
        new("gl", "Galego", "Galician", "gl"),
        new("gu", "ગુજરાતી", "Gujarati", "gu"),
        new("ha", "Hausa", "Hausa", "ha"),
        new("haw", "ʻŌlelo Hawaiʻi", "Hawaiian", "haw"),
        new("he", "עברית", "Hebrew", "he"),
        new("hi", "हिन्दी", "Hindi", "hi"),
        new("hr", "Hrvatski", "Croatian", "hr"),
        new("ht", "Kreyòl Ayisyen", "Haitian Creole", "ht"),
        new("hu", "Magyar", "Hungarian", "hu"),
        new("hy", "Հայերեն", "Armenian", "hy"),
        new("id", "Bahasa Indonesia", "Indonesian", "id"),
        new("is", "Íslenska", "Icelandic", "is"),
        new("it", "Italiano", "Italian", "it"),
        new("ja", "日本語", "Japanese", "ja"),
        new("jw", "Basa Jawa", "Javanese", "jw"),
        new("ka", "ქართული", "Georgian", "ka"),
        new("kk", "Қазақша", "Kazakh", "kk"),
        new("km", "ខ្មែរ", "Khmer", "km"),
        new("kn", "ಕನ್ನಡ", "Kannada", "kn"),
        new("ko", "한국어", "Korean", "ko"),
        new("la", "Latina", "Latin", "la"),
        new("lb", "Lëtzebuergesch", "Luxembourgish", "lb"),
        new("ln", "Lingála", "Lingala", "ln"),
        new("lo", "ລາວ", "Lao", "lo"),
        new("lt", "Lietuvių", "Lithuanian", "lt"),
        new("lv", "Latviešu", "Latvian", "lv"),
        new("mg", "Malagasy", "Malagasy", "mg"),
        new("mi", "Māori", "Maori", "mi"),
        new("mk", "Македонски", "Macedonian", "mk"),
        new("ml", "മലയാളം", "Malayalam", "ml"),
        new("mn", "Монгол", "Mongolian", "mn"),
        new("mr", "मराठी", "Marathi", "mr"),
        new("ms", "Bahasa Melayu", "Malay", "ms"),
        new("mt", "Malti", "Maltese", "mt"),
        new("my", "မြန်မာ", "Burmese", "my"),
        new("ne", "नेपाली", "Nepali", "ne"),
        new("nl", "Nederlands", "Dutch", "nl"),
        new("nn", "Nynorsk", "Norwegian Nynorsk", "nn"),
        new("no", "Norsk", "Norwegian", "no"),
        new("oc", "Occitan", "Occitan", "oc"),
        new("pa", "ਪੰਜਾਬੀ", "Punjabi", "pa"),
        new("pl", "Polski", "Polish", "pl"),
        new("ps", "پښتو", "Pashto", "ps"),
        new("pt", "Português", "Portuguese", "pt"),
        new("ro", "Română", "Romanian", "ro"),
        new("ru", "Русский", "Russian", "ru"),
        new("sa", "संस्कृतम्", "Sanskrit", "sa"),
        new("sd", "سنڌي", "Sindhi", "sd"),
        new("si", "සිංහල", "Sinhala", "si"),
        new("sk", "Slovenčina", "Slovak", "sk"),
        new("sl", "Slovenščina", "Slovenian", "sl"),
        new("sn", "ChiShona", "Shona", "sn"),
        new("so", "Soomaali", "Somali", "so"),
        new("sq", "Shqip", "Albanian", "sq"),
        new("sr", "Српски", "Serbian", "sr"),
        new("su", "Basa Sunda", "Sundanese", "su"),
        new("sv", "Svenska", "Swedish", "sv"),
        new("sw", "Kiswahili", "Swahili", "sw"),
        new("ta", "தமிழ்", "Tamil", "ta"),
        new("te", "తెలుగు", "Telugu", "te"),
        new("tg", "Тоҷикӣ", "Tajik", "tg"),
        new("th", "ไทย", "Thai", "th"),
        new("tk", "Türkmençe", "Turkmen", "tk"),
        new("tl", "Tagalog", "Tagalog", "tl"),
        new("tr", "Türkçe", "Turkish", "tr"),
        new("tt", "Татарча", "Tatar", "tt"),
        new("uk", "Українська", "Ukrainian", "uk"),
        new("ur", "اردو", "Urdu", "ur"),
        new("uz", "Oʻzbekcha", "Uzbek", "uz"),
        new("vi", "Tiếng Việt", "Vietnamese", "vi"),
        new("yi", "ייִדיש", "Yiddish", "yi"),
        new("yo", "Yorùbá", "Yoruba", "yo"),
        new("yue", "粵語", "Cantonese", "yue"),
        new("zh", "中文", "Chinese", "zh"),
    ];
}

/// <summary>Deserializes both legacy WhisperLanguagePreference values ("Automatic", "English", "French", "German", "Spanish", or 0..4) and ISO language codes.</summary>
public sealed class WhisperLanguageJsonConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var index))
        {
            return index switch
            {
                0 => LanguageCatalog.AutomaticCode,
                1 => "en",
                2 => "fr",
                3 => "de",
                4 => "es",
                _ => throw new JsonException($"Invalid legacy WhisperLanguage numeric value '{index}'."),
            };
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var raw = reader.GetString();
            if (LanguageCatalog.TryNormalizeStoredCode(raw, out var normalized))
            {
                return normalized;
            }

            throw new JsonException($"Invalid WhisperLanguage value '{raw}'.");
        }

        throw new JsonException($"Unexpected token '{reader.TokenType}' for WhisperLanguage.");
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        var normalized = LanguageCatalog.TryNormalizeStoredCode(value, out var code)
            ? code
            : LanguageCatalog.AutomaticCode;
        writer.WriteStringValue(normalized);
    }
}
