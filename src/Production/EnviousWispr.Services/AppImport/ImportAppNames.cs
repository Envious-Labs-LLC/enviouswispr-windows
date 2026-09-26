namespace EnviousWispr.Services.AppImport;

/// <summary>The app names for a "none found" sentence, joined the way a person writes a list (macOS <c>SmartImportSupportedAppsCopy</c>).</summary>
public static class ImportAppNames
{
    public static string Join(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return names.Count switch
        {
            0 => string.Empty,
            1 => names[0],
            2 => $"{names[0]} and {names[1]}",
            _ => string.Join(", ", names.Take(names.Count - 1)) + ", and " + names[^1],
        };
    }
}
