namespace EnviousWispr.Core.Networking;

public enum HttpClientProfile
{
    Default = 0,
    NoRedirect = 1,
    NoRedirectNoCookies = 2,
}

/// <summary>
/// Process-wide pooled <see cref="SocketsHttpHandler"/> registry and <see cref="HttpClient"/> factory.
/// Returned clients always set <c>disposeHandler: false</c> so caller disposal never tears down shared pools
/// or test-injected message handlers.
/// </summary>
public static class SharedHttpClients
{
    private static readonly TimeSpan ConnectionLifetime = TimeSpan.FromMinutes(2);

    private static readonly SocketsHttpHandler DefaultPool = new()
    {
        PooledConnectionLifetime = ConnectionLifetime,
        ConnectTimeout = TimeSpan.FromSeconds(10),
    };

    private static readonly SocketsHttpHandler NoRedirectPool = new()
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = ConnectionLifetime,
        ConnectTimeout = TimeSpan.FromSeconds(5),
    };

    private static readonly SocketsHttpHandler NoRedirectNoCookiesPool = new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        PooledConnectionLifetime = ConnectionLifetime,
        ConnectTimeout = TimeSpan.FromSeconds(5),
    };

    public static SocketsHttpHandler GetHandler(HttpClientProfile profile = HttpClientProfile.Default) =>
        profile switch
        {
            HttpClientProfile.Default => DefaultPool,
            HttpClientProfile.NoRedirect => NoRedirectPool,
            HttpClientProfile.NoRedirectNoCookies => NoRedirectNoCookiesPool,
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
        };

    public static HttpClient CreateClient(
        HttpMessageHandler? customHandler = null,
        HttpClientProfile profile = HttpClientProfile.Default,
        TimeSpan? timeout = null)
    {
        var handler = customHandler ?? GetHandler(profile);
        var client = new HttpClient(handler, disposeHandler: false);
        if (timeout.HasValue)
        {
            client.Timeout = timeout.Value;
        }

        return client;
    }
}
