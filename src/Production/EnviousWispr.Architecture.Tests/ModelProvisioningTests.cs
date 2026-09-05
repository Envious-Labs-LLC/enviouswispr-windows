using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EnviousWispr.ModelDelivery;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// The wiring that #92 found missing: a bundled manifest, a provisioner, and a resolver that
/// prefers what the store admitted over what happens to be on disk.
/// </summary>
public sealed class ModelProvisioningTests
{
    private static readonly byte[] ModelBytes = Enumerable.Range(0, 4096).Select(v => (byte)(v % 253)).ToArray();
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    [Fact]
    public void BundledManifestVerifiesByCanonicalDigestAndRefusesAnEditedOne()
    {
        var verifier = new ModelManifestVerifier(new Dictionary<string, string>());
        var document = BundledDocument("speech", "1.0.0");

        var verified = verifier.VerifyBundled(document);
        Assert.True(verified.Succeeded, verified.Status.ToString());
        Assert.Equal(ModelManifestVerifier.BundledKeyId, verified.Manifest!.KeyId);
        Assert.Equal("speech", verified.Manifest.Payload.ModelId);

        // ONE BYTE OF THE PIN CHANGED AND THE DIGEST WAS NOT RECOMPUTED - the hand edit the digest exists to catch.
        var edited = Encoding.UTF8.GetString(document).Replace("\"sizeBytes\": 4096", "\"sizeBytes\": 4095", StringComparison.Ordinal);
        Assert.NotEqual(Encoding.UTF8.GetString(document), edited);
        Assert.Equal(
            ManifestVerificationStatus.InvalidSignature,
            verifier.VerifyBundled(Encoding.UTF8.GetBytes(edited)).Status);

        // A DOCUMENT WITHOUT A DIGEST IS NOT A BUNDLED MANIFEST AT ALL.
        var undigested = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(document)!;
        undigested.Remove(ModelManifestCanonicalJson.DigestPropertyName);
        Assert.Equal(
            ManifestVerificationStatus.InvalidEnvelope,
            verifier.VerifyBundled(JsonSerializer.SerializeToUtf8Bytes(undigested)).Status);
    }

    [Fact]
    public void CanonicalFormIsIndependentOfKeyOrderAndWhitespace()
    {
        using var ordered = JsonDocument.Parse("""{"b":[1,{"z":"x/y","a":true}],"a":"é","c":null}""");
        using var shuffled = JsonDocument.Parse("""
            {
              "c": null,
              "a": "é",
              "b": [ 1, { "a": true, "z": "x/y" } ],
              "manifestDigest": "ignored-at-the-top-level-only"
            }
            """);

        var canonical = ModelManifestCanonicalJson.CanonicalizeWithoutDigest(ordered.RootElement);
        Assert.Equal(canonical, ModelManifestCanonicalJson.CanonicalizeWithoutDigest(shuffled.RootElement));
        // SLASHES AND NON-ASCII STAY RAW, as the Mac's serialiser leaves them.
        Assert.Equal("""{"a":"é","b":[1,{"a":true,"z":"x/y"}],"c":null}""", Encoding.UTF8.GetString(canonical));
    }

    [Fact]
    public void TheSignedPathNeverAdmitsABundledDocument()
    {
        // A remote fetch goes through Verify, and Verify must not know about digest-only documents;
        // otherwise a mirror could serve an unsigned manifest and have it trusted.
        var verifier = new ModelManifestVerifier(new Dictionary<string, string>());
        Assert.Equal(
            ManifestVerificationStatus.InvalidEnvelope,
            verifier.Verify(BundledDocument("speech", "1.0.0")).Status);
    }

    [Fact]
    public async Task ProvisionerInstallsFromTheMirrorAndFailsOverToTheBackup()
    {
        var verifier = new ModelManifestVerifier(new Dictionary<string, string>());
        var manifest = verifier.VerifyBundled(BundledDocument(
            "speech",
            "1.0.0",
            sources: ["https://mirror.invalid/speech/model.bin", "https://backup.invalid/speech/model.bin"]));
        Assert.True(manifest.Succeeded);
        var requested = new List<string>();
        using var handler = new RoutingHandler(request =>
        {
            requested.Add(request.RequestUri!.Host);
            return request.RequestUri.Host == "mirror.invalid"
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ModelBytes) };
        });
        var root = Directory.CreateTempSubdirectory("EnviousWispr.Provisioning.").FullName;
        try
        {
            var store = new ModelStore(
                root,
                new HttpClient(handler, disposeHandler: false),
                verifier,
                new Version(1, 0, 0),
                new FixedDiskSpaceProbe(long.MaxValue),
                options: new ModelDeliveryOptions(DiskReserveBytes: 0, MaximumAttemptsPerSource: 1, RetryDelay: _ => TimeSpan.Zero));
            var provisioner = new ModelProvisioner(store, _ => manifest);

            var result = await provisioner.ProvisionAsync("speech");

            Assert.True(result.Succeeded, result.Failure.ToString());
            Assert.Equal(["mirror.invalid", "backup.invalid"], requested);
            var active = await store.OpenActiveOfflineAsync("speech");
            Assert.True(active.Succeeded);
            Assert.Equal(ModelBytes, await File.ReadAllBytesAsync(Path.Combine(active.Installed!.DirectoryPath, "model.bin")));

            // THE NAME ON THE FILE AND THE NAME IN THE PAYLOAD MUST AGREE.
            var wrongName = await new ModelProvisioner(store, _ => manifest).ProvisionAsync("other");
            Assert.Equal(ModelDeliveryFailure.InvalidManifest, wrongName.Failure);

            var unknown = await new ModelProvisioner(store, _ => new(ManifestVerificationStatus.Unreachable)).ProvisionAsync("speech");
            Assert.Equal(ModelDeliveryFailure.NetworkUnavailable, unknown.Failure);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EveryBundledManifestInTheBuildVerifiesAndNamesItsOwnModel()
    {
        var verifier = new ModelManifestVerifier(new Dictionary<string, string>());
        Assert.NotEmpty(BundledModelManifests.ModelIds);
        foreach (var modelId in BundledModelManifests.ModelIds)
        {
            var loaded = BundledModelManifests.Load(modelId, verifier);
            Assert.True(loaded.Succeeded, $"{modelId}: {loaded.Status}");
            Assert.Equal(modelId, loaded.Manifest!.Payload.ModelId);
            foreach (var file in loaded.Manifest.Payload.Files)
            {
                // MIRROR FIRST, PINNED BACKUP SECOND, matching the macOS contract. A backup pinned to
                // `main` is a pin to whatever is there today, which is not a pin.
                Assert.DoesNotContain("/resolve/main/", file.Sources[^1].AbsolutePath, StringComparison.Ordinal);
                Assert.Equal("huggingface.co", file.Sources[^1].Host);
                if (file.IsSharded)
                {
                    // THE MIRROR HOLDS THE PARTS AND NEVER THE WHOLE, so the whole-file sources must
                    // not name it: a mirror URL that cannot resolve is a guaranteed 404 on every
                    // fallback, and the edge caches that 404 for the rule's full TTL.
                    Assert.DoesNotContain(file.Sources, source => source.Host == "models.enviouslabs.co");
                    Assert.All(file.Parts!, part => Assert.Equal("models.enviouslabs.co", part.Sources[0].Host));
                    Assert.True(file.Parts!.All(part => part.SizeBytes <= 256L * 1024 * 1024), $"{modelId}/{file.RelativePath} has a part over 256 MiB");
                }
                else
                {
                    Assert.True(file.Sources.Count >= 2, $"{modelId}/{file.RelativePath} has no backup source");
                    Assert.Equal("models.enviouslabs.co", file.Sources[0].Host);
                    Assert.True(file.SizeBytes <= 512L * 1024 * 1024, $"{modelId}/{file.RelativePath} is over the edge-cache ceiling and not sharded");
                }
            }
        }

        Assert.Equal(ManifestVerificationStatus.Unreachable, BundledModelManifests.Load("no-such-model", verifier).Status);
    }

    [Fact]
    public void LocatorPrefersOverrideThenStoreThenCompleteLegacyThenDevelopment()
    {
        var root = Directory.CreateTempSubdirectory("EnviousWispr.Locator.").FullName;
        try
        {
            var configured = Directory.CreateDirectory(Path.Combine(root, "configured")).FullName;
            var store = Directory.CreateDirectory(Path.Combine(root, "store")).FullName;
            var legacy = Directory.CreateDirectory(Path.Combine(root, "legacy")).FullName;
            var development = Directory.CreateDirectory(Path.Combine(root, "development")).FullName;
            var complete = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { store, legacy, development };
            bool IsComplete(string path) => complete.Contains(path);

            Assert.Equal(configured, InstalledModelLocator.Resolve(configured, store, legacy, development, IsComplete));
            Assert.Equal(store, InstalledModelLocator.Resolve(null, store, legacy, development, IsComplete));
            Assert.Equal(legacy, InstalledModelLocator.Resolve(null, null, legacy, development, IsComplete));

            // THE DEFECT: a legacy directory that EXISTS but is incomplete used to win, because the
            // store keeps its staging files under it, and the app then reported the model missing while
            // pointing at a directory that was there.
            complete.Remove(legacy);
            Assert.Equal(development, InstalledModelLocator.Resolve(null, null, legacy, development, IsComplete));
            complete.Remove(development);
            Assert.Null(InstalledModelLocator.Resolve(null, null, legacy, development, IsComplete));
            Assert.Null(InstalledModelLocator.Resolve(Path.Combine(root, "absent"), null, legacy, null, IsComplete));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }


    [Theory]
    [InlineData("mirror-serves-every-part", false, false)]
    [InlineData("one-part-is-missing", true, false)]
    [InlineData("one-part-is-corrupt", false, true)]
    public async Task ShardedArtifactIsReassembledFromPartsOrFallsBackToTheWholeFile(
        string scenario, bool missingPart, bool corruptPart)
    {
        var verifier = new ModelManifestVerifier(new Dictionary<string, string>());
        var partSizes = new[] { 1500, 1500, ModelBytes.Length - 3000 };
        var parts = new List<Dictionary<string, object?>>();
        var offset = 0;
        foreach (var (size, index) in partSizes.Select((size, index) => (size, index)))
        {
            parts.Add(new Dictionary<string, object?>
            {
                ["sizeBytes"] = size,
                ["sha256"] = Convert.ToHexString(SHA256.HashData(ModelBytes.AsSpan(offset, size))).ToLowerInvariant(),
                ["sources"] = new List<string> { $"https://mirror.invalid/speech/model.bin.part{index}" },
            });
            offset += size;
        }

        var manifest = verifier.VerifyBundled(BundledDocument(
            "speech",
            "1.0.0",
            sources: ["https://mirror.invalid/speech/model.bin", "https://backup.invalid/speech/model.bin"],
            parts: parts));
        Assert.True(manifest.Succeeded, manifest.Status.ToString());
        var requested = new List<string>();
        using var handler = new RoutingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            requested.Add(request.RequestUri.Host + path);
            if (path.EndsWith(".part1", StringComparison.Ordinal) && missingPart)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (path.Contains(".part", StringComparison.Ordinal))
            {
                var index = int.Parse(path[^1..], System.Globalization.CultureInfo.InvariantCulture);
                var start = partSizes.Take(index).Sum();
                var bytes = ModelBytes[start..(start + partSizes[index])].ToArray();
                if (index == 1 && corruptPart)
                {
                    bytes[0] ^= 0xFF;
                }

                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            }

            return request.RequestUri.Host == "mirror.invalid"
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ModelBytes) };
        });
        var root = Directory.CreateTempSubdirectory("EnviousWispr.Shards.").FullName;
        try
        {
            var store = new ModelStore(
                root,
                new HttpClient(handler, disposeHandler: false),
                verifier,
                new Version(1, 0, 0),
                new FixedDiskSpaceProbe(long.MaxValue),
                options: new ModelDeliveryOptions(DiskReserveBytes: 0, MaximumAttemptsPerSource: 1, RetryDelay: _ => TimeSpan.Zero));

            var result = await new ModelProvisioner(store, _ => manifest).ProvisionAsync("speech");

            Assert.True(result.Succeeded, $"{scenario}: {result.Failure}");
            var installed = Path.Combine(result.Installed!.DirectoryPath, "model.bin");
            Assert.Equal(ModelBytes, await File.ReadAllBytesAsync(installed));
            // NOTHING BUT THE FILES THE MANIFEST NAMES SURVIVES ADMISSION: no parts, no partials.
            Assert.DoesNotContain(Directory.GetFiles(result.Installed.DirectoryPath), path => path.Contains(".part", StringComparison.Ordinal));
            var wholeFileRequests = requested.Count(path => path.EndsWith("/model.bin", StringComparison.Ordinal));
            if (missingPart || corruptPart)
            {
                Assert.Equal(["mirror.invalid/speech/model.bin", "backup.invalid/speech/model.bin"], requested.Where(path => path.EndsWith("/model.bin", StringComparison.Ordinal)));
            }
            else
            {
                Assert.Equal(0, wholeFileRequests);
                Assert.Equal(3, requested.Count);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PartsThatDoNotAddUpToTheFileAreRefused()
    {
        var verifier = new ModelManifestVerifier(new Dictionary<string, string>());
        var parts = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["sizeBytes"] = ModelBytes.Length - 1,
                ["sha256"] = Convert.ToHexString(SHA256.HashData(ModelBytes)).ToLowerInvariant(),
                ["sources"] = new List<string> { "https://mirror.invalid/speech/model.bin.part0" },
            },
        };
        Assert.Equal(
            ManifestVerificationStatus.InvalidPayload,
            verifier.VerifyBundled(BundledDocument("speech", "1.0.0", parts: parts)).Status);
    }

    private static byte[] BundledDocument(
        string modelId,
        string version,
        string[]? sources = null,
        List<Dictionary<string, object?>>? parts = null)
    {
        sources ??= ["https://models.enviouslabs.co/test/model.bin"];
        var payload = new Dictionary<string, object?>
        {
            ["schemaVersion"] = ModelManifestVerifier.CurrentManifestSchemaVersion,
            ["modelId"] = modelId,
            ["version"] = version,
            ["minimumAppVersion"] = "1.0.0",
            ["license"] = new Dictionary<string, object?>
            {
                ["name"] = "Fixture",
                ["url"] = "https://example.invalid/license",
                ["notice"] = "Fixture weights for tests.",
            },
            ["files"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["relativePath"] = "model.bin",
                    ["sizeBytes"] = ModelBytes.Length,
                    ["sha256"] = Convert.ToHexString(SHA256.HashData(ModelBytes)).ToLowerInvariant(),
                    ["sources"] = sources,
                    ["parts"] = parts,
                },
            },
        };
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        payload[ModelManifestCanonicalJson.DigestPropertyName] = ModelManifestCanonicalJson.DigestOf(document.RootElement);
        return JsonSerializer.SerializeToUtf8Bytes(payload, Indented);
    }

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(route(request));
    }

    private sealed class FixedDiskSpaceProbe(long availableBytes) : IDiskSpaceProbe
    {
        public long GetAvailableBytes(string path) => availableBytes;
    }
}
