using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// The app notices it keeps hearing one language, and offers to stop guessing.
/// </summary>
/// <remarks>
/// macOS OFFERS THIS AND WINDOWS SAID NOTHING. Somebody dictating Spanish on automatic detection got
/// a fresh guess every single time, with no hint that pinning the language exists.
/// </remarks>
public sealed class LanguageLockSuggesterTests
{
    [Fact]
    public void OneSentenceInAnotherLanguageIsNotEnoughToAskAboutIt()
    {
        var suggester = new LanguageLockSuggester();

        Assert.Null(suggester.Observe("es", WhisperLanguagePreference.Automatic));
        Assert.Null(suggester.Observe("es", WhisperLanguagePreference.Automatic));
    }

    [Fact]
    public void HearingTheSameLanguageThreeTimesOffersToPinIt()
    {
        var suggester = new LanguageLockSuggester();

        suggester.Observe("es", WhisperLanguagePreference.Automatic);
        suggester.Observe("es", WhisperLanguagePreference.Automatic);
        var offer = suggester.Observe("es", WhisperLanguagePreference.Automatic);

        Assert.NotNull(offer);
        Assert.Equal(WhisperLanguagePreference.Spanish, offer.Language);
        Assert.Equal("Spanish", offer.DisplayName);
        Assert.Equal(LanguageOfferKind.AskToLock, offer.Kind);
    }

    [Fact]
    public void TheRegionAWordCameFromDoesNotBreakTheRun()
    {
        // Recognition may answer es-ES one moment and es the next, and those are the same language.
        var suggester = new LanguageLockSuggester();

        suggester.Observe("es-ES", WhisperLanguagePreference.Automatic);
        suggester.Observe("ES", WhisperLanguagePreference.Automatic);
        var offer = suggester.Observe("es_419", WhisperLanguagePreference.Automatic);

        Assert.Equal(WhisperLanguagePreference.Spanish, offer?.Language);
    }

    [Fact]
    public void AnEnglishSentenceInTheMiddleDoesNotStartTheCountOver()
    {
        // Somebody working in French who says one English sentence has not stopped working in French.
        var suggester = new LanguageLockSuggester();

        suggester.Observe("fr", WhisperLanguagePreference.Automatic);
        Assert.Null(suggester.Observe("en", WhisperLanguagePreference.Automatic));
        suggester.Observe("fr", WhisperLanguagePreference.Automatic);
        var offer = suggester.Observe("fr", WhisperLanguagePreference.Automatic);

        Assert.Equal(WhisperLanguagePreference.French, offer?.Language);
    }

    [Fact]
    public void ADifferentLanguageStartsTheCountOver()
    {
        var suggester = new LanguageLockSuggester();

        suggester.Observe("de", WhisperLanguagePreference.Automatic);
        suggester.Observe("de", WhisperLanguagePreference.Automatic);
        suggester.Observe("fr", WhisperLanguagePreference.Automatic);
        Assert.Null(suggester.Observe("de", WhisperLanguagePreference.Automatic));
    }

    [Fact]
    public void TheOfferDoesNotComeBackOnTheVerySentenceAfterIt()
    {
        var suggester = new LanguageLockSuggester();

        for (var index = 0; index < LanguageLockSuggester.RunLength; index++)
        {
            suggester.Observe("es", WhisperLanguagePreference.Automatic);
        }

        Assert.Null(suggester.Observe("es", WhisperLanguagePreference.Automatic));
    }

    [Fact]
    public void TheThirdOfferStopsAskingAndSaysWhereTheSettingIs()
    {
        var suggester = new LanguageLockSuggester();

        var kinds = new List<LanguageOfferKind>();
        for (var offer = 0; offer < LanguageLockSuggester.OffersPerLanguage; offer++)
        {
            kinds.Add(RunToOffer(suggester, "es")!.Kind);
        }

        Assert.Equal(
            [LanguageOfferKind.AskToLock, LanguageOfferKind.AskToLock, LanguageOfferKind.PointAtSettings],
            kinds);
    }

    [Fact]
    public void AfterTheLastOfferTheAppStopsBringingItUp()
    {
        var suggester = new LanguageLockSuggester();

        for (var offer = 0; offer < LanguageLockSuggester.OffersPerLanguage; offer++)
        {
            RunToOffer(suggester, "es");
        }

        Assert.Null(RunToOffer(suggester, "es"));
    }

    [Fact]
    public void APinnedLanguageIsNeverAskedAbout()
    {
        // Somebody who has chosen a language has answered this question already.
        var suggester = new LanguageLockSuggester();

        Assert.Null(RunToOffer(suggester, "es", WhisperLanguagePreference.French));
    }

    [Fact]
    public void NoOfferIsMadeForALanguageTheSettingCannotHold()
    {
        // Recognition can name any language it likes; the setting holds five. A button that would do
        // nothing is worse than no button.
        var suggester = new LanguageLockSuggester();

        Assert.Null(RunToOffer(suggester, "it"));
        Assert.Null(RunToOffer(suggester, "ja"));
    }

    [Fact]
    public void SilenceIsNotALanguage()
    {
        var suggester = new LanguageLockSuggester();

        Assert.Null(suggester.Observe(null, WhisperLanguagePreference.Automatic));
        Assert.Null(suggester.Observe("  ", WhisperLanguagePreference.Automatic));

        // A dictation recognition could not name has not moved the count in either direction, so
        // three Spanish sentences after it are still three Spanish sentences.
        Assert.NotNull(RunToOffer(suggester, "es"));
    }

    [Fact]
    public void PinningALanguageClearsWhatWasCountedAgainstIt()
    {
        var suggester = new LanguageLockSuggester();
        for (var offer = 0; offer < LanguageLockSuggester.OffersPerLanguage; offer++)
        {
            RunToOffer(suggester, "es");
        }

        suggester.Accepted(WhisperLanguagePreference.Spanish);

        Assert.Equal(LanguageOfferKind.AskToLock, RunToOffer(suggester, "es")?.Kind);
    }

    [Fact]
    public void GoingQuietSurvivesARestart()
    {
        // A promise to stop asking that a relaunch undoes is not a promise.
        var first = new LanguageLockSuggester();
        for (var offer = 0; offer < LanguageLockSuggester.OffersPerLanguage; offer++)
        {
            RunToOffer(first, "es");
        }

        var second = new LanguageLockSuggester(first.OfferHistory);

        Assert.Null(RunToOffer(second, "es"));
    }

    [Fact]
    public void APartlyUsedLanguageComesBackWhereItLeftOff()
    {
        var first = new LanguageLockSuggester();
        RunToOffer(first, "fr");
        RunToOffer(first, "fr");

        var second = new LanguageLockSuggester(first.OfferHistory);

        Assert.Equal(LanguageOfferKind.PointAtSettings, RunToOffer(second, "fr")?.Kind);
        Assert.Null(RunToOffer(second, "fr"));
    }

    [Fact]
    public void TheRunItselfIsNotCarriedAcross()
    {
        // Two sentences in Spanish yesterday are not two thirds of a reason to ask today.
        var first = new LanguageLockSuggester();
        first.Observe("es", WhisperLanguagePreference.Automatic);
        first.Observe("es", WhisperLanguagePreference.Automatic);

        var second = new LanguageLockSuggester(first.OfferHistory);

        Assert.Null(second.Observe("es", WhisperLanguagePreference.Automatic));
    }

    [Fact]
    public void PinningALanguageIsWrittenDownAsForgivingIt()
    {
        var first = new LanguageLockSuggester();
        for (var offer = 0; offer < LanguageLockSuggester.OffersPerLanguage; offer++)
        {
            RunToOffer(first, "es");
        }

        first.Accepted(WhisperLanguagePreference.Spanish);
        var second = new LanguageLockSuggester(first.OfferHistory);

        Assert.Equal(LanguageOfferKind.AskToLock, RunToOffer(second, "es")?.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("es:")]
    [InlineData("es:notanumber")]
    [InlineData(":3")]
    [InlineData("es:-4")]
    [InlineData("es:0")]
    [InlineData("es:4")]
    [InlineData("es:99")]
    [InlineData("es:1:2")]
    [InlineData("es:3|es:1")]
    [InlineData("es:3|es:3")]
    [InlineData("es:3|fr")]
    [InlineData("es:3|")]
    [InlineData("|es:3")]
    [InlineData("it:2")]
    [InlineData("en:2")]
    public void AHistoryNobodyCanReadIsDroppedWholeRatherThanInPart(string history)
    {
        // ALL OF IT OR NONE OF IT. Reading what parsed and skipping the rest turned "es:3|es:1" into
        // a Spanish count of one - a number nothing ever wrote, standing in for a promise this
        // person may well have earned. A wrong count silently changes what they are owed; a missing
        // one costs at most three more offers.
        var suggester = new LanguageLockSuggester(history);

        Assert.Equal(string.Empty, suggester.OfferHistory);
        Assert.NotNull(RunToOffer(suggester, "es"));
        Assert.NotNull(RunToOffer(suggester, "fr"));
    }

    [Fact]
    public void AHistoryThatIsEntirelyReadableIsKeptEntirely()
    {
        var suggester = new LanguageLockSuggester("de:1|es:3");

        Assert.Null(RunToOffer(suggester, "es"));
        Assert.Equal(LanguageOfferKind.AskToLock, RunToOffer(suggester, "de")?.Kind);
    }

    [Fact]
    public void NothingToRememberIsWrittenAsNothing()
    {
        Assert.Equal(string.Empty, new LanguageLockSuggester().OfferHistory);
    }

    private static LanguageLockOffer? RunToOffer(
        LanguageLockSuggester suggester,
        string language,
        WhisperLanguagePreference current = WhisperLanguagePreference.Automatic)
    {
        LanguageLockOffer? last = null;
        for (var index = 0; index < LanguageLockSuggester.RunLength; index++)
        {
            last = suggester.Observe(language, current);
        }

        return last;
    }
}
