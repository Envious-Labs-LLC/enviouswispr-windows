using System.Text.Json;
using System.Text;
using System.Threading.Channels;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Services.Diagnostics;

public interface IPrivacySafeTelemetryTransport : IAsyncDisposable
{
    Task SendAsync(
        PrivacySafeDiagnosticRecord record,
        CancellationToken cancellationToken = default);
}

public static class TelemetryEndpointPolicy
{
    public static bool TryNormalize(string? value, bool allowLoopbackHttp, out Uri? endpoint)
    {
        endpoint = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            !string.IsNullOrEmpty(parsed.UserInfo) ||
            !string.IsNullOrEmpty(parsed.Query) ||
            !string.IsNullOrEmpty(parsed.Fragment))
        {
            return false;
        }

        var secure = parsed.Scheme == Uri.UriSchemeHttps;
        var loopbackUat = allowLoopbackHttp &&
            parsed.Scheme == Uri.UriSchemeHttp &&
            parsed.IsLoopback;
        if (!secure && !loopbackUat)
        {
            return false;
        }

        endpoint = parsed;
        return true;
    }
}

public sealed class HttpPrivacySafeTelemetryTransport : IPrivacySafeTelemetryTransport
{
    private readonly HttpClient _client;
    private readonly Uri _endpoint;
    private readonly bool _ownsClient;

    public HttpPrivacySafeTelemetryTransport(Uri endpoint, HttpClient? client = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        _endpoint = endpoint;
        _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        _ownsClient = client is null;
    }

    public async Task SendAsync(
        PrivacySafeDiagnosticRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        // SERIALISED HERE RATHER THAN THROUGH THE LOCAL LOG'S HELPER. What is sent and what is
        // written to disk are different shapes on purpose - the local line carries a dictation id
        // that must not cross the network - and borrowing the log's serialiser is how the two would
        // silently become one shape again the next time somebody adds a field to it.
        using var content = new StringContent(
            JsonSerializer.Serialize(record, JsonLineFileLogger.SerializerOptions),
            Encoding.UTF8,
            "application/json");
        using var response = await _client.PostAsync(
            _endpoint,
            content,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public ValueTask DisposeAsync()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}

public sealed class PrivacySafeObservabilityLogger : IAppLogger, IAsyncDisposable
{
    private readonly JsonLineFileLogger _localLogger;
    private readonly IPrivacySafeTelemetryTransport? _transport;
    private readonly Channel<PrivacySafeDiagnosticRecord> _telemetryQueue;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task? _sender;
    private volatile bool _shareAnonymousTelemetry;

    public PrivacySafeObservabilityLogger(
        JsonLineFileLogger localLogger,
        IPrivacySafeTelemetryTransport? transport = null)
    {
        ArgumentNullException.ThrowIfNull(localLogger);
        _localLogger = localLogger;
        _transport = transport;
        _telemetryQueue = Channel.CreateBounded<PrivacySafeDiagnosticRecord>(
            new BoundedChannelOptions(256)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropWrite,
            });
        _sender = transport is null ? null : SendLoopAsync(_lifetime.Token);
    }

    public bool TelemetryAvailable => _transport is not null;

    public void Configure(ObservabilityPreferences preferences, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        _localLogger.Configure(preferences, now);
        _shareAnonymousTelemetry = preferences.ShareAnonymousTelemetry && TelemetryAvailable;
    }

    public void Write(AppLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var record = PrivacySafeDiagnosticRecord.From(entry);
        _localLogger.WriteRecord(record);
        if (_shareAnonymousTelemetry)
        {
            _telemetryQueue.Writer.TryWrite(record);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _telemetryQueue.Writer.TryComplete();
        if (_sender is not null)
        {
            try
            {
                await _sender.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
                _lifetime.Cancel();
            }
        }

        _lifetime.Cancel();
        _lifetime.Dispose();
        if (_transport is not null)
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task SendLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var record in _telemetryQueue.Reader.ReadAllAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                try
                {
                    await _transport!.SendAsync(record, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is HttpRequestException or
                    TaskCanceledException or OperationCanceledException)
                {
                    // Operational telemetry is best-effort and cannot affect the product path.
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
