using System.Collections;
using System.Reflection;

namespace EnviousWispr.Services.Diagnostics;

public sealed record SentryDsnDescriptor(
    string RawDsn,
    string PublicKey,
    string ProjectId,
    Uri EnvelopeUri)
{
    public static bool TryParse(string? value, out SentryDsnDescriptor? descriptor)
    {
        descriptor = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        var publicKey = uri.UserInfo.Split(':', 2)[0].Trim();
        if (string.IsNullOrWhiteSpace(publicKey))
        {
            return false;
        }

        var pathSegments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (pathSegments.Length == 0)
        {
            return false;
        }

        var projectId = pathSegments[^1];
        if (string.IsNullOrWhiteSpace(projectId) ||
            !projectId.All(char.IsLetterOrDigit))
        {
            return false;
        }

        var prefixSegments = pathSegments[..^1];
        var prefix = prefixSegments.Length == 0
            ? string.Empty
            : "/" + string.Join("/", prefixSegments);
        var authority = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        var envelopeUri = new Uri($"https://{authority}{prefix}/api/{projectId}/envelope/");
        descriptor = new SentryDsnDescriptor(trimmed, publicKey, projectId, envelopeUri);
        return true;
    }
}

public sealed record ResolvedObservabilityKeys(
    string? PostHogApiKey,
    Uri PostHogHost,
    SentryDsnDescriptor? SentryDsn,
    bool IsTelemetrySuppressed)
{
    public bool HasPostHog => !IsTelemetrySuppressed && !string.IsNullOrWhiteSpace(PostHogApiKey);

    public bool HasSentry => !IsTelemetrySuppressed && SentryDsn is not null;
}

public static class ObservabilityKeyResolver
{
    public const string DefaultPostHogHost = "https://us.i.posthog.com";

    public const string PostHogApiKeyEnvVar = "ENVIOUSWISPR_POSTHOG_API_KEY";
    public const string PostHogHostEnvVar = "ENVIOUSWISPR_POSTHOG_HOST";
    public const string SentryDsnEnvVar = "ENVIOUSWISPR_SENTRY_DSN";
    public const string DisableTelemetryEnvVar = "ENVIOUSWISPR_DISABLE_TELEMETRY";
    public const string AllowUatTelemetryEnvVar = "ENVIOUSWISPR_UAT_ALLOW_TELEMETRY";

    public const string PostHogApiKeyFileName = "posthog-api-key";
    public const string PostHogHostFileName = "posthog-host";
    public const string SentryDsnFileName = "sentry-dsn";

    private static readonly string[] PostHogApiKeyMetadataKeys =
    [
        "EnviousWisprPostHogApiKey",
        "PostHogApiKey",
        "PostHogAPIKey",
    ];

    private static readonly string[] PostHogHostMetadataKeys =
    [
        "EnviousWisprPostHogHost",
        "PostHogHost",
    ];

    private static readonly string[] SentryDsnMetadataKeys =
    [
        "EnviousWisprSentryDsn",
        "SentryDsn",
        "SentryDSN",
    ];

    private static readonly string[] KnownUatEnvironmentVariables =
    [
        "ENVIOUSWISPR_UAT_CREDENTIAL_SUFFIX",
        "ENVIOUSWISPR_UAT_READY_EVENT",
        "ENVIOUSWISPR_UAT_RUNTIME_READY_EVENT",
        "ENVIOUSWISPR_UAT_LIVE_PREVIEW",
        "ENVIOUSWISPR_UAT_JOURNEY",
        "ENVIOUSWISPR_UAT_JOURNEY_CANCEL",
        "ENVIOUSWISPR_UAT_AUDIO_FAILURE",
        "ENVIOUSWISPR_UAT_AUDIO_FIXTURE",
        "ENVIOUSWISPR_UAT_JOURNEY_START_EVENT",
        "ENVIOUSWISPR_UAT_JOURNEY_COMPLETE_EVENT",
        "ENVIOUSWISPR_UAT_JOURNEY_EXIT_AFTER_COMPLETION",
        "ENVIOUSWISPR_UAT_JOURNEY_EXIT_EVENT",
        "ENVIOUSWISPR_UAT_JOURNEY_HOLD_MILLISECONDS",
        "ENVIOUSWISPR_UAT_EXIT_AFTER_MILLISECONDS",
        "ENVIOUSWISPR_UAT_OVERLAY_STATE",
        "ENVIOUSWISPR_UAT_RECOVERY_STATE",
        "ENVIOUSWISPR_UAT_DISABLE_LOCAL_RUNTIME",
        "ENVIOUSWISPR_UAT_ALLOW_LOOPBACK_TELEMETRY",
        "ENVIOUSWISPR_UAT_PHASE_JOURNAL",
    ];

    public static ResolvedObservabilityKeys Resolve(
        Assembly? assembly = null,
        IReadOnlyDictionary<string, string?>? assemblyMetadataOverride = null,
        Func<string, string?>? getEnvironmentVariable = null,
        IEnumerable<KeyValuePair<string, string?>>? allEnvironmentVariables = null,
        string? keysDirectory = null)
    {
        var envReader = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        var metadata = assemblyMetadataOverride ?? ReadAssemblyMetadata(assembly ?? Assembly.GetEntryAssembly());
        var resolvedKeysDir = keysDirectory ?? DefaultKeysDirectory();

        var suppressed = IsTelemetrySuppressed(envReader, allEnvironmentVariables);

        var rawPostHogKey = ResolveValue(
            metadata,
            PostHogApiKeyMetadataKeys,
            envReader,
            PostHogApiKeyEnvVar,
            resolvedKeysDir,
            PostHogApiKeyFileName);

        var rawPostHogHost = ResolveValue(
            metadata,
            PostHogHostMetadataKeys,
            envReader,
            PostHogHostEnvVar,
            resolvedKeysDir,
            PostHogHostFileName);

        var rawSentryDsn = ResolveValue(
            metadata,
            SentryDsnMetadataKeys,
            envReader,
            SentryDsnEnvVar,
            resolvedKeysDir,
            SentryDsnFileName);

        var postHogHost = NormalizePostHogHost(rawPostHogHost);
        _ = SentryDsnDescriptor.TryParse(rawSentryDsn, out var sentryDsn);

        if (suppressed)
        {
            return new ResolvedObservabilityKeys(
                PostHogApiKey: null,
                PostHogHost: postHogHost,
                SentryDsn: null,
                IsTelemetrySuppressed: true);
        }

        return new ResolvedObservabilityKeys(
            PostHogApiKey: string.IsNullOrWhiteSpace(rawPostHogKey) ? null : rawPostHogKey.Trim(),
            PostHogHost: postHogHost,
            SentryDsn: sentryDsn,
            IsTelemetrySuppressed: false);
    }

    public static bool IsTelemetryExplicitlyDisabled(
        Func<string, string?>? getEnvironmentVariable = null)
    {
        var envReader = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        return IsTruthy(envReader(DisableTelemetryEnvVar));
    }

    public static bool IsTelemetrySuppressed(
        Func<string, string?>? getEnvironmentVariable = null,
        IEnumerable<KeyValuePair<string, string?>>? allEnvironmentVariables = null)
    {
        var envReader = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        if (IsTelemetryExplicitlyDisabled(envReader))
        {
            return true;
        }

        if (IsTruthy(envReader(AllowUatTelemetryEnvVar)))
        {
            return false;
        }

        foreach (var knownUatVar in KnownUatEnvironmentVariables)
        {
            if (!string.IsNullOrWhiteSpace(envReader(knownUatVar)))
            {
                return true;
            }
        }

        if (allEnvironmentVariables is not null)
        {
            foreach (var entry in allEnvironmentVariables)
            {
                if (entry.Key.StartsWith("ENVIOUSWISPR_UAT_", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(entry.Key, AllowUatTelemetryEnvVar, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(entry.Value))
                {
                    return true;
                }
            }

            return false;
        }

        if (getEnvironmentVariable is null)
        {
            try
            {
                foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
                {
                    if (entry.Key is string key &&
                        key.StartsWith("ENVIOUSWISPR_UAT_", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(key, AllowUatTelemetryEnvVar, StringComparison.OrdinalIgnoreCase) &&
                        entry.Value is string val &&
                        !string.IsNullOrWhiteSpace(val))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // Reading environment variables is best-effort.
            }
        }

        return false;
    }

    public static Uri NormalizePostHogHost(string? rawHost)
    {
        if (!string.IsNullOrWhiteSpace(rawHost) &&
            TelemetryEndpointPolicy.TryNormalize(rawHost.Trim(), allowLoopbackHttp: false, out var parsed) &&
            parsed is not null)
        {
            var baseUri = parsed.GetLeftPart(UriPartial.Authority);
            return new Uri(baseUri);
        }

        return new Uri(DefaultPostHogHost);
    }

    private static string? ResolveValue(
        IReadOnlyDictionary<string, string?> metadata,
        IReadOnlyList<string> metadataKeys,
        Func<string, string?> envReader,
        string envVarName,
        string? keysDirectory,
        string fileName)
    {
        foreach (var key in metadataKeys)
        {
            if (metadata.TryGetValue(key, out var metadataValue) &&
                !string.IsNullOrWhiteSpace(metadataValue))
            {
                return metadataValue.Trim();
            }
        }

        var envValue = envReader(envVarName);
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            return envValue.Trim();
        }

        if (!string.IsNullOrWhiteSpace(keysDirectory))
        {
            try
            {
                var filePath = Path.Combine(keysDirectory, fileName);
                if (File.Exists(filePath))
                {
                    var fileValue = File.ReadAllText(filePath).Trim();
                    if (!string.IsNullOrWhiteSpace(fileValue))
                    {
                        return fileValue;
                    }
                }
            }
            catch
            {
                // Local dev key lookup is best-effort and must never throw on startup.
            }
        }

        return null;
    }

    private static Dictionary<string, string?> ReadAssemblyMetadata(Assembly? assembly)
    {
        if (assembly is null)
        {
            return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var attribute in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            {
                if (!string.IsNullOrWhiteSpace(attribute.Key) &&
                    !string.IsNullOrWhiteSpace(attribute.Value))
                {
                    dict[attribute.Key] = attribute.Value;
                }
            }

            return dict;
        }
        catch
        {
            return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string? DefaultKeysDirectory()
    {
        try
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrWhiteSpace(userProfile)
                ? null
                : Path.Combine(userProfile, ".enviouswispr-keys");
        }
        catch
        {
            return null;
        }
    }

    private static bool IsTruthy(string? value) =>
        string.Equals(value?.Trim(), "1", StringComparison.Ordinal) ||
        string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
}
