using System.Net;

namespace EnviousWispr.LLM;

public static class OllamaEndpointPolicy
{
    public const string DefaultEndpoint = "http://localhost:11434";

    public static bool TryNormalize(string? configuredEndpoint, out Uri? endpoint)
    {
        endpoint = null;
        var candidate = string.IsNullOrWhiteSpace(configuredEndpoint)
            ? DefaultEndpoint
            : configuredEndpoint.Trim();
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var parsed) ||
            (!string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) ||
            !string.IsNullOrEmpty(parsed.UserInfo) ||
            !string.IsNullOrEmpty(parsed.Query) ||
            !string.IsNullOrEmpty(parsed.Fragment) ||
            parsed.AbsolutePath is not ("" or "/") ||
            !IsLoopbackHost(parsed.Host))
        {
            return false;
        }

        var builder = new UriBuilder(parsed)
        {
            Path = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty,
        };
        endpoint = builder.Uri;
        return true;
    }

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
}
