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

/// <summary>Turns the position of a choice in the picker into the choice itself.</summary>
/// <remarks>
/// IN CORE SO IT CAN BE MEASURED, because the alternative was a switch inside a window that no test
/// can reach. A gate could see that the switch READ the picker and could not see whether it read it
/// correctly - a mapping whose every arm returned the ordinary rule would have satisfied it while
/// throwing the person's choice away.
///
/// THE PICKER'S ORDER IS STATED HERE ONCE. It is a contract between this method and three strings in
/// MainWindow.xaml, and a contract stated in two places is a contract that will disagree with itself.
/// A position nobody has chosen arrives as -1, which is the ordinary rule.
/// </remarks>
public static class MatchStrictnessChoice
{
    public static MatchStrictness FromPickerIndex(int index) => index switch
    {
        1 => MatchStrictness.Loose,
        2 => MatchStrictness.Strict,
        _ => MatchStrictness.Default,
    };
}
