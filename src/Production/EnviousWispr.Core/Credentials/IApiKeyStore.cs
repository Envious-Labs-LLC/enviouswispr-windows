using EnviousWispr.Core.Settings;

namespace EnviousWispr.Core.Credentials;

public enum ApiKeyReadStatus
{
    Found,
    Missing,
    Unavailable,
}

public sealed record ApiKeyReadResult(ApiKeyReadStatus Status, string? Value = null)
{
    public static ApiKeyReadResult Found(string value) => new(ApiKeyReadStatus.Found, value);

    public static ApiKeyReadResult Missing { get; } = new(ApiKeyReadStatus.Missing);

    public static ApiKeyReadResult Unavailable { get; } = new(ApiKeyReadStatus.Unavailable);
}

public interface IApiKeyStore
{
    ApiKeyReadResult Read(PolishProvider provider);

    void Store(PolishProvider provider, string value);

    void Delete(PolishProvider provider);
}
