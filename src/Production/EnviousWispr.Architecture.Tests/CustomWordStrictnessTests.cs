using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Settings;
using EnviousWispr.PostProcessing;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// A word can be corrected generously or meanly, and the choice is the user's, word by word.
/// </summary>
/// <remarks>
/// EVERY NUMBER BELOW IS MEASURED RATHER THAN CHOSEN TO PASS. The pairs were picked by computing
/// their similarity first and finding ones that land BETWEEN two of the three bars, because a pair
/// that clears all three proves nothing about which bar was used. "stenhaus" against "Stenhouse" is
/// 0.778, which is above the loose bar and below the ordinary one; "magnuson" against "Magnusson" is
/// 0.889, which is above the ordinary bar and below the strict one. Each test therefore fails if the
/// word's own choice is ignored, in whichever direction it is ignored.
/// </remarks>
public sealed class CustomWordStrictnessTests
{
    [Fact]
    public void ALooseWordIsCorrectedFromFurtherAwayThanTheOrdinaryRuleAllows()
    {
        var heard = "I spoke to stenhaus about it";
        var ordinary = CustomWordCorrector.Correct(
            heard,
            [new CustomWordEntry("Stenhouse", "Stenhouse")]);
        var loose = CustomWordCorrector.Correct(
            heard,
            [new CustomWordEntry("Stenhouse", "Stenhouse", MatchStrictness.Loose)]);

        Assert.Equal(heard, ordinary.Text);
        Assert.Equal(0, ordinary.ReplacementCount);
        Assert.Equal("I spoke to Stenhouse about it", loose.Text);
        Assert.Equal(1, loose.ReplacementCount);
    }

    [Fact]
    public void AStrictWordRefusesWhatTheOrdinaryRuleWouldHaveTaken()
    {
        var heard = "ask magnuson to sign it";
        var ordinary = CustomWordCorrector.Correct(
            heard,
            [new CustomWordEntry("Magnusson", "Magnusson")]);
        var strict = CustomWordCorrector.Correct(
            heard,
            [new CustomWordEntry("Magnusson", "Magnusson", MatchStrictness.Strict)]);

        Assert.Equal("ask Magnusson to sign it", ordinary.Text);
        Assert.Equal(1, ordinary.ReplacementCount);
        Assert.Equal(heard, strict.Text);
        Assert.Equal(0, strict.ReplacementCount);
    }

    [Fact]
    public void AWordWithNoChoiceIsCorrectedTheWayEveryWordAlwaysWas()
    {
        var entry = new CustomWordEntry("Magnusson", "Magnusson");

        Assert.Equal(MatchStrictness.Default, entry.Strictness);
        Assert.Equal(CustomWordCorrector.SimilarityThreshold, CustomWordCorrector.BaseThreshold(entry.Strictness));
    }

    [Fact]
    public void TheThreeBarsAreOrderedAndDistinct()
    {
        Assert.True(
            CustomWordCorrector.BaseThreshold(MatchStrictness.Loose) <
            CustomWordCorrector.BaseThreshold(MatchStrictness.Default));
        Assert.True(
            CustomWordCorrector.BaseThreshold(MatchStrictness.Default) <
            CustomWordCorrector.BaseThreshold(MatchStrictness.Strict));
    }

    [Fact]
    public void AnExportedListSaysWhichRuleEachWordIsUnder()
    {
        var written = CustomWordImport.Write(
        [
            new CustomWordEntry("envy wisper", "EnviousWispr"),
            new CustomWordEntry("Stenhouse", "Stenhouse", MatchStrictness.Loose),
            new CustomWordEntry("Magnusson", "Magnusson", MatchStrictness.Strict),
        ]);

        var lines = written.Split(Environment.NewLine);
        Assert.Equal(CustomWordImport.Header, lines[0]);
        Assert.Equal("envy wisper,EnviousWispr,default", lines[1]);
        Assert.Equal("Stenhouse,Stenhouse,loose", lines[2]);
        Assert.Equal("Magnusson,Magnusson,strict", lines[3]);
    }

    [Fact]
    public void AListWrittenBeforeThisColumnExistedStillReads()
    {
        var plan = CustomWordImport.Read("envy wisper,EnviousWispr", []);

        var added = Assert.Single(plan.Additions);
        Assert.Equal(MatchStrictness.Default, added.Strictness);
    }

    [Fact]
    public void AnExportedListCanBeImportedBackUnchanged()
    {
        CustomWordEntry[] original =
        [
            new("envy wisper", "EnviousWispr"),
            new("Stenhouse", "Stenhouse", MatchStrictness.Loose),
            new("Magnusson", "Magnusson", MatchStrictness.Strict),
        ];

        var plan = CustomWordImport.Read(CustomWordImport.Write(original), []);

        Assert.Equal(original, plan.Additions);
    }

    [Fact]
    public void AWordNobodyRecognisesIsRefusedRatherThanTakenAsOrdinary()
    {
        // Reading "strickt" as the ordinary rule would correct that word MORE widely than the file
        // asked for, and say nothing about having done so.
        var plan = CustomWordImport.Read($"{CustomWordImport.Header}\nMagnusson,Magnusson,strickt", []);

        Assert.Empty(plan.Additions);
        Assert.Equal(
            ImportedWordOutcome.Unreadable,
            Assert.Single(plan.Lines, line => line.Outcome != ImportedWordOutcome.Ignored).Outcome);
    }

    [Fact]
    public void TheSameWordUnderADifferentRuleIsAConflictRatherThanADuplicate()
    {
        var existing = new CustomWordEntry("Magnusson", "Magnusson");

        var plan = CustomWordImport.Read($"{CustomWordImport.Header}\nMagnusson,Magnusson,strict", [existing]);

        Assert.Empty(plan.Additions);
        var conflict = Assert.Single(plan.Conflicts);
        Assert.Equal(MatchStrictness.Strict, conflict.Strictness);
    }

    [Fact]
    public void TakingAConflictTakesTheRuleWithTheSpelling()
    {
        CustomWordEntry[] existing = [new("Magnusson", "Magnuson")];
        var plan = CustomWordImport.Read($"{CustomWordImport.Header}\nMagnusson,Magnusson,strict", existing);

        var taken = CustomWordImport.Merge(existing, plan.Conflicts);

        var only = Assert.Single(taken);
        Assert.Equal("Magnusson", only.Replacement);
        Assert.Equal(MatchStrictness.Strict, only.Strictness);
    }

    [Fact]
    public void AScreenReaderIsToldWhenAWordIsNotUnderTheOrdinaryRule()
    {
        Assert.Equal(
            "Magnusson becomes Magnusson",
            new CustomWordEntry("Magnusson", "Magnusson").ToString());
        Assert.Equal(
            "Stenhouse becomes Stenhouse, matched loosely",
            new CustomWordEntry("Stenhouse", "Stenhouse", MatchStrictness.Loose).ToString());
        Assert.Equal(
            "Magnusson becomes Magnusson, matched strictly",
            new CustomWordEntry("Magnusson", "Magnusson", MatchStrictness.Strict).ToString());
    }

    [Fact]
    public void AFileWithNoHeaderCannotHaveOneOfItsLinesReinterpreted()
    {
        // "say,hello,strict" was refused before this column existed. Reading it now as "hello"
        // matched strictly would be a decision made for somebody who may well have been writing a
        // replacement with a comma in it, on a line that never worked either way.
        var plan = CustomWordImport.Read("say,hello,strict", []);

        Assert.Empty(plan.Additions);
        Assert.Equal(ImportedWordOutcome.Unreadable, Assert.Single(plan.Lines).Outcome);
    }

    [Fact]
    public void AHeaderFoundHalfwayDownDoesNotReachTheLinesAboveIt()
    {
        var plan = CustomWordImport.Read($"say,hello,strict\n{CustomWordImport.Header}", []);

        Assert.Empty(plan.Additions);
        Assert.Contains(plan.Lines, line => line.Outcome == ImportedWordOutcome.Unreadable);
    }

    [Fact]
    public void AStrictWordDoesNotStandInFrontOfALooseOneThatWouldHaveMatched()
    {
        // "magnuson" is 0.889 from Magnusson, which is below the strict bar of 0.915, and 0.778 from
        // Magnesson, which is above the loose bar of 0.715. Ranking by score before checking each
        // bar let the strict word - which was never going to be corrected - decide the outcome for
        // the loose one.
        var result = CustomWordCorrector.Correct(
            "ask magnuson to sign it",
            [
                new CustomWordEntry("Magnusson", "Magnusson", MatchStrictness.Strict),
                new CustomWordEntry("Magnesson", "Magnesson", MatchStrictness.Loose),
            ]);

        Assert.Equal("ask Magnesson to sign it", result.Text);
        Assert.Equal(1, result.ReplacementCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwoRowsThatAgreeOnTheWordTakeTheStricterOneWhicheverComesFirst(bool reversed)
    {
        CustomWordEntry[] words =
        [
            new("Magnusson", "Magnusson", MatchStrictness.Loose),
            new("Magnusson", "Magnusson", MatchStrictness.Strict),
        ];
        if (reversed)
        {
            words = [words[1], words[0]];
        }

        var result = CustomWordCorrector.Correct("ask magnuson to sign it", words);

        Assert.Equal("ask magnuson to sign it", result.Text);
        Assert.Equal(0, result.ReplacementCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwoPhrasesThatWantTheSameWordsAreBothLeftAloneWhicheverComesFirst(bool reversed)
    {
        // "red blue" and "blue sun" are the same length and the same number of words, and both are
        // inside "red blue sun". Replacing surface by surface gave whichever row came first.
        CustomWordEntry[] words =
        [
            new("red blue", "Alpha"),
            new("blue sun", "Beta"),
        ];
        if (reversed)
        {
            words = [words[1], words[0]];
        }

        var result = CustomWordCorrector.Correct("the red blue sun rose", words);

        Assert.Equal("the red blue sun rose", result.Text);
        Assert.Equal(0, result.ReplacementCount);
    }

    [Fact]
    public void ALongerPhraseStillWinsOverAShorterOneInsideIt()
    {
        var result = CustomWordCorrector.Correct(
            "the red blue sun rose",
            [
                new CustomWordEntry("red blue sun", "Alpha"),
                new CustomWordEntry("blue sun", "Beta"),
            ]);

        Assert.Equal("the Alpha rose", result.Text);
        Assert.Equal(1, result.ReplacementCount);
    }

    [Fact]
    public void AWordOneRowWritesIsNotRewrittenAgainByTheNext()
    {
        // Replacing in the growing result let the word "Alpha" - written in by the first row - be
        // found and rewritten by the second, so the text ended up saying something neither row asked
        // for.
        var result = CustomWordCorrector.Correct(
            "the zebracode is ready",
            [
                new CustomWordEntry("zebracode", "Alpha"),
                new CustomWordEntry("Alpha", "Omega"),
            ]);

        Assert.Equal("the Alpha is ready", result.Text);
        Assert.Equal(1, result.ReplacementCount);
    }

    [Theory]
    [InlineData("WHEN I SAY,WRITE,HOW CLOSELY\nMagnusson,Magnusson,strict", true)]
    [InlineData("\n\n# a note\nwhen I say,write,how closely\nMagnusson,Magnusson,strict", true)]
    [InlineData("when I say,write,how closely\nwhen I say,write,how closely\nMagnusson,Magnusson,strict", true)]
    [InlineData("when I say,write,how closely\nMagnusson,Magnusson", false)]
    [InlineData("when I say,write,how closely\nMagnusson,Magnusson,", false)]
    [InlineData("Magnusson,Magnusson,strict", false)]
    public void AFileIsReadTheWayItsFirstLineSaysItShouldBe(string text, bool reads)
    {
        var plan = CustomWordImport.Read(text, []);

        if (reads)
        {
            Assert.Equal(MatchStrictness.Strict, Assert.Single(plan.Additions).Strictness);
        }
        else
        {
            Assert.Empty(plan.Additions);
            Assert.Contains(plan.Lines, line => line.Outcome == ImportedWordOutcome.Unreadable);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AWordTwoRowsDisagreeAboutIsLeftAloneWhicheverRowComesFirst(bool reversed)
    {
        CustomWordEntry[] words =
        [
            new("zebracode", "Alpha"),
            new("zebracode", "Beta"),
        ];
        if (reversed)
        {
            words = [words[1], words[0]];
        }

        var result = CustomWordCorrector.Correct("the zebracode is ready", words);

        Assert.Equal("the zebracode is ready", result.Text);
        Assert.Equal(0, result.ReplacementCount);
    }

    [Fact]
    public void OneRowOnItsOwnStillCorrectsTheWordTheTwoDisagreedAbout()
    {
        var result = CustomWordCorrector.Correct(
            "the zebracode is ready",
            [new CustomWordEntry("zebracode", "Alpha")]);

        Assert.Equal("the Alpha is ready", result.Text);
    }

    [Fact]
    public void ASettingsFileCarryingAStrictnessNobodyDefinedIsRefused()
    {
        var settings = AppSettings.Default with
        {
            UserData = new ReusableUserData(
                [new CustomWordEntry("envy wisper", "EnviousWispr", (MatchStrictness)99)],
                []),
        };

        Assert.NotNull(AppSettingsValidator.Validate(settings, AppErrorStage.SettingsLoad));
    }
}
