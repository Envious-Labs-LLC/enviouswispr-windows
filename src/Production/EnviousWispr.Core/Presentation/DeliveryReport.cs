using EnviousWispr.Core.Dictation;

namespace EnviousWispr.Core.Presentation;

/// <summary>Where a take's words ended up, said once from the delivery itself.</summary>
public enum DeliveryLanding
{
    /// <summary>The words went into the window the take was started in.</summary>
    Landed,

    /// <summary>The words are on the clipboard: a copy the person asked for, or a paste refused and kept there.</summary>
    Clipboard,

    /// <summary>The words reached neither the window nor the clipboard; they are held for the person.</summary>
    Failed,
}

/// <summary>What the window is told about one delivery: the sentence to show, and where the words went.</summary>
/// <remarks>
/// WHERE THE WORDS WENT IS READ FROM THE DELIVERY, NEVER FROM THE PILL. Onboarding and the language
/// offer used to ask whether the status was a Success pill; once a landed paste stopped raising a
/// pill, that question would have read every good take as a failure. The delivery is the truth both
/// of them need, so it travels beside the sentence.
///
/// THE SENTENCE IS A METHOD, NOT A PROPERTY, so the status gate (EW0003) can verify it: it is built
/// here by <see cref="DeliveryStatusReport.For"/>, which names its pill through a factory.
/// </remarks>
public sealed class DeliveryReport
{
    private readonly DeliveryResult _result;

    private DeliveryReport(DeliveryResult result, DeliveryLanding landing, bool invitesLanguageOffer)
    {
        _result = result;
        Landing = landing;
        InvitesLanguageOffer = invitesLanguageOffer;
    }

    /// <summary>Where the words ended up.</summary>
    public DeliveryLanding Landing { get; }

    /// <summary>
    /// The words landed cleanly enough that a question about the language is not crowding out news
    /// the person needs: delivered, and the clipboard is not in doubt.
    /// </summary>
    public bool InvitesLanguageOffer { get; }

    /// <summary>The report for one delivery.</summary>
    public static DeliveryReport For(DeliveryResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var landing = result switch
        {
            { Delivered: true, Route: TextDeliveryRoute.ClipboardOnly } => DeliveryLanding.Clipboard,
            { Delivered: true } => DeliveryLanding.Landed,
            { ClipboardFallback: true } => DeliveryLanding.Clipboard,
            _ => DeliveryLanding.Failed,
        };
        return new DeliveryReport(
            result,
            landing,
            invitesLanguageOffer: result is { Delivered: true, ClipboardUncertain: false });
    }

    /// <summary>The sentence for the status line, and the pill when one is due.</summary>
    public DictationStatus Status() => DeliveryStatusReport.For(_result);

    /// <summary>The sentence a suggestion pill carries when it takes this delivery's place.</summary>
    /// <remarks>
    /// THE SUGGESTION CARRIES THE DELIVERY'S SENTENCE ONLY WHEN THAT SENTENCE HAD A PILL OF ITS OWN.
    /// Somebody who asked for the clipboard was going to be told "Copied to your clipboard", and a
    /// question about languages in its place would leave them believing the words were inserted. A
    /// landed paste had no pill to replace, so the question stands alone rather than opening with
    /// "Pasted safely." in front of it.
    /// </remarks>
    public string SuggestionText(string question)
    {
        var status = Status();
        return status.State == DictationOverlayState.Hidden || string.IsNullOrWhiteSpace(status.Text)
            ? question
            : $"{status.Text}. {question}";
    }
}
