using EnviousWispr.Core.Settings;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// Choosing one row removes that row.
/// </summary>
public sealed class CustomWordRemovalTests
{
    [Fact]
    public void ADuplicateEntryKeepsItsTwinWhenOnlyOneIsChosen()
    {
        // THE DEFECT THIS EXISTS FOR. CustomWordEntry is a record, so these two are EQUAL, and
        // filtering the list by value removed both when a person had selected one. A duplicate is
        // reachable: importing a profile or a word list does not require uniqueness.
        var first = new CustomWordEntry("envy wisper", "EnviousWispr");
        var twin = new CustomWordEntry("envy wisper", "EnviousWispr");
        var other = new CustomWordEntry("git hub", "GitHub");

        var remaining = CustomWordRemoval.Without([first, twin, other], [first]);

        Assert.Equal(2, remaining.Count);
        Assert.Same(twin, remaining[0]);
        Assert.Same(other, remaining[1]);
    }

    [Fact]
    public void SeveralChosenRowsAllGo()
    {
        var first = new CustomWordEntry("one", "1");
        var second = new CustomWordEntry("two", "2");
        var third = new CustomWordEntry("three", "3");

        var remaining = CustomWordRemoval.Without([first, second, third], [first, third]);

        Assert.Single(remaining);
        Assert.Same(second, remaining[0]);
    }

    [Fact]
    public void ChoosingNothingChangesNothing()
    {
        var words = new[] { new CustomWordEntry("one", "1") };
        Assert.Same(words, CustomWordRemoval.Without(words, []));
    }
}
