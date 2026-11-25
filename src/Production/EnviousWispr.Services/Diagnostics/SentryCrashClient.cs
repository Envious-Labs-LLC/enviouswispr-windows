using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EnviousWispr.Services.Diagnostics;

public sealed record SentryBreadcrumbItem(
    string Timestamp,
    string Type,
    string Category,
    string Message,
    string Level,
    Dictionary<string, object?>? Data = null);

public sealed record SentryStackFrameItem(
    string? Function,
    string? Module,
    string? Filename,
    string? AbsPath,
    int? Lineno,
    string? InstructionAddr = null,
    string? Package = null);

public sealed record SentryExceptionItem(
    string Type,
    string Value,
    string MechanismType,
    bool Handled,
    IReadOnlyList<SentryStackFrameItem>? Frames = null,
    Dictionary<string, object?>? MechanismData = null);

public sealed record SentryCaptureEvent(
    string Level,
    string? Message = null,
    IReadOnlyList<SentryExceptionItem>? Exceptions = null,
    IReadOnlyDictionary<string, string>? Tags = null,
    IReadOnlyDictionary<string, object?>? Extra = null,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>>? Contexts = null,
    IReadOnlyList<string>? Fingerprint = null,
    DateTimeOffset? Timestamp = null,
    string? ReleaseOverride = null);

/// <summary>
/// Owned lightweight Sentry Envelope client for Windows, initialized synchronously at launch ONLY when
/// <c>SendCrashReports == true</c>. Stamps Windows MSIX release identity (<c>EnviousLabsLLC.EnviousWispr@&lt;version&gt;</c>),
/// <c>sdk.settings.infer_ip = "never"</c>, and runs dual-stage privacy sanitization.
/// </summary>
public sealed class SentryCrashClient : IDisposable
{
    public const string SdkName = "enviouswispr.windows.sentry";
    public const string JoinTagKey = "analytics.distinct_id";
    public const int MaxBreadcrumbs = 50;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly SentryDsnDescriptor? _dsn;
    private readonly WindowsObservabilityIdentity _identity;
    private readonly HttpClient? _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Lock _scopeLock = new();
    private readonly Dictionary<string, string> _tags = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, object?>> _contexts = new(StringComparer.Ordinal);
    private readonly Queue<SentryBreadcrumbItem> _breadcrumbs = new();

    private string? _offPeriodUserId;
    private bool _disposed;

    public SentryCrashClient(
        SentryDsnDescriptor? dsn,
        WindowsObservabilityIdentity identity,
        bool sendCrashReportsAtLaunch,
        bool usageMetricsEnabled = true,
        string? postHogDistinctId = null,
        HttpMessageHandler? messageHandler = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        _identity = identity;
        LaunchedCrashReports = sendCrashReportsAtLaunch;

        if (sendCrashReportsAtLaunch && dsn is not null)
        {
            _dsn = dsn;
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

        InitializeDefaultScope(usageMetricsEnabled, postHogDistinctId);
    }

    /// <summary>The <c>SendCrashReports</c> setting value this process launched with.</summary>
    public bool LaunchedCrashReports { get; }

    /// <summary>Whether Sentry is active in this process (requires <c>SendCrashReports == true</c> at launch and a valid DSN).</summary>
    public bool IsEnabled => LaunchedCrashReports && _dsn is not null && _httpClient is not null && !_disposed;

    /// <summary>Whether the stored setting differs from the launch-locked state and therefore requires a restart.</summary>
    public bool NeedsRestart(bool storedSendCrashReports) =>
        storedSendCrashReports != LaunchedCrashReports;

    public IReadOnlyDictionary<string, string> GetScopeTagsSnapshot()
    {
        lock (_scopeLock)
        {
            return new Dictionary<string, string>(_tags, StringComparer.Ordinal);
        }
    }

    public string? GetOffPeriodUserIdSnapshot()
    {
        lock (_scopeLock)
        {
            return _offPeriodUserId;
        }
    }

    /// <summary>
    /// Synchronizes Sentry's cross-vendor join tag and metrics-OFF user ID with the live usage metrics switch.
    /// When usage metrics are ON, tags <c>analytics.distinct_id</c> and clears <c>user.id</c>.
    /// When usage metrics are OFF, removes <c>analytics.distinct_id</c> and mints a fresh random ephemeral UUID for <c>user.id</c>.
    /// </summary>
    public void UpdateUsageMetricsState(bool usageMetricsEnabled, string? postHogDistinctId)
    {
        lock (_scopeLock)
        {
            if (usageMetricsEnabled)
            {
                _offPeriodUserId = null;
                if (ObservabilitySanitizer.CanonicalAnonymousPostHogId(postHogDistinctId) is { } validJoinKey)
                {
                    _tags[JoinTagKey] = ObservabilitySanitizer.RedactString(validJoinKey);
                }
                else
                {
                    _tags.Remove(JoinTagKey);
                }
            }
            else
            {
                _tags.Remove(JoinTagKey);
                _offPeriodUserId = ObservabilitySanitizer.RedactString(Guid.NewGuid().ToString("D"));
            }
        }
    }

    /// <summary>
    /// Updates the runtime ASR engine, acceleration provider, and GPU vendor tags on the global scope.
    /// Stage 1: every tag value is sanitized at write time.
    /// </summary>
    public void UpdateRuntimeScope(string asrEngine, string acceleration, string gpuVendors)
    {
        SetTag("runtime.asr_engine", asrEngine);
        SetTag("runtime.acceleration", acceleration);
        SetTag("gpu.vendors", gpuVendors);
    }

    /// <summary>Stage 1: scrubs tag value at write time.</summary>
    public void SetTag(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        lock (_scopeLock)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                _tags.Remove(key);
            }
            else
            {
                _tags[key] = ObservabilitySanitizer.RedactString(value);
            }
        }
    }

    /// <summary>Stage 1: scrubs context dictionary at write time.</summary>
    public void SetContext(string key, IReadOnlyDictionary<string, object?>? values)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        lock (_scopeLock)
        {
            if (values is null)
            {
                _contexts.Remove(key);
            }
            else
            {
                _contexts[key] = ObservabilitySanitizer.RedactDictionary(values);
            }
        }
    }

    /// <summary>Stage 1: scrubs breadcrumb stage, message, and data at write time.</summary>
    public void AddBreadcrumb(
        string stage,
        string message,
        string level = "info",
        IReadOnlyDictionary<string, object?>? data = null,
        DateTimeOffset? timestamp = null)
    {
        if (!LaunchedCrashReports)
        {
            return;
        }

        var redactedStage = ObservabilitySanitizer.RedactString(stage);
        var redactedMessage = ObservabilitySanitizer.RedactString(message);
        var redactedData = data is null ? null : ObservabilitySanitizer.RedactDictionary(data);
        var crumb = new SentryBreadcrumbItem(
            Timestamp: (timestamp ?? DateTimeOffset.UtcNow).UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            Type: "default",
            Category: $"pipeline.{redactedStage}",
            Message: redactedMessage,
            Level: string.IsNullOrWhiteSpace(level) ? "info" : level,
            Data: redactedData);

        lock (_scopeLock)
        {
            while (_breadcrumbs.Count >= MaxBreadcrumbs)
            {
                _breadcrumbs.Dequeue();
            }

            _breadcrumbs.Enqueue(crumb);
        }
    }

    public Task<Guid?> CaptureExceptionAsync(
        Exception exception,
        string mechanismType = "unhandled",
        bool handled = false,
        string level = "fatal",
        IReadOnlyDictionary<string, string>? tags = null,
        IReadOnlyDictionary<string, object?>? extra = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var frames = ExtractStackFrames(exception);
        var exceptionType = exception.GetType().FullName ?? exception.GetType().Name;
        // Layer A structural value: never copy free-form exception.Message onto the wire,
        // because short validation or pipeline messages could otherwise include user text.
        var structuralValue = $"{exceptionType}#0x{exception.HResult:X8}";
        var item = new SentryExceptionItem(
            Type: exceptionType,
            Value: structuralValue,
            MechanismType: mechanismType,
            Handled: handled,
            Frames: frames);

        return CaptureEventAsync(
            new SentryCaptureEvent(
                Level: level,
                Message: $"{mechanismType}: {exceptionType}",
                Exceptions: [item],
                Tags: tags,
                Extra: extra),
            cancellationToken);
    }

    public async Task<Guid?> CaptureEventAsync(
        SentryCaptureEvent captureEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(captureEvent);
        if (!IsEnabled || _dsn is null || _httpClient is null)
        {
            return null;
        }

        var eventId = Guid.NewGuid();
        var now = captureEvent.Timestamp ?? DateTimeOffset.UtcNow;
        var payload = BuildAndSanitizeEventPayload(captureEvent, eventId, now);
        var envelope = BuildEnvelope(_dsn, _identity, eventId, now, payload);

        using var request = new HttpRequestMessage(HttpMethod.Post, _dsn.EnvelopeUri);
        request.Headers.TryAddWithoutValidation(
            "X-Sentry-Auth",
            BuildSentryAuthHeader(_dsn.PublicKey, _identity.AppVersion));
        request.Content = new StringContent(
            envelope,
            Encoding.UTF8,
            new MediaTypeHeaderValue("application/x-sentry-envelope"));

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? eventId : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // Crash reporting is a limb and must never fail the application.
            return null;
        }
    }

    public static string BuildSentryAuthHeader(string publicKey, string appVersion) =>
        $"Sentry sentry_version=7, sentry_client={SdkName}/{appVersion}, sentry_key={publicKey}";

    /// <summary>
    /// Stage 2 (<c>beforeSend</c>): builds the final Sentry event JSON dictionary, stamps Windows differentiation
    /// contexts and tags, strips <c>server_name</c>, and scrubs every surface through <see cref="ObservabilitySanitizer"/>.
    /// </summary>
    public Dictionary<string, object?> BuildAndSanitizeEventPayload(
        SentryCaptureEvent captureEvent,
        Guid eventId,
        DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(captureEvent);

        Dictionary<string, string> mergedTags;
        Dictionary<string, Dictionary<string, object?>> mergedContexts;
        List<SentryBreadcrumbItem> crumbsSnapshot;
        string? offPeriodUserId;

        lock (_scopeLock)
        {
            mergedTags = new Dictionary<string, string>(_tags, StringComparer.Ordinal);
            mergedContexts = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
            foreach (var (key, ctx) in _contexts)
            {
                mergedContexts[key] = new Dictionary<string, object?>(ctx, StringComparer.Ordinal);
            }

            crumbsSnapshot = [.. _breadcrumbs];
            offPeriodUserId = _offPeriodUserId;
        }

        if (captureEvent.Tags is not null)
        {
            foreach (var (key, value) in captureEvent.Tags)
            {
                mergedTags[key] = value;
            }
        }

        if (captureEvent.Contexts is not null)
        {
            foreach (var (key, value) in captureEvent.Contexts)
            {
                mergedContexts[key] = new Dictionary<string, object?>(value, StringComparer.Ordinal);
            }
        }

        // Stamp unmistakable Windows differentiation tags and OS context.
        mergedTags["app.platform"] = WindowsObservabilityIdentity.PlatformIdentifier;
        mergedTags["app.channel"] = _identity.ReleaseChannel;
        mergedTags["app.packaging"] = _identity.Packaging;
        if (!mergedTags.ContainsKey("process.role"))
        {
            mergedTags["process.role"] = _identity.ProcessRole;
        }

        mergedTags["crash.context_schema"] = WindowsObservabilityIdentity.CrashContextSchemaVersion;
        mergedTags["process.launch_id"] = _identity.ProcessLaunchId.ToString("D");

        mergedContexts["os"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = WindowsObservabilityIdentity.OsName,
            ["version"] = _identity.OsVersion,
            ["build"] = _identity.OsBuild,
        };

        // Stage 2 beforeSend sanitization on tags, contexts, extra, breadcrumbs, message, and exceptions.
        var sanitizedTags = ObservabilitySanitizer.RedactStringDictionary(mergedTags);

        var sanitizedContexts = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (ctxKey, ctxDict) in mergedContexts)
        {
            sanitizedContexts[ctxKey] = ObservabilitySanitizer.RedactDictionary(ctxDict);
        }

        Dictionary<string, object?>? sanitizedExtra = null;
        if (captureEvent.Extra is not null && captureEvent.Extra.Count > 0)
        {
            sanitizedExtra = ObservabilitySanitizer.RedactDictionary(captureEvent.Extra);
        }

        List<Dictionary<string, object?>>? sanitizedBreadcrumbs = null;
        if (crumbsSnapshot.Count > 0)
        {
            sanitizedBreadcrumbs = new List<Dictionary<string, object?>>(crumbsSnapshot.Count);
            foreach (var crumb in crumbsSnapshot)
            {
                var crumbDict = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["timestamp"] = crumb.Timestamp,
                    ["type"] = crumb.Type,
                    ["category"] = ObservabilitySanitizer.RedactString(crumb.Category),
                    ["message"] = ObservabilitySanitizer.RedactString(crumb.Message),
                    ["level"] = crumb.Level,
                };
                if (crumb.Data is not null && crumb.Data.Count > 0)
                {
                    crumbDict["data"] = ObservabilitySanitizer.RedactDictionary(crumb.Data);
                }

                sanitizedBreadcrumbs.Add(crumbDict);
            }
        }

        Dictionary<string, object?>? exceptionPayload = null;
        if (captureEvent.Exceptions is { Count: > 0 } exceptions)
        {
            var sanitizedExceptions = new List<Dictionary<string, object?>>(exceptions.Count);
            var reasons = new List<string>();
            var hasFramesOrAddresses = false;

            foreach (var exc in exceptions)
            {
                var redactedValue = ObservabilitySanitizer.RedactString(exc.Value);
                if (!string.IsNullOrEmpty(redactedValue))
                {
                    reasons.Add(redactedValue);
                }

                var mechanism = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = ObservabilitySanitizer.RedactString(exc.MechanismType),
                    ["handled"] = exc.Handled,
                };
                if (exc.MechanismData is not null && exc.MechanismData.Count > 0)
                {
                    mechanism["data"] = ObservabilitySanitizer.RedactDictionary(exc.MechanismData);
                }

                var excDict = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = ObservabilitySanitizer.RedactUserPath(exc.Type),
                    ["value"] = redactedValue,
                    ["mechanism"] = mechanism,
                };

                if (exc.Frames is { Count: > 0 } frames)
                {
                    var sanitizedFrames = new List<Dictionary<string, object?>>(frames.Count);
                    foreach (var frame in frames)
                    {
                        hasFramesOrAddresses = true;
                        var frameDict = new Dictionary<string, object?>(StringComparer.Ordinal);
                        if (!string.IsNullOrWhiteSpace(frame.Function))
                        {
                            frameDict["function"] = ObservabilitySanitizer.RedactUserPath(frame.Function);
                        }

                        if (!string.IsNullOrWhiteSpace(frame.Module))
                        {
                            frameDict["module"] = ObservabilitySanitizer.RedactUserPath(frame.Module);
                        }

                        if (!string.IsNullOrWhiteSpace(frame.Filename))
                        {
                            frameDict["filename"] = ObservabilitySanitizer.RedactUserPath(frame.Filename);
                        }

                        if (!string.IsNullOrWhiteSpace(frame.AbsPath))
                        {
                            frameDict["abs_path"] = ObservabilitySanitizer.RedactUserPath(frame.AbsPath);
                        }

                        if (frame.Lineno is { } lineNo && lineNo > 0)
                        {
                            frameDict["lineno"] = lineNo;
                        }

                        if (!string.IsNullOrWhiteSpace(frame.InstructionAddr))
                        {
                            frameDict["instruction_addr"] = frame.InstructionAddr;
                        }

                        if (!string.IsNullOrWhiteSpace(frame.Package))
                        {
                            frameDict["package"] = ObservabilitySanitizer.RedactUserPath(frame.Package);
                        }

                        sanitizedFrames.Add(frameDict);
                    }

                    excDict["stacktrace"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["frames"] = sanitizedFrames,
                    };
                }

                sanitizedExceptions.Add(excDict);
            }

            sanitizedTags["exception.reason_status"] = reasons.Any(r => r.Contains(ObservabilitySanitizer.RedactedPlaceholder, StringComparison.Ordinal))
                ? "redacted"
                : (reasons.Count == 0 ? "not_provided" : "provided");
            sanitizedTags["exception.stack_status"] = hasFramesOrAddresses ? "addresses_present" : "unavailable";

            exceptionPayload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["values"] = sanitizedExceptions,
            };
        }

        var release = !string.IsNullOrWhiteSpace(captureEvent.ReleaseOverride)
            ? captureEvent.ReleaseOverride!
            : _identity.SentryRelease;

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["event_id"] = eventId.ToString("N"),
            ["timestamp"] = timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            ["platform"] = WindowsObservabilityIdentity.SentryPlatformIdentifier,
            ["level"] = string.IsNullOrWhiteSpace(captureEvent.Level) ? "error" : captureEvent.Level,
            ["release"] = release,
            ["environment"] = _identity.Environment,
            ["sdk"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = SdkName,
                ["version"] = _identity.AppVersion,
                ["settings"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["infer_ip"] = "never",
                },
            },
            ["contexts"] = sanitizedContexts,
            ["tags"] = sanitizedTags,
        };

        if (!string.IsNullOrWhiteSpace(captureEvent.Message))
        {
            payload["message"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["formatted"] = ObservabilitySanitizer.RedactString(captureEvent.Message),
            };
        }

        if (exceptionPayload is not null)
        {
            payload["exception"] = exceptionPayload;
        }

        if (sanitizedBreadcrumbs is not null)
        {
            payload["breadcrumbs"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["values"] = sanitizedBreadcrumbs,
            };
        }

        if (sanitizedExtra is not null)
        {
            payload["extra"] = sanitizedExtra;
        }

        if (captureEvent.Fingerprint is { Count: > 0 } fingerprint)
        {
            payload["fingerprint"] = fingerprint.Select(ObservabilitySanitizer.RedactString).ToArray();
        }

        if (!string.IsNullOrWhiteSpace(offPeriodUserId))
        {
            payload["user"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = ObservabilitySanitizer.RedactString(offPeriodUserId),
            };
        }

        // Explicitly ensure server_name is never included in the wire payload.
        payload.Remove("server_name");
        return payload;
    }

    public static string BuildEnvelope(
        SentryDsnDescriptor dsn,
        WindowsObservabilityIdentity identity,
        Guid eventId,
        DateTimeOffset sentAt,
        IReadOnlyDictionary<string, object?> eventPayload)
    {
        ArgumentNullException.ThrowIfNull(dsn);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(eventPayload);

        var envelopeHeader = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["event_id"] = eventId.ToString("N"),
            ["sent_at"] = sentAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            ["dsn"] = dsn.RawDsn,
            ["sdk"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = SdkName,
                ["version"] = identity.AppVersion,
            },
        };

        var eventJson = JsonSerializer.Serialize(eventPayload, JsonOptions);
        var eventByteCount = Encoding.UTF8.GetByteCount(eventJson);
        var itemHeader = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "event",
            ["content_type"] = "application/json",
            ["length"] = eventByteCount,
        };

        var envelopeHeaderJson = JsonSerializer.Serialize(envelopeHeader, JsonOptions);
        var itemHeaderJson = JsonSerializer.Serialize(itemHeader, JsonOptions);
        return $"{envelopeHeaderJson}\n{itemHeaderJson}\n{eventJson}\n";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsHttpClient)
        {
            _httpClient?.Dispose();
        }
    }

    private void InitializeDefaultScope(bool usageMetricsEnabled, string? postHogDistinctId)
    {
        lock (_scopeLock)
        {
            _tags["app.platform"] = WindowsObservabilityIdentity.PlatformIdentifier;
            _tags["app.channel"] = ObservabilitySanitizer.RedactString(_identity.ReleaseChannel);
            _tags["app.packaging"] = ObservabilitySanitizer.RedactString(_identity.Packaging);
            _tags["process.role"] = ObservabilitySanitizer.RedactString(_identity.ProcessRole);
            _tags["runtime.asr_engine"] = "parakeet";
            _tags["runtime.acceleration"] = "unknown";
            _tags["gpu.vendors"] = "unknown";
            _tags["crash.context_schema"] = WindowsObservabilityIdentity.CrashContextSchemaVersion;
            _tags["process.launch_id"] = _identity.ProcessLaunchId.ToString("D");

            _contexts["os"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = WindowsObservabilityIdentity.OsName,
                ["version"] = _identity.OsVersion,
                ["build"] = _identity.OsBuild,
            };
        }

        UpdateUsageMetricsState(usageMetricsEnabled, postHogDistinctId);
    }

    private static List<SentryStackFrameItem> ExtractStackFrames(Exception exception)
    {
        var trace = new StackTrace(exception, fNeedFileInfo: true);
        var rawFrames = trace.GetFrames();
        if (rawFrames is null || rawFrames.Length == 0)
        {
            return [];
        }

        // Sentry expects frames ordered oldest-to-newest (caller first, throw site last).
        var items = new List<SentryStackFrameItem>(rawFrames.Length);
        for (var i = rawFrames.Length - 1; i >= 0; i--)
        {
            var frame = rawFrames[i];
            var method = frame.GetMethod();
            var functionName = method is null
                ? "<unknown>"
                : $"{method.DeclaringType?.FullName ?? "<global>"}.{method.Name}";
            var moduleName = method?.DeclaringType?.Assembly.GetName().Name;
            var rawFileName = frame.GetFileName();
            var leafFileName = string.IsNullOrWhiteSpace(rawFileName)
                ? null
                : ObservabilitySanitizer.RedactString(Path.GetFileName(rawFileName));
            var lineNo = frame.GetFileLineNumber();
            var ilOffset = frame.GetILOffset();
            var instructionAddr = ilOffset >= 0 ? $"0x{ilOffset:x4}" : null;

            items.Add(new SentryStackFrameItem(
                Function: ObservabilitySanitizer.RedactUserPath(functionName),
                Module: ObservabilitySanitizer.RedactUserPath(moduleName),
                Filename: leafFileName,
                AbsPath: leafFileName,
                Lineno: lineNo > 0 ? lineNo : null,
                InstructionAddr: instructionAddr,
                Package: ObservabilitySanitizer.RedactUserPath(moduleName)));
        }

        return items;
    }
}
