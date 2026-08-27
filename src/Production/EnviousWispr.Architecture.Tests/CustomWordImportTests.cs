using EnviousWispr.Core.Settings;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// Reading a word list must never silently lose a line or silently overwrite a tuned correction.
/// </summary>
public sealed class CustomWordImportTests
{
    private static CustomWordImportPlan Read(string text, params (string Spoken, string Replacement)[] existing) =>
        CustomWordImport.Read(
            text,
            existing.Select(pair => new CustomWordEntry(pair.Spoken, pair.Replacement)).ToArray());

    [Theory]
    [InlineData("kubernetes,Kubernetes")]
    [InlineData("kubernetes\tKubernetes")]
    [InlineData("kubernetes=Kubernetes")]
    [InlineData("  kubernetes ,  Kubernetes  ")]
    public void EverySeparatorPeopleActuallyProduceIsRead(string line)
    {
        var plan = Read(line);

        var entry = Assert.Single(plan.Additions);
        Assert.Equal("kubernetes", entry.SpokenForm);
        Assert.Equal("Kubernetes", entry.Replacement);
    }

    /// <summary>
    /// The control for the theory above: a line with NO separator must not become a word. Without
    /// it, a reader that accepted anything would pass every case there.
    /// </summary>
    [Fact]
    public void ALineWithNoSeparatorIsUnreadableRatherThanAWord()
    {
        var plan = Read("just some prose with no separator");

        Assert.Empty(plan.Additions);
        Assert.Equal(1, plan.UnreadableCount);
    }

    /// <summary>
    /// Unreadable and ignored are different, and collapsing them is how half a file disappears
    /// while the import reports success.
    /// </summary>
    [Fact]
    public void BlanksAndCommentsAreIgnoredRatherThanReportedAsProblems()
    {
        var plan = Read("# my words\n\nkubernetes,Kubernetes\n\n# end");

        Assert.Single(plan.Additions);
        Assert.Equal(0, plan.UnreadableCount);
        // Five lines: two comments, two blanks, one word. My first count said three ignored and
        // the run said four, because the blank BETWEEN the word and the closing comment is a line
        // too. Counted from the input rather than from what the code returned.
        Assert.Equal(4, plan.Lines.Count(line => line.Outcome == ImportedWordOutcome.Ignored));
    }

    /// <summary>
    /// A word the user already has, with a DIFFERENT replacement, is the one case they must be
    /// told about. Silently overwriting a correction someone tuned by hand is worse than importing
    /// nothing at all.
    /// </summary>
    [Fact]
    public void AWordThatWouldOverwriteATunedCorrectionIsAConflictRatherThanAnAddition()
    {
        var plan = Read("kubernetes,K8s", ("kubernetes", "Kubernetes"));

        Assert.Empty(plan.Additions);
        Assert.Equal(1, plan.ConflictCount);
    }

    /// <summary>
    /// The same word with the SAME replacement is not a conflict and not an addition. Re-importing
    /// a file a user already imported must be a no-op rather than a wall of warnings.
    /// </summary>
    [Fact]
    public void ReimportingTheSameFileChangesNothingAndWarnsAboutNothing()
    {
        var plan = Read("kubernetes,Kubernetes", ("kubernetes", "Kubernetes"));

        Assert.Empty(plan.Additions);
        Assert.Equal(0, plan.ConflictCount);
        Assert.Equal(1, plan.Lines.Count(line => line.Outcome == ImportedWordOutcome.AlreadyPresent));
    }

    /// <summary>
    /// A file listing one word twice must not add it twice. The second line collides with the
    /// FIRST LINE OF THE SAME IMPORT, which nothing in the existing list would catch.
    /// </summary>
    [Fact]
    public void AFileThatListsOneWordTwiceAddsItOnce()
    {
        var plan = Read("kubernetes,Kubernetes\nkubernetes,K8s");

        Assert.Single(plan.Additions);
        Assert.Equal(1, plan.ConflictCount);
    }

    [Fact]
    public void MatchingIsCaseInsensitiveBecauseSpeechIsNot()
    {
        var plan = Read("Kubernetes,K8s", ("kubernetes", "Kubernetes"));

        Assert.Equal(1, plan.ConflictCount);
    }

    /// <summary>
    /// A pasted document would otherwise become one enormous "word".
    /// </summary>
    [Fact]
    public void AnAbsurdlyLongFieldIsRefused()
    {
        var plan = Read(new string('x', CustomWordImport.MaximumFieldLength + 1) + ",short");

        Assert.Empty(plan.Additions);
        Assert.Equal(1, plan.UnreadableCount);
    }

    /// <summary>
    /// The stated limit, asserted so it stays a KNOWN limit rather than becoming a surprise. A
    /// quoted field containing a comma splits into three parts and is refused, which is visible.
    /// Half a CSV parser would succeed on the easy rows and mangle exactly the ones a user could
    /// not predict.
    /// </summary>
    [Fact]
    public void AQuotedFieldContainingASeparatorIsRefusedRatherThanGuessedAt()
    {
        var plan = Read("\"hello, world\",Greeting");

        Assert.Empty(plan.Additions);
        Assert.Equal(1, plan.UnreadableCount);
    }

    [Fact]
    public void EmptyInputIsAnEmptyPlanRatherThanACrash()
    {
        Assert.Empty(CustomWordImport.Read(string.Empty, []).Lines);
    }

    /// <summary>
    /// Export then import must return the same words. The expected value is a LITERAL rather than
    /// a second call to Write, or the two sides would be transformed identically and the row would
    /// pass against code that round-trips nothing.
    /// </summary>
    [Fact]
    public void WhatIsExportedCanBeImportedBack()
    {
        CustomWordEntry[] entries =
        [
            new("kubernetes", "Kubernetes"),
            new("post gres", "PostgreSQL"),
        ];

        var written = CustomWordImport.Write(entries);

        Assert.Contains("kubernetes,Kubernetes", written, StringComparison.Ordinal);
        Assert.Contains("post gres,PostgreSQL", written, StringComparison.Ordinal);

        var plan = CustomWordImport.Read(written, []);
        Assert.Equal(2, plan.Additions.Count);
        Assert.Equal(entries[0], plan.Additions[0]);
        Assert.Equal(entries[1], plan.Additions[1]);
    }

    /// <summary>
    /// Every line is accounted for. A reader that dropped lines would otherwise report a clean
    /// import of a file it half read.
    /// </summary>
    [Fact]
    public void EveryLineOfTheInputAppearsInThePlan()
    {
        const string text = "# comment\nkubernetes,Kubernetes\nbroken line\n\npost gres,PostgreSQL";

        var plan = Read(text);

        Assert.Equal(5, plan.Lines.Count);
        Assert.Equal(Enumerable.Range(1, 5), plan.Lines.Select(line => line.LineNumber));
    }
}
