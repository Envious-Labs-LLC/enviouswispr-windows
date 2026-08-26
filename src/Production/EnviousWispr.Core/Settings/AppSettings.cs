namespace EnviousWispr.Core.Settings;

public sealed record AppSettings(
    int SchemaVersion,
    int LaunchCount,
    bool HasCompletedOnboarding,
    UserPreferences Preferences,
    ReusableUserData UserData,
    string? PreferredMicrophoneId = null)
{
    public const int CurrentSchemaVersion = 5;

    public static AppSettings Default { get; } = new(
        CurrentSchemaVersion,
        LaunchCount: 0,
        HasCompletedOnboarding: false,
        UserPreferences.Default,
        ReusableUserData.Empty,
        PreferredMicrophoneId: null);

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
