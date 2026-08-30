namespace EnviousWispr.Core.Settings;

/// <summary>
/// How close a heard word has to be to a custom word before it is corrected.
/// </summary>
/// <remarks>
/// ONE SETTING FOR EVERY WORD IS WRONG IN BOTH DIRECTIONS AT ONCE. A short surname is heard several
/// ways and wants a generous match; a word that looks like an ordinary English word wants a mean
/// one, or it eats sentences it was never meant to touch. A single number has to be a compromise
/// between those two, and the compromise is worse for both than either would choose.
///
/// DEFAULT IS ZERO ON PURPOSE. Settings written before this existed have no strictness in them at
/// all, so the value they deserialize to is whatever zero means - and the only honest answer for a
/// word somebody added without being asked this question is the behaviour they already had.
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
