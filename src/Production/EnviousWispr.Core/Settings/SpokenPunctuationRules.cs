using System.Globalization;
using System.Text;

namespace EnviousWispr.Core.Settings;

/// <summary>#2450 / #3738: Spoken punctuation and symbol commands across supported dictation languages.</summary>
public enum SpokenPunctuationCommand
{
    Period,
    Comma,
    QuestionMark,
    ExclamationMark,
    Colon,
    Semicolon,
    LineBreak,
    ParagraphBreak,
    OpenParenthesis,
    CloseParenthesis,
    OpenSquareBracket,
    CloseSquareBracket,
    OpenBrace,
    CloseBrace,
    OpenAngleBracket,
    CloseAngleBracket,
    OpenQuote,
    CloseQuote,
    OpenSingleQuote,
    CloseSingleQuote,
    Hyphen,
    Underscore,
    Apostrophe,
    EmDash,
    EnDash,
    Ellipsis,
    Ampersand,
    PlusSign,
    EqualsSign,
    VerticalBar,
    Asterisk,
    HashSign,
    Tilde,
}

/// <summary>Category of a spoken punctuation command so symbol passes never absorb sentence marks.</summary>
public enum SpokenPunctuationRole
{
    Mark,
    LineBreak,
    Symbol,
}

/// <summary>How a spoken symbol sits against adjacent words.</summary>
public enum SpokenSymbolSpacing
{
    Opens,
    Closes,
    Joins,
    Spaced,
}

/// <summary>One spoken punctuation command and its spoken phrases in one language.</summary>
public sealed record SpokenPunctuationRule(
    SpokenPunctuationCommand Command,
    IReadOnlyList<string> SpokenForms,
    string Replacement);

public static class SpokenPunctuationCommandExtensions
{
    public static SpokenPunctuationRole GetRole(this SpokenPunctuationCommand command) => command switch
    {
        SpokenPunctuationCommand.Period
        or SpokenPunctuationCommand.Comma
        or SpokenPunctuationCommand.QuestionMark
        or SpokenPunctuationCommand.ExclamationMark
        or SpokenPunctuationCommand.Colon
        or SpokenPunctuationCommand.Semicolon => SpokenPunctuationRole.Mark,

        SpokenPunctuationCommand.LineBreak
        or SpokenPunctuationCommand.ParagraphBreak => SpokenPunctuationRole.LineBreak,

        _ => SpokenPunctuationRole.Symbol,
    };

    public static SpokenSymbolSpacing? GetSpacing(this SpokenPunctuationCommand command) => command switch
    {
        SpokenPunctuationCommand.Period
        or SpokenPunctuationCommand.Comma
        or SpokenPunctuationCommand.QuestionMark
        or SpokenPunctuationCommand.ExclamationMark
        or SpokenPunctuationCommand.Colon
        or SpokenPunctuationCommand.Semicolon
        or SpokenPunctuationCommand.LineBreak
        or SpokenPunctuationCommand.ParagraphBreak => null,

        SpokenPunctuationCommand.OpenParenthesis
        or SpokenPunctuationCommand.OpenSquareBracket
        or SpokenPunctuationCommand.OpenBrace
        or SpokenPunctuationCommand.OpenAngleBracket
        or SpokenPunctuationCommand.OpenQuote
        or SpokenPunctuationCommand.OpenSingleQuote
        or SpokenPunctuationCommand.HashSign
        or SpokenPunctuationCommand.Tilde => SpokenSymbolSpacing.Opens,

        SpokenPunctuationCommand.CloseParenthesis
        or SpokenPunctuationCommand.CloseSquareBracket
        or SpokenPunctuationCommand.CloseBrace
        or SpokenPunctuationCommand.CloseAngleBracket
        or SpokenPunctuationCommand.CloseQuote
        or SpokenPunctuationCommand.CloseSingleQuote
        or SpokenPunctuationCommand.Ellipsis
        or SpokenPunctuationCommand.Asterisk => SpokenSymbolSpacing.Closes,

        SpokenPunctuationCommand.Hyphen
        or SpokenPunctuationCommand.Underscore
        or SpokenPunctuationCommand.Apostrophe
        or SpokenPunctuationCommand.EmDash => SpokenSymbolSpacing.Joins,

        SpokenPunctuationCommand.EnDash
        or SpokenPunctuationCommand.Ampersand
        or SpokenPunctuationCommand.PlusSign
        or SpokenPunctuationCommand.EqualsSign
        or SpokenPunctuationCommand.VerticalBar => SpokenSymbolSpacing.Spaced,

        _ => null,
    };
}

/// <summary>
/// Port of macOS <c>SpokenPunctuationRules</c> (#2450, #3738, #4216): start-word tables for
/// <c>de</c>, <c>fr</c>, <c>es</c>, <c>it</c>, optional English start-word rules, and shared English symbols.
/// </summary>
public static class SpokenPunctuationRules
{
    public static readonly IReadOnlyList<string> SupportedLanguages = ["de", "fr", "es", "it"];

    public static readonly IReadOnlyList<string> StartWordLanguages = ["en", "de", "fr", "es", "it"];

    private static readonly IReadOnlyList<SpokenPunctuationRule> German =
    [
        new(SpokenPunctuationCommand.ParagraphBreak, ["neuer Absatz", "neuen Absatz", "Neuabsatz"], "\n\n"),
        new(SpokenPunctuationCommand.LineBreak, ["neue Zeile"], "\n"),
        new(SpokenPunctuationCommand.QuestionMark, ["Fragezeichen"], "?"),
        new(SpokenPunctuationCommand.ExclamationMark, ["Ausrufezeichen"], "!"),
        new(SpokenPunctuationCommand.Colon, ["Doppelpunkt"], ":"),
        new(SpokenPunctuationCommand.Semicolon, ["Semikolon", "Strichpunkt"], ";"),
        new(SpokenPunctuationCommand.Comma, ["Komma"], ","),
        new(SpokenPunctuationCommand.Period, ["Punkt"], "."),
        new(SpokenPunctuationCommand.OpenParenthesis, ["linke Klammer", "Klammer auf"], "("),
        new(SpokenPunctuationCommand.CloseParenthesis, ["rechte Klammer", "Klammer zu"], ")"),
        new(SpokenPunctuationCommand.OpenQuote, ["Anführungszeichen unten", "Anführungszeichen auf"], "\u201E"),
        new(SpokenPunctuationCommand.CloseQuote, ["Anführungszeichen oben", "Anführungszeichen zu"], "\u201C"),
        new(SpokenPunctuationCommand.Hyphen, ["Bindestrich"], "-"),
        new(SpokenPunctuationCommand.Ellipsis, ["Ellipse", "Auslassungspunkte"], "\u2026"),
    ];

    private static readonly IReadOnlyList<SpokenPunctuationRule> French =
    [
        new(SpokenPunctuationCommand.ParagraphBreak, ["nouveau paragraphe"], "\n\n"),
        new(SpokenPunctuationCommand.LineBreak, ["nouvelle ligne", "à la ligne"], "\n"),
        new(SpokenPunctuationCommand.QuestionMark, ["point d'interrogation"], "?"),
        new(SpokenPunctuationCommand.ExclamationMark, ["point d'exclamation"], "!"),
        new(SpokenPunctuationCommand.Semicolon, ["point-virgule"], ";"),
        new(SpokenPunctuationCommand.Colon, ["deux points"], ":"),
        new(SpokenPunctuationCommand.Comma, ["virgule"], ","),
        new(SpokenPunctuationCommand.Period, ["point"], "."),
        new(SpokenPunctuationCommand.OpenParenthesis, ["parenthèse ouvrante"], "("),
        new(SpokenPunctuationCommand.CloseParenthesis, ["parenthèse fermante"], ")"),
        new(SpokenPunctuationCommand.OpenQuote, ["guillemet ouvrant"], "\u00AB\u00A0"),
        new(SpokenPunctuationCommand.CloseQuote, ["guillemet fermant"], "\u00A0\u00BB"),
        new(SpokenPunctuationCommand.Hyphen, ["trait d'union"], "-"),
        new(SpokenPunctuationCommand.Ellipsis, ["points de suspension"], "\u2026"),
    ];

    private static readonly IReadOnlyList<SpokenPunctuationRule> Spanish =
    [
        new(SpokenPunctuationCommand.ParagraphBreak, ["nuevo párrafo"], "\n\n"),
        new(SpokenPunctuationCommand.LineBreak, ["nueva línea"], "\n"),
        new(SpokenPunctuationCommand.QuestionMark, ["signo de interrogación"], "?"),
        new(SpokenPunctuationCommand.ExclamationMark, ["signo de exclamación"], "!"),
        new(SpokenPunctuationCommand.Semicolon, ["punto y coma"], ";"),
        new(SpokenPunctuationCommand.Colon, ["dos puntos"], ":"),
        new(SpokenPunctuationCommand.Comma, ["coma"], ","),
        new(SpokenPunctuationCommand.Period, ["punto"], "."),
        new(SpokenPunctuationCommand.OpenParenthesis, ["paréntesis de apertura", "abrir paréntesis"], "("),
        new(SpokenPunctuationCommand.CloseParenthesis, ["paréntesis de cierre", "cerrar paréntesis"], ")"),
        new(SpokenPunctuationCommand.OpenQuote, ["abrir comillas", "comillas"], "\u201C"),
        new(SpokenPunctuationCommand.CloseQuote, ["comillas de cierre", "cerrar comillas"], "\u201D"),
        new(SpokenPunctuationCommand.Hyphen, ["guion"], "-"),
        new(SpokenPunctuationCommand.Ellipsis, ["puntos suspensivos"], "\u2026"),
    ];

    private static readonly IReadOnlyList<SpokenPunctuationRule> Italian =
    [
        new(SpokenPunctuationCommand.ParagraphBreak, ["nuovo paragrafo"], "\n\n"),
        new(SpokenPunctuationCommand.LineBreak, ["nuova riga"], "\n"),
        new(SpokenPunctuationCommand.QuestionMark, ["punto interrogativo"], "?"),
        new(SpokenPunctuationCommand.ExclamationMark, ["punto esclamativo"], "!"),
        new(SpokenPunctuationCommand.Semicolon, ["punto e virgola"], ";"),
        new(SpokenPunctuationCommand.Colon, ["due punti"], ":"),
        new(SpokenPunctuationCommand.Comma, ["virgola"], ","),
        new(SpokenPunctuationCommand.Period, ["punto"], "."),
        new(SpokenPunctuationCommand.OpenParenthesis, ["parentesi aperta", "parentesi tonda aperta"], "("),
        new(SpokenPunctuationCommand.CloseParenthesis, ["parentesi chiusa", "parentesi tonda chiusa"], ")"),
        new(SpokenPunctuationCommand.OpenQuote, ["apri virgolette"], "\u201C"),
        new(SpokenPunctuationCommand.CloseQuote, ["chiudi virgolette"], "\u201D"),
        new(SpokenPunctuationCommand.Hyphen, ["trattino"], "-"),
        new(SpokenPunctuationCommand.Ellipsis, ["puntini di sospensione"], "\u2026"),
    ];

    private static readonly IReadOnlyList<SpokenPunctuationRule> English =
    [
        new(SpokenPunctuationCommand.ParagraphBreak, ["new paragraph"], "\n\n"),
        new(SpokenPunctuationCommand.LineBreak, ["new line"], "\n"),
        new(SpokenPunctuationCommand.QuestionMark, ["question mark"], "?"),
        new(SpokenPunctuationCommand.ExclamationMark, ["exclamation mark", "exclamation point"], "!"),
        new(SpokenPunctuationCommand.Colon, ["colon"], ":"),
        new(SpokenPunctuationCommand.Semicolon, ["semicolon"], ";"),
        new(SpokenPunctuationCommand.Comma, ["comma"], ","),
        new(SpokenPunctuationCommand.Period, ["period", "full stop"], "."),
        new(SpokenPunctuationCommand.OpenParenthesis, ["open parenthesis", "open paren", "left parenthesis", "left paren"], "("),
        new(SpokenPunctuationCommand.CloseParenthesis, ["close parenthesis", "close paren", "right parenthesis", "right paren"], ")"),
        new(SpokenPunctuationCommand.OpenSquareBracket, ["open square bracket", "left square bracket"], "["),
        new(SpokenPunctuationCommand.CloseSquareBracket, ["close square bracket", "right square bracket"], "]"),
        new(
            SpokenPunctuationCommand.OpenBrace,
            ["open curly bracket", "open curly brace", "open brace", "left curly bracket", "left curly brace", "left brace"],
            "{"),
        new(
            SpokenPunctuationCommand.CloseBrace,
            ["close curly bracket", "close curly brace", "close brace", "right curly bracket", "right curly brace", "right brace"],
            "}"),
        new(SpokenPunctuationCommand.OpenAngleBracket, ["open angle bracket", "left angle bracket"], "<"),
        new(SpokenPunctuationCommand.CloseAngleBracket, ["close angle bracket", "right angle bracket"], ">"),
        new(
            SpokenPunctuationCommand.OpenQuote,
            ["open double quote", "begin double quote", "open quotes", "begin quotes", "open quote", "begin quote"],
            "\""),
        new(
            SpokenPunctuationCommand.CloseQuote,
            ["close double quote", "end double quote", "close quotes", "end quotes", "close quote", "end quote", "unquote"],
            "\""),
        new(SpokenPunctuationCommand.OpenSingleQuote, ["open single quote", "begin single quote"], "'"),
        new(SpokenPunctuationCommand.CloseSingleQuote, ["close single quote", "end single quote"], "'"),
        new(SpokenPunctuationCommand.Hyphen, ["hyphen"], "-"),
        new(SpokenPunctuationCommand.Underscore, ["underscore"], "_"),
        new(SpokenPunctuationCommand.Apostrophe, ["apostrophe"], "'"),
        new(SpokenPunctuationCommand.EmDash, ["em dash"], "\u2014"),
        new(SpokenPunctuationCommand.EnDash, ["en dash"], "\u2013"),
        new(SpokenPunctuationCommand.Ellipsis, ["ellipsis"], "\u2026"),
        new(SpokenPunctuationCommand.Ampersand, ["ampersand"], "&"),
        new(SpokenPunctuationCommand.PlusSign, ["plus sign"], "+"),
        new(SpokenPunctuationCommand.EqualsSign, ["equals sign", "equal sign"], "="),
        new(SpokenPunctuationCommand.VerticalBar, ["vertical bar"], "|"),
        new(SpokenPunctuationCommand.Asterisk, ["asterisk"], "*"),
        new(SpokenPunctuationCommand.HashSign, ["hash sign", "number sign"], "#"),
        new(SpokenPunctuationCommand.Tilde, ["tilde"], "~"),
    ];

    public static IReadOnlyList<SpokenPunctuationRule> EnglishSymbolRules { get; } =
        English.Where(static r => r.Command.GetRole() == SpokenPunctuationRole.Symbol).ToArray();

    private static readonly IReadOnlyList<SpokenPunctuationRule> EnglishWithoutPeriodWord =
        English.Select(static r => r.Command == SpokenPunctuationCommand.Period
            ? r with { SpokenForms = r.SpokenForms.Where(static f => f != "period").ToArray() }
            : r).ToArray();

    public static IReadOnlyList<SpokenPunctuationRule>? RulesFor(string? language) => BaseCode(language) switch
    {
        "de" => German,
        "fr" => French,
        "es" => Spanish,
        "it" => Italian,
        _ => null,
    };

    public static IReadOnlyList<SpokenPunctuationRule>? StartWordRulesFor(string? language) =>
        BaseCode(language) == "en" ? English : RulesFor(language);

    public static IReadOnlyList<SpokenPunctuationRule> EnglishRules(bool periodWord = true) =>
        periodWord ? English : EnglishWithoutPeriodWord;

    public static IReadOnlyList<SpokenPunctuationRule>? ActiveStartWordRules(
        string? language,
        bool englishPeriodWord = true) =>
        BaseCode(language) == "en" ? EnglishRules(englishPeriodWord) : RulesFor(language);

    public static string? DefaultStartWord(string? language) => BaseCode(language) switch
    {
        "de" => "Diktiere",
        "fr" => "Place",
        "es" => "A\u00F1ade",
        "it" => "Metti",
        _ => null,
    };

    public static string? StartWordDefaultFor(string? language) =>
        BaseCode(language) == "en" ? string.Empty : DefaultStartWord(language);

    public static IReadOnlyList<string>? SpokenFormsFor(string? language) =>
        RulesFor(language)
            ?.Where(static r => r.Command.GetRole() != SpokenPunctuationRole.Symbol)
            .SelectMany(static r => r.SpokenForms)
            .ToArray();

    public static IReadOnlyList<string>? StartWordFormsFor(string? language) =>
        StartWordRulesFor(language)
            ?.Where(static r => r.Command.GetRole() != SpokenPunctuationRole.Symbol)
            .SelectMany(static r => r.SpokenForms)
            .ToArray();

    public static string? BaseCode(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        var lowered = language.Trim().ToLowerInvariant();
        var sep = lowered.IndexOfAny(['-', '_']);
        return sep > 0 ? lowered[..sep] : (sep == 0 ? null : lowered);
    }

    public static bool TryValidateStartWord(
        string? raw,
        string? language,
        IReadOnlyList<string> spokenForms,
        out string validated)
    {
        validated = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var word = raw.Trim().Normalize(NormalizationForm.FormC);
        foreach (var rune in word.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                return false;
            }
        }

        var runes = word.EnumerateRunes().ToArray();
        if (runes.Length is < 2 or > 20)
        {
            return false;
        }

        for (var i = 0; i < runes.Length; i++)
        {
            var rune = runes[i];
            if (Rune.IsLetter(rune))
            {
                continue;
            }

            var ch = rune.Value;
            if ((ch == '\'' || ch == '-') && i > 0 && i < runes.Length - 1 && Rune.IsLetter(runes[i - 1]))
            {
                continue;
            }

            return false;
        }

        var culture = BaseCode(language) is { } code
            ? CultureInfo.GetCultureInfo(code)
            : CultureInfo.InvariantCulture;
        var folded = word.ToLower(culture);
        foreach (var form in spokenForms)
        {
            var normalizedForm = form.Normalize(NormalizationForm.FormC).ToLower(culture);
            if (normalizedForm == folded)
            {
                return false;
            }

            var first = normalizedForm.Split([' ', '\t', '\u00A0', '-'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (first == folded)
            {
                return false;
            }
        }

        validated = word;
        return true;
    }
}
