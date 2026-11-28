using System.Globalization;
using System.Text.RegularExpressions;

namespace EnviousWispr.PostProcessing;

public static partial class InverseTextNormalizer
{
    private static readonly string[] StreetTypes =
    [
        "Road", "Street", "Avenue", "Lane", "Drive", "Boulevard", "Court", "Way", "Place", "Circle",
        "Terrace", "Parkway", "Trail", "Highway",
    ];

    private static readonly string[] StreetDirections =
    [
        "Northeast", "Northwest", "Southeast", "Southwest", "North", "South", "East", "West",
    ];

    private static readonly string[] StreetUnitWords =
    [
        "Apartment", "apartment", "Apt", "apt", "Suite", "suite", "Unit", "unit", "Room", "room",
        "Floor", "floor",
    ];

    private static readonly string[] UsStates =
    [
        "District of Columbia",
        "Alabama", "Alaska", "Arizona", "Arkansas", "California", "Colorado", "Connecticut",
        "Delaware", "Florida", "Georgia", "Hawaii", "Idaho", "Illinois", "Indiana", "Iowa", "Kansas",
        "Kentucky", "Louisiana", "Maine", "Maryland", "Massachusetts", "Michigan", "Minnesota",
        "Mississippi", "Missouri", "Montana", "Nebraska", "Nevada", "New Hampshire", "New Jersey",
        "New Mexico", "New York", "North Carolina", "North Dakota", "Ohio", "Oklahoma", "Oregon",
        "Pennsylvania", "Rhode Island", "South Carolina", "South Dakota", "Tennessee", "Texas", "Utah",
        "Vermont", "Virginia", "Washington", "West Virginia", "Wisconsin", "Wyoming",
    ];

    private static readonly string[] UsStateCodes =
    [
        "AL", "AK", "AZ", "AR", "CA", "CO", "CT", "DE", "FL", "GA", "HI", "ID", "IL", "IN", "IA", "KS",
        "KY", "LA", "ME", "MD", "MA", "MI", "MN", "MS", "MO", "MT", "NE", "NV", "NH", "NJ", "NM", "NY",
        "NC", "ND", "OH", "OK", "OR", "PA", "RI", "SC", "SD", "TN", "TX", "UT", "VT", "VA", "WA", "WV",
        "WI", "WY", "DC",
    ];

    private static readonly HashSet<string> StreetTypeSet = new(StreetTypes, StringComparer.Ordinal);

    private static readonly HashSet<string> AddressIntroducers = new(StringComparer.OrdinalIgnoreCase)
    {
        "to", "at", "for", "is", "was", "are", "were", "be", "from", "on", "into", "onto", "via",
        "and", "or", "near", "of", "as", "address",
    };

    private static readonly string StreetUnitsTensAlt = Alternation(Units.Keys.Concat(Tens.Keys));

    private static readonly string StreetAddressPattern = BuildStreetAddressPattern();

    [GeneratedRegex(
        @"\d{5}|(?:zero|oh|o|one|two|three|four|five|six|seven|eight|nine)\s+\w+\s+\w+\s+\w+\s+\w+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RecognizerRegexTimeoutMilliseconds)]
    private static partial Regex StreetAddressPrefilterRegex();

    private static string BuildStreetAddressPattern()
    {
        var numWord = $@"(?i:{StreetUnitsTensAlt}|hundred|thousand)";
        const string digitWord = "(?i:zero|oh|o|one|two|three|four|five|six|seven|eight|nine)";
        const string cap = @"[A-Z][\p{L}'’\-]*";
        const string hs = @"[\t\p{Zs}]+";
        const string cityCap = @"(?:(?:St|Ft|Mt)\.|[A-Z][\p{L}'’\-]*)";
        var house = $@"(?<house>\d{{1,6}}|{numWord}(?:{hs}(?:(?i:and){hs})?{numWord}){{0,5}})";
        var street =
            $@"(?<street>(?:(?:{cap}|\d{{1,3}}(?:st|nd|rd|th)){hs}){{1,4}}(?:{string.Join('|', StreetTypes)}))";
        const string sepStreet = @"(?<sepStreet>(?>[^\S\n]*,[^\S\n]*\n?[^\S\n]*|[^\S\n]*\n[^\S\n]*|[^\S\n]+))";
        const string sepUnit = @"(?<sepUnit>(?>[^\S\n]*,[^\S\n]*\n?[^\S\n]*|[^\S\n]*\n[^\S\n]*|[^\S\n]+))";
        const string sepCity = @"(?<sepCity>(?>[^\S\n]*,[^\S\n]*\n?[^\S\n]*|[^\S\n]*\n[^\S\n]*|[^\S\n]+))";
        const string sepState = @"(?<sepState>(?>[^\S\n]*,[^\S\n]*\n?[^\S\n]*|[^\S\n]*\n[^\S\n]*|[^\S\n]+))";
        var unitNum =
            $@"(?:\d{{1,5}}(?:[A-Z]|{hs}[A-Z])?(?![\p{{L}}\d])|{numWord}(?:{hs}(?:(?i:and){hs})?{numWord}){{0,5}}(?:{hs}[A-Z](?![\p{{L}}]))?)";
        var unit =
            $@"(?:(?<unit>(?:{string.Join('|', StreetUnitWords)})\.?{hs}{unitNum}|#\s?\d{{1,5}}[A-Z]?){sepUnit})?";
        var city = $@"(?<city>{cityCap}(?:{hs}(?:(?:of|on|upon|de|del|la|le|du|the){hs})?{cap}){{0,2}})";
        var state = $@"(?<state>{Alternation(UsStates)}|{string.Join('|', UsStateCodes)})";
        var zip =
            $@"(?<zip>\d{{5}}(?:-\d{{4}}|\s+(?i:dash|hyphen)\s+(?:\d{{4}}|{digitWord}(?:\s+{digitWord}){{3}}))?|{digitWord}(?:\s+{digitWord}){{4}}(?:\s+(?i:dash|hyphen)\s+(?:\d{{4}}|{digitWord}(?:\s+{digitWord}){{3}}))?)";
        return $@"(?<![^\s(\[{{""“'‘]){house}{hs}(?:(?:{string.Join('|', StreetDirections)}){hs})?{street}{sepStreet}{unit}{city}{sepCity}{state}{sepState}{zip}(?![\p{{L}}\d-])(?!\s+(?:\d|(?i:dash|hyphen)\b))";
    }

    private static string ApplyStreetAddresses(string input, List<string> protectedSpans)
    {
        if (!StreetAddressPrefilterRegex().IsMatch(input))
        {
            return input;
        }

        return Replace(
            input,
            StreetAddressPattern,
            match =>
            {
                var houseWords = match.Groups["house"].Value;
                var street = match.Groups["street"].Value;
                var city = match.Groups["city"].Value;
                var state = match.Groups["state"].Value;
                var zipWords = match.Groups["zip"].Value;

                var location = match.Index;
                var windowStart = Math.Max(0, location - 48);
                var before = input[windowStart..location];
                var partOfLonger = $@"(?i)(?:\b(?:{StreetUnitsTensAlt}|hundred|thousand)(?:[^\S\n]+and)?|\d)[^\S\n]*$";
                if (Compiled(partOfLonger, ignoreCase: false).IsMatch(before))
                {
                    return match.Value;
                }

                if (!AddressMayStart(before, atTextStart: windowStart == 0))
                {
                    return match.Value;
                }

                if (Compiled(@"\b[A-Z][a-z]+\s+\d{1,2},\s+$", ignoreCase: false).IsMatch(before) ||
                    Compiled(@"(?i)(?:\b(?:one|two|three|four|five|six|seven|eight|nine|\d+)\s+(?:to|through)|\d+\s*[-\u2013:])\s+$", ignoreCase: false).IsMatch(before) ||
                    Compiled(@"^(?:AM|PM|UTC|GMT|EST|EDT|CST|CDT|MST|MDT|PST|PDT|FT)\s", ignoreCase: false).IsMatch(street))
                {
                    return match.Value;
                }

                var cityTokens = city.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (cityTokens.Length == 0 || cityTokens[0].Length == 1 || cityTokens.Any(StreetTypeSet.Contains))
                {
                    return match.Value;
                }

                if (zipWords.Length > 0 && !char.IsDigit(zipWords[0]))
                {
                    var end = location + match.Length;
                    var afterLen = Math.Min(24, input.Length - end);
                    var after = input.Substring(end, afterLen);
                    if (Compiled(@"(?i)^\s+(?:zero|oh|o|one|two|three|four|five|six|seven|eight|nine)(?![\p{L}])", ignoreCase: false).IsMatch(after))
                    {
                        return match.Value;
                    }
                }

                var house = ParseAddressNumber(houseWords);
                var zip = ParseZipCode(zipWords);
                if (house is null || zip is null)
                {
                    return match.Value;
                }

                if (house.Length == 4 &&
                    int.TryParse(house, NumberStyles.None, CultureInfo.InvariantCulture, out var year) &&
                    year is >= 1900 and <= 2099 &&
                    Compiled(@"(?i)\b(?:from|of|for|on)\s+$", ignoreCase: false).IsMatch(before))
                {
                    return match.Value;
                }

                var rendered = house + " " + DirectionAndStreet(match.Value, houseWords, street) +
                    AddressSeparator(match.Groups["sepStreet"].Value);
                if (match.Groups["unit"].Success)
                {
                    var renderedUnit = RenderAddressUnit(match.Groups["unit"].Value);
                    if (renderedUnit is null)
                    {
                        return match.Value;
                    }

                    rendered += renderedUnit + AddressSeparator(match.Groups["sepUnit"].Value);
                }

                rendered += city + AddressSeparator(match.Groups["sepCity"].Value) + state +
                    ExtractLineBreakOrDefault(match.Groups["sepState"].Value, " ") + zip;

                return ProtectValue(rendered, protectedSpans);
            },
            ignoreCase: false);
    }

    private static string DirectionAndStreet(string whole, string houseWords, string street)
    {
        var houseIdx = whole.IndexOf(houseWords, StringComparison.Ordinal);
        if (houseIdx < 0)
        {
            return street;
        }

        var houseEnd = houseIdx + houseWords.Length;
        var streetStart = whole.IndexOf(street, houseEnd, StringComparison.Ordinal);
        if (streetStart < 0)
        {
            return street;
        }

        var between = whole[houseEnd..streetStart].Trim();
        return string.IsNullOrEmpty(between) ? street : $"{between} {street}";
    }

    private static bool AddressMayStart(string before, bool atTextStart)
    {
        var lead = before.TrimEnd(' ', '\t', '\u00A0');
        if (lead.Length == 0)
        {
            return atTextStart || before.Contains('\n') || before.Contains('\r');
        }

        var last = lead[^1];
        if (last is '\n' or '\r' || ",:;([{\"\u201C'\u2018\u2014\u2013-!?".Contains(last))
        {
            return true;
        }

        var word = lead.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return word is not null && AddressIntroducers.Contains(word);
    }

    private static string AddressSeparator(string sep)
    {
        var brk = ExtractLineBreakOrDefault(sep, string.Empty);
        return string.IsNullOrEmpty(brk) ? ", " : (sep.Contains(',') ? "," + brk : brk);
    }

    private static string ExtractLineBreakOrDefault(string sep, string otherwise)
    {
        var brk = new string(sep.Where(static c => c is '\n' or '\r').ToArray());
        return brk.Length == 0 ? otherwise : brk;
    }

    private static string? ParseAddressNumber(string raw)
    {
        if (raw.Length > 0 && raw.All(char.IsDigit))
        {
            return raw;
        }

        var words = SplitWords(raw.ToLowerInvariant());
        for (var split = 1; split < words.Count; split++)
        {
            var tailCount = words.Count - split;
            if (tailCount > 2)
            {
                continue;
            }

            long low;
            if (tailCount == 1 && Units.TryGetValue(words[split], out var teen) && teen is >= 10 and <= 19)
            {
                low = teen;
            }
            else if (Tens.TryGetValue(words[split], out var ten))
            {
                low = ten;
                if (tailCount == 2)
                {
                    if (!Units.TryGetValue(words[split + 1], out var u) || u is < 1 or > 9)
                    {
                        continue;
                    }

                    low += u;
                }
            }
            else
            {
                continue;
            }

            if (WordsToInteger(words.Take(split)) is long high && high is >= 1 and <= 99)
            {
                return (high * 100 + low).ToString(CultureInfo.InvariantCulture);
            }
        }

        if (DigitWordSequence(words) is { } digits && !digits.StartsWith('0'))
        {
            return digits;
        }

        if (WordsToInteger(words) is > 0 and long val)
        {
            return val.ToString(CultureInfo.InvariantCulture);
        }

        return null;
    }

    private static string? DigitWordSequence(IReadOnlyList<string> words)
    {
        if (words.Count == 0)
        {
            return null;
        }

        var chars = new char[words.Count];
        for (var i = 0; i < words.Count; i++)
        {
            if (!Units.TryGetValue(words[i], out var v) || v < 0 || v >= 10)
            {
                return null;
            }

            chars[i] = (char)('0' + v);
        }

        return new string(chars);
    }

    private static string? ParseZipCode(string raw)
    {
        var parts = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1 && parts[0].Length > 0 && char.IsDigit(parts[0][0]))
        {
            return parts[0];
        }

        var dashAt = Array.FindIndex(parts, static p =>
            p.Equals("dash", StringComparison.OrdinalIgnoreCase) ||
            p.Equals("hyphen", StringComparison.OrdinalIgnoreCase));
        var headEnd = dashAt >= 0 ? dashAt : parts.Length;
        string five;
        if (headEnd == 1 && parts[0].Length == 5 && parts[0].All(char.IsDigit))
        {
            five = parts[0];
        }
        else if (headEnd == 5 && DigitWordSequence(parts.Take(5).Select(static p => p.ToLowerInvariant()).ToArray()) is { } spoken5)
        {
            five = spoken5;
        }
        else
        {
            return null;
        }

        if (dashAt < 0)
        {
            return five;
        }

        var tailCount = parts.Length - (dashAt + 1);
        if (tailCount == 1 && parts[dashAt + 1].Length == 4 && parts[dashAt + 1].All(char.IsDigit))
        {
            return five + "-" + parts[dashAt + 1];
        }

        if (tailCount == 4 &&
            DigitWordSequence(parts.Skip(dashAt + 1).Select(static p => p.ToLowerInvariant()).ToArray()) is { } four)
        {
            return five + "-" + four;
        }

        return null;
    }

    private static string? RenderAddressUnit(string unit)
    {
        if (unit.StartsWith('#'))
        {
            return unit;
        }

        var parts = unit.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        if (parts.Length == 2 && Compiled(@"^\d{1,5}[A-Z]?$", ignoreCase: false).IsMatch(parts[1]))
        {
            return unit;
        }

        if (parts.Length == 3 && parts[1].All(char.IsDigit) && parts[2].Length == 1 && char.IsUpper(parts[2][0]))
        {
            return $"{parts[0]} {parts[1]}{parts[2]}";
        }

        var numberWords = parts.Skip(1).ToList();
        var letter = string.Empty;
        if (numberWords.Count > 1 && numberWords[^1].Length == 1 && char.IsUpper(numberWords[^1][0]))
        {
            letter = numberWords[^1];
            numberWords.RemoveAt(numberWords.Count - 1);
        }

        var num = ParseAddressNumber(string.Join(' ', numberWords));
        return num is null ? null : $"{parts[0]} {num}{letter}";
    }
}
