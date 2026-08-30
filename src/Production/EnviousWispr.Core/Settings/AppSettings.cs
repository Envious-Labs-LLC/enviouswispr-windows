namespace EnviousWispr.Core.Settings;

public sealed record AppSettings(
    int SchemaVersion,
    int LaunchCount,
    bool HasCompletedOnboarding,
    UserPreferences Preferences,
    ReusableUserData UserData,
    string? PreferredMicrophoneId = null,
    ObservabilityPreferences? Observability = null,
    /// <summary>The build whose release notes this person has already looked at.</summary>
    /// <remarks>
    /// APP STATE, NOT A PREFERENCE, WHICH IS WHY IT SITS BESIDE HasCompletedOnboarding. Nobody
    /// chooses it; the app records it. Putting it in UserPreferences would carry it into a portable
    /// profile, so importing somebody else's settings would mark YOUR release notes as read.
    ///
    /// NULL MEANS NEVER LOOKED, and on a first run that is the truth: the notes are new to them.
    /// </remarks>
    string? LastSeenReleaseNotes = null,

    /// <summary>How many times each language has been offered as a lock.</summary>
    /// <remarks>
    /// APP STATE, NOT A PREFERENCE, FOR THE SAME REASON AS LastSeenReleaseNotes. Nobody chooses it;
    /// the app records it. In UserPreferences it would travel in a portable profile, so importing
    /// somebody else's settings would silence an offer this person has never seen.
    ///
    /// A PROMISE TO GO QUIET THAT A RESTART UNDOES IS NOT A PROMISE. The offer stops after three
    /// times per language, and holding that only in memory gave every relaunch three more.
    ///
    /// NULL MEANS NOBODY HAS BEEN ASKED ANYTHING, which is the truth on a first run.
    /// </remarks>
    string? LanguageOfferHistory = null)
{
    public const int CurrentSchemaVersion = 15;

    public static AppSettings Default { get; } = new(
        CurrentSchemaVersion,
        LaunchCount: 0,
        HasCompletedOnboarding: false,
        UserPreferences.Default,
        ReusableUserData.Empty,
        PreferredMicrophoneId: null,
        Observability: ObservabilityPreferences.Default);

    public PortableProfile ToPortableProfile() => new(
        PortableProfile.CurrentSchemaVersion,
        Preferences,
        UserData);

    public AppSettings Apply(PortableProfile profile) => this with
    {
        Preferences = profile.Preferences,
        UserData = profile.UserData,
    };
}
