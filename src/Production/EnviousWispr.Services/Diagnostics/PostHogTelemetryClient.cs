using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using EnviousWispr.Core.Distribution;

namespace EnviousWispr.Services.Diagnostics;

public sealed record WindowsObservabilityIdentity(
    string AppVersion,
    string OsVersion,
    string OsBuild,
    string ReleaseChannel,
    string Packaging,
    string Environment,
    Guid ProcessLaunchId,
    string ProcessRole = "main")
{
    public const string PostHogAppIdentifier = "enviouswispr-windows";
    public const string PlatformIdentifier = "windows";
    public const string OsName = "Windows";
    public const string SentryPlatformIdentifier = "csharp";
    public const string SentryReleasePrefix = "EnviousLabsLLC.EnviousWispr";
    public const string CrashContextSchemaVersion = "1";

    public string SentryRelease => $"{SentryReleasePrefix}@{AppVersion}";

    public static WindowsObservabilityIdentity CreateDefault(
        ReleaseIdentity? releaseIdentity = null,
        bool isPackagedMsix = false,
        string? appVersion = null,
        string? environment = null,
        Guid? processLaunchId = null,
        string processRole = "main")
    {
        var channel = releaseIdentity ?? ReleaseIdentity.Stable;
        var version = string.IsNullOrWhiteSpace(appVersion)
            ? (typeof(WindowsObservabilityIdentity).Assembly.GetName().Version?.ToString(3) ?? "0.19.0")
            : appVersion.Trim();
        var osVersionObj = System.Environment.OSVersion.Version;
        var osVersion = osVersionObj.ToString();
        var osBuild = osVersionObj.Build.ToString(CultureInfo.InvariantCulture);
        var packaging = isPackagedMsix ? "msix" : "unpackaged";
        var resolvedEnvironment = !string.IsNullOrWhiteSpace(environment)
            ? environment.Trim()
            : (isPackagedMsix && channel.Channel == EnviousWispr.Core.Distribution.ReleaseChannel.Stable
                ? "production"
                : "development");

        return new WindowsObservabilityIdentity(
            AppVersion: version,
            OsVersion: osVersion,
            OsBuild: osBuild,
            ReleaseChannel: channel.ChannelName,
            Packaging: packaging,
            Environment: resolvedEnvironment,
            ProcessLaunchId: processLaunchId ?? Guid.NewGuid(),
            ProcessRole: string.IsNullOrWhiteSpace(processRole) ? "main" : processRole.Trim());
    }
}

public sealed record PostHogQueuedEvent(
    string EventName,
    Guid Uuid,
    string DistinctId,
    DateTimeOffset Timestamp,
    Dictionary<string, object?> Properties);

/// <summary>
/// Owned lightweight PostHog batch client for Windows with dual-stage sanitization,
/// deterministic 10% volume bucketing, anonymous distinct_id persistence, and immediate queue drop on opt-out.
/// </summary>
public sealed class PostHogTelemetryClient : IAsyncDisposable
{
    public const int DefaultFlushAt = 20;
    public static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromSeconds(30);
    public const int DefaultMaxQueueSize = 1000;
    public const string TelemetryIdFileName = "telemetry-id.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly string? _apiKey;
    private readonly Uri _batchEndpoint;
    private readonly string _dataDirectory;
    private readonly WindowsObservabilityIdentity _identity;
    private readonly int _flushAt;
    private readonly TimeSpan _flushInterval;
    private readonly HttpClient? _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Channel<PostHogQueuedEvent> _queue;
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly Lock _stateLock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task? _backgroundLoop;

    private volatile bool _usageMetricsEnabled;
    private volatile bool _disposed;
    private string? _distinctId;
    private long _operationGeneration;

    public PostHogTelemetryClient(
        string? apiKey,
        Uri host,
        string dataDirectory,
        WindowsObservabilityIdentity identity,
        bool usageMetricsEnabled = true,
        int flushAt = DefaultFlushAt,
        TimeSpan? flushInterval = null,
        int maxQueueSize = DefaultMaxQueueSize,
        HttpMessageHandler? messageHandler = null,
        bool startBackgroundFlushLoop = true)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentOutOfRangeException.ThrowIfLessThan(flushAt, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxQueueSize, 1);

        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        var baseHost = ObservabilityKeyResolver.NormalizePostHogHost(host.ToString());
        _batchEndpoint = new Uri(baseHost, "/batch/");
        _dataDirectory = Path.GetFullPath(dataDirectory);
        _identity = identity;
        _flushAt = flushAt;
        _flushInterval = flushInterval ?? DefaultFlushInterval;

        _queue = Channel.CreateBounded<PostHogQueuedEvent>(new BoundedChannelOptions(maxQueueSize)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite,
        });

        if (_apiKey is not null)
        {
            if (messageHandler is not null)
            {
                _httpClient = new HttpClient(messageHandler, disposeHandler: false)
                {
                    Timeout = TimeSpan.FromSeconds(10),
                };
                _ownsHttpClient = true;
            }
            else
            {
                var handler = new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    ConnectTimeout = TimeSpan.FromSeconds(5),
                };
                _httpClient = new HttpClient(handler, disposeHandler: true)
                {
                    Timeout = TimeSpan.FromSeconds(10),
                };
                _ownsHttpClient = true;
            }
        }

        SetUsageMetricsEnabled(usageMetricsEnabled);

        if (_apiKey is not null && startBackgroundFlushLoop)
        {
            _backgroundLoop = RunBackgroundFlushLoopAsync(_lifetime.Token);
        }
    }

    public bool IsConfigured => _apiKey is not null;

    public bool IsCapturing => IsConfigured && _usageMetricsEnabled && !_disposed && _distinctId is not null;

    public string? DistinctId
    {
        get
        {
            lock (_stateLock)
            {
                return _usageMetricsEnabled ? _distinctId : null;
            }
        }
    }

    public int QueuedCount => _queue.Reader.Count;

    public string TelemetryIdFilePath => Path.Combine(_dataDirectory, TelemetryIdFileName);

    /// <summary>
    /// Live-updates usage metrics consent. Turning OFF immediately disables capture, clears the active distinct_id,
    /// and drains/drops all queued events so nothing leaves after opt-out.
    /// </summary>
    public void SetUsageMetricsEnabled(bool enabled)
    {
        lock (_stateLock)
        {
            _operationGeneration++;
            if (!enabled)
            {
                _usageMetricsEnabled = false;
                _distinctId = null;
                DrainAndDropQueue();
                return;
            }

            if (_apiKey is null)
            {
                _usageMetricsEnabled = true;
                _distinctId = null;
                return;
            }

            _distinctId = LoadOrCreateAnonymousDistinctId();
            _usageMetricsEnabled = _distinctId is not null;
        }
    }

    /// <summary>
    /// Stage 1 &amp; Stage 2 processing: scrubs caller properties at call time, stamps Windows differentiation properties,
    /// applies the 10% deterministic volume policy, and runs the final <c>beforeSend</c> sanitizer before queueing.
    /// </summary>
    public bool Capture(
        string eventName,
        IReadOnlyDictionary<string, object?>? properties = null,
        Guid? eventUuid = null,
        DateTimeOffset? timestamp = null)
    {
        if (string.IsNullOrWhiteSpace(eventName) || !IsCapturing)
        {
            return false;
        }

        // Stage 1: scrub caller-supplied properties at write time.
        var stageOneProperties = ObservabilitySanitizer.RedactDictionary(properties);

        string? distinctId;
        lock (_stateLock)
        {
            if (!_usageMetricsEnabled || _distinctId is null || _disposed)
            {
                return false;
            }

            distinctId = _distinctId;
        }

        var uuid = eventUuid ?? Guid.NewGuid();
        var processed = ProcessBeforeSend(eventName, stageOneProperties, uuid, _identity, distinctId);
        if (processed is null)
        {
            return false;
        }

        var queuedEvent = new PostHogQueuedEvent(
            eventName,
            uuid,
            distinctId,
            timestamp ?? DateTimeOffset.UtcNow,
            processed);

        if (!_queue.Writer.TryWrite(queuedEvent))
        {
            return false;
        }

        if (!_usageMetricsEnabled)
        {
            DrainAndDropQueue();
            return false;
        }

        if (_queue.Reader.Count >= _flushAt)
        {
            _ = FlushAsync(_lifetime.Token);
        }

        return true;
    }

    /// <summary>
    /// Pure <c>beforeSend</c> seam shared by live capture and unit tests:
    /// stamps Windows differentiation fields, runs <see cref="TelemetryVolumePolicy"/>, and runs <see cref="ObservabilitySanitizer"/>.
    /// </summary>
    public static Dictionary<string, object?>? ProcessBeforeSend(
        string eventName,
        IReadOnlyDictionary<string, object?>? properties,
        Guid eventUuid,
        WindowsObservabilityIdentity identity,
        string? distinctId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(identity);

        var input = properties is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(properties, StringComparer.Ordinal);

        // Stamp unmistakable Windows differentiation properties BEFORE volume policy and final sanitization.
        input["app"] = WindowsObservabilityIdentity.PostHogAppIdentifier;
        input["platform"] = WindowsObservabilityIdentity.PlatformIdentifier;
        input["$os"] = WindowsObservabilityIdentity.OsName;
        input["os_version"] = identity.OsVersion;
        input["app_version"] = identity.AppVersion;
        input["release_channel"] = identity.ReleaseChannel;
        input["packaging"] = identity.Packaging;
        input["environment"] = identity.Environment;
        input["process_launch_id"] = identity.ProcessLaunchId.ToString("D");

        var kept = TelemetryVolumePolicy.Apply(eventName, input, eventUuid);
        if (kept is null)
        {
            return null;
        }

        // Stage 2: final beforeSend sanitization of the entire property bag.
        var sanitized = ObservabilitySanitizer.RedactDictionary(kept);
        if (ObservabilitySanitizer.CanonicalAnonymousPostHogId(distinctId) is { } validDistinctId)
        {
            sanitized["distinct_id"] = validDistinctId;
        }

        return sanitized;
    }

    public async Task<int> FlushAsync(CancellationToken cancellationToken = default)
    {
        if (!IsCapturing || _httpClient is null || _apiKey is null)
        {
            if (!_usageMetricsEnabled)
            {
                DrainAndDropQueue();
            }

            return 0;
        }

        await _flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sentCount = 0;
            while (_usageMetricsEnabled && !_disposed)
            {
                long generationSnapshot;
                lock (_stateLock)
                {
                    if (!_usageMetricsEnabled)
                    {
                        DrainAndDropQueue();
                        break;
                    }

                    generationSnapshot = _operationGeneration;
                }

                var batch = new List<PostHogQueuedEvent>(_flushAt);
                while (batch.Count < _flushAt && _queue.Reader.TryRead(out var item))
                {
                    batch.Add(item);
                }

                if (batch.Count == 0)
                {
                    break;
                }

                lock (_stateLock)
                {
                    if (!_usageMetricsEnabled || generationSnapshot != _operationGeneration)
                    {
                        DrainAndDropQueue();
                        break;
                    }
                }

                var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["api_key"] = _apiKey,
                    ["batch"] = batch.Select(item => new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["event"] = item.EventName,
                        ["uuid"] = item.Uuid.ToString("D"),
                        ["distinct_id"] = item.DistinctId,
                        ["timestamp"] = item.Timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                        ["properties"] = item.Properties,
                    }).ToArray(),
                };

                var json = JsonSerializer.Serialize(payload, JsonOptions);
                using var content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
                try
                {
                    using var response = await _httpClient.PostAsync(_batchEndpoint, content, cancellationToken)
                        .ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        sentCount += batch.Count;
                    }
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or OperationCanceledException)
                {
                    // Telemetry is best-effort and must never throw into product flows.
                    break;
                }
            }

            return sentCount;
        }
        finally
        {
            _flushGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (IsCapturing)
        {
            try
            {
                using var flushTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await FlushAsync(flushTimeout.Token).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort shutdown flush.
            }
        }

        _disposed = true;
        _lifetime.Cancel();
        if (_backgroundLoop is not null)
        {
            try
            {
                await _backgroundLoop.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        if (_ownsHttpClient)
        {
            _httpClient?.Dispose();
        }

        _flushGate.Dispose();
        _lifetime.Dispose();
    }

    private void DrainAndDropQueue()
    {
        while (_queue.Reader.TryRead(out _))
        {
        }
    }

    private string? LoadOrCreateAnonymousDistinctId()
    {
        var path = TelemetryIdFilePath;
        try
        {
            if (File.Exists(path))
            {
                var rawJson = File.ReadAllText(path);
                var persisted = JsonSerializer.Deserialize<PersistedTelemetryId>(rawJson, JsonOptions);
                if (ObservabilitySanitizer.CanonicalAnonymousPostHogId(persisted?.DistinctId) is { } validExisting)
                {
                    return validExisting;
                }
            }

            Directory.CreateDirectory(_dataDirectory);
            var minted = Guid.NewGuid().ToString("D");
            var serialized = JsonSerializer.Serialize(new PersistedTelemetryId(minted), JsonOptions);
            File.WriteAllText(path, serialized, Encoding.UTF8);
            return minted;
        }
        catch
        {
            // Fall back to an in-memory canonical UUID if disk write is unavailable.
            return Guid.NewGuid().ToString("D");
        }
    }

    private async Task RunBackgroundFlushLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_flushInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed record PersistedTelemetryId(
        [property: JsonPropertyName("distinctId")] string? DistinctId);
}
