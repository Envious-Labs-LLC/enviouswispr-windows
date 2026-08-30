namespace EnviousWispr.Core.Distribution;

public static class UpdateEndpointPolicy
{
    public static bool TryNormalize(string? value, bool allowLoopbackForUat, out Uri? endpoint)
    {
        endpoint = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            !string.IsNullOrEmpty(parsed.UserInfo) ||
            !string.IsNullOrEmpty(parsed.Query) ||
            !string.IsNullOrEmpty(parsed.Fragment))
        {
            return false;
        }

        var isHttps = string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var isLoopbackUat = allowLoopbackForUat &&
            string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            parsed.IsLoopback;
        if (!isHttps && !isLoopbackUat)
        {
            return false;
        }

        endpoint = new Uri(parsed.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
        return true;
    }
}
