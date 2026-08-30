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
        Assert.Equal("envy wisper,EnviousWispr,default", lines[0]);
        Assert.Equal("Stenhouse,Stenhouse,loose", lines[1]);
        Assert.Equal("Magnusson,Magnusson,strict", lines[2]);
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
        var plan = CustomWordImport.Read("Magnusson,Magnusson,strickt", []);

        Assert.Empty(plan.Additions);
        Assert.Equal(ImportedWordOutcome.Unreadable, Assert.Single(plan.Lines).Outcome);
    }

    [Fact]
    public void TheSameWordUnderADifferentRuleIsAConflictRatherThanADuplicate()
    {
        var existing = new CustomWordEntry("Magnusson", "Magnusson");

        var plan = CustomWordImport.Read("Magnusson,Magnusson,strict", [existing]);

        Assert.Empty(plan.Additions);
        var conflict = Assert.Single(plan.Conflicts);
        Assert.Equal(MatchStrictness.Strict, conflict.Strictness);
    }

    [Fact]
    public void TakingAConflictTakesTheRuleWithTheSpelling()
    {
        CustomWordEntry[] existing = [new("Magnusson", "Magnuson")];
        var plan = CustomWordImport.Read("Magnusson,Magnusson,strict", existing);

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
}
