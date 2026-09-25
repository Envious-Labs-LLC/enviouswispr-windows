using System.Globalization;
using EnviousWispr.Core.Settings;
using EnviousWispr.PostProcessing;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// The snippet matcher, ported case for case from macOS <c>SnippetExpanderTests</c>.
/// </summary>
/// <remarks>
/// WHEN THIS FAILS SOMEBODY GETS THE WRONG TEXT PASTED INTO THEIR DOCUMENT. The cases that carry the
/// most weight are the NEGATIVE ones - a trigger spoken without the keyword, and the keyword spoken
/// with nothing after it - because a matcher that fires too eagerly looks like a working feature right
/// up until it rewrites a sentence somebody meant. Every expectation is a literal string.
/// </remarks>
public sealed class SnippetExpanderTests
{
    /// <summary>The values for cases whose snippets hold no fill-in: stated, so no case is quietly indifferent to them.</summary>
    private static readonly SnippetDynamicValues NoFillIns = new(
        DateTimeOffset.FromUnixTimeSeconds(0),
        CultureInfo.GetCultureInfo("en-US"),
        TimeZoneInfo.Utc,
        Clipboard: null);

    private static SnippetVocabulary Vocabulary(params (string Trigger, string Body)[] pairs) =>
        new(pairs.Select(pair => new SnippetEntry(pair.Trigger, pair.Body)).ToArray(), SnippetVocabulary.DefaultKeyword);

    /// <summary>A predictable sentinel source so a case can name the token; it repeats its last value once spent.</summary>
    private static SnippetExpander Fixed(params string[] values)
    {
        var index = 0;
        return new SnippetExpander(() => values[Math.Min(index++, values.Length - 1)]);
    }

    // The keyword rule.

    [Fact]
    public void ATriggerAfterTheKeywordExpandsInPlaceMidSentence()
    {
        var outcome = Fixed("EWSNIPAAAA").Expand(
            "feel free to email me at backslash my email address any time",
            Vocabulary(("my email address", "sam@example.com")),
            NoFillIns);

        Assert.Equal("feel free to email me at EWSNIPAAAA any time", outcome.Text);
        Assert.Equal([new SnippetExpansionRecord("EWSNIPAAAA", "sam@example.com")], outcome.Records);
    }

    [Fact]
    public void TheSameWordsWithoutTheKeywordAreLeftCompletelyAlone()
    {
        const string input = "can you send me my email address from that form";
        var outcome = new SnippetExpander().Expand(input, Vocabulary(("my email address", "sam@example.com")), NoFillIns);

        Assert.Equal(input, outcome.Text);
        Assert.Empty(outcome.Records);
        Assert.False(outcome.DidFire);
    }

    [Fact]
    public void TheKeywordWithNothingMatchingAfterItIsLeftInPlace()
    {
        const string input = "the path is backslash users backslash shared";
        var outcome = new SnippetExpander().Expand(input, Vocabulary(("my email address", "sam@example.com")), NoFillIns);

        Assert.Equal(input, outcome.Text);
        Assert.Empty(outcome.Records);
    }

    [Fact]
    public void AChosenKeywordReplacesBackslash()
    {
        var vocabulary = new SnippetVocabulary([new SnippetEntry("my email", "sam@example.com")], "insert");

        Assert.Equal(
            "EWSNIPK",
            Fixed("EWSNIPK").Expand("insert my email", vocabulary, NoFillIns).Text);
        Assert.Equal(
            "backslash my email",
            Fixed("EWSNIPK").Expand("backslash my email", vocabulary, NoFillIns).Text);
    }

    // Matching semantics.

    [Fact]
    public void TheLongestTriggerWinsWhenTwoMatchAtTheSamePosition()
    {
        var outcome = Fixed("EWSNIPLONG").Expand(
            "backslash my email address please",
            Vocabulary(("my email", "SHORT"), ("my email address", "LONG")),
            NoFillIns);

        Assert.Equal("EWSNIPLONG please", outcome.Text);
        Assert.Equal("LONG", outcome.Records[0].Expansion);
    }

    [Fact]
    public void PunctuationClingingToTheLastTriggerWordSurvivesTheSubstitution()
    {
        var outcome = Fixed("EWSNIPDOT").Expand(
            "email me at backslash my email address.",
            Vocabulary(("my email address", "sam@example.com")),
            NoFillIns);

        Assert.Equal("email me at EWSNIPDOT.", outcome.Text);
    }

    [Fact]
    public void MatchingIsCaseInsensitiveBecauseSpeechIsTranscribedWithSentenceCasing()
    {
        var outcome = Fixed("EWSNIPCASE").Expand(
            "Backslash My Email Address",
            Vocabulary(("my email address", "sam@example.com")),
            NoFillIns);

        Assert.Equal("EWSNIPCASE", outcome.Text);
    }

    [Fact]
    public void TwoSnippetsInOneUtteranceEachFireWithDistinctSentinels()
    {
        var outcome = Fixed("EWSNIPONE", "EWSNIPTWO").Expand(
            "backslash my email or backslash support address",
            Vocabulary(("my email", "sam@example.com"), ("support address", "help@example.com")),
            NoFillIns);

        Assert.Equal("EWSNIPONE or EWSNIPTWO", outcome.Text);
        Assert.Equal(2, outcome.Records.Count);
        Assert.Equal(2, outcome.Records.Select(record => record.Sentinel).Distinct().Count());
    }

    [Fact]
    public void OriginalSpacingAndLineBreaksArePreservedAroundAnExpansion()
    {
        var outcome = Fixed("EWSNIPGAP").Expand(
            "first line\n\nbackslash my email  trailing",
            Vocabulary(("my email", "sam@example.com")),
            NoFillIns);

        Assert.Equal("first line\n\nEWSNIPGAP  trailing", outcome.Text);
    }

    [Fact]
    public void ATriggerAtTheVeryEndOfTheUtteranceStillFires()
    {
        var outcome = Fixed("EWSNIPEND").Expand(
            "email me at backslash my email address",
            Vocabulary(("my email address", "sam@example.com")),
            NoFillIns);

        Assert.Equal("email me at EWSNIPEND", outcome.Text);
        Assert.Single(outcome.Records);
    }

    /// <summary>Leading whitespace belongs to no word; macOS dropped it on every armed dictation until review caught it.</summary>
    [Fact]
    public void WhitespaceBeforeTheFirstWordSurvivesWithAndWithoutAMatch()
    {
        var vocabulary = Vocabulary(("my email", "sam@example.com"));

        Assert.Equal("\n  hello there", Fixed("EWSNIPLEAD").Expand("\n  hello there", vocabulary, NoFillIns).Text);
        Assert.Equal("  EWSNIPLEAD", Fixed("EWSNIPLEAD").Expand("  backslash my email", vocabulary, NoFillIns).Text);
    }

    [Fact]
    public void AQuotedTriggerKeepsBothQuotesAroundThePastedText()
    {
        var outcome = Fixed("EWSNIPQ").Expand(
            "he said “backslash my email” and left",
            Vocabulary(("my email", "sam@example.com")),
            NoFillIns);

        Assert.Equal("he said “EWSNIPQ” and left", outcome.Text);
    }

    /// <summary>The other arrangement: a fix covering one position looks identical to a fix covering both.</summary>
    [Fact]
    public void AnOpeningMarkOnTheFirstTriggerWordIsKeptToo()
    {
        var outcome = Fixed("EWSNIPT").Expand(
            "he said backslash “my email” and left",
            Vocabulary(("my email", "sam@example.com")),
            NoFillIns);

        Assert.Equal("he said “EWSNIPT” and left", outcome.Text);
    }

    [Fact]
    public void ATriggerInBracketsKeepsBothBrackets()
    {
        var outcome = Fixed("EWSNIPB").Expand("(backslash my email)", Vocabulary(("my email", "sam@example.com")), NoFillIns);

        Assert.Equal("(EWSNIPB)", outcome.Text);
    }

    /// <summary>A full stop INSIDE the phrase means two sentences, not one trigger; normalisation alone would hide it.</summary>
    [Fact]
    public void ATriggerDoesNotMatchAcrossASentenceBoundary()
    {
        const string input = "send me backslash my. Email address is below";
        var outcome = new SnippetExpander().Expand(input, Vocabulary(("my email address", "sam@example.com")), NoFillIns);

        Assert.Equal(input, outcome.Text);
        Assert.Empty(outcome.Records);
    }

    [Fact]
    public void ASentenceBoundaryOnTheKeywordBlocksTheMatch()
    {
        const string input = "send me backslash. My email address is below";
        var outcome = new SnippetExpander().Expand(input, Vocabulary(("my email address", "sam@example.com")), NoFillIns);

        Assert.Equal(input, outcome.Text);
        Assert.Empty(outcome.Records);
    }

    [Fact]
    public void AFullStopOnTheLastTriggerWordStillMatchesAndSurvives()
    {
        var outcome = Fixed("EWSNIPEND2").Expand(
            "write to backslash my email address. Thanks.",
            Vocabulary(("my email address", "sam@example.com")),
            NoFillIns);

        Assert.Equal("write to EWSNIPEND2. Thanks.", outcome.Text);
    }

    /// <summary>The phrase is REPLACED, so an interior comma has no destination; frozen so nobody "fixes" it back.</summary>
    [Fact]
    public void ACommaInsideThePhraseIsConsumedWithThePhrase()
    {
        var outcome = Fixed("EWSNIPCOMMA").Expand(
            "email me at backslash my, email today",
            Vocabulary(("my email", "sam@example.com")),
            NoFillIns);

        Assert.Equal("email me at EWSNIPCOMMA today", outcome.Text);
    }

    // The snippet's own ending wins (macOS #2637).

    [Fact]
    public void ASnippetSpokenOnItsOwnDoesNotGetAFullStopWeldedOn()
    {
        var outcome = Fixed("EWSNIPALONE").Expand(
            "backslash my email address.",
            Vocabulary(("my email address", "sam@example.com")),
            NoFillIns);

        Assert.Equal("EWSNIPALONE", outcome.Text);
        Assert.Equal([new SnippetExpansionRecord("EWSNIPALONE", "sam@example.com", SuppressFollowingSentenceEnding: true)], outcome.Records);
    }

    /// <summary>The two-way control: with no stop the text is unchanged, and the decision is still recorded.</summary>
    [Fact]
    public void ASnippetSpokenOnItsOwnWithNoStopIsUnchangedAndStillOwnsItsEnding()
    {
        var outcome = Fixed("EWSNIPALONE2").Expand(
            "backslash my email address",
            Vocabulary(("my email address", "sam@example.com")),
            NoFillIns);

        Assert.Equal("EWSNIPALONE2", outcome.Text);
        Assert.True(outcome.Records[0].SuppressFollowingSentenceEnding);
    }

    [Fact]
    public void ACommaOnASnippetSpokenOnItsOwnIsStillReattached()
    {
        var outcome = Fixed("EWSNIPCOMMA2").Expand(
            "backslash my email address,",
            Vocabulary(("my email address", "sam@example.com")),
            NoFillIns);

        Assert.Equal("EWSNIPCOMMA2,", outcome.Text);
    }

    [Fact]
    public void ASnippetInsideASentenceKeepsTheSentencesFullStop()
    {
        var outcome = Fixed("EWSNIPMID").Expand(
            "please contact me at backslash my email address.",
            Vocabulary(("my email address", "sam@example.com")),
            NoFillIns);

        Assert.Equal("please contact me at EWSNIPMID.", outcome.Text);
        Assert.False(outcome.Records[0].SuppressFollowingSentenceEnding);
    }

    [Fact]
    public void ASavedTextThatAlreadyEndsASentenceDoesNotGetASecondStop()
    {
        var outcome = Fixed("EWSNIPSIG").Expand(
            "tell them backslash my sign off. Then send it.",
            Vocabulary(("my sign off", "Let me know if that works.")),
            NoFillIns);

        Assert.Equal("tell them EWSNIPSIG Then send it.", outcome.Text);
        Assert.Equal("Let me know if that works.", outcome.Records[0].Expansion);
    }

    [Fact]
    public void ATrailingNewlineDoesNotHideTheSavedTextsOwnFullStop()
    {
        var outcome = Fixed("EWSNIPNL").Expand(
            "tell them backslash my sign off. Then send it.",
            Vocabulary(("my sign off", "Speak soon.\n")),
            NoFillIns);

        Assert.Equal("tell them EWSNIPNL Then send it.", outcome.Text);
    }

    [Fact]
    public void ASavedTextEndingMidPhraseStillReceivesTheSentencesStop()
    {
        var outcome = Fixed("EWSNIPMID2").Expand(
            "tell them backslash my sign off. Then send it.",
            Vocabulary(("my sign off", "Best,\nSaurabh")),
            NoFillIns);

        Assert.Equal("tell them EWSNIPMID2. Then send it.", outcome.Text);
    }

    [Fact]
    public void WithTwoSnippetsInOneUtteranceNeitherCountsAsTheWholeDictation()
    {
        var outcome = Fixed("EWSNIPA", "EWSNIPB").Expand(
            "backslash my email. backslash my cell.",
            Vocabulary(("my email", "sam@example.com"), ("my cell", "555-0100")),
            NoFillIns);

        Assert.Equal("EWSNIPA. EWSNIPB.", outcome.Text);
        Assert.Equal(2, outcome.Records.Count);
    }

    [Fact]
    public void AFullStopFollowedByAClosingQuoteIsSuppressedAndTheQuoteIsKept()
    {
        var outcome = Fixed("EWSNIPMIX").Expand(
            "he said “backslash my sign off.” Then he left.",
            Vocabulary(("my sign off", "Let me know if that works.")),
            NoFillIns);

        Assert.Equal("he said “EWSNIPMIX” Then he left.", outcome.Text);
        Assert.True(outcome.Records[0].SuppressFollowingSentenceEnding);
    }

    // A sentence boundary hiding behind a closing mark (macOS #2605).

    [Fact]
    public void ASentenceBoundaryHiddenBehindAClosingQuoteBlocksTheMatch()
    {
        const string input = "he said backslash my.” Email address is below";
        var outcome = Fixed("EWSNIPQUOTE").Expand(input, Vocabulary(("my email address", "sam@example.com")), NoFillIns);

        Assert.Equal(input, outcome.Text);
        Assert.Empty(outcome.Records);
    }

    [Fact]
    public void ASentenceBoundaryHiddenBehindAClosingBracketBlocksTheMatch()
    {
        var outcome = Fixed("EWSNIPPAREN").Expand(
            "he said (backslash my.) Email address is below",
            Vocabulary(("my email address", "sam@example.com")),
            NoFillIns);

        Assert.Empty(outcome.Records);
    }

    // The punctuation sets and the comparison form.

    /// <summary>Pinned against a LITERAL: an expectation built from the same union would pass any definition.</summary>
    [Fact]
    public void TheTrailingSetIsExactlyTheThirteenMarksNormalisationStrips()
    {
        Assert.Equal(
            new HashSet<char> { '.', ',', '!', '?', ';', ':', ')', ']', '}', '"', '\'', '”', '’' },
            SnippetText.Trailing.ToHashSet());
        Assert.True(SnippetText.SentenceEnding.IsSubsetOf(SnippetText.Trailing));
        Assert.True(SnippetText.Closing.IsSubsetOf(SnippetText.Trailing));
        Assert.False(SnippetText.SentenceEnding.Overlaps(SnippetText.Closing));
    }

    [Fact]
    public void EndsSentenceReadsThroughTrailingClosingMarksAndWhitespace()
    {
        Assert.True(SnippetText.EndsSentence("done."));
        Assert.True(SnippetText.EndsSentence("my.”"));
        Assert.True(SnippetText.EndsSentence("email.)"));
        Assert.True(SnippetText.EndsSentence("done.\"'"));
        Assert.True(SnippetText.EndsSentence("Speak soon.\n"));
        Assert.True(SnippetText.EndsSentence("What?  "));

        Assert.False(SnippetText.EndsSentence("Saurabh"));
        Assert.False(SnippetText.EndsSentence("email,"));
        Assert.False(SnippetText.EndsSentence("”"));
        Assert.False(SnippetText.EndsSentence(string.Empty));
        Assert.False(SnippetText.EndsSentence("   "));
    }

    [Theory]
    [InlineData("My", "my")]
    [InlineData("Email.", "email")]
    [InlineData("“Backslash", "backslash")]
    [InlineData(" \"hello\" ", "hello")]
    [InlineData("(email),", "email")]
    [InlineData("...", "")]
    [InlineData("   ", "")]
    public void NormalisationLowersAndStripsOnlyTheOuterPunctuation(string token, string expected) =>
        Assert.Equal(expected, SnippetText.Normalize(token));

    // The disabled path, which is what an ordinary person takes.

    [Fact]
    public void AnEmptyStoreReturnsTheInputUnchanged()
    {
        const string input = "backslash my email address";
        var outcome = new SnippetExpander().Expand(input, SnippetVocabulary.Empty, NoFillIns);

        Assert.Equal(input, outcome.Text);
        Assert.Empty(outcome.Records);
    }

    [Fact]
    public void ABlankKeywordCannotFireEvenWithSnippetsSaved()
    {
        const string input = "backslash my email address";
        var vocabulary = new SnippetVocabulary([new SnippetEntry("my email address", "sam@example.com")], "   ");

        Assert.False(vocabulary.CanFire);
        Assert.Equal(input, new SnippetExpander().Expand(input, vocabulary, NoFillIns).Text);
    }

    // Sentinel uniqueness, all three domains.

    [Fact]
    public void ASentinelAlreadyPresentInTheDictatedTextIsRejected()
    {
        var outcome = Fixed("EWSNIPDUPE", "EWSNIPFRESH").Expand(
            "the code is EWSNIPDUPE and backslash my email",
            Vocabulary(("my email", "sam@example.com")),
            NoFillIns);

        Assert.Equal("EWSNIPFRESH", outcome.Records[0].Sentinel);
        Assert.Equal("the code is EWSNIPDUPE and EWSNIPFRESH", outcome.Text);
    }

    /// <summary>The domain easiest to miss: restoration puts expansions back, so a sentinel inside one would reappear.</summary>
    [Fact]
    public void ASentinelThatAppearsInsideASavedTextIsRejected()
    {
        var outcome = Fixed("EWSNIPINSIDE", "EWSNIPCLEAN").Expand(
            "backslash my email",
            Vocabulary(("my email", "sam@example.com"), ("my note", "reference EWSNIPINSIDE for details")),
            NoFillIns);

        Assert.Equal("EWSNIPCLEAN", outcome.Records[0].Sentinel);
    }

    [Fact]
    public void ADegenerateSourceThatAlwaysReturnsOneValueStillYieldsDistinctSentinels()
    {
        var outcome = Fixed("EWSNIPSAME").Expand(
            "backslash my email or backslash support address",
            Vocabulary(("my email", "sam@example.com"), ("support address", "help@example.com")),
            NoFillIns);

        Assert.Equal(2, outcome.Records.Count);
        Assert.NotEqual(outcome.Records[0].Sentinel, outcome.Records[1].Sentinel);
    }

    /// <summary>The fallback mint checks the same three domains; nothing else drives it with a colliding value in the text.</summary>
    [Fact]
    public void AFallbackSentinelAlreadyInTheDictatedTextIsSkippedAndIssuedOnesToo()
    {
        var outcome = Fixed("EWSNIPSAME").Expand(
            "EWSNIPSAME EWSNIPFALLBACK0 backslash my email or backslash support address",
            Vocabulary(("my email", "sam@example.com"), ("support address", "help@example.com")),
            NoFillIns);

        Assert.Equal(["EWSNIPFALLBACK1", "EWSNIPFALLBACK2"], outcome.Records.Select(record => record.Sentinel));
    }

    [Fact]
    public void TheDomainsCanOnlyCollideWhenTheyContainTheSentinelPrefix()
    {
        static SnippetResolvedExpansions Domain(string[] saved, string? clipboard = null) =>
            new(saved, NoFillIns with { Clipboard = clipboard });

        Assert.False(SnippetExpander.DomainCanCollide("backslash my email", Domain(["sam@example.com"])));
        Assert.True(SnippetExpander.DomainCanCollide("code EWSNIPX here", Domain(["sam@example.com"])));
        Assert.True(SnippetExpander.DomainCanCollide("backslash my email", Domain(["see EWSNIP"])));
        Assert.False(SnippetExpander.DomainCanCollide(string.Empty, Domain([])));
        // The prefix arriving through a SUBSTITUTED value, the domain the fill-ins added.
        Assert.True(SnippetExpander.DomainCanCollide("backslash my link", Domain(["see {{clipboard}}"], "EWSNIPX")));
        // And assembled ACROSS a splice: neither piece holds the prefix, the resolved text does.
        Assert.True(SnippetExpander.DomainCanCollide("backslash my link", Domain(["EWS{{clipboard}}"], "NIPX")));
    }

    [Fact]
    public void ACandidateWithoutThePrefixIsStillCheckedAgainstTheDictatedText()
    {
        var outcome = Fixed("plain", "EWSNIPOK").Expand(
            "the code is plain and backslash my email",
            Vocabulary(("my email", "sam@example.com")),
            NoFillIns);

        Assert.Equal("EWSNIPOK", outcome.Records[0].Sentinel);
    }

    [Fact]
    public void ACandidateWithoutThePrefixIsStillCheckedAgainstSavedTexts()
    {
        var outcome = Fixed("plain", "EWSNIPOK").Expand(
            "backslash my email",
            Vocabulary(("my email", "sam@example.com"), ("my note", "in plain words")),
            NoFillIns);

        Assert.Equal("EWSNIPOK", outcome.Records[0].Sentinel);
    }

    [Fact]
    public void ARandomSentinelIsThePrefixAndThirtyTwoUpperCaseHexDigits()
    {
        var sentinel = SnippetExpander.RandomCandidate();

        Assert.Matches("^EWSNIP[0-9A-F]{32}$", sentinel);
        Assert.NotEqual(sentinel, SnippetExpander.RandomCandidate());
    }

    // The duplicate rule, stated once.

    [Fact]
    public void TwoTriggersDifferingOnlyByCaseAndPunctuationAreTheSameTrigger()
    {
        Assert.Equal("my email address", SnippetText.CollisionKey("my email address"));
        Assert.Equal("my email address", SnippetText.CollisionKey("My Email Address."));
        Assert.Equal("my email", SnippetText.CollisionKey("my email"));
    }

    [Fact]
    public void AnAllPunctuationTriggerHasNoKeyAndCollidesWithNothing()
    {
        Assert.Empty(SnippetText.TriggerTokens("..."));
        Assert.Null(SnippetText.CollisionKey("..."));
    }

    // Fill-ins.

    /// <summary>2026-09-16T18:45:00Z in New York, in the United States' own format.</summary>
    private static SnippetDynamicValues FillIns(string? clipboard = null) => new(
        DateTimeOffset.FromUnixTimeSeconds(1_789_584_300),
        CultureInfo.GetCultureInfo("en-US"),
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York"),
        clipboard);

    [Fact]
    public void AFiredSnippetsRecordCarriesTheResolvedTextNeverTheToken()
    {
        var outcome = Fixed("EWSNIPAAAA").Expand(
            "please send it backslash my stamp today",
            Vocabulary(("my stamp", "Filed {{date}} at {{time}} from {{clipboard}}")),
            FillIns("https://x.dev"));

        Assert.Equal("please send it EWSNIPAAAA today", outcome.Text);
        Assert.Equal("Filed Sep 16, 2026 at 2:45 PM from https://x.dev", outcome.Records[0].Expansion);
    }

    [Fact]
    public void UsedPlaceholdersIsTheUnionOverTheSnippetsThatFiredAndOnlyThose()
    {
        var vocabulary = Vocabulary(("my stamp", "on {{date}}"), ("my link", "see {{clipboard}}"), ("my sign off", "thanks"));

        Assert.Equal(
            new HashSet<SnippetPlaceholder> { SnippetPlaceholder.Date },
            Fixed("EWSNIPA", "EWSNIPB").Expand("backslash my stamp", vocabulary, FillIns()).UsedPlaceholders.ToHashSet());
        Assert.Equal(
            new HashSet<SnippetPlaceholder> { SnippetPlaceholder.Date, SnippetPlaceholder.Clipboard },
            Fixed("EWSNIPA", "EWSNIPB").Expand("backslash my stamp and backslash my link", vocabulary, FillIns()).UsedPlaceholders.ToHashSet());

        // The saved clipboard snippet is in the vocabulary and did not fire, so it is not here.
        var noFillIn = Fixed("EWSNIPA").Expand("backslash my sign off", vocabulary, FillIns());
        Assert.True(noFillIn.DidFire);
        Assert.Empty(noFillIn.UsedPlaceholders);
        Assert.Empty(Fixed("EWSNIPA").Expand("no keyword here", vocabulary, FillIns()).UsedPlaceholders);
    }

    /// <summary>The clipboard is the one domain a person can put "EWSNIP..." into; the collision domain is the RESOLVED text.</summary>
    [Fact]
    public void ASentinelCandidateLivingInTheClipboardTextIsRejected()
    {
        var outcome = Fixed("EWSNIPINSIDE", "EWSNIPCLEAN").Expand(
            "backslash my link",
            Vocabulary(("my link", "see {{clipboard}} for details")),
            FillIns("reference EWSNIPINSIDE here"));

        Assert.Equal("EWSNIPCLEAN", outcome.Records[0].Sentinel);
        Assert.Equal("see reference EWSNIPINSIDE here for details", outcome.Records[0].Expansion);
    }

    /// <summary>Both halves of the stage's two-pass gate, measured rather than assumed.</summary>
    [Fact]
    public void TheSameCandidateIsCleanWithNoClipboardAndCollidingWithOne()
    {
        var vocabulary = Vocabulary(("my link", "see {{clipboard}} for details"));
        var dry = Fixed("EWSNIPINSIDE", "EWSNIPCLEAN").Expand("backslash my link", vocabulary, FillIns(null));
        var wet = Fixed("EWSNIPINSIDE", "EWSNIPCLEAN").Expand("backslash my link", vocabulary, FillIns("EWSNIPINSIDE"));

        Assert.Equal("EWSNIPINSIDE", dry.Records[0].Sentinel);
        Assert.Equal(new HashSet<SnippetPlaceholder> { SnippetPlaceholder.Clipboard }, dry.UsedPlaceholders.ToHashSet());
        Assert.Equal("EWSNIPCLEAN", wet.Records[0].Sentinel);
    }

    [Fact]
    public void AFallbackCandidateThatTheClipboardAlreadyContainsIsSkippedToo()
    {
        var outcome = Fixed("EWSNIPSAME").Expand(
            "backslash my link",
            Vocabulary(("my link", "see {{clipboard}}")),
            FillIns("EWSNIPSAME and EWSNIPFALLBACK0 both appear"));

        Assert.Equal("EWSNIPFALLBACK1", outcome.Records[0].Sentinel);
        Assert.Equal("see EWSNIPSAME and EWSNIPFALLBACK0 both appear", outcome.Records[0].Expansion);
    }

    [Fact]
    public void ADateSnippetSpokenAsTheWholeDictationStillOwnsItsEnding()
    {
        var outcome = Fixed("EWSNIPAAAA").Expand("backslash my stamp.", Vocabulary(("my stamp", "{{date}}")), FillIns());

        Assert.Equal("EWSNIPAAAA", outcome.Text);
        Assert.Equal(new SnippetExpansionRecord("EWSNIPAAAA", "Sep 16, 2026", SuppressFollowingSentenceEnding: true), outcome.Records[0]);
    }

    /// <summary>Embedded, the decision reads the DELIVERED text: a date does not end a sentence, so the person's stop stays.</summary>
    [Fact]
    public void TheSameDateSnippetEmbeddedKeepsThePersonsOwnFullStop()
    {
        var outcome = Fixed("EWSNIPAAAA").Expand("filed on backslash my stamp.", Vocabulary(("my stamp", "{{date}}")), FillIns());

        Assert.Equal("filed on EWSNIPAAAA.", outcome.Text);
        Assert.False(outcome.Records[0].SuppressFollowingSentenceEnding);
    }

    [Fact]
    public void AnEmbeddedClipboardSnippetWhoseCopiedTextEndsASentenceSuppressesTheDuplicate()
    {
        var outcome = Fixed("EWSNIPAAAA").Expand(
            "here you go backslash my link. Thanks",
            Vocabulary(("my link", "see {{clipboard}}")),
            FillIns("https://x.dev."));

        Assert.Equal("here you go EWSNIPAAAA Thanks", outcome.Text);
        Assert.Equal(new SnippetExpansionRecord("EWSNIPAAAA", "see https://x.dev.", SuppressFollowingSentenceEnding: true), outcome.Records[0]);
    }

    [Fact]
    public void TheSameSnippetWithAClipboardThatDoesNotEndASentenceKeepsTheStop()
    {
        var outcome = Fixed("EWSNIPAAAA").Expand(
            "here you go backslash my link. Thanks",
            Vocabulary(("my link", "see {{clipboard}}")),
            FillIns("https://x.dev"));

        Assert.Equal("here you go EWSNIPAAAA. Thanks", outcome.Text);
        Assert.False(outcome.Records[0].SuppressFollowingSentenceEnding);
    }
}
