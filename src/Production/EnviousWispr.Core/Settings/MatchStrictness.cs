namespace EnviousWispr.Core.Settings;

/// <summary>
/// How close a heard word has to be to a custom word before it is corrected.
/// </summary>
/// <remarks>
/// PER WORD, BECAUSE ONE SETTING FOR ALL IS WRONG IN BOTH DIRECTIONS: a short surname heard several
/// ways wants a generous match, and a word that resembles ordinary English wants a strict one or it
/// corrects sentences it was never meant to touch.
///
/// DEFAULT IS ZERO ON PURPOSE. Settings written before this existed carry no strictness, so they
/// deserialize to zero - which must mean the behaviour those words already had.
/// </remarks>
public enum MatchStrictness
{
    /// <summary>Correct this word the way every word was corrected before this choice existed.</summary>
    Default = 0,

    /// <summary>Correct this word even when what was heard is some way off.</summary>
    Loose = 1,

    /// <summary>Correct this word only when what was heard is nearly the word itself.</summary>
    Strict = 2,
}

/// <summary>Plain-English display labels for <see cref="MatchStrictness"/> choices.</summary>
public static class MatchStrictnessFormatting
{
    public static readonly IReadOnlyList<(MatchStrictness Value, string Label)> Options =
    [
        (MatchStrictness.Default, Label(MatchStrictness.Default)),
        (MatchStrictness.Loose, Label(MatchStrictness.Loose)),
        (MatchStrictness.Strict, Label(MatchStrictness.Strict)),
    ];

    public static string Label(MatchStrictness strictness) => strictness switch
    {
        MatchStrictness.Loose => "Loose (match similar-sounding words)",
        MatchStrictness.Strict => "Strict (exact match only)",
        _ => "Balanced (recommended)",
    };
}
