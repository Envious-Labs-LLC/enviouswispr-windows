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

    private const int DefaultPort = 11434;

    /// <summary>
    /// Whether an OLLAMA_HOST value keeps a server this app starts on this PC, at the default port. Read the way Ollama
    /// reads it: optional scheme, host, optional port; unset or an empty host is Ollama's own default, which is loopback.
    /// </summary>
    public static bool IsLoopbackHostSetting(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var text = value.Trim();
        var scheme = text.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            text = text[(scheme + 3)..];
        }

        text = text.TrimEnd('/');
        string host;
        var port = DefaultPort;
        if (text.StartsWith('['))
        {
            var close = text.IndexOf(']', StringComparison.Ordinal);
            if (close < 0)
            {
                return false;
            }

            host = text[1..close];
            if (text.Length > close + 1 && (text[close + 1] != ':' || !int.TryParse(text[(close + 2)..], out port)))
            {
                return false;
            }
        }
        else
        {
            var colon = text.LastIndexOf(':');
            host = colon >= 0 ? text[..colon] : text;
            if (colon >= 0 && !int.TryParse(text[(colon + 1)..], out port))
            {
                return false;
            }
        }

        return port == DefaultPort &&
            (host.Length == 0 ||
             string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
             IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
    }

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
}
