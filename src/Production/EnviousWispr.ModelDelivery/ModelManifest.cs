using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace EnviousWispr.ModelDelivery;

public sealed record ModelLicenseNotice(string Name, Uri Url, string Notice);

public sealed record ModelArtifact(
    string RelativePath,
    long SizeBytes,
    string Sha256,
    IReadOnlyList<Uri> Sources);

public sealed record ModelManifestPayload(
    int SchemaVersion,
    string ModelId,
    string Version,
    string MinimumAppVersion,
    ModelLicenseNotice License,
    IReadOnlyList<ModelArtifact> Files);

public sealed record SignedModelManifestEnvelope(
    int EnvelopeVersion,
    string KeyId,
    string PayloadBase64,
    string SignatureBase64);

public sealed class VerifiedModelManifest
{
    internal VerifiedModelManifest(
        ModelManifestPayload payload,
        string keyId,
        string manifestDigest,
        byte[] envelopeBytes)
    {
        Payload = payload;
        KeyId = keyId;
        ManifestDigest = manifestDigest;
        EnvelopeBytes = envelopeBytes;
    }

    public ModelManifestPayload Payload { get; }
    public string KeyId { get; }
    public string ManifestDigest { get; }
    internal byte[] EnvelopeBytes { get; }
}

public enum ManifestVerificationStatus
{
    Verified,
    InvalidEnvelope,
    UntrustedKey,
    InvalidSignature,
    InvalidPayload,
    UnsupportedSchema,
}

public sealed record ManifestVerificationResult(
    ManifestVerificationStatus Status,
    VerifiedModelManifest? Manifest = null)
{
    public bool Succeeded => Status == ManifestVerificationStatus.Verified && Manifest is not null;
}

public sealed partial class ModelManifestVerifier
{
    public const int CurrentEnvelopeVersion = 1;
    public const int CurrentManifestSchemaVersion = 1;
    public const int MaximumEnvelopeBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.Strict,
    };

    private readonly Dictionary<string, string> _trustedPublicKeys;
    private readonly bool _allowLoopbackHttp;

    public ModelManifestVerifier(
        IReadOnlyDictionary<string, string> trustedPublicKeys,
        bool allowLoopbackHttp = false)
    {
        ArgumentNullException.ThrowIfNull(trustedPublicKeys);
        _trustedPublicKeys = new Dictionary<string, string>(
            trustedPublicKeys,
            StringComparer.Ordinal);
        _allowLoopbackHttp = allowLoopbackHttp;
    }

    public ManifestVerificationResult Verify(ReadOnlySpan<byte> envelopeBytes)
    {
        if (envelopeBytes.IsEmpty || envelopeBytes.Length > MaximumEnvelopeBytes)
        {
            return new(ManifestVerificationStatus.InvalidEnvelope);
        }

        SignedModelManifestEnvelope? envelope;
        byte[] payloadBytes;
        byte[] signatureBytes;
        try
        {
            envelope = JsonSerializer.Deserialize<SignedModelManifestEnvelope>(envelopeBytes, JsonOptions);
            if (envelope is null ||
                envelope.EnvelopeVersion != CurrentEnvelopeVersion ||
                string.IsNullOrWhiteSpace(envelope.KeyId) ||
                string.IsNullOrWhiteSpace(envelope.PayloadBase64) ||
                string.IsNullOrWhiteSpace(envelope.SignatureBase64))
            {
                return new(ManifestVerificationStatus.InvalidEnvelope);
            }

            payloadBytes = Convert.FromBase64String(envelope.PayloadBase64);
            signatureBytes = Convert.FromBase64String(envelope.SignatureBase64);
        }
        catch (JsonException)
        {
            return new(ManifestVerificationStatus.InvalidEnvelope);
        }
        catch (FormatException)
        {
            return new(ManifestVerificationStatus.InvalidEnvelope);
        }

        if (!_trustedPublicKeys.TryGetValue(envelope.KeyId, out var publicKeyPem))
        {
            return new(ManifestVerificationStatus.UntrustedKey);
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            if (!key.VerifyData(payloadBytes, signatureBytes, HashAlgorithmName.SHA256))
            {
                return new(ManifestVerificationStatus.InvalidSignature);
            }
        }
        catch (CryptographicException)
        {
            return new(ManifestVerificationStatus.InvalidSignature);
        }

        ModelManifestPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<ModelManifestPayload>(payloadBytes, JsonOptions);
        }
        catch (JsonException)
        {
            return new(ManifestVerificationStatus.InvalidPayload);
        }

        if (payload is null)
        {
            return new(ManifestVerificationStatus.InvalidPayload);
        }

        if (payload.SchemaVersion != CurrentManifestSchemaVersion)
        {
            return new(ManifestVerificationStatus.UnsupportedSchema);
        }

        if (!IsValidPayload(payload, _allowLoopbackHttp))
        {
            return new(ManifestVerificationStatus.InvalidPayload);
        }

        var envelopeCopy = envelopeBytes.ToArray();
        return new(
            ManifestVerificationStatus.Verified,
            new VerifiedModelManifest(
                payload,
                envelope.KeyId,
                Convert.ToHexString(SHA256.HashData(envelopeCopy)).ToLowerInvariant(),
                envelopeCopy));
    }

    internal static JsonSerializerOptions SerializerOptions => JsonOptions;

    private static bool IsValidPayload(ModelManifestPayload payload, bool allowLoopbackHttp)
    {
        if (!IsSafeModelId(payload.ModelId) ||
            !IsSemanticVersion(payload.Version) ||
            !Version.TryParse(payload.MinimumAppVersion, out _) ||
            string.IsNullOrWhiteSpace(payload.License.Name) ||
            string.IsNullOrWhiteSpace(payload.License.Notice) ||
            !IsAllowedUri(payload.License.Url, allowLoopbackHttp) ||
            payload.Files is null ||
            payload.Files.Count == 0)
        {
            return false;
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (var file in payload.Files)
        {
            if (!IsSafeRelativePath(file.RelativePath) ||
                file.SizeBytes <= 0 ||
                !Sha256Regex().IsMatch(file.Sha256) ||
                file.Sources is null ||
                file.Sources.Count == 0 ||
                file.Sources.Any(uri => !IsAllowedUri(uri, allowLoopbackHttp)) ||
                !paths.Add(file.RelativePath))
            {
                return false;
            }

            try
            {
                totalBytes = checked(totalBytes + file.SizeBytes);
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        return totalBytes > 0;
    }

    internal static bool IsSafeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            Path.IsPathRooted(relativePath) ||
            relativePath.Contains('\\', StringComparison.Ordinal) ||
            relativePath.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 &&
            segments.All(segment => segment is not "." and not ".." && segment.Length <= 128);
    }

    internal static bool IsSafeModelId(string modelId) =>
        !string.IsNullOrWhiteSpace(modelId) && SafeIdentifierRegex().IsMatch(modelId);

    internal static bool IsSemanticVersion(string version) =>
        !string.IsNullOrWhiteSpace(version) && SemanticVersionRegex().IsMatch(version);

    internal static bool IsManifestDigest(string digest) =>
        !string.IsNullOrWhiteSpace(digest) && Sha256Regex().IsMatch(digest);

    private static bool IsAllowedUri(Uri uri, bool allowLoopbackHttp) =>
        uri.IsAbsoluteUri &&
        (uri.Scheme == Uri.UriSchemeHttps ||
            (allowLoopbackHttp && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));

    [GeneratedRegex("^[a-z0-9][a-z0-9.-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeIdentifierRegex();

    [GeneratedRegex("^[0-9]+\\.[0-9]+\\.[0-9]+(?:-[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SemanticVersionRegex();

    [GeneratedRegex("^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();
}

public static class ModelManifestSigning
{
    public static byte[] CreateEnvelope(
        ModelManifestPayload payload,
        string keyId,
        ECDsa privateKey)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        ArgumentNullException.ThrowIfNull(privateKey);

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(
            payload,
            ModelManifestVerifier.SerializerOptions);
        var signature = privateKey.SignData(payloadBytes, HashAlgorithmName.SHA256);
        var envelope = new SignedModelManifestEnvelope(
            ModelManifestVerifier.CurrentEnvelopeVersion,
            keyId,
            Convert.ToBase64String(payloadBytes),
            Convert.ToBase64String(signature));
        return JsonSerializer.SerializeToUtf8Bytes(
            envelope,
            ModelManifestVerifier.SerializerOptions);
    }

    public static string ExportPublicKeyPem(ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.ExportSubjectPublicKeyInfoPem();
    }
}
