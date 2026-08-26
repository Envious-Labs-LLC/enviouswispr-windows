namespace EnviousWispr.Core.Settings;

public enum SettingsLoadStatus
{
    Loaded,
    Missing,
    Migrated,
    Invalid,
    NewerVersion,
    Unavailable,
}
