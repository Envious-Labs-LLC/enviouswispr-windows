using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EnviousWispr.Core.Runtime;
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

    /// <summary>Packs with no external upstream that serves the raw files, so the Envious Labs mirror is their only source.</summary>
    private static readonly HashSet<string> MirrorOnlyPacks = new(StringComparer.Ordinal)
    {
        CudaRuntimeDirectory.PackId,
        EnviousWispr.Core.Polish.EgOneModelCatalog.FullId,
        EnviousWispr.Core.Polish.EgOneModelCatalog.MiniId,
        EnviousWispr.Core.Polish.EgOneModelCatalog.MicroId,
    };

    [Fact]
    public void TheGraphicsRuntimePackCarriesExactlyTheFilesTheProbeRequiresFromOnePinnedRelease()
    {
        // THE PACK AND THE PROBE MUST AGREE. A file the probe requires and the pack lacks is a download that
        // installs and still leaves the card unused; a file the pack carries and nothing needs is size for
        // nothing. Named here as literals as well, so a change to both at once still has to be made on purpose.
        var loaded = BundledModelManifests.Load(
            CudaRuntimeDirectory.PackId,
            new ModelManifestVerifier(new Dictionary<string, string>()));
        Assert.True(loaded.Succeeded, loaded.Status.ToString());
        var files = loaded.Manifest!.Payload.Files.Select(file => file.RelativePath).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(
            EnviousWispr.Services.Runtime.CudaRuntimeDependencyProbe.RequiredLibraryNames.Order(StringComparer.Ordinal),
            files);
        Assert.Equal(
            [
                "cublas64_13.dll", "cublasLt64_13.dll", "cudart64_13.dll", "cudnn64_9.dll", "cudnn_adv64_9.dll",
                "cudnn_engines_precompiled64_9.dll", "cudnn_engines_runtime_compiled64_9.dll",
                "cudnn_engines_tensor_ir64_9.dll", "cudnn_graph64_9.dll", "cudnn_heuristic64_9.dll",
                "cudnn_ops64_9.dll", "cufft64_12.dll",
            ],
            files);
        Assert.All(
            loaded.Manifest.Payload.Files,
            file => Assert.Contains(
                "/cuda-runtime/cublas13.6.0.2-cudart13.3.29-cudnn9.24.0.43-cufft12.3.0.29/",
                file.Sources[0].AbsolutePath,
                StringComparison.Ordinal));
        Assert.Contains("docs.nvidia.com/cuda/eula", loaded.Manifest.Payload.License.Url.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("cudnn", loaded.Manifest.Payload.License.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCorePointerReadNamesTheDirectoryTheStoreAdmittedThePackTo()
    {
        // THE TWIN. Core reads the store's pointer without the store (a harness has none); this installs a
        // pack through the real store and requires both to name the same folder.
        var verifier = new ModelManifestVerifier(new Dictionary<string, string>());
        var manifest = verifier.VerifyBundled(BundledDocument(CudaRuntimeDirectory.PackId, "1.0.0"));
        Assert.True(manifest.Succeeded);
        using var handler = new RoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ModelBytes) });
        var data = Directory.CreateTempSubdirectory("EnviousWispr.CudaPack.").FullName;
        try
        {
            var store = new ModelStore(
                Path.Combine(data, "models"),
                new HttpClient(handler, disposeHandler: false),
                verifier,
                new Version(1, 0, 0),
                new FixedDiskSpaceProbe(long.MaxValue),
                options: new ModelDeliveryOptions(DiskReserveBytes: 0, MaximumAttemptsPerSource: 1, RetryDelay: _ => TimeSpan.Zero));
            Assert.Null(CudaRuntimeDirectory.ActivePackDirectory(data, path => File.Exists(path) ? File.ReadAllText(path) : null));

            var result = await new ModelProvisioner(store, _ => manifest).ProvisionAsync(CudaRuntimeDirectory.PackId);

            Assert.True(result.Succeeded, result.Failure.ToString());
            var active = await store.OpenActiveOfflineAsync(CudaRuntimeDirectory.PackId);
            Assert.True(active.Succeeded);
            Assert.Equal(
                Path.GetFullPath(active.Installed!.DirectoryPath),
                CudaRuntimeDirectory.ActivePackDirectory(data, path => File.Exists(path) ? File.ReadAllText(path) : null));
            Assert.True(File.Exists(Path.Combine(active.Installed.DirectoryPath, "model.bin")));
        }
        finally
        {
            Directory.Delete(data, recursive: true);
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
            if (MirrorOnlyPacks.Contains(modelId))
            {
                // NO EXTERNAL UPSTREAM SERVES THESE FILES: NVIDIA CUDA redistributables ship inside archives
                // and first-party EG-1 models are hosted solely on our own R2 bucket. Every file is whole
                // (or a native GGUF shard), under the edge-cache ceiling, and pinned under its own prefix.
                var isEgOne = EnviousWispr.Core.Polish.EgOneModelCatalog.IsKnown(modelId);
                var expectedHost = isEgOne ? "models.enviouswispr.com" : "models.enviouslabs.co";
                var expectedPrefix = isEgOne ? "/eg1/" : $"/{modelId}/";
                foreach (var file in loaded.Manifest.Payload.Files)
                {
                    var source = Assert.Single(file.Sources);
                    Assert.Equal(expectedHost, source.Host);
                    Assert.StartsWith(expectedPrefix, source.AbsolutePath, StringComparison.Ordinal);
                    Assert.EndsWith("/" + file.RelativePath, source.AbsolutePath, StringComparison.Ordinal);
                    Assert.False(file.IsSharded, $"{modelId}/{file.RelativePath} is sharded with no whole-file source to fall back to");
                    Assert.True(file.SizeBytes <= 512L * 1024 * 1024, $"{modelId}/{file.RelativePath} is over the edge-cache ceiling");
                }

                continue;
            }

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

        Assert.Contains(ParakeetModelIds.Final, BundledModelManifests.ModelIds);
        Assert.DoesNotContain(ParakeetModelIds.LegacyV3, BundledModelManifests.ModelIds);
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

    [Fact]
    public async Task ParakeetModelLocatorPrefersUltraOverLegacyDonorAndTriggersSilentUpgradeOnlyForDonor()
    {
        var tempRoot = Directory.CreateTempSubdirectory("EnviousWispr.ParakeetLocator.").FullName;
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            var storeRoot = Path.Combine(tempRoot, "data", "models");
            var overrideDir = Directory.CreateDirectory(Path.Combine(tempRoot, "override")).FullName;
            var devUltraDir = Directory.CreateDirectory(Path.Combine(tempRoot, "repo", "models", ParakeetModelIds.Final)).FullName;
            var devLegacyDir = Directory.CreateDirectory(Path.Combine(tempRoot, "repo", "models", ParakeetModelIds.LegacyV3)).FullName;
            var donorLegacyDataDir = Directory.CreateDirectory(Path.Combine(storeRoot, ParakeetModelIds.LegacyV3)).FullName;
            var ultraLegacyDataDir = Directory.CreateDirectory(Path.Combine(storeRoot, ParakeetModelIds.Final)).FullName;

            WriteCompleteParakeetPack(devUltraDir);
            WriteCompleteParakeetPack(devLegacyDir);
            WriteCompleteParakeetPack(donorLegacyDataDir);
            WriteCompleteParakeetPack(ultraLegacyDataDir);

            var verifier = new ModelManifestVerifier(new Dictionary<string, string>
            {
                ["test-2026"] = ModelManifestSigning.ExportPublicKeyPem(key),
            });
            var payloadBytes = Encoding.UTF8.GetBytes("synthetic-parakeet-onnx-bytes");
            using var handler = new RoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payloadBytes),
            });
            var store = new ModelStore(
                storeRoot,
                new HttpClient(handler, disposeHandler: false),
                verifier,
                new Version(1, 0, 0),
                new FixedDiskSpaceProbe(long.MaxValue),
                options: new ModelDeliveryOptions(DiskReserveBytes: 0, MaximumAttemptsPerSource: 1, RetryDelay: _ => TimeSpan.Zero));

            Assert.True((await store.InstallAsync(SignedParakeetEnvelope(key, ParakeetModelIds.Final, payloadBytes))).Succeeded);
            Assert.True((await store.InstallAsync(SignedParakeetEnvelope(key, ParakeetModelIds.LegacyV3, payloadBytes))).Succeeded);

            // 1. Override wins over everything and never schedules silent upgrade.
            var fromOverride = await ParakeetModelLocator.ResolveAsync(store, storeRoot, overrideDir, devUltraDir);
            Assert.Equal(ParakeetModelSource.Override, fromOverride.Source);
            Assert.Equal(overrideDir, fromOverride.DirectoryPath);
            Assert.True(fromOverride.IsUltra);
            Assert.False(fromOverride.ShouldTriggerSilentUpgrade);

            // 2. Ultra store beats Ultra legacy data, Ultra development, and legacy donors.
            var fromUltraStore = await ParakeetModelLocator.ResolveAsync(store, storeRoot, null, devUltraDir);
            Assert.Equal(ParakeetModelSource.UltraStore, fromUltraStore.Source);
            Assert.True(fromUltraStore.IsUltra);
            Assert.True(fromUltraStore.IsUltraStoreOrData);
            Assert.False(fromUltraStore.ShouldTriggerSilentUpgrade);

            // 3. Remove Ultra store -> falls back to Ultra legacy data.
            Assert.True((await store.RemoveVersionAsync(ParakeetModelIds.Final, "1.0.0")).Succeeded);
            var fromUltraLegacy = await ParakeetModelLocator.ResolveAsync(store, storeRoot, null, devUltraDir);
            Assert.Equal(ParakeetModelSource.UltraLegacyData, fromUltraLegacy.Source);
            Assert.Equal(ultraLegacyDataDir, fromUltraLegacy.DirectoryPath);
            Assert.True(fromUltraLegacy.IsUltraStoreOrData);
            Assert.False(fromUltraLegacy.ShouldTriggerSilentUpgrade);

            // 4. Remove Ultra legacy data -> falls back to Ultra development checkout.
            Directory.Delete(ultraLegacyDataDir, recursive: true);
            var fromUltraDev = await ParakeetModelLocator.ResolveAsync(store, storeRoot, null, devUltraDir);
            Assert.Equal(ParakeetModelSource.UltraDevelopment, fromUltraDev.Source);
            Assert.Equal(devUltraDir, fromUltraDev.DirectoryPath);
            Assert.True(fromUltraDev.IsUltra);
            Assert.False(fromUltraDev.IsUltraStoreOrData);
            Assert.False(fromUltraDev.ShouldTriggerSilentUpgrade);

            // 5. No Ultra anywhere -> falls back to LegacyV3 store donor and flags silent upgrade.
            Directory.Delete(devUltraDir, recursive: true);
            var fromLegacyStore = await ParakeetModelLocator.ResolveAsync(store, storeRoot, null, devUltraDir);
            Assert.Equal(ParakeetModelSource.LegacyV3DonorStore, fromLegacyStore.Source);
            Assert.True(fromLegacyStore.IsInstalled);
            Assert.True(fromLegacyStore.IsLegacyDonor);
            Assert.True(fromLegacyStore.ShouldTriggerSilentUpgrade);

            // 6. Remove LegacyV3 store -> falls back to LegacyV3 data-folder donor.
            Assert.True((await store.RemoveVersionAsync(ParakeetModelIds.LegacyV3, "1.0.0")).Succeeded);
            var fromLegacyData = await ParakeetModelLocator.ResolveAsync(store, storeRoot, null, devUltraDir);
            Assert.Equal(ParakeetModelSource.LegacyV3DonorData, fromLegacyData.Source);
            Assert.Equal(donorLegacyDataDir, fromLegacyData.DirectoryPath);
            Assert.True(fromLegacyData.IsLegacyDonor);
            Assert.True(fromLegacyData.ShouldTriggerSilentUpgrade);

            // 7. Remove LegacyV3 data folder -> returns None even though devLegacyDir exists in repo checkout.
            Directory.Delete(donorLegacyDataDir, recursive: true);
            var absent = await ParakeetModelLocator.ResolveAsync(store, storeRoot, null, devUltraDir);
            Assert.Equal(ParakeetModelSource.None, absent.Source);
            Assert.Null(absent.DirectoryPath);
            Assert.False(absent.IsInstalled);
            Assert.False(absent.ShouldTriggerSilentUpgrade);
            Assert.True(Directory.Exists(devLegacyDir));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ParakeetModelLocatorRetiresLegacyV3SafelyOnlyAfterUltraInstallAndNeverDeletesCheckout()
    {
        var tempRoot = Directory.CreateTempSubdirectory("EnviousWispr.ParakeetRetire.").FullName;
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            var storeRoot = Path.Combine(tempRoot, "data", "models");
            var devLegacyDir = Directory.CreateDirectory(Path.Combine(tempRoot, "repo", "models", ParakeetModelIds.LegacyV3)).FullName;
            var donorLegacyDataDir = Directory.CreateDirectory(Path.Combine(storeRoot, ParakeetModelIds.LegacyV3)).FullName;
            WriteCompleteParakeetPack(devLegacyDir);
            WriteCompleteParakeetPack(donorLegacyDataDir);

            var verifier = new ModelManifestVerifier(new Dictionary<string, string>
            {
                ["test-2026"] = ModelManifestSigning.ExportPublicKeyPem(key),
            });
            var payloadBytes = Encoding.UTF8.GetBytes("synthetic-parakeet-onnx-bytes");
            using var handler = new RoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payloadBytes),
            });
            var store = new ModelStore(
                storeRoot,
                new HttpClient(handler, disposeHandler: false),
                verifier,
                new Version(1, 0, 0),
                new FixedDiskSpaceProbe(long.MaxValue),
                options: new ModelDeliveryOptions(DiskReserveBytes: 0, MaximumAttemptsPerSource: 1, RetryDelay: _ => TimeSpan.Zero));

            Assert.True((await store.InstallAsync(SignedParakeetEnvelope(key, ParakeetModelIds.LegacyV3, payloadBytes))).Succeeded);

            // Refuses to retire legacy v3 before Ultra is installed in store or data folder.
            var beforeUltra = await ParakeetModelLocator.RetireLegacyV3AfterUltraInstalledAsync(
                store,
                storeRoot,
                unloadEnginesAsync: () => Task.CompletedTask,
                developmentLegacyDirectory: devLegacyDir);
            Assert.False(beforeUltra.Succeeded);
            Assert.True(Directory.Exists(donorLegacyDataDir));
            Assert.NotEmpty(await store.ListInstalledAsync(ParakeetModelIds.LegacyV3));

            // Install Ultra into store -> retire legacy v3 cleanly, leaving Ultra active and devLegacyDir intact.
            Assert.True((await store.InstallAsync(SignedParakeetEnvelope(key, ParakeetModelIds.Final, payloadBytes))).Succeeded);
            var unloaded = false;
            var retired = await ParakeetModelLocator.RetireLegacyV3AfterUltraInstalledAsync(
                store,
                storeRoot,
                unloadEnginesAsync: () =>
                {
                    unloaded = true;
                    return Task.CompletedTask;
                },
                developmentLegacyDirectory: devLegacyDir);
            Assert.True(retired.Succeeded);
            Assert.True(unloaded);
            Assert.Empty(await store.ListInstalledAsync(ParakeetModelIds.LegacyV3));
            Assert.False(Directory.Exists(donorLegacyDataDir));
            Assert.True((await store.OpenActiveOfflineAsync(ParakeetModelIds.Final)).Succeeded);
            Assert.True(Directory.Exists(devLegacyDir));

            // Guard check: if storeRoot ever equalled the repo models directory, legacy checkout deletion is refused.
            var repoModelsRoot = Path.Combine(tempRoot, "repo", "models");
            Assert.False(ParakeetModelLocator.TryGetSafeLegacyDataDirectory(repoModelsRoot, devLegacyDir, out _));

            // Remove Parakeet path: even without Ultra installed, RemoveLegacyV3FromStoreOrDataAsync cleans leftover legacy store + data files.
            Assert.True((await store.InstallAsync(SignedParakeetEnvelope(key, ParakeetModelIds.LegacyV3, payloadBytes))).Succeeded);
            WriteCompleteParakeetPack(donorLegacyDataDir);
            Assert.True(await ParakeetModelLocator.HasAnyInstalledPackageAsync(store, storeRoot));
            var removeBoth = await ModelPackageCleaner.RemoveAsync(
                store,
                ParakeetModelIds.Final,
                Path.Combine(storeRoot, ParakeetModelIds.Final),
                () => Task.CompletedTask);
            var removeLegacy = await ParakeetModelLocator.RemoveLegacyV3FromStoreOrDataAsync(
                store,
                storeRoot,
                () => Task.CompletedTask,
                devLegacyDir);
            Assert.True(removeBoth.Succeeded);
            Assert.True(removeLegacy.Succeeded);
            Assert.False(await ParakeetModelLocator.HasAnyInstalledPackageAsync(store, storeRoot));
            Assert.False(Directory.Exists(donorLegacyDataDir));
            Assert.True(Directory.Exists(devLegacyDir));

            // Upgrade action evaluator & active legacy path detection.
            Assert.Equal(
                ParakeetUpgradeAction.None,
                await ParakeetModelLocator.EvaluateUpgradeActionAsync(store, storeRoot, null, developmentUltraDirectory: null, devLegacyDir));
            WriteCompleteParakeetPack(donorLegacyDataDir);
            Assert.Equal(
                ParakeetUpgradeAction.StartSilentUpgrade,
                await ParakeetModelLocator.EvaluateUpgradeActionAsync(store, storeRoot, null, developmentUltraDirectory: null, devLegacyDir));
            Assert.Equal(
                ParakeetUpgradeAction.None,
                await ParakeetModelLocator.EvaluateUpgradeActionAsync(store, storeRoot, donorLegacyDataDir, developmentUltraDirectory: null, devLegacyDir));
            Assert.True(ParakeetModelLocator.IsDirectoryUnderLegacyV3(donorLegacyDataDir, storeRoot));
            Assert.True(ParakeetModelLocator.IsDirectoryUnderLegacyV3(Path.Combine(donorLegacyDataDir, "versions", "1.0.0"), storeRoot));
            Assert.False(ParakeetModelLocator.IsDirectoryUnderLegacyV3(Path.Combine(storeRoot, ParakeetModelIds.Final), storeRoot));
            // Corrupt hand-copied Ultra folder must NOT trigger legacy v3 retirement even if all filenames exist.
            var corruptUltraDir = Path.Combine(storeRoot, ParakeetModelIds.Final);
            WriteCompleteParakeetPack(corruptUltraDir);
            Assert.False(await ParakeetModelLocator.IsUltraInstalledInStoreOrDataAsync(store, storeRoot));
            var blockedRetire = await ParakeetModelLocator.RetireLegacyV3AfterUltraInstalledAsync(
                store,
                storeRoot,
                unloadEnginesAsync: () => Task.CompletedTask,
                developmentLegacyDirectory: devLegacyDir);
            Assert.False(blockedRetire.Succeeded);
            Assert.Equal(ModelDeliveryFailure.VersionNotInstalled, blockedRetire.Failure);
            Assert.True(Directory.Exists(donorLegacyDataDir));
            Directory.Delete(corruptUltraDir, recursive: true);

            Assert.True((await store.InstallAsync(SignedParakeetEnvelope(key, ParakeetModelIds.Final, payloadBytes))).Succeeded);
            Assert.Equal(
                ParakeetUpgradeAction.RetireLegacyV3,
                await ParakeetModelLocator.EvaluateUpgradeActionAsync(store, storeRoot, null, developmentUltraDirectory: null, devLegacyDir));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static readonly string[] ParakeetPackFileNames =
        ["nemo128.onnx", "vocab.txt", "encoder-model.int8.onnx", "decoder_joint-model.int8.onnx"];

    private static void WriteCompleteParakeetPack(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var fileName in ParakeetPackFileNames)
        {
            File.WriteAllBytes(Path.Combine(directory, fileName), [1, 2, 3, 4]);
        }
    }

    private static byte[] SignedParakeetEnvelope(ECDsa key, string modelId, byte[] bytes)
    {
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var artifacts = ParakeetPackFileNames
            .Select(name => new ModelArtifact(
                name,
                bytes.Length,
                sha,
                [new Uri($"https://example.invalid/{modelId}/1.0.0/{name}")]))
            .ToArray();
        var payload = new ModelManifestPayload(
            ModelManifestVerifier.CurrentManifestSchemaVersion,
            modelId,
            "1.0.0",
            "1.0.0",
            new ModelLicenseNotice("Fixture", new Uri("https://example.invalid/license"), "Test weights."),
            artifacts);
        return ModelManifestSigning.CreateEnvelope(payload, "test-2026", key);
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
