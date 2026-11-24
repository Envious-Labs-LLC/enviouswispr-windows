using System.Buffers;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using EnviousWispr.Core.Polish;

namespace EnviousWispr.LLM;

/// <summary>Downloading and removing local models through Ollama's documented API. Ref: #213, macOS OllamaSetupService.</summary>
/// <remarks>
/// NO DEADLINE ON A DOWNLOAD, AND NONE ON A SILENCE EITHER. A 7 GB model on a slow line takes as long as it takes, and
/// after the last byte Ollama hashes the layer without saying anything - measured as possible, never bounded. So the
/// only timer is on the wait for the response to begin; a minute without a line is reported as <see
/// cref="OllamaPullUpdate.Quiet"/> and the download goes on. The person's Stop is the only way it ends early.
///
/// SUCCESS IS OLLAMA SAYING SO. A layer reaching its total is one layer; a stream that ends without the final
/// "success" line was cut, whatever the bytes looked like. Neither the raw status words nor an error's text leave this
/// file - they are classified here and the classification is all anybody sees or logs.
/// </remarks>
public sealed partial class OllamaApiClient
{
    /// <summary>How long a download may wait for Ollama to start answering.</summary>
    internal static readonly TimeSpan PullResponseTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long without a line before the page is told the download is quiet (not failed).</summary>
    internal static readonly TimeSpan PullQuietAfter = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan DeleteTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ShowProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WarmupTimeout = TimeSpan.FromSeconds(30);

    /// <summary>A progress line is a few hundred bytes; one longer than this is not Ollama.</summary>
    private const int MaximumLineBytes = 64 * 1024;

    /// <summary>Downloads a model, reporting each progress line. Returns how it ended; throws only for a caller's bug.</summary>
    public async Task<OllamaPullOutcome> PullAsync(
        string modelId,
        IProgress<OllamaPullUpdate>? progress,
        CancellationToken cancellationToken,
        TimeSpan? quietAfter = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        if (_endpoint is null)
        {
            return OllamaPullOutcome.EndpointInvalid;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_endpoint, "api/pull"))
        {
            Content = JsonContent.Create(new Dictionary<string, object?>
            {
                ["model"] = modelId,
                ["stream"] = true,
            }),
        };

        HttpResponseMessage response;
        using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            waiting.CancelAfter(PullResponseTimeout);
            try
            {
                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, waiting.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return OllamaPullOutcome.Cancelled;
            }
            catch (Exception exception) when (exception is OperationCanceledException or HttpRequestException)
            {
                return OllamaPullOutcome.ServerUnavailable;
            }
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return response.StatusCode == HttpStatusCode.NotFound
                    ? OllamaPullOutcome.NotFound
                    : OllamaPullOutcome.Refused;
            }

            var last = new OllamaPullUpdate(OllamaPullPhase.Starting);
            progress?.Report(last);
            try
            {
                var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using (stream.ConfigureAwait(false))
                {
                    var lines = new LineReader(stream);
                    while (true)
                    {
                        var next = lines.ReadLineAsync(cancellationToken);
                        using (var quiet = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                        {
                            while (await Task.WhenAny(next, Task.Delay(quietAfter ?? PullQuietAfter, quiet.Token))
                                       .ConfigureAwait(false) != next)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                progress?.Report(last with { Quiet = true });
                            }

                            await quiet.CancelAsync().ConfigureAwait(false);
                        }

                        var line = await next.ConfigureAwait(false);
                        if (line is null)
                        {
                            return OllamaPullOutcome.Interrupted;
                        }

                        if (string.IsNullOrWhiteSpace(line))
                        {
                            continue;
                        }

                        switch (ReadPullLine(line))
                        {
                            case { Outcome: { } ended }:
                                return ended;
                            case { Update: { } update }:
                                last = update;
                                progress?.Report(update);
                                break;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return OllamaPullOutcome.Cancelled;
            }
            catch (Exception exception) when (exception is IOException or HttpRequestException or OperationCanceledException)
            {
                return OllamaPullOutcome.Interrupted;
            }
            catch (Exception exception) when (exception is InvalidDataException or JsonException)
            {
                return OllamaPullOutcome.Refused;
            }
            catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
            {
                // NOTHING ESCAPES A DOWNLOAD: a caller holding the page's one change must always get an ending.
                return OllamaPullOutcome.Refused;
            }
        }
    }

    /// <summary>Removes a model from Ollama. Other apps that use it lose it too; layers other models share stay.</summary>
    public async Task<OllamaDeleteOutcome> DeleteAsync(string modelId, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        if (_endpoint is null)
        {
            return OllamaDeleteOutcome.EndpointInvalid;
        }

        using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri(_endpoint, "api/delete"))
        {
            Content = JsonContent.Create(new Dictionary<string, object?> { ["model"] = modelId }),
        };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(DeleteTimeout);
        try
        {
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
            return response.StatusCode switch
            {
                HttpStatusCode.OK => OllamaDeleteOutcome.Deleted,
                HttpStatusCode.NotFound => OllamaDeleteOutcome.NotFound,
                _ => OllamaDeleteOutcome.Refused,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is OperationCanceledException or HttpRequestException)
        {
            return OllamaDeleteOutcome.ServerUnavailable;
        }
    }

    /// <summary>
    /// Resolves a hosted Ollama model's pullable name by probing <c>&lt;id&gt;-cloud</c> and <c>&lt;id&gt;:cloud</c>
    /// via <c>POST /api/show</c> with identity fingerprint comparison, then registers its manifest via <c>POST /api/pull</c>.
    /// Ref: macOS OllamaSetupService.addHostedModel (#1956).
    /// </summary>
    public async Task<OllamaHostedAddResult> ResolveAndAddHostedModelAsync(
        string advertisedId,
        IProgress<OllamaPullUpdate>? progress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(advertisedId);
        if (_endpoint is null)
        {
            return new OllamaHostedAddResult(OllamaHostedAddOutcome.EndpointInvalid);
        }

        var baseId = StripCloudSuffix(advertisedId.Trim());
        var dashCandidate = $"{baseId}-cloud";
        var colonCandidate = $"{baseId}:cloud";

        // Both candidates are probed unconditionally in fixed dash-then-colon order.
        var dashProbe = await ProbeHostedCandidateAsync(dashCandidate, cancellationToken).ConfigureAwait(false);
        var colonProbe = await ProbeHostedCandidateAsync(colonCandidate, cancellationToken).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
        {
            return new OllamaHostedAddResult(OllamaHostedAddOutcome.Cancelled);
        }

        string resolved;
        switch ((dashProbe.Status, colonProbe.Status))
        {
            case (HostedProbeStatus.Proven, HostedProbeStatus.Absent):
                resolved = dashCandidate;
                break;
            case (HostedProbeStatus.Absent, HostedProbeStatus.Proven):
                resolved = colonCandidate;
                break;
            case (HostedProbeStatus.Proven, HostedProbeStatus.Proven)
                when dashProbe.Fingerprint is not null &&
                     string.Equals(dashProbe.Fingerprint, colonProbe.Fingerprint, StringComparison.Ordinal):
                // Aliases for the same model (e.g. gpt-oss:20b-cloud and gpt-oss:20b:cloud).
                resolved = dashCandidate;
                break;
            case (HostedProbeStatus.Proven, HostedProbeStatus.Proven):
                return new OllamaHostedAddResult(OllamaHostedAddOutcome.Ambiguous);
            case (HostedProbeStatus.Absent, HostedProbeStatus.Absent):
                return new OllamaHostedAddResult(OllamaHostedAddOutcome.NoUsableName);
            default:
                return new OllamaHostedAddResult(OllamaHostedAddOutcome.Unreachable);
        }

        var pullOutcome = await PullAsync(resolved, progress, cancellationToken).ConfigureAwait(false);
        return pullOutcome switch
        {
            OllamaPullOutcome.Succeeded => new OllamaHostedAddResult(OllamaHostedAddOutcome.Succeeded, resolved, pullOutcome),
            OllamaPullOutcome.Cancelled => new OllamaHostedAddResult(OllamaHostedAddOutcome.Cancelled, resolved, pullOutcome),
            OllamaPullOutcome.ServerUnavailable => new OllamaHostedAddResult(OllamaHostedAddOutcome.ServerUnavailable, resolved, pullOutcome),
            OllamaPullOutcome.EndpointInvalid => new OllamaHostedAddResult(OllamaHostedAddOutcome.EndpointInvalid, resolved, pullOutcome),
            _ => new OllamaHostedAddResult(OllamaHostedAddOutcome.PullFailed, resolved, pullOutcome),
        };
    }

    /// <summary>
    /// Warms up a local Ollama model by loading its weights into memory (<c>POST /api/chat</c>, <c>keep_alive: "60m"</c>,
    /// <c>num_predict: 1</c>). Remote/hosted and unknown models are skipped without issuing any chat request.
    /// Ref: macOS OllamaSetupService.warmUpModel (#1914).
    /// </summary>
    public async Task<OllamaWarmupOutcome> WarmUpAsync(
        string modelId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return OllamaWarmupOutcome.SkippedUnknownModel;
        }

        if (_endpoint is null)
        {
            return OllamaWarmupOutcome.EndpointInvalid;
        }

        OllamaModelReadinessResult readiness;
        try
        {
            readiness = await ProbeModelAsync(modelId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return OllamaWarmupOutcome.Cancelled;
        }

        if (readiness.Readiness == OllamaModelReadiness.EndpointInvalid)
        {
            return OllamaWarmupOutcome.EndpointInvalid;
        }

        if (readiness.Readiness is OllamaModelReadiness.ServerUnavailable or OllamaModelReadiness.ServerUnhealthy)
        {
            return OllamaWarmupOutcome.ServerUnavailable;
        }

        if (readiness.Readiness != OllamaModelReadiness.Ready || readiness.Model is null)
        {
            return OllamaWarmupOutcome.SkippedUnknownModel;
        }

        if (readiness.Model.IsRemote)
        {
            return OllamaWarmupOutcome.SkippedRemote;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_endpoint, "api/chat"))
        {
            Content = JsonContent.Create(MakeWarmupRequestBody(readiness.Model.Id, readiness.Model.SupportsThinking)),
        };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(WarmupTimeout);
        try
        {
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                deadline.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? OllamaWarmupOutcome.Warmed
                : OllamaWarmupOutcome.Failed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return OllamaWarmupOutcome.Cancelled;
        }
        catch (Exception exception) when (exception is OperationCanceledException or HttpRequestException)
        {
            return OllamaWarmupOutcome.ServerUnavailable;
        }
    }

    public static Dictionary<string, object?> MakeWarmupRequestBody(string modelId, bool? supportsThinking)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        var body = new Dictionary<string, object?>
        {
            ["model"] = modelId,
            ["messages"] = new object[]
            {
                new { role = "user", content = "hi" },
            },
            ["stream"] = false,
            ["keep_alive"] = "60m",
            ["options"] = new Dictionary<string, object?>
            {
                ["num_predict"] = 1,
            },
        };
        if (supportsThinking == true)
        {
            body["think"] = "low";
        }

        return body;
    }

    /// <summary>
    /// Computes the canonical identity fingerprint of an <c>/api/show</c> JSON body, excluding per-registration
    /// <c>modified_at</c> and rejecting vacuous bodies that contain neither <c>details.family</c> nor <c>model_info</c>.
    /// Ref: macOS OllamaSetupService.hostedIdentityFingerprint (#1956).
    /// </summary>
    public static string? HostedIdentityFingerprint(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? null
            : HostedIdentityFingerprint(Encoding.UTF8.GetBytes(json));

    public static string? HostedIdentityFingerprint(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.IsEmpty)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(utf8Json.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var hasFamily = root.TryGetProperty("details", out var details) &&
                details.ValueKind == JsonValueKind.Object &&
                details.TryGetProperty("family", out var family) &&
                family.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(family.GetString());
            var hasModelInfo = root.TryGetProperty("model_info", out var modelInfo) &&
                modelInfo.ValueKind == JsonValueKind.Object &&
                modelInfo.EnumerateObject().Any();
            if (!hasFamily && !hasModelInfo)
            {
                return null;
            }

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                WriteCanonicalElement(writer, root, isRoot: true);
            }

            return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void WriteCanonicalElement(Utf8JsonWriter writer, JsonElement element, bool isRoot)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                             .Where(prop => !isRoot || !string.Equals(prop.Name, "modified_at", StringComparison.Ordinal))
                             .OrderBy(prop => prop.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalElement(writer, property.Value, isRoot: false);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonicalElement(writer, item, isRoot: false);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static string StripCloudSuffix(string modelId)
    {
        if (modelId.EndsWith("-cloud", StringComparison.OrdinalIgnoreCase))
        {
            return modelId[..^"-cloud".Length];
        }

        if (modelId.EndsWith(":cloud", StringComparison.OrdinalIgnoreCase))
        {
            return modelId[..^":cloud".Length];
        }

        return modelId;
    }

    private async Task<(HostedProbeStatus Status, string? Fingerprint)> ProbeHostedCandidateAsync(
        string candidate,
        CancellationToken cancellationToken)
    {
        if (_endpoint is null)
        {
            return (HostedProbeStatus.Indeterminate, null);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_endpoint, "api/show"))
        {
            Content = JsonContent.Create(new Dictionary<string, object?>
            {
                ["model"] = candidate,
            }),
        };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ShowProbeTimeout);
        try
        {
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                deadline.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return (HostedProbeStatus.Absent, null);
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                return (HostedProbeStatus.Indeterminate, null);
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(deadline.Token).ConfigureAwait(false);
            var fingerprint = HostedIdentityFingerprint(bytes);
            return fingerprint is not null
                ? (HostedProbeStatus.Proven, fingerprint)
                : (HostedProbeStatus.Indeterminate, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (HostedProbeStatus.Indeterminate, null);
        }
        catch (Exception exception) when (exception is OperationCanceledException or HttpRequestException or IOException)
        {
            return (HostedProbeStatus.Indeterminate, null);
        }
    }

    private enum HostedProbeStatus
    {
        Proven,
        Absent,
        Indeterminate,
    }

    /// <summary>One progress line: an update, an ending, or neither. Internal so the classification is tested directly.</summary>
    internal static (OllamaPullUpdate? Update, OllamaPullOutcome? Outcome) ReadPullLine(string line)
    {
        try
        {
            return ReadPullLineUnchecked(line);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            // A string the JSON reader cannot decode (a lone surrogate) throws from GetString, not from Parse.
            throw new InvalidDataException("A progress line could not be read.", exception);
        }
    }

    private static (OllamaPullUpdate? Update, OllamaPullOutcome? Outcome) ReadPullLineUnchecked(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("A progress line was not an object.");
        }

        if (root.TryGetProperty("error", out var error))
        {
            return (null, ClassifyPullError(error.ValueKind == JsonValueKind.String ? error.GetString() : null));
        }

        var status = root.TryGetProperty("status", out var statusValue) && statusValue.ValueKind == JsonValueKind.String
            ? statusValue.GetString() ?? string.Empty
            : string.Empty;
        if (string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
        {
            return (null, OllamaPullOutcome.Succeeded);
        }

        long? Number(string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt64(out var number) && number >= 0
                ? number
                : null;
        var digest = root.TryGetProperty("digest", out var digestValue) && digestValue.ValueKind == JsonValueKind.String
            ? digestValue.GetString()
            : null;
        var phase = status switch
        {
            _ when status.StartsWith("verifying", StringComparison.OrdinalIgnoreCase) => OllamaPullPhase.Verifying,
            _ when status.StartsWith("writing", StringComparison.OrdinalIgnoreCase) ||
                   status.StartsWith("removing", StringComparison.OrdinalIgnoreCase) => OllamaPullPhase.Finishing,
            _ when digest is not null || Number("total") is not null => OllamaPullPhase.Downloading,
            _ => OllamaPullPhase.Starting,
        };
        return (new OllamaPullUpdate(phase, digest, Number("completed"), Number("total")), null);
    }

    /// <summary>Ollama's error text, reduced to what the page can say something useful about. The text itself goes nowhere.</summary>
    internal static OllamaPullOutcome ClassifyPullError(string? message)
    {
        var text = message?.ToLowerInvariant() ?? string.Empty;
        if (text.Contains("no space", StringComparison.Ordinal) || text.Contains("errno 28", StringComparison.Ordinal) ||
            text.Contains("not enough space", StringComparison.Ordinal) || text.Contains("disk full", StringComparison.Ordinal))
        {
            return OllamaPullOutcome.DiskFull;
        }

        if (text.Contains("file does not exist", StringComparison.Ordinal) || text.Contains("not found", StringComparison.Ordinal) ||
            text.Contains("manifest unknown", StringComparison.Ordinal))
        {
            return OllamaPullOutcome.NotFound;
        }

        string[] network = ["dial tcp", "no such host", "i/o timeout", "connection refused", "connection reset", "tls", "timeout", "network"];
        return network.Any(word => text.Contains(word, StringComparison.Ordinal))
            ? OllamaPullOutcome.NetworkFailed
            : OllamaPullOutcome.Refused;
    }

    /// <summary>Whether nothing was listening, as against a server that was slow or said no.</summary>
    private static bool IsConnectionRefused(HttpRequestException exception) =>
        exception.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused };

    /// <summary>Newline-delimited UTF-8, one bounded line at a time.</summary>
    private sealed class LineReader(Stream stream)
    {
        private readonly byte[] _buffer = new byte[8192];
        private readonly ArrayBufferWriter<byte> _line = new();
        private int _start;
        private int _end;

        /// <summary>The next line without its terminator, or null at the end of the stream (a final unterminated line is returned first).</summary>
        public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                var newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
                if (newline >= 0)
                {
                    Append(newline - _start);
                    _start = newline + 1;
                    return Take();
                }

                Append(_end - _start);
                _start = _end = 0;
                var read = await stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return _line.WrittenCount > 0 ? Take() : null;
                }

                _end = read;
            }
        }

        private void Append(int count)
        {
            if (_line.WrittenCount + count > MaximumLineBytes)
            {
                throw new InvalidDataException("A progress line was longer than any Ollama writes.");
            }

            _line.Write(_buffer.AsSpan(_start, count));
        }

        private string Take()
        {
            var text = Encoding.UTF8.GetString(_line.WrittenSpan).TrimEnd('\r');
            _line.ResetWrittenCount();
            return text;
        }
    }
}
