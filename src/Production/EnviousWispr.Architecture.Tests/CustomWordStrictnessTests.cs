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
    public void APhraseAlreadyBeatenByALongerOneDoesNotVetoItsNeighbour(bool reversed)
    {
        // "extraword red" takes the front of the sentence, so "red blue" can never apply. It was
        // still allowed to argue with "blue sun", and a phrase that was already dead stopped a live
        // one from correcting anything.
        CustomWordEntry[] words =
        [
            new("extraword red", "Top"),
            new("red blue", "Alpha"),
            new("blue sun", "Beta"),
        ];
        if (reversed)
        {
            words = [words[2], words[1], words[0]];
        }

        var result = CustomWordCorrector.Correct("extraword red blue sun", words);

        Assert.Equal("Top Beta", result.Text);
        Assert.Equal(2, result.ReplacementCount);
    }


    [Fact]
    public void AWordWrittenInByOneRuleIsNotThenHeardAsAnother()
    {
        // The close-enough pass used to read the CORRECTED sentence, so it examined words nobody had
        // said: "Alphaa" was written in by the first rule and then heard as "Alpha" by the second,
        // and one span of what the person actually said was counted as two corrections.
        var result = CustomWordCorrector.Correct(
            "the zebracode is ready",
            [
                new CustomWordEntry("zebracode", "Alphaa"),
                new CustomWordEntry("Alpha", "Omega", MatchStrictness.Loose),
            ]);

        Assert.Equal("the Alphaa is ready", result.Text);
        Assert.Equal(1, result.ReplacementCount);
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

    [Fact]
    public void APhraseIsCorrectedWhenWhatWasHeardIsCloseEnough()
    {
        var result = CustomWordCorrector.Correct(
            "I met stenhaus partners today",
            [new CustomWordEntry("Stenhouse Partners", "Stenhouse Partners")]);

        Assert.Equal("I met Stenhouse Partners today", result.Text);
        Assert.Equal(1, result.ReplacementCount);
    }

    [Fact]
    public void ALoosePhraseIsCorrectedFromFurtherAwayThanTheOrdinaryRuleAllows()
    {
        // "stenhaus partnas" is 0.778 from the phrase: above the loose bar of 0.72 and below the
        // ordinary phrase bar of 0.85.
        var heard = "I met stenhaus partnas today";
        var ordinary = CustomWordCorrector.Correct(
            heard,
            [new CustomWordEntry("Stenhouse Partners", "Stenhouse Partners")]);
        var loose = CustomWordCorrector.Correct(
            heard,
            [new CustomWordEntry("Stenhouse Partners", "Stenhouse Partners", MatchStrictness.Loose)]);

        Assert.Equal(heard, ordinary.Text);
        Assert.Equal("I met Stenhouse Partners today", loose.Text);
    }

    [Fact]
    public void AStrictPhraseRefusesWhatTheOrdinaryRuleWouldHaveTaken()
    {
        // "magnuson holdins" is 0.889 from the phrase: above the ordinary bar of 0.85 and below the
        // strict bar of 0.92.
        var heard = "ask magnuson holdins to sign";
        var ordinary = CustomWordCorrector.Correct(
            heard,
            [new CustomWordEntry("Magnusson Holdings", "Magnusson Holdings")]);
        var strict = CustomWordCorrector.Correct(
            heard,
            [new CustomWordEntry("Magnusson Holdings", "Magnusson Holdings", MatchStrictness.Strict)]);

        Assert.Equal("ask Magnusson Holdings to sign", ordinary.Text);
        Assert.Equal(heard, strict.Text);
    }

    [Fact]
    public void APhraseCarryingAnEverydayWordHasToBeACloserMatch()
    {
        // The two scores are within six thousandths of each other - 0.882 and 0.889 - and only one
        // of the phrases contains "of". That word is the whole difference between the outcomes.
        var withEveryday = CustomWordCorrector.Correct(
            "send it to bank of stenhaus",
            [new CustomWordEntry("Bank of Stenhouse", "Bank of Stenhouse")]);
        var without = CustomWordCorrector.Correct(
            "ask magnuson holdins to sign",
            [new CustomWordEntry("Magnusson Holdings", "Magnusson Holdings")]);

        Assert.Equal("send it to bank of stenhaus", withEveryday.Text);
        Assert.Equal("ask Magnusson Holdings to sign", without.Text);
    }

    [Fact]
    public void AChoiceTheUserMadeReplacesTheEverydayWordPenaltyRatherThanAddingToIt()
    {
        // Somebody who asked for a generous match on a phrase asked for it knowing what the phrase
        // contains, so the penalty is not stacked on top of their answer.
        var result = CustomWordCorrector.Correct(
            "send it to bank of stenhaus",
            [new CustomWordEntry("Bank of Stenhouse", "Bank of Stenhouse", MatchStrictness.Loose)]);

        Assert.Equal("send it to Bank of Stenhouse", result.Text);
        Assert.Equal(1, result.ReplacementCount);
    }

    [Fact]
    public void APhraseAlreadyWrittenCorrectlyIsLeftExactlyAsItIs()
    {
        var result = CustomWordCorrector.Correct(
            "send it to Bank of Stenhouse",
            [new CustomWordEntry("Bank of Stenhouse", "Bank of Stenhouse")]);

        Assert.Equal("send it to Bank of Stenhouse", result.Text);
        Assert.Equal(0, result.ReplacementCount);
    }

    [Fact]
    public void ALongerPhraseWinsOverAShorterOneInsideItWhenNeitherIsExact()
    {
        // "red blue sunn" is 0.923 from the three-word phrase and its tail is 0.889 from the
        // two-word one, so both clear the bar and only the run order decides.
        var result = CustomWordCorrector.Correct(
            "the red blue sunn rose",
            [
                new CustomWordEntry("red blue sun", "Alpha"),
                new CustomWordEntry("blue sun", "Beta"),
            ]);

        Assert.Equal("the Alpha rose", result.Text);
        Assert.Equal(1, result.ReplacementCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void APhraseThatCouldBeEitherOfTwoIsLeftAloneWhicheverComesFirst(bool reversed)
    {
        // "north bae" is 0.889 from both, so nothing in the list says which was meant.
        CustomWordEntry[] words =
        [
            new("north bay", "Alpha"),
            new("north bax", "Beta"),
        ];
        if (reversed)
        {
            words = [words[1], words[0]];
        }

        var result = CustomWordCorrector.Correct("we drove to north bae", words);

        Assert.Equal("we drove to north bae", result.Text);
        Assert.Equal(0, result.ReplacementCount);
    }

    [Fact]
    public void ASingleWordEntryIsUntouchedByThePhrasePass()
    {
        var result = CustomWordCorrector.Correct(
            "ask magnuson to sign it",
            [new CustomWordEntry("Magnusson", "Magnusson")]);

        Assert.Equal("ask Magnusson to sign it", result.Text);
        Assert.Equal(1, result.ReplacementCount);
    }

    [Fact]
    public void AStrictPhraseDoesNotStandInFrontOfALooseOneThatWouldHaveMatched()
    {
        // "alpha betx" is 0.90 from the strict phrase, below its 0.92, and 0.80 from the loose one,
        // above its 0.72. Ranking by score before checking each bar left the sentence alone.
        var result = CustomWordCorrector.Correct(
            "the alpha betx here",
            [
                new CustomWordEntry("alpha beta", "Alpha Beta", MatchStrictness.Strict),
                new CustomWordEntry("alpha zeta", "Alpha Zeta", MatchStrictness.Loose),
            ]);

        Assert.Equal("the Alpha Zeta here", result.Text);
        Assert.Equal(1, result.ReplacementCount);
    }

    [Fact]
    public void ThePhrasePassUsesTheReferencePlatformsListOfEverydayWords()
    {
        // "drove from bostin" is 0.882 from the phrase. "from" is in the single-word list of 44 and
        // NOT in the phrase list of 14, so reusing the larger list raised the bar to 0.90 and
        // refused a correction macOS makes.
        var result = CustomWordCorrector.Correct(
            "I drove from bostin yesterday",
            [new CustomWordEntry("drive from boston", "Drive from Boston")]);

        Assert.Equal("I Drive from Boston yesterday", result.Text);
        Assert.Equal(1, result.ReplacementCount);
    }

    [Theory]
    [InlineData(false, "Beta")]
    [InlineData(true, "Alpha")]
    public void TheLastRowToClaimASpokenFormIsTheOneThatOwnsIt(bool reversed, string expected)
    {
        // WINDOWS USED TO DROP BOTH, on the reasoning that picking one silently is picking by
        // position. macOS resolves it - the last row written wins - and dropping both meant the same
        // word list produced different text on the two platforms. Parity decides this, not safety.
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

        Assert.Equal($"the {expected} is ready", result.Text);
        Assert.Equal(1, result.ReplacementCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARowsWrittenFormYieldsToAnyRowThatSaysItAloud(bool reversed)
    {
        // "Alpha" is one row's written form and another row's spoken form. macOS gives the spoken
        // form the surface, so saying "Alpha" reaches the row that listens for it.
        CustomWordEntry[] words =
        [
            new("zebracode", "Alpha"),
            new("Alpha", "Omega"),
        ];
        if (reversed)
        {
            words = [words[1], words[0]];
        }

        var result = CustomWordCorrector.Correct("the Alpha is ready", words);

        Assert.Equal("the Omega is ready", result.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheLeftmostPhraseWinsAndNothingUnderneathRewritesWhatItTook(bool reversed)
    {
        // "red blue" and "blue sun" both fit inside "red blue sun". Windows used to call that a tie
        // and leave the sentence alone; macOS reads left to right and takes the one that starts
        // first, so their starting positions are a precedence rule rather than a coincidence. The
        // plain "blue" rule underneath cannot reach the words the winner took.
        CustomWordEntry[] words =
        [
            new("red blue", "Alpha"),
            new("blue sun", "Beta"),
            new("blue", "Gamma"),
        ];
        if (reversed)
        {
            words = [words[2], words[1], words[0]];
        }

        var result = CustomWordCorrector.Correct("the red blue sun rose", words);

        Assert.Equal("the Alpha sun rose", result.Text);
        Assert.Equal(1, result.ReplacementCount);
    }

    [Fact]
    public void APhraseWrittenTheWayTheListSpellsItSettlesItsWordsEvenThoughNothingChanges()
    {
        // "red blue" writing "red blue" changes nothing, and it still settles those two words:
        // saying a phrase the way the list spells it is an answer, so a rule overlapping it does not
        // get to rewrite half of what was said.
        var result = CustomWordCorrector.Correct(
            "the red blue sun rose",
            [
                new CustomWordEntry("red blue", "red blue"),
                new CustomWordEntry("blue sun", "Beta"),
            ]);

        Assert.Equal("the red blue sun rose", result.Text);
        Assert.Equal(0, result.ReplacementCount);
    }

    [Theory]
    [InlineData(false, MatchStrictness.Strict)]
    [InlineData(true, MatchStrictness.Loose)]
    public void TwoRowsClaimingOneWordLeaveTheLastOnesChoiceInForce(
        bool reversed,
        MatchStrictness owner)
    {
        // Windows used to take the strictest of the two. macOS takes the last one written, and its
        // choice comes with it - so the outcome follows the row that owns the word.
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

        // "magnuson" is 0.889 from the word: above the loose bar and below the strict one.
        var corrected = owner == MatchStrictness.Loose;
        Assert.Equal(corrected ? "ask Magnusson to sign it" : "ask magnuson to sign it", result.Text);
        Assert.Equal(corrected ? 1 : 0, result.ReplacementCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void APhraseTakenAtOnePositionLeavesTheWordsAfterItStillReachable(bool reversed)
    {
        // "small planet" is taken at the start, and "large" is still corrected on its own. The words
        // a phrase took are off limits; the words after it are not.
        CustomWordEntry[] words =
        [
            new("small planet", "Alpha"),
            new("planet large", "Beta"),
            new("large", "Gamma"),
        ];
        if (reversed)
        {
            words = [words[2], words[1], words[0]];
        }

        var result = CustomWordCorrector.Correct("small planet large", words);

        Assert.Equal("Alpha Gamma", result.Text);
        Assert.Equal(2, result.ReplacementCount);
    }

    [Fact]
    public void AWrittenFormOfSeveralWordsIsNotItselfSomethingToListenFor()
    {
        // "zed" writes "red blue". That phrase is not something anybody says, and treating it as a
        // surface let it reserve the front of the sentence and stop a rule that WAS being said.
        // macOS is explicit here: a written form only becomes matchable when it is a single word.
        var result = CustomWordCorrector.Correct(
            "red blue sun",
            [
                new CustomWordEntry("zed", "red blue"),
                new CustomWordEntry("blue sun", "Beta"),
            ]);

        Assert.Equal("red Beta", result.Text);
        Assert.Equal(1, result.ReplacementCount);
    }
}
