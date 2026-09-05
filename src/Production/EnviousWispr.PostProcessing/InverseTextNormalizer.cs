using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;

namespace EnviousWispr.PostProcessing;

public static class InverseTextNormalizer
{
    private static readonly IReadOnlyDictionary<string, long> Units =
        new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["zero"] = 0,
            ["oh"] = 0,
            ["o"] = 0,
            ["one"] = 1,
            ["two"] = 2,
            ["three"] = 3,
            ["four"] = 4,
            ["five"] = 5,
            ["six"] = 6,
            ["seven"] = 7,
            ["eight"] = 8,
            ["nine"] = 9,
            ["ten"] = 10,
            ["eleven"] = 11,
            ["twelve"] = 12,
            ["thirteen"] = 13,
            ["fourteen"] = 14,
            ["fifteen"] = 15,
            ["sixteen"] = 16,
            ["seventeen"] = 17,
            ["eighteen"] = 18,
            ["nineteen"] = 19,
        };

    private static readonly IReadOnlyDictionary<string, long> Tens =
        new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["twenty"] = 20,
            ["thirty"] = 30,
            ["forty"] = 40,
            ["fifty"] = 50,
            ["sixty"] = 60,
            ["seventy"] = 70,
            ["eighty"] = 80,
            ["ninety"] = 90,
        };

    private static readonly IReadOnlyDictionary<string, long> Scales =
        new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["hundred"] = 100,
            ["thousand"] = 1_000,
            ["million"] = 1_000_000,
            ["billion"] = 1_000_000_000,
        };

    private static readonly IReadOnlyDictionary<string, int> Months =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["january"] = 1,
            ["february"] = 2,
            ["march"] = 3,
            ["april"] = 4,
            ["may"] = 5,
            ["june"] = 6,
            ["july"] = 7,
            ["august"] = 8,
            ["september"] = 9,
            ["october"] = 10,
            ["november"] = 11,
            ["december"] = 12,
        };

    private static readonly string[] MonthNames =
        ["", "January", "February", "March", "April", "May", "June", "July",
            "August", "September", "October", "November", "December"];

    private static readonly IReadOnlyDictionary<string, int> Ordinals =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["first"] = 1,
            ["second"] = 2,
            ["third"] = 3,
            ["fourth"] = 4,
            ["fifth"] = 5,
            ["sixth"] = 6,
            ["seventh"] = 7,
            ["eighth"] = 8,
            ["ninth"] = 9,
            ["tenth"] = 10,
            ["eleventh"] = 11,
            ["twelfth"] = 12,
            ["thirteenth"] = 13,
            ["fourteenth"] = 14,
            ["fifteenth"] = 15,
            ["sixteenth"] = 16,
            ["seventeenth"] = 17,
            ["eighteenth"] = 18,
            ["nineteenth"] = 19,
            ["twentieth"] = 20,
            ["thirtieth"] = 30,
            ["fortieth"] = 40,
            ["fiftieth"] = 50,
            ["sixtieth"] = 60,
            ["seventieth"] = 70,
            ["eightieth"] = 80,
            ["ninetieth"] = 90,
            ["twenty first"] = 21,
            ["twenty second"] = 22,
            ["twenty third"] = 23,
            ["twenty fourth"] = 24,
            ["twenty fifth"] = 25,
            ["twenty sixth"] = 26,
            ["twenty seventh"] = 27,
            ["twenty eighth"] = 28,
            ["twenty ninth"] = 29,
            ["thirty first"] = 31,
        };

    private static readonly HashSet<string> UnitNouns = new(
        [
            "mile", "miles", "foot", "feet", "inch", "inches", "yard", "yards",
            "pound", "pounds", "ounce", "ounces", "kg", "kilogram", "kilograms",
            "gram", "grams", "km", "kilometer", "kilometers", "meter", "meters",
            "metre", "metres", "cm", "centimeter", "centimeters", "liter", "liters",
            "litre", "litres", "gallon", "gallons", "cup", "cups", "tablespoon",
            "tablespoons", "teaspoon", "teaspoons", "degree", "degrees", "mph",
            "percent", "milligram", "milligrams", "mg", "milliliter", "milliliters",
            "ml", "millimeter", "millimeters", "mm",
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> AgePeriods = new(
        ["year", "years", "month", "months", "week", "weeks", "day", "days"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> DurationNouns = new(
        [
            "second", "seconds", "video", "clip", "ad", "ads", "advert", "advertisement",
            "commercial", "timer", "countdown", "break", "intro", "introduction", "delay",
            "pause", "window", "interval", "mark", "segment", "spot", "trailer", "teaser",
            "rule", "gap", "lead", "burst", "sprint", "rest", "head",
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly string NumberWordAlternation = Alternation(
        Units.Keys.Concat(Tens.Keys).Concat(Scales.Keys).Append("and"));
    private static readonly string NumberWordNoAndAlternation = Alternation(
        Units.Keys.Concat(Tens.Keys).Concat(Scales.Keys));
    private static readonly string DigitWordAlternation = Alternation(
        Units.Where(pair => pair.Value < 10).Select(pair => pair.Key));
    private static readonly string NumberToken = $@"(?:{NumberWordAlternation}|\d[\d,]*)";
    private static readonly string HourToken = $@"(?:{Alternation(Units.Keys.Concat(Tens.Keys))}|\d{{1,2}})";
    private static readonly string NumberRun =
        $@"(?:{NumberWordNoAndAlternation})(?:\s+(?:{NumberWordAlternation}))*";
    private static readonly string NumberOrDigitRun =
        $@"(?:{NumberWordNoAndAlternation}|\d[\d,]*)(?:\s+(?:{NumberWordAlternation}|\d[\d,]*))*";
    private static readonly string OrdinalAlternation = Alternation(Ordinals.Keys);
    private static readonly string SimpleOrdinalAlternation = Alternation(
        Ordinals.Keys.Where(key => !key.Contains(' ')));
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    // A LOOKBEHIND WOULD KEEP THESE PATTERNS ON THE BACKTRACKING ENGINE, and these four are the ones
    // that open with a number run, which is exactly where the quadratic re-scan lived: measured on
    // the 800-word #91 input, the two money patterns cost 593 ms and the two time patterns 138 ms
    // while every non-backtracking pass cost nothing. So the character that must not precede the
    // match is CONSUMED as a named group instead of looked behind at, and each evaluator puts it
    // back in front of what it returns. A match that gives back match.Value is unchanged, because
    // the lead is already in it.
    private const string Lead = "lead";
    private static readonly string NotAfterDigitOrDot = $@"(?<{Lead}>^|[^\d.])";
    private static readonly string NotAfterDigitOrColon = $@"(?<{Lead}>^|[^\d:])";

    public static string Normalize(string text, bool spokenPunctuation = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrWhiteSpace(text))
        {
            return text.Trim();
        }

        var working = " " + text.Trim() + " ";
        var alphabetic = new string(text.Where(char.IsLetter).ToArray());
        var shout = alphabetic.Length > 1 && string.Equals(
            alphabetic,
            alphabetic.ToUpperInvariant(),
            StringComparison.Ordinal);
        var protectedSpans = new List<string>();
        working = Protect(
            working,
            @"\b(?:a |an )?(?:quarter|half)\s+(?:past|to)\s+\w+|\beleventh hour\b|\bseventh heaven\b|\bthe fourth wall\b|\bthe fifth wheel\b|\bthe whole nine yards\b",
            protectedSpans);
        working = Protect(
            working,
            @"\b(?:a |an )?(?:couple|few|several|many)\s+hundred\b",
            protectedSpans);
        working = Replace(
            working,
            @"\b(?:a|an)\s+(hundred\b)(?!-)",
            match => match.Groups[1].Value,
            ignoreCase: false);
        working = Replace(
            working,
            @"\b(a|an|the|this|that|another)\s+catch[\s-]+(?:twenty[\s-]+two|22)\b",
            match => match.Groups[1].Value + " " + ProtectValue("Catch-22", protectedSpans));
        working = Replace(
            working,
            $@"\b((?i:{NumberWordNoAndAlternation}))-(?=(?:{NumberWordNoAndAlternation}|{OrdinalAlternation})\b)",
            match => match.Groups[1].Value + " ",
            ignoreCase: false);

        working = NormalizeEmails(working);
        working = NormalizeUrls(working);
        var dotPart = $@"(?:{DigitWordAlternation}|\d[\d,]*)(?:\s+(?:{DigitWordAlternation}|\d[\d,]*))*";
        working = Protect(
            working,
            $@"\b{dotPart}(?:\s+dot\s+{dotPart}){{2,}}\b",
            protectedSpans);
        working = NormalizeDecimals(working);
        working = NormalizeMoneyAndPercent(working);
        working = NormalizeTimes(working);
        working = NormalizeDates(working);
        working = NormalizeOrdinals(working);
        working = NormalizeYears(working);
        working = NormalizeMoneyAndPercent(working);
        working = NormalizeDigitScales(working);
        working = Replace(
            working,
            $@"\b(?:o|oh|zero)\s+(?<digit>{Alternation(Units.Where(pair => pair.Value is >= 1 and <= 9).Select(pair => pair.Key))})\s+hundred\b",
            match => (Units[match.Groups["digit"].Value] * 100).ToString(CultureInfo.InvariantCulture));
        working = NormalizeDigitReads(working);
        working = NormalizeRangesAndDimensions(working);
        working = NormalizeCardinals(working, shout);
        working = Replace(
            working,
            @"\b[\d,]+(?:\s+slash\s+[\d,]+)+\b",
            match => string.Join('/', Regex.Split(match.Value, @"\s+slash\s+", RegexOptions.IgnoreCase)
                .Select(part => part.Replace(",", string.Empty, StringComparison.Ordinal).Trim())));
        working = Replace(
            working,
            @"(?<![-\d.])\b(\d+)\s+to\s+(\d+)\b",
            match => $"{match.Groups[1].Value}-{match.Groups[2].Value}",
            ignoreCase: false);
        working = Replace(
            working,
            @"\b(\d[\d,]*\.\d+)\s+(?:percent|per\s+cent)\b",
            match => match.Groups[1].Value + "%");
        working = KeepMagnitude(working);
        working = ApplyPunctuation(working, spokenPunctuation);

        for (var index = 0; index < protectedSpans.Count; index++)
        {
            working = working.Replace(
                Placeholder(index),
                protectedSpans[index].Trim(),
                StringComparison.Ordinal);
        }

        working = Replace(working, @"\s+([,.!?;:])", match => match.Groups[1].Value, ignoreCase: false);
        working = Replace(working, @"([(\[\u201c\u2018])\s+", match => match.Groups[1].Value, ignoreCase: false);
        working = Replace(working, @"\s+([)\]\u201d\u2019])", match => match.Groups[1].Value, ignoreCase: false);
        working = Replace(
            working,
            @"(\d(?:st|nd|rd|th)?)\s+-(\w)",
            match => $"{match.Groups[1].Value}-{match.Groups[2].Value}",
            ignoreCase: false);
        working = Replace(working, @"[ \t]+", _ => " ", ignoreCase: false);
        return working.Trim();
    }

    internal static long? WordsToInteger(IEnumerable<string> source)
    {
        var words = source.Where(word => word.Length > 0).Select(word => word.ToLowerInvariant()).ToArray();
        if (words.Length == 0)
        {
            return null;
        }

        if (words.Length == 1 && (words[0] == "zero" || words[0] == "0"))
        {
            return 0;
        }

        long total = 0;
        long current = 0;
        string? last = null;
        var seen = false;
        foreach (var word in words)
        {
            if (word == "and")
            {
                continue;
            }

            if (long.TryParse(word.Replace(",", string.Empty, StringComparison.Ordinal),
                    NumberStyles.None, CultureInfo.InvariantCulture, out var digits))
            {
                if (digits == 0)
                {
                    return null;
                }

                current += digits;
                last = digits < 10 ? "unit" : digits < 20 ? "teen" : digits < 100 ? "ten" : "big";
                seen = true;
            }
            else if (Units.TryGetValue(word, out var unit))
            {
                if (unit == 0)
                {
                    return null;
                }

                if (unit < 10)
                {
                    if (last is "unit" or "teen")
                    {
                        return null;
                    }

                    current += unit;
                    last = "unit";
                }
                else
                {
                    if (last is "unit" or "teen" or "ten")
                    {
                        return null;
                    }

                    current += unit;
                    last = "teen";
                }

                seen = true;
            }
            else if (Tens.TryGetValue(word, out var ten))
            {
                if (last is "unit" or "teen" or "ten")
                {
                    return null;
                }

                current += ten;
                last = "ten";
                seen = true;
            }
            else if (word == "hundred")
            {
                if (last is "unit" or "teen" or "ten" && current is >= 1 and <= 99)
                {
                    current *= 100;
                }
                else if (last is null or "scale")
                {
                    current = 100;
                }
                else
                {
                    return null;
                }

                last = "hundred";
                seen = true;
            }
            else if (Scales.TryGetValue(word, out var scale))
            {
                if (scale == 100)
                {
                    return null;
                }

                if (current == 0 && total == 0 && last is null)
                {
                    return null;
                }

                total += (current == 0 ? 1 : current) * scale;
                current = 0;
                last = "scale";
                seen = true;
            }
            else
            {
                return null;
            }
        }

        return seen ? total + current : null;
    }

    private static string NormalizeEmails(string input) => Replace(
        input,
        @"\b(?<name>[a-z][a-z0-9_]*)\s+at\s+(?<domain>[a-z][a-z0-9-]*)\s+dot\s+(?<tld>com|org|io|co|dev|me|net|edu|gov)\b",
        match => $"{match.Groups["name"].Value}@{match.Groups["domain"].Value}.{match.Groups["tld"].Value}");

    private static string NormalizeUrls(string input)
    {
        var result = Replace(
            input,
            @"(?<![@a-z0-9.-])\b(?<host>[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?)\s+dot\s+(?<tld>com|org|io|co|dev|me|net)\b(?<path>(?:\s+slash\s+[a-z0-9-]+)*)",
            match => ShouldDeclineUrl(input, match, checkSpokenAt: false)
                ? match.Value
                : BuildUrl(
                    match.Groups["host"].Value + "." + match.Groups["tld"].Value,
                    match.Groups["path"].Value));
        return Replace(
            result,
            @"(?<![@a-z0-9.-])\b(?<host>(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+(?:com|org|io|co|dev|me|net|ai|app|xyz))\b(?<path>(?:\s+slash\s+[a-z0-9-]+)+)",
            match => ShouldDeclineUrl(result, match, checkSpokenAt: true)
                ? match.Value
                : BuildUrl(match.Groups["host"].Value, match.Groups["path"].Value));
    }

    private static bool ShouldDeclineUrl(string source, Match match, bool checkSpokenAt)
    {
        var beforeStart = Math.Max(0, match.Index - 30);
        var before = source[beforeStart..match.Index];
        if (Regex.IsMatch(
                before,
                @"(?:colon|:)\s{0,4}slash\s{0,4}slash\s{0,4}$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
            Regex.IsMatch(
                before,
                @"(?:\b(?:dot|dash|hyphen|underscore|question\s+mark|equals|ampersand|percent|tilde|hash|pound)\s{1,4}|[._:?=&%~#-])$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
            Regex.IsMatch(before, @"@\s{1,4}$", RegexOptions.CultureInvariant) ||
            checkSpokenAt && !EndsWithCommonWordTld(match.Groups["host"].Value) && Regex.IsMatch(
                before,
                @"\bat\s{1,4}$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return true;
        }

        var after = source[(match.Index + match.Length)..];
        return match.Groups["path"].Success && match.Groups["path"].Value.Length > 0 && Regex.IsMatch(
            after,
            @"^\s+(?:dot|dash|hyphen|underscore|colon|question\s+mark|equals|ampersand|percent|tilde|hash|pound|slash)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool EndsWithCommonWordTld(string host) =>
        host.EndsWith(".ai", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".xyz", StringComparison.OrdinalIgnoreCase);

    private static string BuildUrl(string host, string path)
    {
        var segments = Regex.Matches(path, @"slash\s+([a-z0-9-]+)", RegexOptions.IgnoreCase)
            .Select(match => match.Groups[1].Value);
        return host + string.Concat(segments.Select(segment => "/" + segment));
    }

    private static string NormalizeDecimals(string input)
    {
        var result = Replace(
            input,
            $@"\b(?<whole>{NumberOrDigitRun})\s+(?:point|dot)\s+(?<fraction>(?:{DigitWordAlternation})(?:\s+(?:{DigitWordAlternation}))*)(?:\s+(?<scale>thousand|million|billion))?\b",
            match =>
            {
                var whole = WordsToInteger(SplitWords(match.Groups["whole"].Value));
                if (whole is null)
                {
                    return match.Value;
                }

                var fraction = string.Concat(SplitWords(match.Groups["fraction"].Value)
                    .Select(word => Units[word].ToString(CultureInfo.InvariantCulture)));
                if (match.Groups["scale"].Success)
                {
                    var value = decimal.Parse(
                        $"{whole}.{fraction}",
                        CultureInfo.InvariantCulture) * Scales[match.Groups["scale"].Value];
                    return FormatInteger(decimal.ToInt64(decimal.Round(value, 0, MidpointRounding.ToEven)));
                }

                return $"{whole}.{fraction}";
            });
        return Replace(
            result,
            $@"\b(?<sign>negative\s+|minus\s+)?point\s+(?<fraction>(?:{DigitWordAlternation})(?:\s+(?:{DigitWordAlternation}))*)\b",
            match =>
            {
                var fraction = string.Concat(SplitWords(match.Groups["fraction"].Value)
                    .Select(word => Units[word].ToString(CultureInfo.InvariantCulture)));
                if (!match.Groups["sign"].Success && fraction.Length < 3)
                {
                    return match.Value;
                }

                return (match.Groups["sign"].Success ? "negative " : string.Empty) + "0." + fraction;
            });
    }

    private static string NormalizeMoneyAndPercent(string input)
    {
        var result = Replace(
            input,
            $@"{NotAfterDigitOrDot}\b(?:and\s+)?(?<dollars>{NumberOrDigitRun})\s+dollars?(?:\s+and\s+(?<cents>{NumberOrDigitRun})\s+cents?)?\b",
            match =>
            {
                var dollars = WordsToInteger(SplitWords(match.Groups["dollars"].Value));
                if (dollars is null)
                {
                    return match.Value;
                }

                if (match.Groups["cents"].Success &&
                    WordsToInteger(SplitWords(match.Groups["cents"].Value)) is long cents)
                {
                    return match.Groups[Lead].Value + $"${FormatInteger(dollars.Value)}.{cents:00}";
                }

                return match.Groups[Lead].Value + "$" + FormatInteger(dollars.Value);
            });
        result = Replace(
            result,
            $@"{NotAfterDigitOrDot}\b(?:and\s+)?(?<cents>{NumberRun})\s+cents?\b",
            match => WordsToInteger(SplitWords(match.Groups["cents"].Value)) is long cents
                ? match.Groups[Lead].Value + (cents / 100m).ToString("$0.00", CultureInfo.InvariantCulture)
                : match.Value);
        return Replace(
            result,
            $@"\b(?<amount>{NumberOrDigitRun})\s+(?:percent|per\s+cent)\b",
            match => WordsToInteger(SplitWords(match.Groups["amount"].Value)) is long amount
                ? amount.ToString(CultureInfo.InvariantCulture) + "%"
                : match.Value);
    }

    private static string NormalizeTimes(string input)
    {
        var result = Replace(
            input,
            $@"{NotAfterDigitOrColon}\b(?<hour>{HourToken})(?:\s+(?<minute>{NumberOrDigitRun}))?\s+(?<period>[ap])\s*m\b",
            match =>
            {
                var hour = WordsToInteger(SplitWords(match.Groups["hour"].Value));
                var minute = match.Groups["minute"].Success
                    ? ParseClockPart(SplitWords(match.Groups["minute"].Value))
                    : 0;
                if (hour is null || minute is null || hour is < 1 or > 12 || minute is < 0 or > 59)
                {
                    return match.Value;
                }

                return match.Groups[Lead].Value +
                    $"{hour}:{minute:00} {match.Groups["period"].Value.ToUpperInvariant()}M";
            });
        return Replace(
            result,
            $@"{NotAfterDigitOrColon}\b(?<hour>{HourToken})\s+o'?clock\b",
            match => WordsToInteger(SplitWords(match.Groups["hour"].Value)) is long hour && hour is >= 1 and <= 12
                ? match.Groups[Lead].Value + $"{hour}:00"
                : match.Value);
    }

    private static long? ParseClockPart(IReadOnlyList<string> words)
    {
        var cardinal = WordsToInteger(words);
        if (cardinal is not null)
        {
            return cardinal;
        }

        if (words.All(word => Units.TryGetValue(word, out var value) && value < 10))
        {
            var digits = string.Concat(words.Select(word => Units[word].ToString(CultureInfo.InvariantCulture)));
            return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
        }

        return null;
    }

    private static string NormalizeDates(string input) => Replace(
        input,
        $@"\b(?<month>{Alternation(Months.Keys)})\s+(?<day>{OrdinalAlternation}|\d{{1,2}}),?\s+(?<year>(?:{NumberWordAlternation})(?:\s+(?:{NumberWordAlternation})){{1,3}})\b",
        match =>
        {
            var dayText = match.Groups["day"].Value;
            var day = int.TryParse(dayText, NumberStyles.None, CultureInfo.InvariantCulture, out var numericDay)
                ? numericDay
                : ParseOrdinal(dayText);
            var year = ParseYear(SplitWords(match.Groups["year"].Value));
            if (day is < 1 or > 31 || year is null)
            {
                return match.Value;
            }

            return $"{MonthNames[Months[match.Groups["month"].Value]]} {day}, {year}";
        });

    private static string NormalizeYears(string input)
    {
        var century = Alternation(["fifteen", "sixteen", "seventeen", "eighteen", "nineteen", "twenty"]);
        var result = Replace(
            input,
            $@"\b(?<century>{century})\s+(?<low>(?:{Alternation(Tens.Keys)})(?:\s+(?:{Alternation(Units.Where(pair => pair.Value is >= 1 and <= 9).Select(pair => pair.Key))}))?|(?:{Alternation(Units.Where(pair => pair.Value is >= 10 and <= 19).Select(pair => pair.Key))})|(?:oh|o)\s+(?:{Alternation(Units.Where(pair => pair.Value is >= 1 and <= 9).Select(pair => pair.Key))}))\b",
            match => ParseYear(SplitWords(match.Value))?.ToString(CultureInfo.InvariantCulture) ?? match.Value);
        return Replace(
            result,
            $@"\btwo thousand(?:\s+and)?\s+(?<low>{NumberRun})\b",
            match =>
            {
                if (WordsToInteger(SplitWords(match.Groups["low"].Value)) is not long low ||
                    low is < 1 or > 99)
                {
                    return match.Value;
                }

                var trailingAnd = match.Groups["low"].Value.TrimEnd()
                    .EndsWith(" and", StringComparison.OrdinalIgnoreCase)
                    ? " and"
                    : string.Empty;
                return (2000 + low).ToString(CultureInfo.InvariantCulture) + trailingAnd;
            });
    }

    private static string NormalizeDigitReads(string input) => Replace(
        input,
        $@"(?:\b(?:{DigitWordAlternation})\b\s*|\b\d{{1,4}}\b\s*){{2,}}",
        match =>
        {
            var tokens = SplitWords(match.Value);
            var digits = new List<string>();
            foreach (var token in tokens)
            {
                if (Units.TryGetValue(token, out var value) && value < 10)
                {
                    digits.Add(value.ToString(CultureInfo.InvariantCulture));
                }
                else if (token.All(char.IsDigit) && token.Length <= 4)
                {
                    digits.Add(token);
                }
                else
                {
                    return match.Value;
                }
            }

            var joined = string.Concat(digits);
            if (joined.Length == 7)
            {
                return $" {joined[..3]}-{joined[3..]} ";
            }

            if (joined.Length == 10)
            {
                return $" {joined[..3]}-{joined[3..6]}-{joined[6..]} ";
            }

            var hasWordZero = tokens.Any(token => token is "zero" or "oh" or "o");
            return hasWordZero && joined.Length <= 6 ? " " + joined + " " : match.Value;
        });

    private static string NormalizeDigitScales(string input) => Replace(
        input,
        $@"\b(?<value>\d[\d,]*)\s+(?<scale>hundred|thousand)(?:\s+(?<tail>{NumberRun}))?\b",
        match =>
        {
            var words = new List<string>
            {
                match.Groups["value"].Value,
                match.Groups["scale"].Value,
            };
            if (match.Groups["tail"].Success)
            {
                words.AddRange(SplitWords(match.Groups["tail"].Value));
            }

            return WordsToInteger(words) is long value ? FormatInteger(value) : match.Value;
        });

    private static string NormalizeRangesAndDimensions(string input)
    {
        var result = Replace(
            input,
            $@"\bbetween\s+(?<left>{NumberOrDigitRun})\s+and\s+(?<right>{NumberOrDigitRun})\b",
            match => match.Groups["left"].Value.Contains(" and ", StringComparison.OrdinalIgnoreCase)
                ? match.Value
                : TryRange(match.Groups["left"].Value, match.Groups["right"].Value, "between ") ?? match.Value);
        result = Replace(
            result,
            $@"\b(?<left>{NumberOrDigitRun})\s+(?:to|through)\s+(?<right>{NumberOrDigitRun})\b",
            match => TryRange(match.Groups["left"].Value, match.Groups["right"].Value, string.Empty) ?? match.Value);
        result = Replace(
            result,
            $@"\b(?<values>{NumberOrDigitRun}(?:\s+slash\s+{NumberOrDigitRun})+)\b",
            match => TryJoinedValues(
                match.Groups["values"].Value,
                "slash",
                "/",
                groupThousands: false) ?? match.Value);
        return Replace(
            result,
            $@"\b(?<values>{NumberOrDigitRun}(?:\s+by\s+{NumberOrDigitRun})+)\b",
            match =>
            {
                var joined = TryJoinedValues(
                    match.Groups["values"].Value,
                    "by",
                    " by ",
                    groupThousands: true);
                return joined is "1 by 1" ? match.Value : joined ?? match.Value;
            });
    }

    private static string NormalizeOrdinals(string input)
    {
        var result = Replace(
            input,
            $@"\b(?<lead>(?:(?:{NumberWordAlternation})\s+)*(?:hundred|thousand|million|billion)(?:\s+and)?)\s+(?<tail>{SimpleOrdinalAlternation})\b",
            match =>
            {
                var next = Regex.Match(
                    input[(match.Index + match.Length)..],
                    @"^\s+(?<word>[\p{L}]+)",
                    RegexOptions.CultureInvariant).Groups["word"].Value;
                if (match.Groups["tail"].Value.Equals("second", StringComparison.OrdinalIgnoreCase) &&
                    DurationNouns.Contains(next))
                {
                    return match.Value;
                }

                var lead = WordsToInteger(SplitWords(match.Groups["lead"].Value));
                var tail = ParseOrdinal(match.Groups["tail"].Value);
                if (lead is null || tail is null)
                {
                    return match.Value;
                }

                var value = lead.Value + tail.Value;
                return FormatInteger(value) + OrdinalSuffix(value);
            });
        result = Replace(
            result,
            $@"\b(?<tens>{Alternation(Tens.Keys)})\s+(?<unit>first|second|third|fourth|fifth|sixth|seventh|eighth|ninth)\b",
            match =>
            {
                var next = Regex.Match(
                    input[(match.Index + match.Length)..],
                    @"^\s+(?<word>[\p{L}]+)",
                    RegexOptions.CultureInvariant).Groups["word"].Value;
                if (match.Groups["unit"].Value.Equals("second", StringComparison.OrdinalIgnoreCase) &&
                    DurationNouns.Contains(next))
                {
                    return match.Value;
                }

                var number = Tens[match.Groups["tens"].Value] + Ordinals[match.Groups["unit"].Value];
                return number + OrdinalSuffix(number);
            });
        result = Replace(
            result,
            $@"\b(?<lead>{NumberRun})\s+(?<scale>hundredth|thousandth|millionth|billionth)\b",
            match =>
            {
                var scaleWord = match.Groups["scale"].Value[..^2];
                var value = WordsToInteger(SplitWords(match.Groups["lead"].Value).Append(scaleWord));
                return value is null ? match.Value : FormatInteger(value.Value) + OrdinalSuffix(value.Value);
            });
        return Replace(
            result,
            $@"\b(?:{OrdinalAlternation})\b",
            match =>
            {
                var afterIndex = Math.Min(input.Length, match.Index + match.Length);
                var next = Regex.Match(
                    input[afterIndex..],
                    @"^\s+(?<word>[\p{L}]+)",
                    RegexOptions.CultureInvariant).Groups["word"].Value;
                if (match.Value.EndsWith(" second", StringComparison.OrdinalIgnoreCase) &&
                    DurationNouns.Contains(next))
                {
                    return match.Value;
                }

                return ParseOrdinal(match.Value) is int number && number >= 10
                    ? number + OrdinalSuffix(number)
                    : match.Value;
            });
    }

    private static string NormalizeCardinals(string input, bool shout) => Replace(
        input,
        $@"\b{NumberRun}\b",
        match =>
        {
            var words = SplitWords(match.Value);
            var trailingAndCount = 0;
            while (words.Count > 0 && words[^1].Equals("and", StringComparison.OrdinalIgnoreCase))
            {
                words.RemoveAt(words.Count - 1);
                trailingAndCount++;
            }

            var trailingAnd = string.Concat(Enumerable.Repeat(" and", trailingAndCount));

            var value = WordsToInteger(words);
            if (value is null)
            {
                var afterFailure = input.AsSpan(match.Index + match.Length).TrimStart().ToString();
                var nextAfterFailure = Regex.Match(
                    afterFailure,
                    @"^[\p{L}]+",
                    RegexOptions.CultureInvariant).Value;
                if (words.Count >= 2 && Units.TryGetValue(words[^1], out var dosage) &&
                    dosage is >= 1 and < 10 && UnitNouns.Contains(nextAfterFailure))
                {
                    return string.Join(' ', words.Take(words.Count - 1)) + " " + dosage + trailingAnd;
                }

                return match.Value;
            }

            var after = input.AsSpan(match.Index + match.Length).TrimStart();
            var next = Regex.Match(after.ToString(), @"^[\p{L}]+", RegexOptions.CultureInvariant).Value;
            var rawWords = Regex.Matches(match.Value, @"[\p{L}]+", RegexOptions.CultureInvariant)
                .Select(rawMatch => rawMatch.Value)
                .ToArray();
            var capitalizedWords = rawWords.Count(word => word.Length > 0 && char.IsUpper(word[0]));
            var runAllCaps = rawWords.Length > 0 && rawWords.All(word => string.Equals(
                word,
                word.ToUpperInvariant(),
                StringComparison.Ordinal));
            var before = input.AsSpan(0, match.Index).TrimEnd();
            var sentenceInitial = before.Length == 0 || ".!?\n\"'([".Contains(before[^1]);
            var nextAllCaps = next.Length > 0 && string.Equals(
                next,
                next.ToUpperInvariant(),
                StringComparison.Ordinal);
            if (runAllCaps && !shout && nextAllCaps)
            {
                return match.Value;
            }

            if (!runAllCaps &&
                (capitalizedWords > 1 ||
                capitalizedWords == 1 && rawWords.Length == 1 && next.Length > 0 && char.IsUpper(next[0]) ||
                capitalizedWords > 0 && !sentenceInitial))
            {
                return match.Value;
            }

            var afterText = after.ToString();
            if (afterText.Length > 1 && afterText[0] == '-' && char.IsUpper(afterText[1]))
            {
                return match.Value;
            }

            if (afterText.StartsWith('-') &&
                Regex.IsMatch(
                    afterText,
                    $@"^-(?:{NumberWordNoAndAlternation}|{OrdinalAlternation})\b",
                    RegexOptions.CultureInvariant))
            {
                return match.Value;
            }

            var force = UnitNouns.Contains(next) ||
                Regex.IsMatch(
                    afterText,
                    @"^(?:square|cubic)\s+[\p{L}]+\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
                UnitNouns.Contains(Regex.Match(
                    afterText,
                    @"^(?:square|cubic)\s+(?<unit>[\p{L}]+)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Groups["unit"].Value) ||
                AgePeriods.Contains(next) && Regex.IsMatch(afterText, @"^[\p{L}]+\s+old\b", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(afterText, @"^-(?:year|month|week|day)s?-old\b", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(
                    afterText,
                    @"^-(?:mile|foot|feet|inch|yard|pound|ounce|kilogram|gram|meter|metre|liter|litre|gallon|cup|degree)s?\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return value >= 10 || force ? FormatInteger(value.Value) + trailingAnd : match.Value;
        });

    private static string ApplyPunctuation(string input, bool enabled)
    {
        var result = input;
        if (enabled)
        {
            var replacements = new (string Pattern, string Value)[]
            {
                (@"\bnew paragraph\b", "\n\n"), (@"\bnew line\b", "\n"),
                (@"\s+comma\b", ","), (@"\s+period\b", "."),
                (@"\s+full stop\b", "."), (@"\s+question mark\b", "?"),
                (@"\s+exclamation (?:mark|point)\b", "!"), (@"\s+colon\b", ":"),
                (@"\s+semicolon\b", ";"),
            };
            foreach (var replacement in replacements)
            {
                result = Replace(result, replacement.Pattern, _ => replacement.Value);
            }
        }

        return Replace(
            result,
            @"([.!?]\s+)([a-z])",
            match => match.Groups[1].Value + match.Groups[2].Value.ToUpperInvariant(),
            ignoreCase: false);
    }

    private static string KeepMagnitude(string input) => Replace(
        input,
        @"(?<currency>\$)?(?<number>\d{1,3}(?:,\d{3})+)\b(?!\.\d)(?!,\d)",
        match =>
        {
            if (!long.TryParse(
                match.Groups["number"].Value.Replace(",", string.Empty, StringComparison.Ordinal),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var number))
            {
                return match.Value;
            }

            foreach (var (name, scale) in new[]
            {
                ("trillion", 1_000_000_000_000L),
                ("billion", 1_000_000_000L),
                ("million", 1_000_000L),
            })
            {
                if (number < scale || number % (scale / 1_000) != 0)
                {
                    continue;
                }

                var coefficient = (decimal)number / scale;
                if (coefficient >= 1_000)
                {
                    continue;
                }

                return match.Groups["currency"].Value +
                    coefficient.ToString("0.###", CultureInfo.InvariantCulture) + " " + name;
            }

            return match.Value;
        },
        ignoreCase: false);

    private static int? ParseOrdinal(string value)
    {
        var words = SplitWords(value);
        if (words.Count == 1)
        {
            return Ordinals.TryGetValue(words[0], out var ordinal) ? ordinal : null;
        }

        if (words.Count == 2 && Tens.TryGetValue(words[0], out var tens) &&
            Ordinals.TryGetValue(words[1], out var unit) && unit < 10)
        {
            return checked((int)(tens + unit));
        }

        return null;
    }

    private static long? ParseYear(IReadOnlyList<string> source)
    {
        var words = source.Select(word => word.Equals("o", StringComparison.OrdinalIgnoreCase) ? "oh" : word).ToArray();
        if (words.Any(word => word is "thousand" or "hundred"))
        {
            return WordsToInteger(words);
        }

        if (words.Length == 3 && words[1] == "oh" &&
            WordsToInteger([words[0]]) is long first &&
            WordsToInteger([words[2]]) is long last)
        {
            return first * 100 + last;
        }

        for (var split = 1; split < words.Length; split++)
        {
            var left = WordsToInteger(words[..split]);
            var right = WordsToInteger(words[split..]);
            if (left is >= 10 and <= 99 && right is >= 0 and <= 99)
            {
                return left * 100 + right;
            }
        }

        return WordsToInteger(words);
    }

    private static string? TryRange(string leftText, string rightText, string prefix)
    {
        var left = WordsToInteger(SplitWords(leftText));
        var right = WordsToInteger(SplitWords(rightText));
        return left is not null && right is not null
            ? $"{prefix}{FormatInteger(left.Value)}-{FormatInteger(right.Value)}"
            : null;
    }

    private static string? TryJoinedValues(
        string value,
        string separatorWord,
        string outputSeparator,
        bool groupThousands)
    {
        var parts = Regex.Split(
            value,
            $@"\s+{Regex.Escape(separatorWord)}\s+",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var values = parts.Select(part => WordsToInteger(SplitWords(part))).ToArray();
        return values.All(item => item is not null)
            ? string.Join(
                outputSeparator,
                values.Select(item => groupThousands
                    ? FormatInteger(item!.Value)
                    : item!.Value.ToString(CultureInfo.InvariantCulture)))
            : null;
    }

    private static string Protect(string input, string pattern, List<string> protectedSpans) =>
        Replace(input, pattern, match =>
        {
            var placeholder = Placeholder(protectedSpans.Count);
            protectedSpans.Add(match.Value);
            return placeholder;
        });

    private static string ProtectValue(string value, List<string> protectedSpans)
    {
        var placeholder = Placeholder(protectedSpans.Count);
        protectedSpans.Add(value);
        return placeholder;
    }

    private static string Placeholder(int index) => $"\uE000{index}\uE001";

    private static List<string> SplitWords(string value) => Regex
        .Matches(value.ToLowerInvariant(), @"[a-z]+|\d[\d,]*", RegexOptions.CultureInvariant)
        .Select(match => match.Value)
        .ToList();

    private static string Alternation(IEnumerable<string> values) => string.Join(
        '|',
        values.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(value => value.Length)
            .ThenBy(value => value, StringComparer.Ordinal)
            .Select(Regex.Escape));

    private static string FormatInteger(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string OrdinalSuffix(long value)
    {
        var lastTwo = Math.Abs(value) % 100;
        if (lastTwo is >= 11 and <= 13)
        {
            return "th";
        }

        return (Math.Abs(value) % 10) switch
        {
            1 => "st",
            2 => "nd",
            3 => "rd",
            _ => "th",
        };
    }

    private static string Replace(
        string input,
        string pattern,
        MatchEvaluator evaluator,
        bool ignoreCase = true) => Compiled(pattern, ignoreCase).Replace(input, evaluator);

    // EVERY PATTERN IS BUILT ONCE, AND BUILT NON-BACKTRACKING WHEREVER THE ENGINE ALLOWS IT.
    //
    // The cost that opened #91 was never one pattern: a run of spoken numbers is re-derived from
    // every starting position by every pattern that begins with a number run, and a backtracking
    // engine pays that quadratically to FAIL, which is the common case because most text is not a
    // number. Atomic grouping was measured and left the exponent alone, because it removes retries
    // inside the run and not the re-scan of the run per position. The non-backtracking engine
    // guarantees linear time in the input; the same alternations then cost what they read.
    //
    // It refuses lookarounds, backreferences and atomic groups by throwing at construction, so a
    // pattern that uses one is built the ordinary way and keeps the timeout as its guard. The choice
    // is made once per pattern and remembered; the static Regex cache holds fifteen entries and this
    // file has more patterns than that, so it was re-parsing them on every dictation.
    private static readonly ConcurrentDictionary<(string Pattern, bool IgnoreCase), Regex> Patterns = new();

    private static Regex Compiled(string pattern, bool ignoreCase) =>
        Patterns.GetOrAdd((pattern, ignoreCase), static key =>
        {
            var options = RegexOptions.CultureInvariant |
                (key.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
            try
            {
                return new Regex(key.Pattern, options | RegexOptions.NonBacktracking, RegexTimeout);
            }
            catch (NotSupportedException)
            {
                return new Regex(key.Pattern, options, RegexTimeout);
            }
        });

    /// <summary>The patterns that still run on the backtracking engine, for the test that keeps that list short.</summary>
    // BUILDING THE NON-BACKTRACKING PATTERNS COSTS ~257 ms, PAID ONCE PER PROCESS ON THE FIRST CALL.
    // That construction must never fall inside the pipeline's 500 ms ITN stage: on a loaded CI runner
    // it crossed the timeout, the stage returned un-normalised text, and the macOS parity oracle
    // mismatched. Worse for a user, their first dictation on a busy machine could lose ITN the same
    // way. So the pipeline calls this from its constructor, which runs off every timed path and off
    // the hotkey path, and the Lazy makes a second pipeline free. Ref: #91, #112.
    private static readonly Lazy<bool> WarmOnce = new(() =>
    {
        _ = Normalize(WarmRepresentative);
        _ = Normalize(WarmRepresentative, spokenPunctuation: true);
        return true;
    });

    private const string WarmRepresentative =
        "one point five dollars and two cents at half past three on the fourth of july nineteen ninety nine";

    /// <summary>Builds every pattern once, so no later call pays construction inside a timed stage.</summary>
    public static void Warm() => _ = WarmOnce.Value;

    internal static IReadOnlyList<string> BacktrackingPatterns()
    {
        Warm();
        return Patterns
            .Where(pair => (pair.Value.Options & RegexOptions.NonBacktracking) == 0)
            .Select(pair => pair.Key.Pattern)
            .OrderBy(pattern => pattern, StringComparer.Ordinal)
            .ToArray();
    }
}
