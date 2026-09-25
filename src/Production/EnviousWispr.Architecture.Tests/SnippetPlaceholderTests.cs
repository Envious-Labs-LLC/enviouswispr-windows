using System.Globalization;
using EnviousWispr.Core.Settings;
using EnviousWispr.PostProcessing;

namespace EnviousWispr.Architecture.Tests;

/// <summary>The fill-in grammar - {{date}}, {{time}}, {{clipboard}} - ported from macOS <c>SnippetPlaceholderTests</c>.</summary>
/// <remarks>
/// WHEN THIS FAILS SOMEBODY SEES "{{date}}" PASTED INTO THEIR EMAIL, or yesterday's date, or text they
/// never copied. The rendered literals were MEASURED on this machine's .NET 10 runtime before the
/// resolver was written - a scratch program printed each culture's patterns and a real instant's
/// output - and are written here as text. The United States time separator on this runtime is a
/// plain space (0x20), where macOS's ICU writes a narrow no-break space; read off the code points.
/// </remarks>
public sealed class SnippetPlaceholderTests
{
    /// <summary>2026-09-16T18:45:00Z, written as its epoch so no formatter builds the input.</summary>
    private static readonly DateTimeOffset Instant = DateTimeOffset.FromUnixTimeSeconds(1_789_584_300);

    /// <summary>2026-09-17T00:00:00Z: midnight in UTC, still the 16th in New York.</summary>
    private static readonly DateTimeOffset MidnightUtc = DateTimeOffset.FromUnixTimeSeconds(1_789_603_200);

    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private static SnippetDynamicValues Values(
        DateTimeOffset? at = null,
        string culture = "en-US",
        TimeZoneInfo? zone = null,
        string? clipboard = null) =>
        new(at ?? Instant, CultureInfo.GetCultureInfo(culture), zone ?? NewYork, clipboard);

    [Theory]
    [InlineData("en-US", "Sep 16, 2026", "2:45 PM")]
    [InlineData("en-GB", "16 Sept 2026", "14:45")]
    [InlineData("de-DE", "16. Sept. 2026", "14:45")]
    public void TheDateAndTheTimeReadAsEachCulturesOwnPersonExpects(string culture, string date, string time)
    {
        Assert.Equal(date, SnippetPlaceholders.Resolve("{{date}}", Values(culture: culture)));
        Assert.Equal(time, SnippetPlaceholders.Resolve("{{time}}", Values(culture: culture)));
    }

    /// <summary>One instant, two zones, two different DATES: a resolver that ignored the zone fails here.</summary>
    [Fact]
    public void TheSameInstantIsADifferentDayInTwoZones()
    {
        Assert.Equal("Sep 16, 2026", SnippetPlaceholders.Resolve("{{date}}", Values(at: MidnightUtc)));
        Assert.Equal("Sep 17, 2026", SnippetPlaceholders.Resolve("{{date}}", Values(at: MidnightUtc, zone: TimeZoneInfo.Utc)));
    }

    [Fact]
    public void AClipboardThatWasNotReadAndAnEmptyOneBothResolveToNothing()
    {
        Assert.Equal("[]", SnippetPlaceholders.Resolve("[{{clipboard}}]", Values()));
        Assert.Equal("[]", SnippetPlaceholders.Resolve("[{{clipboard}}]", Values(clipboard: string.Empty)));
        Assert.Equal("[https://x.dev]", SnippetPlaceholders.Resolve("[{{clipboard}}]", Values(clipboard: "https://x.dev")));
    }

    /// <summary>The one a second pass would get wrong: whatever was copied is CONTENT, never grammar.</summary>
    [Fact]
    public void AFillInInsideTheClipboardTextIsPastedNotResolved()
    {
        Assert.Equal(
            "{{date}} and {{clipboard}}",
            SnippetPlaceholders.Resolve("{{clipboard}}", Values(clipboard: "{{date}} and {{clipboard}}")));
    }

    [Theory]
    [InlineData("{{date}}")]
    [InlineData("{{DATE}}")]
    [InlineData("{{Date}}")]
    [InlineData("{{dAtE}}")]
    [InlineData("{{ date }}")]
    [InlineData("{{\tdate\t}}")]
    [InlineData("{{\ndate\n}}")]
    public void ASupportedNameMatchesWhateverCaseAndPaddingItIsWrittenIn(string spelling)
    {
        Assert.Equal("Sep 16, 2026", SnippetPlaceholders.Resolve(spelling, Values()));
        Assert.Equal(new HashSet<SnippetPlaceholder> { SnippetPlaceholder.Date }, SnippetPlaceholders.Used(spelling).ToHashSet());
        Assert.False(SnippetPlaceholders.CarriesUnsupportedPlaceholder(spelling));
    }

    [Fact]
    public void UsedReportsTheFillInsUsedAndOnlyThose()
    {
        Assert.Equal(new HashSet<SnippetPlaceholder> { SnippetPlaceholder.Clipboard }, SnippetPlaceholders.Used("{{CLIPBOARD}}").ToHashSet());
        Assert.Empty(SnippetPlaceholders.Used("{{clip}}"));
        Assert.Empty(SnippetPlaceholders.Used("no braces here"));
        Assert.Equal(
            new HashSet<SnippetPlaceholder> { SnippetPlaceholder.Date, SnippetPlaceholder.Time },
            SnippetPlaceholders.Used("{{date}} {{time}} {{date}}").ToHashSet());
    }

    [Fact]
    public void EveryFillInsCanonicalTokenRoundTripsThroughTheScanner()
    {
        Assert.Equal(["{{date}}", "{{time}}", "{{clipboard}}"], SnippetPlaceholders.All.Select(SnippetPlaceholders.Token));
        foreach (var placeholder in SnippetPlaceholders.All)
        {
            Assert.Equal(new HashSet<SnippetPlaceholder> { placeholder }, SnippetPlaceholders.Used(SnippetPlaceholders.Token(placeholder)).ToHashSet());
        }

        Assert.Equal(Enum.GetValues<SnippetPlaceholder>().Length, SnippetPlaceholders.All.Count);
    }

    /// <summary>One row per class of input: what the person gets, and whether the span is one this app cannot fill.</summary>
    [Theory]
    [InlineData("{{date}}", "Sep 16, 2026", false)]
    [InlineData("{{TIME}}", "2:45 PM", false)]
    [InlineData("{{ clipboard }}", "copied", false)]
    [InlineData("{{cursor}}", "{{cursor}}", true)]
    [InlineData("{{}}", "{{}}", true)]
    [InlineData("{{date time}}", "{{date time}}", true)]
    [InlineData("plain text", "plain text", false)]
    [InlineData("a {{ b", "a {{ b", false)]
    [InlineData("}} then {{", "}} then {{", false)]
    [InlineData("{{date}} {{time}}", "Sep 16, 2026 2:45 PM", false)]
    [InlineData("{{date}} {{cursor}}", "Sep 16, 2026 {{cursor}}", true)]
    [InlineData("{{{{date}}}}", "{{{{date}}}}", true)]
    [InlineData("Hi {{date}}, from {{clipboard}}.", "Hi Sep 16, 2026, from copied.", false)]
    public void TheSpanTableDecidesEveryClassOfInput(string input, string resolved, bool unsupported)
    {
        Assert.Equal(resolved, SnippetPlaceholders.Resolve(input, Values(clipboard: "copied")));
        Assert.Equal(unsupported, SnippetPlaceholders.CarriesUnsupportedPlaceholder(input));
    }

    [Fact]
    public void DoubledBracesAreOneUnsupportedSpanFollowedByALiteralCloser()
    {
        Assert.Empty(SnippetPlaceholders.Used("{{{{date}}}}"));
        Assert.True(SnippetPlaceholders.CarriesUnsupportedPlaceholder("{{{{date}}}}"));
    }

    [Fact]
    public void TextWithNoFillInComesBackByteForByte()
    {
        Assert.Equal(
            "Dear {name},\n\tThe path is C:\\Users\\{{  }}\\x, see attached.\n\nRegards",
            SnippetPlaceholders.Resolve("Dear {name},\n\tThe path is C:\\Users\\{{  }}\\x, see attached.\n\nRegards", Values(clipboard: "x")));
        Assert.Equal("Kind regards,\nSam\n", SnippetPlaceholders.Resolve("Kind regards,\nSam\n", Values(clipboard: "x")));
    }

    /// <summary>
    /// The collision domain never MISSES what the resolved strings hold: every corpus needle found in a
    /// resolved expansion is reported. The oracle is a plain ordinal search of each resolved string.
    /// </summary>
    [Fact]
    public void TheSegmentDomainNeverMissesWhatTheResolvedStringsHold()
    {
        string[] saved =
        [
            "sam@example.com", "see {{clipboard}}", "{{clipboard}}", "{{date}}{{time}}", "EWS{{clipboard}}",
            "{{clipboard}}NIP", "E{{clipboard}}S{{clipboard}}NIP", "a{{clipboard}}b{{clipboard}}c",
            "{{cursor}} {{clipboard}}", "EWS", "NIP",
        ];
        string?[] clipboards =
        [
            null, string.Empty, "W", "NIP", "NIPX", "EWSNIPINSIDE", "EWSNIPFALLBACK0", "abc",
            new string('z', 300) + "EWSNIPINSIDE",
        ];
        string[] needles =
        [
            "EWSNIP", "EWSNIPINSIDE", "EWSNIPFALLBACK0", "EWSNIPFALLBACK1", "EWSWNIP", "EWSNIPX", "abc",
            "Sep 16, 2026", "2:45 PM", "{{cursor}}",
        ];
        var checkedHits = 0;
        foreach (var clipboard in clipboards)
        {
            var values = Values(clipboard: clipboard);
            var domain = new SnippetResolvedExpansions(saved, values);
            var resolved = saved.Select(text => SnippetPlaceholders.Resolve(text, values)).ToArray();
            foreach (var needle in needles)
            {
                if (resolved.Any(text => text.Contains(needle, StringComparison.Ordinal)))
                {
                    checkedHits++;
                    Assert.True(domain.Contains(needle), $"missed '{needle}' with clipboard '{clipboard}'");
                }
            }
        }

        // Control: the corpus must actually put needles in the resolved text, splices included.
        Assert.True(checkedHits > 20, $"only {checkedHits} hits; the corpus is not exercising the domain");
        Assert.True(new SnippetResolvedExpansions(["EWS{{clipboard}}"], Values(clipboard: "NIPINSIDE")).Contains("EWSNIPINSIDE"));
    }

    [Fact]
    public void ANeedleIsNeverAssembledAcrossTwoSeparateExpansions()
    {
        Assert.False(new SnippetResolvedExpansions(["EWS", "NIP"], Values()).Contains("EWSNIP"));
        Assert.True(new SnippetResolvedExpansions(["EWSNIP"], Values()).Contains("EWSNIP"));
    }
}
