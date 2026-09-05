namespace EnviousWispr.ModelDelivery;

/// <summary>
/// Decides which directory a model is loaded from, in the one order the application uses.
/// </summary>
/// <remarks>
/// THE ORDER IS THE CONTRACT. An explicit override wins because a person set it on purpose. A
/// store-activated version comes next because every byte in it was hashed at admission. A legacy
/// directory - files copied by hand into `models/&lt;modelId&gt;` - is accepted only when the probe
/// says it is complete, because the store keeps its own state UNDER that same directory and a
/// half-finished download must never be mistaken for an installed model. The development
/// checkout comes last and only when it, too, is complete.
///
/// Every candidate except the override is asked whether it is COMPLETE rather than whether it
/// EXISTS. The previous resolver returned the first directory that existed, which is how the
/// application came to report "model is not installed" while pointing at a directory that was
/// there. Ref: #92.
/// </remarks>
public static class InstalledModelLocator
{
    public static string? Resolve(
        string? configuredDirectory,
        string? activeStoreDirectory,
        string legacyDirectory,
        string? developmentDirectory,
        Func<string, bool> isComplete)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyDirectory);
        ArgumentNullException.ThrowIfNull(isComplete);

        if (!string.IsNullOrWhiteSpace(configuredDirectory) && Directory.Exists(configuredDirectory))
        {
            return Path.GetFullPath(configuredDirectory);
        }

        if (!string.IsNullOrWhiteSpace(activeStoreDirectory) &&
            Directory.Exists(activeStoreDirectory) &&
            isComplete(activeStoreDirectory))
        {
            return Path.GetFullPath(activeStoreDirectory);
        }

        if (Directory.Exists(legacyDirectory) && isComplete(legacyDirectory))
        {
            return Path.GetFullPath(legacyDirectory);
        }

        if (!string.IsNullOrWhiteSpace(developmentDirectory) &&
            Directory.Exists(developmentDirectory) &&
            isComplete(developmentDirectory))
        {
            return Path.GetFullPath(developmentDirectory);
        }

        return null;
    }
}
