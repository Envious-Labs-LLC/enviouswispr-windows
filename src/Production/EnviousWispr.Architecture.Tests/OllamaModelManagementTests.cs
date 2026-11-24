using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using EnviousWispr.Core.Polish;
using EnviousWispr.LLM;
using EnviousWispr.Services.AppImport;

namespace EnviousWispr.Architecture.Tests;

/// <summary>Downloading and removing local models through Ollama, against scripted streams. Ref: #213.</summary>
public sealed class OllamaModelManagementTests
{
    private const string Endpoint = "http://127.0.0.1:11434";

    [Fact]
    public async Task ADownloadSucceedsOnlyWhenOllamaSaysSuccessAndReportsEachPhase()
    {
        var handler = new StreamHandler(Chunks(
            """{"status":"pulling manifest"}""",
            """{"status":"pulling aaa","digest":"sha256:aaa","total":1000,"completed":500}""",
            """{"status":"pulling aaa","digest":"sha256:aaa","total":1000,"completed":1000}""",
            """{"status":"pulling bbb","digest":"sha256:bbb","total":20,"completed":20}""",
            """{"status":"verifying sha256 digest"}""",
            """{"status":"writing manifest"}""",
            """{"status":"success"}"""));
        await using var client = new OllamaApiClient(Endpoint, handler);
        var seen = new List<OllamaPullUpdate>();

        var outcome = await client.PullAsync("qwen3:0.6b", new Collect(seen), CancellationToken.None);

        Assert.Equal(OllamaPullOutcome.Succeeded, outcome);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal(new Uri($"{Endpoint}/api/pull"), handler.Uri);
        Assert.Contains("\"model\":\"qwen3:0.6b\"", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"stream\":true", handler.Body, StringComparison.Ordinal);
        Assert.Equal(
            [OllamaPullPhase.Starting, OllamaPullPhase.Starting, OllamaPullPhase.Downloading, OllamaPullPhase.Downloading,
             OllamaPullPhase.Downloading, OllamaPullPhase.Verifying, OllamaPullPhase.Finishing],
            seen.Select(update => update.Phase));
        Assert.Equal("sha256:bbb", seen[4].Digest);
        Assert.Equal(1000, seen[3].Total);
    }

    /// <summary>A layer at 100% is not the model: a stream that stops before "success" was cut.</summary>
    [Fact]
    public async Task AStreamThatEndsBeforeSuccessIsInterruptedWhateverTheBytesSaid()
    {
        var handler = new StreamHandler(Chunks(
            """{"status":"pulling aaa","digest":"sha256:aaa","total":1000,"completed":1000}"""));
        await using var client = new OllamaApiClient(Endpoint, handler);

        Assert.Equal(OllamaPullOutcome.Interrupted, await client.PullAsync("qwen3:0.6b", null, CancellationToken.None));
    }

    /// <summary>The network hands over bytes where it likes; a line split across reads is still one line.</summary>
    [Fact]
    public async Task ALineSplitAcrossReadsIsReadWhole()
    {
        var text = "{\"status\":\"pulling manifest\"}\r\n{\"status\":\"succ" + "ess\"}\n";
        var bytes = Encoding.UTF8.GetBytes(text);
        var pieces = bytes.Chunk(3).Select(piece => (piece, TimeSpan.Zero)).ToArray();
        await using var client = new OllamaApiClient(Endpoint, new StreamHandler(pieces));

        Assert.Equal(OllamaPullOutcome.Succeeded, await client.PullAsync("qwen3:0.6b", null, CancellationToken.None));
    }

    [Theory]
    [InlineData("pull model manifest: file does not exist", OllamaPullOutcome.NotFound)]
    [InlineData("write /models/blobs/x: no space left on device", OllamaPullOutcome.DiskFull)]
    [InlineData("Get \"https://registry.ollama.ai/v2/\": dial tcp: lookup registry.ollama.ai: no such host", OllamaPullOutcome.NetworkFailed)]
    [InlineData("something Ollama has never said before", OllamaPullOutcome.Refused)]
    public async Task AnErrorLineEndsTheDownloadAsWhatItMeans(string message, OllamaPullOutcome expected)
    {
        var line = "{\"error\":" + System.Text.Json.JsonSerializer.Serialize(message) + "}";
        await using var client = new OllamaApiClient(Endpoint, new StreamHandler(Chunks("""{"status":"pulling manifest"}""", line)));

        Assert.Equal(expected, await client.PullAsync("qwen3:0.6b", null, CancellationToken.None));
    }

    /// <summary>Ollama hashes a big layer without a word. A silence is reported as quiet and the download goes on.</summary>
    [Fact]
    public async Task ASilenceIsQuietNotAFailure()
    {
        var handler = new StreamHandler(
        [
            (Line("""{"status":"verifying sha256 digest"}"""), TimeSpan.Zero),
            (Line("""{"status":"success"}"""), TimeSpan.FromMilliseconds(400)),
        ]);
        await using var client = new OllamaApiClient(Endpoint, handler);
        var seen = new List<OllamaPullUpdate>();

        var outcome = await client.PullAsync("qwen3:0.6b", new Collect(seen), CancellationToken.None, quietAfter: TimeSpan.FromMilliseconds(50));

        Assert.Equal(OllamaPullOutcome.Succeeded, outcome);
        Assert.Contains(seen, update => update is { Quiet: true, Phase: OllamaPullPhase.Verifying });
    }

    [Fact]
    public async Task AStopEndsTheDownloadAsCancelled()
    {
        using var stop = new CancellationTokenSource();
        var handler = new StreamHandler(
        [
            (Line("""{"status":"pulling aaa","digest":"sha256:aaa","total":10,"completed":1}"""), TimeSpan.Zero),
            (Line("""{"status":"success"}"""), TimeSpan.FromSeconds(30)),
        ]);
        await using var client = new OllamaApiClient(Endpoint, handler);
        var pull = client.PullAsync("qwen3:0.6b", new Collect([], _ => stop.Cancel()), stop.Token);

        Assert.Equal(OllamaPullOutcome.Cancelled, await pull.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task AGarbledLineIsRefusedNotThrown()
    {
        await using var client = new OllamaApiClient(Endpoint, new StreamHandler(Chunks("<html>proxy error</html>")));

        Assert.Equal(OllamaPullOutcome.Refused, await client.PullAsync("qwen3:0.6b", null, CancellationToken.None));
    }

    /// <summary>A string the JSON reader cannot decode throws from GetString, not Parse; it ends the download, never escapes it.</summary>
    [Fact]
    public async Task AnUndecodableLineIsRefusedNotThrown()
    {
        var bad = "{\"status\":\"" + (char)92 + "uD800\"}";
        await using var client = new OllamaApiClient(Endpoint, new StreamHandler(Chunks(bad)));

        Assert.Equal(OllamaPullOutcome.Refused, await client.PullAsync("qwen3:0.6b", null, CancellationToken.None));
    }

    /// <summary>The endpoint is checked to be this PC; a redirect would send the request wherever the answer said. Ref: #213 review.</summary>
    [Fact]
    public async Task ARedirectFromTheEndpointIsNotFollowed()
    {
        var elsewhere = new TcpListener(IPAddress.Loopback, 0);
        elsewhere.Start();
        var redirector = new TcpListener(IPAddress.Loopback, 0);
        redirector.Start();
        try
        {
            var target = ((IPEndPoint)elsewhere.LocalEndpoint).Port;
            var reached = elsewhere.AcceptTcpClientAsync();
            var answered = Task.Run(async () =>
            {
                using var connection = await redirector.AcceptTcpClientAsync();
                var stream = connection.GetStream();
                var buffer = new byte[4096];
                _ = await stream.ReadAsync(buffer);
                var reply = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{target}/api/tags\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(reply);
            });
            await using var client = new OllamaApiClient(
                $"http://127.0.0.1:{((IPEndPoint)redirector.LocalEndpoint).Port}",
                readinessTimeout: TimeSpan.FromSeconds(5));

            var discovery = await client.DiscoverAsync();
            await answered.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.NotEqual(OllamaHealth.Ready, discovery.Health);
            using var delayCancellation = new CancellationTokenSource();
            var first = await Task.WhenAny(reached, Task.Delay(TimeSpan.FromMilliseconds(500), delayCancellation.Token));
            await delayCancellation.CancelAsync();
            Assert.False(first == reached, "the redirect was followed to another address");
        }
        finally
        {
            redirector.Stop();
            elsewhere.Stop();
        }
    }

    [Fact]
    public async Task AnInvalidEndpointNeverSendsAnything()
    {
        var handler = new StreamHandler(Chunks("""{"status":"success"}"""));
        await using var client = new OllamaApiClient("http://example.com:11434", handler);

        Assert.Equal(OllamaPullOutcome.EndpointInvalid, await client.PullAsync("qwen3:0.6b", null, CancellationToken.None));
        Assert.Equal(OllamaDeleteOutcome.EndpointInvalid, await client.DeleteAsync("qwen3:0.6b", CancellationToken.None));
        Assert.Null(handler.Uri);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, OllamaDeleteOutcome.Deleted)]
    [InlineData(HttpStatusCode.NotFound, OllamaDeleteOutcome.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError, OllamaDeleteOutcome.Refused)]
    public async Task ARemovalSaysHowItEnded(HttpStatusCode status, OllamaDeleteOutcome expected)
    {
        var handler = new StreamHandler([], status);
        await using var client = new OllamaApiClient(Endpoint, handler);

        Assert.Equal(expected, await client.DeleteAsync("qwen3:0.6b", CancellationToken.None));
        Assert.Equal(HttpMethod.Delete, handler.Method);
        Assert.Equal(new Uri($"{Endpoint}/api/delete"), handler.Uri);
        Assert.Contains("\"model\":\"qwen3:0.6b\"", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscoveryKeepsWhatAModelOccupiesAndItsParameterLabel()
    {
        var handler = new StreamHandler(Chunks(
            """{"models":[{"name":"qwen2.5:3b","size":1929912432,"details":{"parameter_size":"3.1B"},"capabilities":["completion"]}]}"""));
        await using var client = new OllamaApiClient(Endpoint, handler);

        var model = Assert.Single((await client.DiscoverAsync()).LocalModels);

        Assert.Equal(1929912432, model.SizeBytes);
        Assert.Equal("3.1B", model.ParameterSize);
    }

    /// <summary>Only "nothing is listening" is grounds to offer starting Ollama; a slow server is not.</summary>
    [Fact]
    public async Task DiscoverySaysWhenNothingIsListening()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await using var closed = new OllamaApiClient($"http://127.0.0.1:{port}", readinessTimeout: TimeSpan.FromSeconds(5));

        var refused = await closed.DiscoverAsync();

        Assert.Equal(OllamaHealth.ServerUnavailable, refused.Health);
        Assert.True(refused.ConnectionRefused);

        var slow = new StreamHandler([(Line("{}"), TimeSpan.FromSeconds(10))]);
        await using var waiting = new OllamaApiClient(Endpoint, slow, TimeSpan.FromMilliseconds(100));
        var timedOut = await waiting.DiscoverAsync();
        Assert.Equal(OllamaHealth.ServerUnavailable, timedOut.Health);
        Assert.False(timedOut.ConnectionRefused);
    }

    [Fact]
    public void TheCatalogueIsTheElevenMacModelsWithTheRecommendationAmongThem()
    {
        Assert.Equal(11, OllamaModelCatalog.Entries.Count);
        Assert.Equal(11, OllamaModelCatalog.Entries.Select(entry => OllamaModelCatalog.Canonical(entry.Id)).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(OllamaModelVerdict.Recommended, OllamaModelCatalog.Offered(OllamaModelCatalog.RecommendedModelId)?.Verdict);
    }

    [Fact]
    public void ModelsAreTheSameUnderEitherSpellingAndOnlyThat()
    {
        Assert.True(OllamaModelCatalog.SameModel("llama3.2", "Llama3.2:latest"));
        Assert.False(OllamaModelCatalog.SameModel("llama3.2", "llama3.2:1b"));
        Assert.NotNull(OllamaModelCatalog.Offered("gemma2:latest"));
        Assert.Equal((OllamaModelVerdict.NotTested, string.Empty), OllamaModelCatalog.VerdictFor("my-own-finetune"));
        Assert.Equal(OllamaModelVerdict.Unreliable, OllamaModelCatalog.VerdictFor("deepseek-r1:1.5b").Verdict);
        Assert.Null(OllamaModelCatalog.Offered("deepseek-r1:1.5b"));
    }

    /// <summary>Bands are ranked; models inside a band keep their order, and untested sits above the measured failures.</summary>
    [Fact]
    public void OrderingRanksBandsAndNeverWithinOne()
    {
        string[] ids = ["tinyllama", "gemma2", "my-own", "qwen2.5:7b", "mistral", "gemma2:2b", "qwen3:0.6b"];

        var ordered = OllamaModelCatalog.OrderedByVerdict(ids, id => id).ToArray();

        Assert.Equal(["qwen2.5:7b", "qwen3:0.6b", "gemma2", "gemma2:2b", "my-own", "mistral", "tinyllama"], ordered);
    }

    [Fact]
    public void CatalogSearchFiltersByModelIdAndDisplayName()
    {
        var qwen = OllamaModelCatalog.FilterEntries("Qwen 2.5");
        Assert.Equal(["qwen2.5:3b", "qwen2.5:7b"], qwen.Select(entry => entry.Id));

        var gemma = OllamaModelCatalog.FilterEntries("gemma3n:e4b");
        Assert.Equal(["gemma3n:e4b"], gemma.Select(entry => entry.Id));

        Assert.Equal(OllamaModelCatalog.Entries.Count, OllamaModelCatalog.FilterEntries("   ").Count);
        Assert.Empty(OllamaModelCatalog.FilterEntries("nonexistent-model-xyz"));
        Assert.True(OllamaModelCatalog.SameHostedModel("gpt-oss:120b", "gpt-oss:120b-cloud"));
        Assert.True(OllamaModelCatalog.SameHostedModel("minimax-m2.7", "minimax-m2.7:cloud"));
        Assert.False(OllamaModelCatalog.SameHostedModel("gpt-oss:120b", "gpt-oss:20b-cloud"));
    }

    [Fact]
    public void CloudCatalogParserAcceptsLiveShapeDeduplicatesAndEnforcesRawCap()
    {
        var json = """
        {
          "object": "list",
          "data": [
            { "id": "glm-5.1", "object": "model", "created": 1775520000, "owned_by": "zai-org" },
            { "id": "gpt-oss:120b", "object": "model", "created": 1754352000, "owned_by": "openai" },
            { "id": "GPT-OSS:120B", "object": "model", "created": 1754352000, "owned_by": "openai" },
            { "id": "   ", "object": "model", "created": 0, "owned_by": "nobody" },
            { "id": "minimax-m2.7", "object": "model", "created": 1773792000, "owned_by": "minimaxai" }
          ]
        }
        """;

        var ids = OllamaCloudCatalogClient.ParseCloudCatalog(Encoding.UTF8.GetBytes(json));

        Assert.Equal(["glm-5.1", "gpt-oss:120b", "minimax-m2.7"], ids);
        Assert.Throws<OllamaCloudCatalogException>(() => OllamaCloudCatalogClient.ParseCloudCatalog("not json"u8));
        Assert.Throws<OllamaCloudCatalogException>(() => OllamaCloudCatalogClient.ParseCloudCatalog("""{"object":"list"}"""u8));

        var rawEntries = Enumerable.Range(0, 201)
            .Select(index => $$$"""{"id":"model-{{{index}}}"}""");
        var overCapDoc = $$$"""{"object":"list","data":[{{{string.Join(",", rawEntries)}}}]}""";
        Assert.Throws<OllamaCloudCatalogException>(() =>
            OllamaCloudCatalogClient.ParseCloudCatalog(Encoding.UTF8.GetBytes(overCapDoc)));
    }

    [Fact]
    public async Task CloudCatalogEnforcesSizeCapRedirectPolicyAndCacheWithFallback()
    {
        Assert.True(OllamaCloudCatalogClient.ShouldFollowRedirect(new Uri("https://ollama.com/v1/models")));
        Assert.False(OllamaCloudCatalogClient.ShouldFollowRedirect(new Uri("https://api.ollama.com/v1/models")));
        Assert.False(OllamaCloudCatalogClient.ShouldFollowRedirect(new Uri("http://ollama.com/v1/models")));
        Assert.False(OllamaCloudCatalogClient.ShouldFollowRedirect(new Uri("https://evil.example.com/v1/models")));
        Assert.False(OllamaCloudCatalogClient.ShouldFollowRedirect(new Uri("https://ollama.com.attacker.test/v1/models")));

        await using var oversizeStream = new MemoryStream(new byte[OllamaCloudCatalogClient.MaximumBytes + 1]);
        var oversizeError = await Assert.ThrowsAsync<OllamaCloudCatalogException>(() =>
            OllamaCloudCatalogClient.ReadBoundedBytesAsync(
                oversizeStream,
                OllamaCloudCatalogClient.MaximumBytes,
                CancellationToken.None));
        Assert.Equal(OllamaCloudCatalogError.ResponseTooLarge, oversizeError.Error);

        var clock = new FakeClock(new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero));
        var calls = 0;
        var shouldFail = false;
        var handler = new ScriptedHttpHandler(_ =>
        {
            calls++;
            if (shouldFail)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"object":"list","data":[{"id":"gpt-oss:120b"},{"id":"glm-5.1"}]}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        });
        using var client = new OllamaCloudCatalogClient(handler, clock);

        var first = await client.TryGetCatalogIdsAsync();
        var second = await client.TryGetCatalogIdsAsync();
        Assert.Equal(["gpt-oss:120b", "glm-5.1"], first);
        Assert.Equal(first, second);
        Assert.Equal(1, calls);

        // After 15 minutes, a failed refresh retains the prior list rather than wiping the UI.
        clock.Advance(TimeSpan.FromMinutes(16));
        shouldFail = true;
        var retained = await client.TryGetCatalogIdsAsync();
        Assert.Equal(["gpt-oss:120b", "glm-5.1"], retained);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void HostedIdentityFingerprintComparesSubstanceAndRejectsVacuousShowResponses()
    {
        var first = """
        {
          "modified_at": "2026-04-01T00:00:00Z",
          "details": { "family": "qwen3", "parameter_size": "30B" },
          "model_info": { "general.architecture": "qwen3" }
        }
        """;
        var second = """
        {
          "model_info": { "general.architecture": "qwen3" },
          "details": { "parameter_size": "30B", "family": "qwen3" },
          "modified_at": "2026-04-02T12:34:56Z"
        }
        """;
        var different = """
        {
          "details": { "family": "qwen3", "parameter_size": "235B" },
          "model_info": { "general.architecture": "qwen3" }
        }
        """;

        var fp1 = OllamaApiClient.HostedIdentityFingerprint(first);
        var fp2 = OllamaApiClient.HostedIdentityFingerprint(second);
        var fpDiff = OllamaApiClient.HostedIdentityFingerprint(different);

        Assert.NotNull(fp1);
        Assert.Equal(fp1, fp2);
        Assert.NotEqual(fp1, fpDiff);
        Assert.Null(OllamaApiClient.HostedIdentityFingerprint("{}"));
        Assert.Null(OllamaApiClient.HostedIdentityFingerprint("""{"modified_at":"2026-04-01T00:00:00Z"}"""));
        Assert.Null(OllamaApiClient.HostedIdentityFingerprint("""{"details":{"format":"gguf"}}"""));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, HttpStatusCode.NotFound, true, OllamaHostedAddOutcome.Succeeded, "gpt-oss:120b-cloud")]
    [InlineData(HttpStatusCode.NotFound, HttpStatusCode.OK, true, OllamaHostedAddOutcome.Succeeded, "gpt-oss:120b:cloud")]
    [InlineData(HttpStatusCode.OK, HttpStatusCode.OK, true, OllamaHostedAddOutcome.Succeeded, "gpt-oss:120b-cloud")]
    [InlineData(HttpStatusCode.OK, HttpStatusCode.OK, false, OllamaHostedAddOutcome.Ambiguous, null)]
    [InlineData(HttpStatusCode.NotFound, HttpStatusCode.NotFound, true, OllamaHostedAddOutcome.NoUsableName, null)]
    [InlineData(HttpStatusCode.OK, HttpStatusCode.InternalServerError, true, OllamaHostedAddOutcome.Unreachable, null)]
    public async Task ResolveAndAddHostedModelProbesBothCloudFormsAndPullsOnlyWhenProven(
        HttpStatusCode dashStatus,
        HttpStatusCode colonStatus,
        bool sameFingerprint,
        OllamaHostedAddOutcome expectedOutcome,
        string? expectedInstalledId)
    {
        var showCalls = new List<string>();
        var pullCalls = new List<string>();
        var handler = new ScriptedHttpHandler(async (request, cancellationToken) =>
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            var model = doc.RootElement.GetProperty("model").GetString()!;

            if (request.RequestUri!.AbsolutePath == "/api/show")
            {
                showCalls.Add(model);
                var isDash = model.EndsWith("-cloud", StringComparison.Ordinal);
                var status = isDash ? dashStatus : colonStatus;
                if (status != HttpStatusCode.OK)
                {
                    return new HttpResponseMessage(status);
                }

                var paramSize = isDash || sameFingerprint ? "120B" : "20B";
                var payload = $$$"""
                {
                  "modified_at": "{{{(isDash ? "2026-04-01T00:00:00Z" : "2026-04-02T00:00:00Z")}}}",
                  "details": { "family": "gpt-oss", "parameter_size": "{{{paramSize}}}" }
                }
                """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                };
            }

            if (request.RequestUri.AbsolutePath == "/api/pull")
            {
                pullCalls.Add(model);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"status\":\"success\"}\n", Encoding.UTF8, "application/x-ndjson"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        await using var client = new OllamaApiClient(Endpoint, handler);

        var result = await client.ResolveAndAddHostedModelAsync("gpt-oss:120b", null, CancellationToken.None);

        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Equal(expectedInstalledId, result.ResolvedModelId);
        Assert.Equal(["gpt-oss:120b-cloud", "gpt-oss:120b:cloud"], showCalls);
        if (expectedInstalledId is not null)
        {
            Assert.Equal([expectedInstalledId], pullCalls);
        }
        else
        {
            Assert.Empty(pullCalls);
        }
    }

    [Fact]
    public async Task WarmUpWarmsLocalModelsAndSkipsRemoteOrUnknownModels()
    {
        var thinkingBody = JsonSerializer.Serialize(OllamaApiClient.MakeWarmupRequestBody("qwen3:4b", true));
        using (var doc = JsonDocument.Parse(thinkingBody))
        {
            Assert.Equal("qwen3:4b", doc.RootElement.GetProperty("model").GetString());
            Assert.Equal("60m", doc.RootElement.GetProperty("keep_alive").GetString());
            Assert.Equal(1, doc.RootElement.GetProperty("options").GetProperty("num_predict").GetInt32());
            Assert.Equal("low", doc.RootElement.GetProperty("think").GetString());
        }

        var nonThinkingBody = JsonSerializer.Serialize(OllamaApiClient.MakeWarmupRequestBody("qwen2.5:3b", false));
        using (var doc = JsonDocument.Parse(nonThinkingBody))
        {
            Assert.False(doc.RootElement.TryGetProperty("think", out _));
        }

        var chatModels = new List<string>();
        var handler = new ScriptedHttpHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/tags")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "models": [
                            { "name": "qwen2.5:3b", "capabilities": ["completion"] },
                            { "name": "gpt-oss:120b-cloud", "remote_host": "https://ollama.com", "capabilities": ["completion"] }
                          ]
                        }
                        """,
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            if (request.RequestUri.AbsolutePath == "/api/chat")
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(body);
                chatModels.Add(doc.RootElement.GetProperty("model").GetString()!);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"message":{"content":""},"done":true}""", Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        await using var client = new OllamaApiClient(Endpoint, handler);

        Assert.Equal(OllamaWarmupOutcome.Warmed, await client.WarmUpAsync("qwen2.5:3b"));
        Assert.Equal(["qwen2.5:3b"], chatModels);

        Assert.Equal(OllamaWarmupOutcome.SkippedRemote, await client.WarmUpAsync("gpt-oss:120b-cloud"));
        Assert.Equal(OllamaWarmupOutcome.SkippedUnknownModel, await client.WarmUpAsync("uninstalled:latest"));
        Assert.Single(chatModels);
    }

    [Fact]
    public void ResolveOllamaLaunchTargetPrefersDesktopTrayAppAndFallsBackToCliServe()
    {
        var localAppData = @"C:\LocalAppData";
        var appExe = @"C:\LocalAppData\Programs\Ollama\ollama app.exe";
        var cliExe = @"C:\LocalAppData\Programs\Ollama\ollama.exe";

        var bothExist = OllamaModelCatalog.ResolveOllamaLaunchTarget(
            localAppData,
            null,
            path => path == appExe || path == cliExe);
        Assert.NotNull(bothExist);
        Assert.Equal(appExe, bothExist!.FileName);
        Assert.Null(bothExist.Arguments);
        Assert.True(bothExist.UseShellExecute);
        Assert.False(bothExist.CreateNoWindow);

        var cliOnly = OllamaModelCatalog.ResolveOllamaLaunchTarget(
            localAppData,
            null,
            path => path == cliExe);
        Assert.NotNull(cliOnly);
        Assert.Equal(cliExe, cliOnly!.FileName);
        Assert.Equal("serve", cliOnly.Arguments);
        Assert.False(cliOnly.UseShellExecute);
        Assert.True(cliOnly.CreateNoWindow);

        var neither = OllamaModelCatalog.ResolveOllamaLaunchTarget(
            localAppData,
            null,
            _ => false);
        Assert.Null(neither);
    }

    [Fact]
    public void CheckLaunchSafetyVerifiesEnvironmentConfigJsonAndSqliteExposeSetting()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"ew-ollama-safety-{Guid.NewGuid():N}");
        var ollamaDir = Path.Combine(tempRoot, "Ollama");
        Directory.CreateDirectory(ollamaDir);
        try
        {
            // Clean directory and loopback OLLAMA_HOST -> SafeLocalOnly.
            Assert.Equal(
                OllamaLaunchSafety.SafeLocalOnly,
                OllamaLocalConfigProbe.CheckLaunchSafety(tempRoot, ["127.0.0.1:11434", "http://localhost:11434", null]));

            // Non-loopback OLLAMA_HOST -> BlockedByNetworkExposure.
            Assert.Equal(
                OllamaLaunchSafety.BlockedByNetworkExposure,
                OllamaLocalConfigProbe.CheckLaunchSafety(tempRoot, ["0.0.0.0:11434"]));
            Assert.Equal(
                OllamaLaunchSafety.BlockedByNetworkExposure,
                OllamaLocalConfigProbe.CheckLaunchSafety(tempRoot, [":11434"]));
            Assert.Equal(
                OllamaLaunchSafety.BlockedByNetworkExposure,
                OllamaLocalConfigProbe.CheckLaunchSafety(tempRoot, ["192.168.1.50:11434"]));

            // config.json with expose: true -> BlockedByNetworkExposure.
            var configJsonPath = Path.Combine(ollamaDir, "config.json");
            File.WriteAllText(configJsonPath, """{"expose":true}""");
            Assert.Equal(
                OllamaLaunchSafety.BlockedByNetworkExposure,
                OllamaLocalConfigProbe.CheckLaunchSafety(tempRoot, [null]));

            // config.json with expose: false -> SafeLocalOnly.
            File.WriteAllText(configJsonPath, """{"expose":false}""");
            Assert.Equal(
                OllamaLaunchSafety.SafeLocalOnly,
                OllamaLocalConfigProbe.CheckLaunchSafety(tempRoot, [null]));
            File.Delete(configJsonPath);

            // db.sqlite with settings.expose = 0 -> SafeLocalOnly; expose = 1 -> BlockedByNetworkExposure.
            var dbPath = Path.Combine(ollamaDir, "db.sqlite");
            WindowsSqlite.ExecuteOnNewDatabase(
                dbPath,
                "CREATE TABLE settings (id INTEGER PRIMARY KEY CHECK (id = 1), expose BOOLEAN NOT NULL DEFAULT 0); INSERT INTO settings (id, expose) VALUES (1, 0);");
            Assert.Equal(
                OllamaLaunchSafety.SafeLocalOnly,
                OllamaLocalConfigProbe.CheckLaunchSafety(tempRoot, [null]));

            File.Delete(dbPath);
            WindowsSqlite.ExecuteOnNewDatabase(
                dbPath,
                "CREATE TABLE settings (id INTEGER PRIMARY KEY CHECK (id = 1), expose BOOLEAN NOT NULL DEFAULT 0); INSERT INTO settings (id, expose) VALUES (1, 1);");
            Assert.Equal(
                OllamaLaunchSafety.BlockedByNetworkExposure,
                OllamaLocalConfigProbe.CheckLaunchSafety(tempRoot, [null]));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void CloudPolishConsentReportsHostedOllamaAsCloudProcessing()
    {
        var consent = CloudPolishConsent.ForOllamaHosted("gpt-oss:120b-cloud");
        Assert.Equal("Ollama Cloud", consent.ProviderName);
        Assert.Equal("ollama.com", consent.ApiHost);
        Assert.Contains("gpt-oss:120b-cloud", consent.Notice, StringComparison.Ordinal);
        Assert.Contains("Ollama's servers", consent.Notice, StringComparison.Ordinal);
    }

    private static byte[] Line(string json) => Encoding.UTF8.GetBytes(json + "\n");

    private static (byte[] Bytes, TimeSpan Delay)[] Chunks(params string[] lines) =>
        lines.Select(line => (Line(line), TimeSpan.Zero)).ToArray();

    private sealed class Collect(List<OllamaPullUpdate> into, Action<OllamaPullUpdate>? then = null) : IProgress<OllamaPullUpdate>
    {
        public void Report(OllamaPullUpdate value)
        {
            into.Add(value);
            then?.Invoke(value);
        }
    }

    /// <summary>Answers any request with a body that arrives piece by piece, each after its own pause.</summary>
    private sealed class StreamHandler((byte[] Bytes, TimeSpan Delay)[] pieces, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }

        public Uri? Uri { get; private set; }

        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Uri = request.RequestUri;
            Body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StreamContent(new PacedStream(pieces)) };
        }
    }

    private sealed class PacedStream((byte[] Bytes, TimeSpan Delay)[] pieces) : Stream
    {
        private int _piece;
        private int _offset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_piece >= pieces.Length)
            {
                return 0;
            }

            var (bytes, delay) = pieces[_piece];
            if (_offset == 0 && delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            var count = Math.Min(buffer.Length, bytes.Length - _offset);
            bytes.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            if (_offset == bytes.Length)
            {
                _piece++;
                _offset = 0;
            }

            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FakeClock(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _now = initial;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    private sealed class ScriptedHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;

        public ScriptedHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> send)
            : this((request, _) => Task.FromResult(send(request)))
        {
        }

        public ScriptedHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        {
            _send = send;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            _send(request, cancellationToken);
    }
}
