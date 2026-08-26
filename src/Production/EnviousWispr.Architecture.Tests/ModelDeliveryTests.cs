using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using EnviousWispr.ModelDelivery;

namespace EnviousWispr.Architecture.Tests;

public sealed class ModelDeliveryTests
{
    [Fact]
    public void ManifestRequiresTrustedSignatureAndSafePinnedFiles()
    {
        using var fixture = new ModelFixture();
        var envelope = fixture.Envelope("speech", "1.0.0", [1, 2, 3]);

        var verified = fixture.Verifier.Verify(envelope);
        Assert.True(verified.Succeeded);

        envelope[^1] ^= 1;
        Assert.NotEqual(ManifestVerificationStatus.Verified, fixture.Verifier.Verify(envelope).Status);

        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var untrusted = new ModelManifestVerifier(new Dictionary<string, string>
        {
            ["other"] = ModelManifestSigning.ExportPublicKeyPem(otherKey),
        });
        Assert.Equal(ManifestVerificationStatus.UntrustedKey, untrusted.Verify(
            fixture.Envelope("speech", "1.0.0", [1])).Status);

        var unsafePayload = ModelFixture.Payload("speech", "1.0.0", [1]) with
        {
            Files =
            [
                ModelFixture.Artifact([1]) with { RelativePath = "../outside.bin" },
            ],
        };
        Assert.Equal(
            ManifestVerificationStatus.InvalidPayload,
            fixture.Verifier.Verify(fixture.Envelope(unsafePayload)).Status);
    }

    [Fact]
    public async Task InterruptedDownloadResumesWithRangeAndValidator()
    {
        using var fixture = new ModelFixture();
        var bytes = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var envelope = fixture.Envelope("speech", "1.0.0", bytes);
        var requestCount = 0;
        RangeHeaderValue? observedRange = null;
        string? observedIfRange = null;
        using var handler = new RoutingHandler(request =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                var interrupted = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new InterruptingStream(bytes, 8)),
                };
                interrupted.Headers.ETag = new EntityTagHeaderValue("\"fixture-v1\"");
                return interrupted;
            }

            observedRange = request.Headers.Range;
            observedIfRange = request.Headers.TryGetValues("If-Range", out var values)
                ? values.Single()
                : null;
            var offset = checked((int)(request.Headers.Range?.Ranges.Single().From ?? 0));
            var resumed = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(bytes[offset..]),
            };
            resumed.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                offset,
                bytes.Length - 1,
                bytes.Length);
            resumed.Headers.ETag = new EntityTagHeaderValue("\"fixture-v1\"");
            return resumed;
        });
        var root = fixture.CreateStoreDirectory();
        var store = fixture.Store(root, handler, maximumAttempts: 1);

        var interrupted = await store.InstallAsync(envelope);
        Assert.False(interrupted.Succeeded);

        var resumed = await store.InstallAsync(envelope);
        Assert.True(resumed.Succeeded);
        Assert.Equal(8, observedRange?.Ranges.Single().From);
        Assert.Equal("\"fixture-v1\"", observedIfRange);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(
            resumed.Installed!.DirectoryPath,
            "model.bin")));
    }

    [Fact]
    public async Task CorruptArtifactIsNeverAdmitted()
    {
        using var fixture = new ModelFixture();
        var expected = new byte[] { 1, 2, 3, 4 };
        using var handler = new RoutingHandler(_ => Ok([4, 3, 2, 1]));
        var root = fixture.CreateStoreDirectory();

        var result = await fixture.Store(root, handler, maximumAttempts: 1)
            .InstallAsync(fixture.Envelope("speech", "1.0.0", expected));

        Assert.False(result.Succeeded);
        Assert.Equal(ModelDeliveryFailure.IntegrityMismatch, result.Failure);
        Assert.False(File.Exists(Path.Combine(root, "speech", "active.json")));
        Assert.Empty(Directory.Exists(Path.Combine(root, "speech", "versions"))
            ? Directory.GetFiles(Path.Combine(root, "speech", "versions"), "model.bin", SearchOption.AllDirectories)
            : []);
    }

    [Fact]
    public async Task PermanentPrimaryFailureFallsBackToPinnedSecondarySource()
    {
        using var fixture = new ModelFixture();
        var bytes = new byte[] { 5, 6, 7, 8 };
        var primaryRequests = 0;
        var secondaryRequests = 0;
        using var handler = new RoutingHandler(request =>
        {
            if (request.RequestUri!.Host == "primary.invalid")
            {
                primaryRequests++;
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            secondaryRequests++;
            return Ok(bytes);
        });
        var payload = ModelFixture.Payload("speech", "1.0.0", bytes) with
        {
            Files =
            [
                ModelFixture.Artifact(bytes) with
                {
                    Sources =
                    [
                        new Uri("https://primary.invalid/model.bin"),
                        new Uri("https://secondary.invalid/model.bin"),
                    ],
                },
            ],
        };

        var result = await fixture.Store(fixture.CreateStoreDirectory(), handler)
            .InstallAsync(fixture.Envelope(payload));

        Assert.True(result.Succeeded);
        Assert.Equal(1, primaryRequests);
        Assert.Equal(1, secondaryRequests);
    }

    [Fact]
    public async Task InsufficientDiskFailsBeforeNetworkOrMutation()
    {
        using var fixture = new ModelFixture();
        var requests = 0;
        using var handler = new RoutingHandler(_ =>
        {
            requests++;
            return Ok([1, 2, 3]);
        });
        var root = fixture.CreateStoreDirectory();
        var store = fixture.Store(root, handler, availableBytes: 2);

        var result = await store.InstallAsync(fixture.Envelope("speech", "1.0.0", [1, 2, 3]));

        Assert.False(result.Succeeded);
        Assert.Equal(ModelDeliveryFailure.InsufficientDisk, result.Failure);
        Assert.Equal(0, requests);
        Assert.False(File.Exists(Path.Combine(root, "speech", "active.json")));
    }

    [Fact]
    public async Task UpgradeDowngradeCleanupAndOfflineReuseRemainPinned()
    {
        using var fixture = new ModelFixture();
        var v1 = new byte[] { 1, 1, 1 };
        var v2 = new byte[] { 2, 2, 2, 2 };
        using var handler = new RoutingHandler(request =>
            Ok(request.RequestUri!.AbsolutePath.Contains("2.0.0", StringComparison.Ordinal) ? v2 : v1));
        var root = fixture.CreateStoreDirectory();
        var store = fixture.Store(root, handler);

        Assert.True((await store.InstallAsync(fixture.Envelope("speech", "1.0.0", v1))).Succeeded);
        Assert.True((await store.InstallAsync(fixture.Envelope("speech", "2.0.0", v2))).Succeeded);
        Assert.Equal("2.0.0", (await store.OpenActiveOfflineAsync("speech")).Installed!.Version);

        Assert.True((await store.ActivateAsync("speech", "1.0.0")).Succeeded);
        var offline = await store.OpenActiveOfflineAsync("speech");
        Assert.True(offline.Succeeded);
        Assert.Equal("1.0.0", offline.Installed!.Version);
        Assert.Equal(v1, await File.ReadAllBytesAsync(Path.Combine(offline.Installed.DirectoryPath, "model.bin")));
        var inventory = await store.ListInstalledAsync("speech");
        Assert.Equal(2, inventory.Count);
        Assert.All(inventory, item => Assert.Equal("Fixture license", item.License.Name));

        Assert.True((await store.ActivateAsync("speech", "2.0.0")).Succeeded);
        Assert.True((await store.CleanupAsync("speech", inactiveVersionsToKeep: 0)).Succeeded);
        Assert.False(Directory.Exists(Path.Combine(root, "speech", "versions", "1.0.0")));
        Assert.True((await store.OpenActiveOfflineAsync("speech")).Succeeded);
    }

    [Fact]
    public async Task LegacyMigrationAndCleanRemovalTouchOnlySelectedModel()
    {
        using var fixture = new ModelFixture();
        var speechBytes = new byte[] { 9, 8, 7 };
        var polishBytes = new byte[] { 6, 5, 4 };
        using var handler = new RoutingHandler(request =>
            Ok(request.RequestUri!.AbsolutePath.Contains("polish", StringComparison.Ordinal)
                ? polishBytes
                : speechBytes));
        var root = fixture.CreateStoreDirectory();
        var legacyRoot = Path.Combine(root, "speech");
        Directory.CreateDirectory(legacyRoot);
        await File.WriteAllBytesAsync(Path.Combine(legacyRoot, "model.bin"), speechBytes);
        var store = fixture.Store(root, handler);

        var migrated = await store.MigrateLegacyAsync(
            fixture.Envelope("speech", "1.0.0", speechBytes));
        Assert.True(migrated.Succeeded);
        Assert.False(File.Exists(Path.Combine(legacyRoot, "model.bin")));
        Assert.True((await store.OpenActiveOfflineAsync("speech")).Succeeded);

        Assert.True((await store.InstallAsync(fixture.Envelope("polish", "1.0.0", polishBytes))).Succeeded);
        Assert.True((await store.RemoveVersionAsync("speech", "1.0.0")).Succeeded);
        Assert.Equal(
            ModelDeliveryFailure.VersionNotInstalled,
            (await store.OpenActiveOfflineAsync("speech")).Failure);
        Assert.True((await store.OpenActiveOfflineAsync("polish")).Succeeded);
        await Assert.ThrowsAsync<ArgumentException>(() => store.RemoveVersionAsync("polish", ".."));
    }

    private static HttpResponseMessage Ok(byte[] bytes) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes),
    };

    private sealed class ModelFixture : IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly List<string> _directories = [];

        public ModelFixture()
        {
            Verifier = new ModelManifestVerifier(new Dictionary<string, string>
            {
                ["test-2026"] = ModelManifestSigning.ExportPublicKeyPem(_key),
            });
        }

        public ModelManifestVerifier Verifier { get; }

        public static ModelManifestPayload Payload(string modelId, string version, byte[] bytes) => new(
            ModelManifestVerifier.CurrentManifestSchemaVersion,
            modelId,
            version,
            "1.0.0",
            new ModelLicenseNotice(
                "Fixture license",
                new Uri("https://example.invalid/license"),
                "Fixture weights are used only by model-delivery tests."),
            [Artifact(bytes, new Uri($"https://example.invalid/{modelId}/{version}/model.bin"))]);

        public static ModelArtifact Artifact(byte[] bytes, Uri? source = null) => new(
            "model.bin",
            bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            [source ?? new Uri("https://example.invalid/model.bin")]);

        public byte[] Envelope(string modelId, string version, byte[] bytes) =>
            Envelope(Payload(modelId, version, bytes));

        public byte[] Envelope(ModelManifestPayload payload) =>
            ModelManifestSigning.CreateEnvelope(payload, "test-2026", _key);

        public string CreateStoreDirectory()
        {
            var directory = Directory.CreateTempSubdirectory("EnviousWispr.ModelDelivery.").FullName;
            _directories.Add(directory);
            return directory;
        }

        public ModelStore Store(
            string root,
            HttpMessageHandler handler,
            long availableBytes = long.MaxValue,
            int maximumAttempts = 2) => new(
                root,
                new HttpClient(handler, disposeHandler: false),
                Verifier,
                new Version(1, 0, 0),
                new FixedDiskSpaceProbe(availableBytes),
                options: new ModelDeliveryOptions(
                    DiskReserveBytes: 0,
                    MaximumAttemptsPerSource: maximumAttempts,
                    RetryDelay: _ => TimeSpan.Zero));

        public void Dispose()
        {
            _key.Dispose();
            foreach (var directory in _directories)
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }
    }

    private sealed class FixedDiskSpaceProbe(long availableBytes) : IDiskSpaceProbe
    {
        public long GetAvailableBytes(string path) => availableBytes;
    }

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> route)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(route(request));
    }

    private sealed class InterruptingStream(byte[] bytes, int bytesBeforeFailure) : Stream
    {
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => _offset; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_offset >= bytesBeforeFailure)
            {
                throw new HttpRequestException("Synthetic interrupted response.");
            }

            var count = Math.Min(buffer.Length, bytesBeforeFailure - _offset);
            bytes.AsSpan(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
