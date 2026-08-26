using EnviousWispr.Core.Errors;

namespace EnviousWispr.Core.Settings;

public sealed record SettingsLoadResult(
    AppSettings Settings,
    SettingsLoadStatus Status,
    AppError? Error = null,
    int? SourceSchemaVersion = null);
