using System.Diagnostics;
using EnviousWispr.PostProcessing;

namespace EnviousWispr.Architecture.Tests;

/// <summary>English (UK): the American to British converter over the SHIPPED table.</summary>
/// <remarks>
/// PORTED CASE FOR CASE from the macOS suite (EnviousWispr #3124, BritishSpellingConverterTests), which holds
/// the same table. Every expected string is written out by hand; none is derived from the table or the
/// converter, so a wrong mapping or a wrong token rule fails here rather than agreeing with itself. When one of
/// these fails, a British user sees American spelling, or a name or a piece of code respelled.
/// </remarks>
public sealed class BritishSpellingConverterTests
{
    private static readonly BritishSpellingConverter Converter = BritishSpellingConverter.LoadBundled();

    /// <summary>Input, expected output, and the tokens that change between them, counted by hand.</summary>
    public static TheoryData<string, string, int> Corpus => new()
    {
        // Persona-style prose.
        { "the organization needs to prioritize the review", "the organisation needs to prioritise the review", 2 },
        { "can you center the logo and change the color to gray", "can you centre the logo and change the colour to grey", 3 },
        { "I realized the analysis was favorable", "I realised the analysis was favourable", 2 },
        { "we traveled to the theater on Tuesday", "we travelled to the theatre on Tuesday", 2 },
        { "my mom's neighbor", "my mum's neighbour", 2 },
        { "The organization moved its color printer to the center of the room.", "The organisation moved its colour printer to the centre of the room.", 3 },
        { "quick note the defense team apologized for the delay", "quick note the defence team apologised for the delay", 2 },
        { "session notes patient reports behavior changes and elevated anxiety", "session notes patient reports behaviour changes and elevated anxiety", 1 },
        { "the pediatric ward uses aluminum trays", "the paediatric ward uses aluminium trays", 2 },
        { "we canceled the catalog order and labeled the boxes", "we cancelled the catalogue order and labelled the boxes", 3 },
        { "that was a humorous rumor about the harbor", "that was a humorous rumour about the harbour", 2 },
        { "my favorite flavors", "my favourite flavours", 2 },
        { "standardize on the hook version in the next refactor", "standardise on the hook version in the next refactor", 1 },
        { "the jewelry was in the gray drawer", "the jewellery was in the grey drawer", 2 },

        // Sense-dependent words stay as transcribed: the table excludes them.
        { "please check the program and practice the license test", "please check the program and practice the license test", 0 },
        { "the meter on the tire and the story of the draft", "the meter on the tire and the story of the draft", 0 },
        { "among the things I learned and spelled", "among the things I learned and spelled", 0 },

        // Names in mid-sentence stay; the same word at a sentence start converts.
        { "We met at the Kennedy Center on Labor Day.", "We met at the Kennedy Center on Labor Day.", 0 },
        { "Color me impressed. Center stage!", "Colour me impressed. Centre stage!", 2 },
        { "hello there\nColor matters here", "hello there\nColour matters here", 1 },
        { "Is it Gray or gray?", "Is it Gray or grey?", 1 },

        // Quotations and brackets.
        { "\"Color\" she said. (Center)", "\"Colour\" she said. (Centre)", 2 },
        { "she said \u201Ccolor\u201D twice", "she said \u201Ccolour\u201D twice", 1 },
        { "I love the color.", "I love the colour.", 1 },
        { "what color? that color!", "what colour? that colour!", 2 },
        { "it said \"the color.\" then", "it said \"the colour.\" then", 1 },

        // Hyphen compounds are prose.
        { "it's color-coded and well-organized", "it's colour-coded and well-organised", 2 },
        { "the gray-blue sky", "the grey-blue sky", 1 },

        // Possessives and apostrophes.
        { "the organization's plans", "the organisation's plans", 1 },
        { "The neighbor\u2019s favorite colors.", "The neighbour\u2019s favourite colours.", 3 },

        // List items and headings start a sentence.
        { "Plan:\n- Color choices\n* Center the logo\n+ Favorite fonts\n\u2022 Gray tones", "Plan:\n- Colour choices\n* Centre the logo\n+ Favourite fonts\n\u2022 Grey tones", 4 },
        { "1) Color first\n2. Center next", "1) Colour first\n2. Centre next", 2 },
        { "## Color guide", "## Colour guide", 1 },

        // A dash or bracket INSIDE a line is not a list marker: the name stays.
        { "we met - Kennedy Center staff", "we met - Kennedy Center staff", 0 },
        { "see (a) Color Street", "see (a) Color Street", 0 },

        // Case the converter must refuse.
        { "COLOR and CoLoR stay", "COLOR and CoLoR stay", 0 },

        // Code, paths, addresses, numbers.
        { "open color.js and self.color and my_color", "open color.js and self.color and my_color", 0 },
        { "see https://center.io/color for details", "see https://center.io/color for details", 0 },
        { "20colors #color $color a=color <color> user@color", "20colors #color $color a=color <color> user@color", 0 },
        { "set color:red then", "set color:red then", 0 },
        { "the color: here it is", "the colour: here it is", 1 },
        { "caf\u00E9Color stays", "caf\u00E9Color stays", 0 },

        // Nothing to convert: identical output.
        { "running fifteen minutes late got stuck on the client call", "running fifteen minutes late got stuck on the client call", 0 },
        { "", "", 0 },
        { "   ", "   ", 0 },
    };

    [Theory]
    [MemberData(nameof(Corpus))]
    public void TheCorpusConvertsExactly(string input, string expected, int swaps)
    {
        var result = Converter.Convert(input);

        Assert.Equal(expected, result.Text);
        Assert.Equal(swaps, result.Swaps);
    }

    [Fact]
    public void TheCorpusCoversConversionsAndProtectionsInRealNumbers()
    {
        var cases = Corpus.Select(row => (int)row[2]).ToArray();

        Assert.True(cases.Length >= 40);
        Assert.True(cases.Count(swaps => swaps > 0) >= 20);
        Assert.True(cases.Count(swaps => swaps == 0) >= 10);
    }

    [Fact]
    public void AnUnchangedTextComesBackAsTheSameString()
    {
        const string input = "Plain words, nothing to convert.\n\tTabs  and  spaces stay.";

        var result = Converter.Convert(input);

        Assert.Equal(0, result.Swaps);
        Assert.Same(input, result.Text);
    }

    [Fact]
    public void BritishTextIsLeftAsItIs()
    {
        const string input = "The organisation moved its colour printer to the centre.";

        var result = Converter.Convert(input);

        Assert.Equal(0, result.Swaps);
        Assert.Equal(input, result.Text);
    }

    [Fact]
    public void CustomWordsAreNeverRespelled()
    {
        var result = Converter.Convert(
            "the color of Center Parcs and the center",
            protectedWords: new HashSet<string>(["color", "center"]));

        Assert.Equal("the color of Center Parcs and the center", result.Text);
        Assert.Equal(0, result.Swaps);
    }

    /// <summary>A multi-word Custom Word protects each of its words.</summary>
    [Fact]
    public void AMultiWordCustomWordProtectsEachWord()
    {
        var protectedWords = BritishSpellingConverter.ProtectedWords(["Kennedy Center", "Labor\u2019s Color"]);

        Assert.Contains("kennedy center", protectedWords);
        Assert.Contains("center", protectedWords);
        Assert.Contains("labor's", protectedWords);
        Assert.Contains("color", protectedWords);
        Assert.Equal("the center of color", Converter.Convert("the center of color", protectedWords).Text);
    }

    [Fact]
    public void AProtectedSpanSurvivesAndTheWordsAroundItConvert()
    {
        const string sentinel = "EWSNIPcolorab12cd34";

        var result = Converter.Convert($"the color {sentinel} color", protectedSpans: [sentinel]);

        Assert.Equal($"the colour {sentinel} colour", result.Text);
        Assert.Equal(2, result.Swaps);
    }

    [Fact]
    public void AProtectedSpanHoldingAConvertibleWordKeepsIt()
    {
        const string span = "[[color]]";

        var result = Converter.Convert($"the color {span} and {span} color", protectedSpans: [span, "unused"]);

        Assert.Equal($"the colour {span} and {span} colour", result.Text);
        Assert.Equal(2, result.Swaps);
    }

    [Fact]
    public void OverlappingProtectedSpansAreAllHonoured()
    {
        var result = Converter.Convert("x color and color", protectedSpans: ["x ", " color"]);

        Assert.Equal("x color and color", result.Text);
        Assert.Equal(0, result.Swaps);
    }

    [Fact]
    public void TheShippedTableMapsTheCoreWordsAndExcludesSenseDependentOnes()
    {
        (string American, string British)[] mappings =
        [
            ("color", "colour"), ("center", "centre"), ("organization", "organisation"),
            ("analyze", "analyse"), ("traveled", "travelled"), ("gray", "grey"),
            ("defense", "defence"), ("theater", "theatre"),
        ];
        foreach (var (american, british) in mappings)
        {
            Assert.Equal(british, Converter.Convert(american).Text);
        }

        foreach (var word in new[] { "program", "check", "practice", "license", "meter", "tire", "story", "among", "learned", "spelled", "draft", "curb" })
        {
            Assert.Equal(word, Converter.Convert(word).Text);
        }

        Assert.True(Converter.EntryCount > 5_000);
    }

    /// <summary>The same budget the step gives this input: 50 ms plus 10 ms per 1,000 words.</summary>
    [Fact]
    public void TenThousandWordsConvertInsideTheStepsBudget()
    {
        var text = string.Concat(Enumerable.Repeat("The organization analyzed ten color samples at the center today. ", 1_000));
        Converter.Convert("warm up the color");

        var timer = Stopwatch.StartNew();
        var result = Converter.Convert(text);
        timer.Stop();

        Assert.Equal(4_000, result.Swaps);
        Assert.True(timer.ElapsedMilliseconds < 150, $"10,000 words took {timer.ElapsedMilliseconds} ms");
    }
}

/// <summary>What the Codex review of the port found where UTF-16 and Swift's Characters part company.</summary>
public sealed class BritishSpellingConverterUnicodeTests
{
    private static readonly BritishSpellingConverter Converter = BritishSpellingConverter.LoadBundled();

    public static TheoryData<string> JoinedToSomethingOutsideAscii => new()
    {
        string.Concat("cafe", ((char)0x0301).ToString(), "color"),   // a combining accent before the word
        string.Concat("color", ((char)0x0301).ToString()),           // and after it
        char.ConvertFromUtf32(0x10400) + "color",                     // a letter outside the basic plane
        char.ConvertFromUtf32(0x1D7CE) + "color",                     // a mathematical digit
    };

    [Theory]
    [MemberData(nameof(JoinedToSomethingOutsideAscii))]
    public void AWordJoinedToANonAsciiNeighbourIsLeftAlone(string text)
    {
        var result = Converter.Convert(text);

        Assert.Equal(text, result.Text);
        Assert.Equal(0, result.Swaps);
    }

    /// <summary>A custom word carrying a combining accent is one word, not a word and a stray "color".</summary>
    [Fact]
    public void ACustomWordWithACombiningAccentIsOneWord()
    {
        var words = BritishSpellingConverter.ProtectedWords([string.Concat("cafe", ((char)0x0301).ToString(), "color")]);

        Assert.DoesNotContain("color", words);
        Assert.Equal("the colour", Converter.Convert("the color", words).Text);
    }

}
