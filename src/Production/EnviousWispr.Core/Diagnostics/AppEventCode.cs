namespace EnviousWispr.Core.Diagnostics;

public enum AppEventCode
{
    ApplicationStarting,
    DuplicateInstanceRejected,
    SettingsLoaded,
    SettingsCreated,
    SettingsMigrated,
    SettingsRecovered,
    SettingsReset,
    SettingsNewerVersionPreserved,
    ShellShown,
    ShellClosed,
    UnhandledFailure,
}
