using EnviousWispr.Core.Settings;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// A shipped word list has to be readable by the same importer a user's own file goes through, and
/// every row in it has to be worth a row in someone's list.
/// </summary>
public sealed class VocabularyPackTests
{
    /// <summary>
    /// A pack that installed by a special route would be a second implementation of merging words,
    /// and the two would drift. This is what pins them to one path.
    /// </summary>
    [Fact]
    public void EveryPackParsesWithTheSameReaderAUserFileUses()
    {
        foreach (var pack in VocabularyPacks.All)
        {
            var plan = CustomWordImport.Read(pack.Words, []);

            Assert.Equal(0, plan.UnreadableCount);
            Assert.Equal(0, plan.ConflictCount);
            Assert.NotEmpty(plan.Additions);
        }
    }

    /// <summary>
    /// The control for the test above. The reader must be capable of reporting an unreadable line,
    /// or "no unreadable lines" would be true of a reader that had stopped checking.
    /// </summary>
    [Fact]
    public void TheReaderUsedAboveCanStillRejectABadLine()
    {
        Assert.Equal(1, CustomWordImport.Read("no separator here", []).UnreadableCount);
    }

    /// <summary>
    /// A pack listing one spoken form twice would install one row and report a conflict on the
    /// other, so the user would be warned about a collision inside a list we shipped them.
    /// </summary>
    [Fact]
    public void NoPackCollidesWithItself()
    {
        foreach (var pack in VocabularyPacks.All)
        {
            var spokenForms = CustomWordImport.Read(pack.Words, [])
                .Additions
                .Select(entry => entry.SpokenForm)
                .ToArray();

            Assert.Equal(spokenForms.Length, spokenForms.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
    }

    /// <summary>
    /// Installing every pack at once must not produce a conflict either. Two packs disagreeing about
    /// one word would report a collision the user cannot resolve, in content we wrote.
    /// </summary>
    [Fact]
    public void NoTwoPacksDisagreeAboutTheSameWord()
    {
        var installed = new List<CustomWordEntry>();
        foreach (var pack in VocabularyPacks.All)
        {
            var plan = CustomWordImport.Read(pack.Words, installed);
            Assert.Equal(0, plan.ConflictCount);
            installed.AddRange(plan.Additions);
        }
    }

    /// <summary>
    /// A row whose spoken form is byte-identical to its replacement corrects nothing. It would sit
    /// in the user's list forever, never firing, making it harder for them to see which of their
    /// OWN corrections still matter - the cost that makes a padded pack worse than no pack.
    /// </summary>
    /// <remarks>
    /// ORDINAL, NOT CASE-INSENSITIVE, and the first version of this test got that wrong. It flagged
    /// "parakeet -> Parakeet", "json -> JSON" and "kubernetes -> Kubernetes" as useless, when
    /// capitalisation is exactly what those rows exist to fix - a recogniser hears the word
    /// correctly and writes it in lower case. Comparing without case makes the test unable to see
    /// the most common kind of correction a pack contains.
    ///
    /// The failing run is what produced this distinction. The pack was right and the test was
    /// stricter than the thing it was checking.
    /// </remarks>
    [Fact]
    public void NoRowCorrectsAWordToItself()
    {
        foreach (var pack in VocabularyPacks.All)
        {
            var useless = CustomWordImport.Read(pack.Words, [])
                .Additions
                .Where(entry => string.Equals(
                    entry.SpokenForm,
                    entry.Replacement,
                    StringComparison.Ordinal))
                .Select(entry => entry.SpokenForm)
                .ToArray();

            Assert.True(
                useless.Length == 0,
                $"{pack.Name} contains rows that correct a word to itself: {string.Join(", ", useless)}");
        }
    }

    [Fact]
    public void EveryPackHasAnIdentityAUserCanSee()
    {
        foreach (var pack in VocabularyPacks.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(pack.Id));
            Assert.False(string.IsNullOrWhiteSpace(pack.Name));
            Assert.False(string.IsNullOrWhiteSpace(pack.Description));
        }

        var ids = VocabularyPacks.All.Select(pack => pack.Id).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }
}
