namespace EnviousWispr.Core.Distribution;

public enum ReleaseChannel
{
    Stable,
    Founder,
    Beta,
}

public sealed record ReleaseIdentity(
    ReleaseChannel Channel,
    string ChannelName,
    string DataDirectoryName,
    string SingleInstanceKey,
    string DisplayName)
{
    public static ReleaseIdentity Stable { get; } = For(ReleaseChannel.Stable);

    public static bool TryParse(string? value, out ReleaseIdentity identity)
    {
        var channel = value?.Trim().ToLowerInvariant() switch
        {
            "stable" or "win-x64-stable" => ReleaseChannel.Stable,
            "founder" or "win-x64-founder" => ReleaseChannel.Founder,
            "beta" or "win-x64-beta" => ReleaseChannel.Beta,
            _ => (ReleaseChannel?)null,
        };

        identity = channel is { } parsed ? For(parsed) : Stable;
        return channel is not null;
    }

    public static ReleaseIdentity For(ReleaseChannel channel) => channel switch
    {
        ReleaseChannel.Stable => new ReleaseIdentity(
            channel,
            "win-x64-stable",
            "EnviousWispr",
            "EnviousLabs.EnviousWispr.Production.Stable",
            "EnviousWispr"),
        ReleaseChannel.Founder => new ReleaseIdentity(
            channel,
            "win-x64-founder",
            "EnviousWispr-Founder",
            "EnviousLabs.EnviousWispr.Production.Founder",
            "EnviousWispr Founder"),
        ReleaseChannel.Beta => new ReleaseIdentity(
            channel,
            "win-x64-beta",
            "EnviousWispr-Beta",
            "EnviousLabs.EnviousWispr.Production.Beta",
            "EnviousWispr Beta"),
        _ => throw new ArgumentOutOfRangeException(nameof(channel)),
    };
}
