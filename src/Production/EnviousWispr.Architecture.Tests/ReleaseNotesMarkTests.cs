using EnviousWispr.Core.Distribution;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// The unread mark appears for notes nobody has opened, and only for those.
/// </summary>
public sealed class ReleaseNotesMarkTests
{
    [Fact]
    public void AFirstRunHasNotReadThem()
    {
        Assert.True(ReleaseNotesMark.IsUnread(lastSeen: null, current: "EnviousWispr 1.2.0 · Stable"));
    }

    [Fact]
    public void ReadingThisBuildsNotesTakesTheMarkOff()
    {
        const string build = "EnviousWispr 1.2.0 · Stable";
        Assert.False(ReleaseNotesMark.IsUnread(build, build));
    }

    [Fact]
    public void AnUpdateBringsTheMarkBack()
    {
        Assert.True(ReleaseNotesMark.IsUnread(
            lastSeen: "EnviousWispr 1.2.0 · Stable",
            current: "EnviousWispr 1.3.0 · Stable"));
    }

    [Fact]
    public void ChangingCHANNELIsAlsoNewNotes()
    {
        // The channel is part of the identity, so moving between Stable and a preview shows the
        // notes for the build you are actually on.
        Assert.True(ReleaseNotesMark.IsUnread(
            lastSeen: "EnviousWispr 1.2.0 · Stable",
            current: "EnviousWispr 1.2.0 · Preview"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABuildThatDoesNotNameItselfIsNotNews(string? current)
    {
        // A MARK NOBODY CAN CLEAR IS WORSE THAN NO MARK. Opening the page would store the same empty
        // value and the comparison would still differ from it on the next launch.
        Assert.False(ReleaseNotesMark.IsUnread(lastSeen: null, current: current));
    }
}
