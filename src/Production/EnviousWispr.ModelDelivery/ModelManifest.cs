using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace EnviousWispr.ModelDelivery;

public sealed record ModelLicenseNotice(string Name, Uri Url, string Notice);

/// <summary>One slice of a large artefact, published as its own object so it stays edge-cacheable.</summary>
/// <remarks>
/// THE MIRROR CANNOT CACHE AN OBJECT OVER 512 MB on any Cloudflare plan below Enterprise, so a
/// 650 MB encoder would serve from one origin region to every user, uncached. The EG-1 model on
/// macOS ships as eight ~361 MB shards for exactly this reason. A part has its own sources and
/// hash; the whole file keeps its own hash and its own sources, and the whole-file sources are
/// the fallback when any part cannot be had. Ref: #92.
/// </remarks>
public sealed record ModelArtifactPart(
    long SizeBytes,
    string Sha256,
    IReadOnlyList<Uri> Sources);

public sealed record ModelArtifact(
    string RelativePath,
    long SizeBytes,
    string Sha256,
    IReadOnlyList<Uri> Sources,
    IReadOnlyList<ModelArtifactPart>? Parts = null)
{
    public bool IsSharded => Parts is { Count: > 0 };
}

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

    /// <summary>The manifest could not be fetched at all: no response, a transport failure, or a non-success status.</summary>
    /// <remarks>
    /// SEPARATE FROM InvalidEnvelope, which is what this case used to be reported as. A user whose
    /// laptop is offline and a user whose manifest is corrupt were told the same sentence, and only
    /// one of them can fix it by reconnecting. Ref: #92.
    /// </remarks>
    Unreachable,
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

    /// <summary>The key id a manifest carries when its trust root is the signed application package rather than a signature.</summary>
    public const string BundledKeyId = "bundled";

    /// <summary>
    /// Verifies a manifest that ships INSIDE the application, the way the macOS app does it.
    /// </summary>
    /// <remarks>
    /// THE TRUST ROOT IS THE SIGNED PACKAGE, NOT A SIGNATURE. The macOS product bundles its delivery
    /// manifests as app resources and never fetches one at runtime, so the pinned digests can only
    /// change through a trusted app update - and it has no signing key at all. This entry point
    /// gives Windows the same shape: the document is a bare payload plus a `manifestDigest`, which
    /// is SHA-256 over the canonical JSON of the object with that key removed (sorted keys, no
    /// insignificant whitespace, slashes unescaped, lowercase hex), exactly as
    /// `DeliveryManifest.canonicalDigest(of:)` computes it on the Mac.
    ///
    /// The digest is a self-check against a corrupted or hand-edited resource, and NOTHING MORE. The
    /// guarantee that matters is per-file SHA-256 at admission, which the store owns. Ref: #92 and the
    /// macOS `docs/model-delivery/model-delivery-contract.md` v1.3.
    ///
    /// DELIBERATELY NOT REACHABLE FROM <see cref="Verify"/>. A remote fetch goes through the signed
    /// path only, so a hosting mistake or a hostile mirror can never present an unsigned document and
    /// have it admitted; only bytes compiled into the assembly arrive here.
    /// </remarks>
    public ManifestVerificationResult VerifyBundled(ReadOnlySpan<byte> documentBytes)
    {
        if (documentBytes.IsEmpty || documentBytes.Length > MaximumEnvelopeBytes)
        {
            return new(ManifestVerificationStatus.InvalidEnvelope);
        }

        string declaredDigest;
        byte[] canonicalBytes;
        try
        {
            using var document = JsonDocument.Parse(documentBytes.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty(
                    ModelManifestCanonicalJson.DigestPropertyName,
                    out var digestElement) ||
                digestElement.ValueKind != JsonValueKind.String)
            {
                return new(ManifestVerificationStatus.InvalidEnvelope);
            }

            declaredDigest = digestElement.GetString() ?? string.Empty;
            canonicalBytes = ModelManifestCanonicalJson.CanonicalizeWithoutDigest(document.RootElement);
        }
        catch (JsonException)
        {
            return new(ManifestVerificationStatus.InvalidEnvelope);
        }

        var actualDigest = Convert.ToHexString(SHA256.HashData(canonicalBytes)).ToLowerInvariant();
        if (!IsManifestDigest(declaredDigest) ||
            !string.Equals(actualDigest, declaredDigest.ToLowerInvariant(), StringComparison.Ordinal))
        {
            return new(ManifestVerificationStatus.InvalidSignature);
        }

        ModelManifestPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<ModelManifestPayload>(canonicalBytes, JsonOptions);
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

        return new(
            ManifestVerificationStatus.Verified,
            new VerifiedModelManifest(payload, BundledKeyId, actualDigest, documentBytes.ToArray()));
    }

    /// <summary>
    /// Verifies the manifest copy the store keeps beside an installed model, which may be either shape.
    /// </summary>
    /// <remarks>
    /// The signed form is tried first; a bundled document is accepted only when it carries the
    /// digest property, so a stray signed envelope can never be re-read as a bundled one. This is
    /// for bytes the store itself wrote at admission and re-checks against every artefact's hash;
    /// the remote path still goes through <see cref="Verify"/> alone.
    /// </remarks>
    public ManifestVerificationResult VerifyStored(ReadOnlySpan<byte> manifestBytes)
    {
        var signed = Verify(manifestBytes);
        if (signed.Status != ManifestVerificationStatus.InvalidEnvelope)
        {
            return signed;
        }

        return VerifyBundled(manifestBytes);
    }

    /// <summary>Re-verifies a manifest by the path that produced it: bundled documents by digest, everything else by signature.</summary>
    public ManifestVerificationResult Reverify(VerifiedModelManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return string.Equals(manifest.KeyId, BundledKeyId, StringComparison.Ordinal)
            ? VerifyBundled(manifest.EnvelopeBytes)
            : Verify(manifest.EnvelopeBytes);
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
                !paths.Add(file.RelativePath) ||
                !PartsAreValid(file, allowLoopbackHttp))
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

    public static bool IsSafeRelativePath(string relativePath)
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

    // PARTS MUST ADD UP TO THE FILE, EXACTLY. A manifest whose slices sum to anything else
    // describes a file that cannot be reassembled, and it is refused here rather than discovered
    // after the download.
    private static bool PartsAreValid(ModelArtifact file, bool allowLoopbackHttp)
    {
        if (file.Parts is null)
        {
            return true;
        }

        if (file.Parts.Count == 0)
        {
            return false;
        }

        long total = 0;
        foreach (var part in file.Parts)
        {
            if (part.SizeBytes <= 0 ||
                !Sha256Regex().IsMatch(part.Sha256) ||
                part.Sources is null ||
                part.Sources.Count == 0 ||
                part.Sources.Any(uri => !IsAllowedUri(uri, allowLoopbackHttp)))
            {
                return false;
            }

            try
            {
                total = checked(total + part.SizeBytes);
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        return total == file.SizeBytes;
    }

    public static bool IsSafeModelId(string modelId) =>
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

/// <summary>
/// The canonical JSON form the bundled-manifest digest is taken over, matching the macOS rule.
/// </summary>
/// <remarks>
/// Object keys sorted ordinally, no insignificant whitespace, `/` left unescaped, non-ASCII left
/// raw, and the digest property itself removed at the top level. Numbers are re-emitted from their
/// source text, so a manifest must carry integers only - the Mac made one field a string for
/// exactly this reason after a float canonicalised differently across serialisers.
/// </remarks>
public static class ModelManifestCanonicalJson
{
    public const string DigestPropertyName = "manifestDigest";

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static byte[] CanonicalizeWithoutDigest(JsonElement root)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            WriteCanonical(writer, root, skipDigest: true);
        }

        return buffer.ToArray();
    }

    public static string DigestOf(JsonElement root) =>
        Convert.ToHexString(SHA256.HashData(CanonicalizeWithoutDigest(root))).ToLowerInvariant();

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element, bool skipDigest)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                             .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    if (skipDigest && property.Name == DigestPropertyName)
                    {
                        continue;
                    }

                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value, skipDigest: false);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item, skipDigest: false);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText());
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
