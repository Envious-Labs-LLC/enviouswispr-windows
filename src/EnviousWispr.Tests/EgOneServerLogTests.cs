using EnviousWispr.Polish;

namespace EnviousWispr.Tests;

public sealed class EgOneServerLogTests
{
    [Fact]
    public void ApiKeyIsRedactedFromLoggedArguments()
    {
        const string secret = "live-secret-value";
        var logged = EgOneServer.FormatArgumentsForLog(
            ["--model", "model.gguf", "--api-key", secret, "--gpu-layers", "all"]);

        Assert.False(logged.Contains(secret, StringComparison.Ordinal));
        Assert.Contains("--api-key <redacted>", logged, StringComparison.Ordinal);
        Assert.Contains("--gpu-layers all", logged, StringComparison.Ordinal);
    }
}
