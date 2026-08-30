using EnviousWispr.Core.Settings;

namespace EnviousWispr.Core.Presentation;

/// <summary>What the offer asks the person to do.</summary>
public enum LanguageOfferKind
{
    /// <summary>Offer to pin the language from the pill itself.</summary>
    AskToLock,

    /// <summary>Say where the setting lives, and stop offering to do it for them.</summary>
    /// <remarks>
    /// THE LAST TIME OF ASKING. Somebody who has let two offers go by is not answering the question,
    /// so the third one stops asking it and tells them where the control is instead. macOS calls the
    /// same state "educate about Settings" and reaches it on the same count.
    /// </remarks>
    PointAtSettings,
}

/// <summary>One offer to pin the language the app keeps hearing.</summary>
/// <param name="Language">The preference that would be saved.</param>
/// <param name="DisplayName">What that language is called on screen.</param>
/// <param name="Kind">Whether this offer still asks, or only points.</param>
public sealed record LanguageLockOffer(
    WhisperLanguagePreference Language,
    string DisplayName,
    LanguageOfferKind Kind);

/// <summary>
/// Notices that the same language keeps being heard, and offers to pin it.
/// </summary>
/// <remarks>
/// IT NEVER CHANGES THE LANGUAGE. The whole feature is one sentence and one button; the setting
/// moves only when the person presses it. That is the property worth stating first, because a
/// detector that quietly switched recognition mid-use would be a different and much worse feature.
///
/// WHAT WINDOWS CANNOT COPY FROM macOS, and it is one thing: the confidence. macOS requires its high
/// tier AND a probability of at least 0.85 before an utterance counts toward the run. The Whisper
/// runtime here reports the language it decided on and no number beside it, so the confidence half
/// of the bar cannot be applied. The run length carries the whole weight instead, which is why it is
/// the macOS value of three rather than something shorter.
///
/// AND ONE THING IT DELIBERATELY REFUSES: an offer to pin a language the app cannot pin. Recognition
/// can name any language it likes, while the setting holds five, so a run of Italian reaches the end
/// of this class and stops there rather than showing a button that would do nothing.
///
/// THE RUN IS FORGOTTEN ON RELAUNCH AND THE OFFERS ARE NOT, matching macOS, where the detector's
/// consecutive count is in memory and the presenter's per-language history is written down. The
/// split is the meaningful one: a run of three sentences is about the last few minutes, while "we
/// have asked this person about Spanish three times" is a fact about them, and a promise to go quiet
/// that a restart undoes is not a promise.
/// </remarks>
public sealed class LanguageLockSuggester
{
    /// <summary>How many times in a row the same language must be heard before the first offer.</summary>
    public const int RunLength = 3;

    /// <summary>How many offers one language gets before it goes quiet.</summary>
    public const int OffersPerLanguage = 3;

    private readonly Dictionary<string, int> _runs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _offers = new(StringComparer.Ordinal);

    /// <summary>Starts with no history, as a first run has none.</summary>
    public LanguageLockSuggester()
    {
    }

    /// <summary>Starts from what was written down last time.</summary>
    /// <param name="offerHistory">
    /// A value from <see cref="OfferHistory"/>, or null on a first run. Anything unreadable is
    /// treated as no history: the worst that costs is one more offer, and refusing to start over a
    /// malformed settings field would be a dictation app that will not dictate.
    /// </param>
    public LanguageLockSuggester(string? offerHistory)
    {
        foreach (var entry in (offerHistory ?? string.Empty).Split(
            '|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = entry.LastIndexOf(':');
            if (separator <= 0 || !int.TryParse(
                entry.AsSpan(separator + 1),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var made))
            {
                continue;
            }

            var code = NormalizedBase(entry[..separator]);
            if (code.Length > 0 && made > 0)
            {
                _offers[code] = Math.Min(made, OffersPerLanguage);
            }
        }
    }

    /// <summary>How many offers each language has had, in a form that can be written down.</summary>
    /// <remarks>
    /// THE RUN IS NOT IN HERE. It describes the last few sentences and means nothing tomorrow, so
    /// writing it down would restore a two-thirds-complete run to somebody who has since said
    /// nothing at all.
    /// </remarks>
    public string OfferHistory => string.Join(
        '|',
        _offers.Where(entry => entry.Value > 0)
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => $"{entry.Key}:{entry.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));

    /// <summary>Takes one finished dictation's detected language and answers with an offer or nothing.</summary>
    /// <param name="detectedLanguage">What recognition said, or null when it said nothing.</param>
    /// <param name="current">The language setting as it stands right now.</param>
    public LanguageLockOffer? Observe(string? detectedLanguage, WhisperLanguagePreference current)
    {
        // ALREADY PINNED MEANS THERE IS NOTHING TO OFFER. Somebody who has chosen a language has
        // answered this question, and asking again would be the app arguing with them.
        if (current != WhisperLanguagePreference.Automatic)
        {
            return null;
        }

        var heard = NormalizedBase(detectedLanguage);
        if (heard.Length == 0)
        {
            return null;
        }

        // ENGLISH IS INVISIBLE HERE, and invisible means it does not break a run either. Somebody
        // working in Spanish who says one English sentence has not stopped working in Spanish.
        if (heard == "en")
        {
            return null;
        }

        var next = (_runs.TryGetValue(heard, out var seen) ? seen : 0) + 1;
        _runs.Clear();
        _runs[heard] = next;
        if (next < RunLength)
        {
            return null;
        }

        // THE RUN IS SPENT WHETHER OR NOT AN OFFER COMES OF IT. Leaving it at three would fire again
        // on the very next sentence, which is a nag rather than a suggestion.
        _runs[heard] = 0;
        if (!TryPin(heard, out var language))
        {
            return null;
        }

        var made = _offers.TryGetValue(heard, out var count) ? count : 0;
        if (made >= OffersPerLanguage)
        {
            return null;
        }

        _offers[heard] = made + 1;
        return new LanguageLockOffer(
            language,
            DisplayName(language),
            made + 1 >= OffersPerLanguage
                ? LanguageOfferKind.PointAtSettings
                : LanguageOfferKind.AskToLock);
    }

    /// <summary>The person pinned a language, so this one starts over with a clean slate.</summary>
    public void Accepted(WhisperLanguagePreference language)
    {
        var code = WhisperLanguageCodes.For(language);
        _runs.Remove(code);
        _offers.Remove(code);
    }

    /// <summary>Everything the person has been shown is forgotten.</summary>
    public void Reset()
    {
        _runs.Clear();
        _offers.Clear();
    }

    /// <summary>What a language is called on the pill.</summary>
    public static string DisplayName(WhisperLanguagePreference language) => language switch
    {
        WhisperLanguagePreference.English => "English",
        WhisperLanguagePreference.French => "French",
        WhisperLanguagePreference.German => "German",
        WhisperLanguagePreference.Spanish => "Spanish",
        _ => "Automatic detection",
    };

    private static bool TryPin(string code, out WhisperLanguagePreference language)
    {
        language = code switch
        {
            "fr" => WhisperLanguagePreference.French,
            "de" => WhisperLanguagePreference.German,
            "es" => WhisperLanguagePreference.Spanish,
            _ => WhisperLanguagePreference.Automatic,
        };
        return language != WhisperLanguagePreference.Automatic;
    }

    private static string NormalizedBase(string? language)
    {
        var trimmed = language?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return string.Empty;
        }

        var separator = trimmed.AsSpan().IndexOfAny('-', '_');
        var head = separator < 0 ? trimmed : trimmed[..separator];
        return head.ToLowerInvariant();
    }
}
