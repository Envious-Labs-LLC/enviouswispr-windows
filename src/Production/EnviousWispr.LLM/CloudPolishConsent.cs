using EnviousWispr.Core.Settings;

namespace EnviousWispr.LLM;

public sealed record CloudPolishConsent(
    PolishProvider Provider,
    string ProviderName,
    string ApiHost,
    string Notice)
{
    public static CloudPolishConsent For(PolishProvider provider)
    {
        var (name, host) = provider switch
        {
            PolishProvider.OpenAI => ("OpenAI", "api.openai.com"),
            PolishProvider.Anthropic => ("Anthropic", "api.anthropic.com"),
            PolishProvider.Gemini => ("Google Gemini", "generativelanguage.googleapis.com"),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Not a cloud provider."),
        };
        return new CloudPolishConsent(
            provider,
            name,
            host,
            $"{name} polish sends your transcribed text directly to {name} using your API key. " +
            "Audio never leaves this PC, and Envious Labs never receives the request.");
    }

    public static CloudPolishConsent ForOllamaHosted(string? modelId = null) =>
        new(
            PolishProvider.Ollama,
            "Ollama Cloud",
            "ollama.com",
            string.IsNullOrWhiteSpace(modelId)
                ? "Hosted Ollama polish sends your transcribed text to Ollama's servers. Audio never leaves this PC, and Envious Labs never receives the request."
                : $"Hosted Ollama polish ({modelId.Trim()}) sends your transcribed text to Ollama's servers. Audio never leaves this PC, and Envious Labs never receives the request.");
}
