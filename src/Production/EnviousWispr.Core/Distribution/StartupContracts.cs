namespace EnviousWispr.Core.Distribution;

/// <summary>What Windows reports about whether EnviousWispr starts when the user signs in. Ref #42.</summary>
/// <remarks>
/// WINDOWS OWNS THE SWITCH FOR A PACKAGED INSTALL. The package manifest declares the
/// <c>EnviousWisprStartup</c> task disabled by default; once the user turns it off in Windows Settings
/// or Task Manager, Windows reports <see cref="DisabledByUser"/> and refuses an in-app enable request
/// until the user turns it back on under Startup Apps in Windows Settings. Unpackaged builds fall back
/// to the per-user <c>Run</c> registry key.
/// </remarks>
public enum StartupRegistrationStatus
{
    /// <summary>EnviousWispr does not start at sign-in and can be turned on from the app.</summary>
    Disabled,

    /// <summary>EnviousWispr starts at sign-in and can be turned off from the app.</summary>
    Enabled,

    /// <summary>The user turned startup off in Windows Settings or Task Manager; only Windows Settings can turn it back on.</summary>
    DisabledByUser,

    /// <summary>A Windows or workplace policy turned startup off; the app cannot change it.</summary>
    DisabledByPolicy,

    /// <summary>A Windows or workplace policy turned startup on; the app cannot change it.</summary>
    EnabledByPolicy,

    /// <summary>Windows could not read or change the startup registration.</summary>
    Failed,
}

/// <param name="Status">What Windows reported after reading or changing the startup registration.</param>
/// <param name="IsPackaged">Whether this copy uses the packaged Windows startup task rather than the unpackaged Run key fallback.</param>
public sealed record StartupRegistrationResult(
    StartupRegistrationStatus Status,
    bool IsPackaged)
{
    public bool IsEnabled =>
        Status is StartupRegistrationStatus.Enabled or StartupRegistrationStatus.EnabledByPolicy;

    public bool CanToggle =>
        Status is StartupRegistrationStatus.Disabled
            or StartupRegistrationStatus.Enabled
            or StartupRegistrationStatus.Failed;

    public bool NeedsWindowsStartupSettings =>
        Status == StartupRegistrationStatus.DisabledByUser;
}
