using EnviousWispr.Core.Dictation;
using EnviousWispr.LLM;

const string syntheticTranscript =
    "so um move the synthetic meeting to thursday no wait friday and email the synthetic notes";
var endpoint = ArgumentValue(args, "--endpoint");
var selectedModelIds = ArgumentValue(args, "--models")?
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
if (!OllamaEndpointPolicy.TryNormalize(endpoint, out var normalizedEndpoint))
{
    Console.Error.WriteLine("FAIL endpoint must be a loopback HTTP or HTTPS address");
    return 2;
}

await using var catalog = new OllamaApiClient(endpoint);
var discovery = await catalog.DiscoverAsync();
Console.WriteLine(
    $"endpoint={normalizedEndpoint} health={discovery.Health} local_models={discovery.LocalModels.Count} remote_models_refused={discovery.RemoteModelIds.Count}");
if (discovery.Health != OllamaHealth.Ready)
{
    Console.WriteLine("UNOBSERVED no ready local Ollama model is available");
    return 3;
}

var models = selectedModelIds is null
    ? discovery.LocalModels.ToArray()
    : discovery.LocalModels
        .Where(model => selectedModelIds.Contains(model.Id, StringComparer.OrdinalIgnoreCase))
        .ToArray();
if (models.Length == 0)
{
    Console.Error.WriteLine("FAIL none of the requested --models are installed local chat models");
    return 2;
}

var failures = 0;
foreach (var model in models)
{
    await using var provider = new OllamaPolishProvider(new OllamaPolishOptions(endpoint, model.Id));
    var input = new ProcessedText(DictationSessionId.Create(), syntheticTranscript);
    var result = await provider.TryPolishAsync(new PolishRequest(input, "en"));
    var safeFallback = !result.UsedFallback || result.Output == input;
    var semanticPass = !result.UsedFallback &&
        result.Output.Text.Contains("friday", StringComparison.OrdinalIgnoreCase) &&
        !result.Output.Text.Contains("thursday", StringComparison.OrdinalIgnoreCase) &&
        !result.Output.Text.Contains("no wait", StringComparison.OrdinalIgnoreCase) &&
        !ContainsWholeWord(result.Output.Text, "um");
    Console.WriteLine(
        $"model={model.Id} thinks={model.SupportsThinking?.ToString() ?? "unknown"} status={result.Status} error={result.Error?.Code.ToString() ?? "none"} elapsed_ms={result.ElapsedMilliseconds} semantic_pass={semanticPass} safe_fallback={safeFallback}");
    if (!semanticPass || !safeFallback)
    {
        failures++;
    }
}

Console.WriteLine(failures == 0 ? "PASS all local models" : $"FAIL models={failures}");
return failures == 0 ? 0 : 1;

static string? ArgumentValue(string[] arguments, string name)
{
    for (var index = 0; index < arguments.Length - 1; index++)
    {
        if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
        {
            return arguments[index + 1];
        }
    }

    return null;
}

static bool ContainsWholeWord(string text, string word)
{
    var index = text.IndexOf(word, StringComparison.OrdinalIgnoreCase);
    while (index >= 0)
    {
        var beforeBoundary = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
        var after = index + word.Length;
        var afterBoundary = after == text.Length || !char.IsLetterOrDigit(text[after]);
        if (beforeBoundary && afterBoundary)
        {
            return true;
        }

        index = text.IndexOf(word, index + 1, StringComparison.OrdinalIgnoreCase);
    }

    return false;
}
