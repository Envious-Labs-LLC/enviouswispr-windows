using System.Globalization;
using EnviousWispr.Presentation;
using Xunit;

namespace EnviousWispr.Architecture.Tests;

public sealed class WordDiffTests
{
    [Fact]
    public void TokenizeEmptyOrWhitespaceReturnsEmpty()
    {
        Assert.Empty(WordDiff.Tokenize(null));
        Assert.Empty(WordDiff.Tokenize(string.Empty));
        Assert.Empty(WordDiff.Tokenize("   \t\r\n  "));
    }

    [Fact]
    public void TokenizePreservesTrailingWhitespaceAndSkipsLeading()
    {
        var tokens = WordDiff.Tokenize("   Hello,  world!\nNext");
        Assert.Equal(3, tokens.Count);
        Assert.Equal(new WordDiffToken("Hello,", "  ", "hello"), tokens[0]);
        Assert.Equal(new WordDiffToken("world!", "\n", "world"), tokens[1]);
        Assert.Equal(new WordDiffToken("Next", string.Empty, "next"), tokens[2]);
    }

    [Fact]
    public void KeyForStripsOuterPunctuationAndCaseFolds()
    {
        Assert.Equal("hello", WordDiff.KeyFor("Hello,"));
        Assert.Equal("hello", WordDiff.KeyFor("\"(Hello!)\""));
        Assert.Equal("don't", WordDiff.KeyFor("Don't."));
        Assert.Equal("...", WordDiff.KeyFor("..."));
        Assert.Equal("i", WordDiff.KeyFor("\u0130", CultureInfo.InvariantCulture));
        Assert.NotEqual(WordDiff.KeyFor("C++"), WordDiff.KeyFor("C#"));
        Assert.NotEqual(WordDiff.KeyFor("$100"), WordDiff.KeyFor("100"));
        Assert.NotEqual(WordDiff.KeyFor("100%"), WordDiff.KeyFor("100"));
        Assert.NotEqual(WordDiff.KeyFor("ano"), WordDiff.KeyFor("año"));
    }

    [Fact]
    public void ComputePreservesSymbolAndDiacriticMeaningChangesAsEdits()
    {
        var symbolDiff = HistoryEditsPresentation.ComputeDiff("Use C++ for $100", "Use C# for 100");
        Assert.Equal(2, symbolDiff.ChangeCount);
        Assert.Equal("2 edits", symbolDiff.SummaryText);

        var diacriticDiff = HistoryEditsPresentation.ComputeDiff("Feliz ano", "Feliz año");
        Assert.Equal(1, diacriticDiff.ChangeCount);
        Assert.Equal("1 edit", diacriticDiff.SummaryText);

        var spacingDiff = HistoryEditsPresentation.ComputeDiff("hello world", "hello  world");
        Assert.Equal(0, spacingDiff.ChangeCount);
        Assert.Equal(HistoryEditsPresentation.SpacingOnlySummaryText, spacingDiff.SummaryText);
    }

    [Fact]
    public void ComputeIdenticalTextsReturnsSingleEqualRun()
    {
        var entries = WordDiff.Compute("Meet me at five.", "Meet me at five.");
        var entry = Assert.Single(entries);
        Assert.Equal(WordDiffKind.Equal, entry.Kind);
        Assert.Equal("Meet me at five.", entry.Text);

        var diff = HistoryEditsPresentation.ComputeDiff("Meet me at five.", "Meet me at five.");
        Assert.Equal(0, diff.ChangeCount);
        Assert.Equal("No edits", diff.SummaryText);
    }

    [Fact]
    public void ComputePunctuationAndCasingOnlyUsesPolishedSurfaceWithoutWordEdits()
    {
        var diff = HistoryEditsPresentation.ComputeDiff(
            "i think we should meet tomorrow",
            "I think we should meet tomorrow.");

        Assert.Equal(0, diff.ChangeCount);
        Assert.Equal(HistoryEditsPresentation.FormattingOnlySummaryText, diff.SummaryText);
        var run = Assert.Single(diff.Runs);
        Assert.Equal(MarkedUpRunKind.Same, run.Kind);
        Assert.Equal("I think we should meet tomorrow.", run.Text);
    }

    [Fact]
    public void ComputeMixedDeletionsInsertionsAndSubstitutionsAssignsSequentialChangeIndicesAndSnippets()
    {
        const string original = "um i think we should probly go to the store tomorrow";
        const string polished = "I think we should probably go to the grocery store today.";

        var diff = HistoryEditsPresentation.ComputeDiff(original, polished);

        Assert.Equal(4, diff.ChangeCount);
        Assert.Equal("4 edits", diff.SummaryText);

        Assert.Collection(
            diff.Runs,
            run =>
            {
                Assert.Equal(MarkedUpRunKind.Removed, run.Kind);
                Assert.Equal("um ", run.Text);
                Assert.Equal(0, run.ChangeIndex);
                Assert.Equal("um", run.OriginalSnippet);
                Assert.Equal(string.Empty, run.PolishedSnippet);
            },
            run =>
            {
                Assert.Equal(MarkedUpRunKind.Same, run.Kind);
                Assert.Equal("I think we should ", run.Text);
                Assert.Null(run.ChangeIndex);
            },
            run =>
            {
                Assert.Equal(MarkedUpRunKind.Removed, run.Kind);
                Assert.Equal("probly ", run.Text);
                Assert.Equal(1, run.ChangeIndex);
                Assert.Equal("probly", run.OriginalSnippet);
                Assert.Equal("probably", run.PolishedSnippet);
            },
            run =>
            {
                Assert.Equal(MarkedUpRunKind.Added, run.Kind);
                Assert.Equal("probably ", run.Text);
                Assert.Equal(1, run.ChangeIndex);
                Assert.Equal("probly", run.OriginalSnippet);
                Assert.Equal("probably", run.PolishedSnippet);
            },
            run =>
            {
                Assert.Equal(MarkedUpRunKind.Same, run.Kind);
                Assert.Equal("go to the ", run.Text);
                Assert.Null(run.ChangeIndex);
            },
            run =>
            {
                Assert.Equal(MarkedUpRunKind.Added, run.Kind);
                Assert.Equal("grocery ", run.Text);
                Assert.Equal(2, run.ChangeIndex);
                Assert.Equal(string.Empty, run.OriginalSnippet);
                Assert.Equal("grocery", run.PolishedSnippet);
            },
            run =>
            {
                Assert.Equal(MarkedUpRunKind.Same, run.Kind);
                Assert.Equal("store ", run.Text);
                Assert.Null(run.ChangeIndex);
            },
            run =>
            {
                Assert.Equal(MarkedUpRunKind.Removed, run.Kind);
                Assert.Equal("tomorrow", run.Text);
                Assert.Equal(3, run.ChangeIndex);
                Assert.Equal("tomorrow", run.OriginalSnippet);
                Assert.Equal("today.", run.PolishedSnippet);
            },
            run =>
            {
                Assert.Equal(MarkedUpRunKind.Added, run.Kind);
                Assert.Equal("today.", run.Text);
                Assert.Equal(3, run.ChangeIndex);
                Assert.Equal("tomorrow", run.OriginalSnippet);
                Assert.Equal("today.", run.PolishedSnippet);
            });
    }

    [Fact]
    public void ComputeEmptyOriginalOrEditedHandlesSingleSidedEdits()
    {
        var insertedOnly = HistoryEditsPresentation.ComputeDiff(string.Empty, "Added words");
        Assert.Equal(1, insertedOnly.ChangeCount);
        Assert.Equal("1 edit", insertedOnly.SummaryText);
        var addedRun = Assert.Single(insertedOnly.Runs);
        Assert.Equal(MarkedUpRunKind.Added, addedRun.Kind);
        Assert.Equal("Added words", addedRun.Text);

        var deletedOnly = HistoryEditsPresentation.ComputeDiff("Removed words", string.Empty);
        Assert.Equal(1, deletedOnly.ChangeCount);
        Assert.Equal("1 edit", deletedOnly.SummaryText);
        var removedRun = Assert.Single(deletedOnly.Runs);
        Assert.Equal(MarkedUpRunKind.Removed, removedRun.Kind);
        Assert.Equal("Removed words", removedRun.Text);

        var bothEmpty = HistoryEditsPresentation.ComputeDiff(string.Empty, "   ");
        Assert.Equal(0, bothEmpty.ChangeCount);
        Assert.Empty(bothEmpty.Runs);
    }

    [Fact]
    public void ComputeReconstructsOriginalAndEditedTextAcrossDiverseSequences()
    {
        var pairs = new (string Original, string Edited)[]
        {
            ("alpha beta gamma delta", "beta epsilon delta zeta"),
            ("one two three four five six", "six five four three two one"),
            ("the quick brown fox jumps over the lazy dog", "a quick red fox leaped over a sleepy dog"),
            ("ship it now please", "ship it later"),
            ("a", "b c d e"),
            ("a b c d e", "c"),
            ("emoji 🚀 test cafe", "emoji 🎉 test café"),
        };

        foreach (var (original, edited) in pairs)
        {
            var entries = WordDiff.Compute(original, edited);
            var reconstructedEdited = string.Concat(entries
                .Where(e => e.Kind != WordDiffKind.Delete)
                .Select(e => e.Text));
            Assert.Equal(edited, reconstructedEdited);

            var deletedAndEqualKeys = entries
                .Where(e => e.Kind != WordDiffKind.Insert)
                .SelectMany(e => WordDiff.Tokenize(e.Text))
                .Select(t => t.Key)
                .ToArray();
            var originalKeys = WordDiff.Tokenize(original).Select(t => t.Key).ToArray();
            Assert.Equal(originalKeys, deletedAndEqualKeys);
        }
    }

    [Fact]
    public void ComputeLongTranscriptCompletesQuicklyWithLinearSpaceMyers()
    {
        var origWords = Enumerable.Range(0, 600).Select(i => $"word{i}");
        var editWords = Enumerable.Range(0, 600).Select(i => i % 25 == 0 ? $"edited{i}" : $"word{i}");
        var original = string.Join(" ", origWords);
        var edited = string.Join(" ", editWords);

        var diff = HistoryEditsPresentation.ComputeDiff(original, edited);

        Assert.Equal(24, diff.ChangeCount);
        Assert.Equal("24 edits", diff.SummaryText);
    }

    [Fact]
    public void HistoryDiffCacheEnforcesBoundedCapacityAndMovesHitsToFront()
    {
        var cache = new HistoryDiffCache(capacity: 3);

        var first = cache.GetOrCompute("a b", "a c");
        var second = cache.GetOrCompute("d e", "d f");
        var third = cache.GetOrCompute("g h", "g i");
        Assert.Equal(3, cache.Count);

        // Touch the oldest entry ("a b" -> "a c") so "d e" -> "d f" becomes least-recently-used.
        var firstHit = cache.GetOrCompute("a b", "a c");
        Assert.Same(first, firstHit);

        // Inserting a fourth distinct pair must evict ("d e", "d f") while keeping capacity at 3.
        _ = cache.GetOrCompute("j k", "j l");
        Assert.Equal(3, cache.Count);

        Assert.Same(first, cache.GetOrCompute("a b", "a c"));
        Assert.Same(third, cache.GetOrCompute("g h", "g i"));

        // Re-requesting evicted ("d e", "d f") recomputes and yields a new instance.
        var recomputedSecond = cache.GetOrCompute("d e", "d f");
        Assert.NotSame(second, recomputedSecond);
        Assert.Equal(second.SummaryText, recomputedSecond.SummaryText);
    }

    [Fact]
    public void FormatEditCountSummaryFormatsSingularPluralZeroAndFormattingOnly()
    {
        Assert.Equal("1 edit", HistoryEditsPresentation.FormatEditCountSummary(1));
        Assert.Equal("4 edits", HistoryEditsPresentation.FormatEditCountSummary(4));
        Assert.Equal("Punctuation & casing", HistoryEditsPresentation.FormatEditCountSummary(0, hasTextDifference: true));
        Assert.Equal("No edits", HistoryEditsPresentation.FormatEditCountSummary(0, hasTextDifference: false));
    }
}
