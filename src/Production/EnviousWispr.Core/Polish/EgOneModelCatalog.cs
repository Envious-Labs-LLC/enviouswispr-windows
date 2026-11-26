namespace EnviousWispr.Core.Polish;

/// <summary>Canonical identifiers and normalization helpers for built-in EG-1 polish sizes.</summary>
public static class EgOneModelCatalog
{
    public const string FullId = "eg-1";
    public const string MiniId = "eg-1-mini";
    public const string MicroId = "eg-1-micro";

    public static IReadOnlyList<string> AllIds { get; } = [FullId, MiniId, MicroId];

    public static bool TryNormalize(string? modelId, out string normalized)
    {
        var trimmed = modelId?.Trim();
        if (string.Equals(trimmed, FullId, StringComparison.OrdinalIgnoreCase))
        {
            normalized = FullId;
            return true;
        }

        if (string.Equals(trimmed, MiniId, StringComparison.OrdinalIgnoreCase))
        {
            normalized = MiniId;
            return true;
        }

        if (string.Equals(trimmed, MicroId, StringComparison.OrdinalIgnoreCase))
        {
            normalized = MicroId;
            return true;
        }

        normalized = string.Empty;
        return false;
    }

    public static bool IsKnown(string? modelId) => TryNormalize(modelId, out _);

    public static string NormalizeOrDefault(string? modelId) =>
        TryNormalize(modelId, out var normalized) ? normalized : FullId;

    public static int DefaultContextTokens(string? modelId) =>
        NormalizeOrDefault(modelId) switch
        {
            MicroId => 8_192,
            _ => 16_384,
        };
}
