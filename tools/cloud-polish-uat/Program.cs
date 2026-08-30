using EnviousWispr.Core.Credentials;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Settings;
using EnviousWispr.LLM;
using EnviousWispr.Services.Credentials;

const string ConsentFlag = "--i-consent-to-send-synthetic-text";
var provider = ParseProvider(ValueAfter("--provider"));
if (provider is null)
{
    Usage();
    return 2;
}

var store = new WindowsCredentialApiKeyStore();
if (args.Contains("--status", StringComparer.OrdinalIgnoreCase))
{
    var status = store.Read(provider.Value).Status;
    Console.WriteLine($"provider={provider.Value} credential={status}");
    return 0;
}

if (args.Contains("--save-key", StringComparer.OrdinalIgnoreCase))
{
    Console.Write("API key (input hidden): ");
    var key = ReadSecret();
    Console.WriteLine();
    if (string.IsNullOrWhiteSpace(key))
    {
        Console.Error.WriteLine("No key supplied; Credential Manager was not changed.");
        return 2;
    }

    store.Store(provider.Value, key);
    Console.WriteLine($"provider={provider.Value} credential=saved");
    return 0;
}

if (args.Contains("--delete-key", StringComparer.OrdinalIgnoreCase))
{
    store.Delete(provider.Value);
    Console.WriteLine($"provider={provider.Value} credential=deleted");
    return 0;
}

if (!args.Contains(ConsentFlag, StringComparer.Ordinal))
{
    Console.Error.WriteLine(
        $"A real provider call was not made. Add {ConsentFlag} to send the fixed synthetic transcript and accept provider charges.");
    Console.Error.WriteLine(CloudPolishConsent.For(provider.Value).Notice);
    return 3;
}

var model = ValueAfter("--model") ?? CloudPolishOptions.DefaultModel(provider.Value);
await using IPolishProvider polisher = provider.Value switch
{
    PolishProvider.OpenAI => new OpenAiPolishProvider(store, model),
    PolishProvider.Anthropic => new AnthropicPolishProvider(store, model),
    PolishProvider.Gemini => new GeminiPolishProvider(store, model),
    _ => throw new InvalidOperationException(),
};
const string syntheticTranscript =
    "so um please move the synthetic test meeting to thursday no wait friday";
var input = new ProcessedText(DictationSessionId.Create(), syntheticTranscript);
var result = await polisher.TryPolishAsync(new PolishRequest(input, "en"));
Console.WriteLine(
    $"provider={provider.Value} model={model} status={result.Status} " +
    $"error={result.Error?.Code.ToString() ?? "none"} elapsed_ms={result.ElapsedMilliseconds} " +
    $"changed={!string.Equals(input.Text, result.Output.Text, StringComparison.Ordinal)}");
return result.UsedFallback ? 1 : 0;

string? ValueAfter(string name)
{
    for (var index = 0; index < args.Length - 1; index++)
    {
        if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
        {
            return args[index + 1];
        }
    }

    return null;
}

static PolishProvider? ParseProvider(string? value) => value?.ToLowerInvariant() switch
{
    "openai" => PolishProvider.OpenAI,
    "anthropic" or "claude" => PolishProvider.Anthropic,
    "gemini" => PolishProvider.Gemini,
    _ => null,
};

static string ReadSecret()
{
    var value = new System.Text.StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
        {
            return value.ToString();
        }

        if (key.Key == ConsoleKey.Backspace && value.Length > 0)
        {
            value.Length--;
        }
        else if (!char.IsControl(key.KeyChar))
        {
            value.Append(key.KeyChar);
        }
    }
}

static void Usage()
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  --provider openai|anthropic|gemini --status");
    Console.Error.WriteLine("  --provider openai|anthropic|gemini --save-key");
    Console.Error.WriteLine("  --provider openai|anthropic|gemini --delete-key");
    Console.Error.WriteLine(
        $"  --provider openai|anthropic|gemini [--model id] {ConsentFlag}");
}
