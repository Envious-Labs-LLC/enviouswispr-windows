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
