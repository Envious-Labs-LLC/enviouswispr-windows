using EnviousWispr.Core.Settings;

namespace EnviousWispr.LLM;

public sealed record CloudPolishOptions(
    PolishProvider Provider,
    string ModelId,
    TimeSpan? RequestTimeout = null)
{
    public TimeSpan EffectiveRequestTimeout => RequestTimeout ?? TimeSpan.FromSeconds(20);

    public static string DefaultModel(PolishProvider provider) => provider switch
    {
        PolishProvider.OpenAI => "gpt-4o-mini",
        PolishProvider.Anthropic => "claude-haiku-4-5",
        PolishProvider.Gemini => "gemini-3.7-flash",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Not a cloud provider."),
    };

    public static bool ModelIdLooksLikeProvider(string? modelId, PolishProvider provider)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return false;
        }

        var id = modelId.ToLowerInvariant();
        return provider switch
        {
            PolishProvider.OpenAI =>
                id.StartsWith("gpt-", StringComparison.Ordinal) ||
                id.StartsWith("o-", StringComparison.Ordinal) ||
                id.StartsWith("o1", StringComparison.Ordinal) ||
                id.StartsWith("o3", StringComparison.Ordinal) ||
                id.StartsWith("o4", StringComparison.Ordinal) ||
                id.StartsWith("chatgpt-", StringComparison.Ordinal),
            PolishProvider.Anthropic => id.StartsWith("claude-", StringComparison.Ordinal),
            PolishProvider.Gemini => id.StartsWith("gemini-", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Not a cloud provider."),
        };
    }
}
