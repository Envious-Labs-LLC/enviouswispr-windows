using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using EnviousWispr.Core.Feedback;
using EnviousWispr.Core.Networking;
using EnviousWispr.Services.Diagnostics;

namespace EnviousWispr.Services.Feedback;

/// <summary>
/// Sends privacy-scrubbed <c>type: "feedback"</c> Sentry envelopes using the Windows Sentry
/// DSN resolution and sanitization infrastructure.
/// </summary>
public sealed partial class SentryFeedbackSender : ISentryFeedbackTransport, IDisposable
{
    public const string DiagnosticsAttachmentFilename = "enviouswispr-diagnostics.json";

    private static readonly HashSet<string> LimitingRateCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        string.Empty,
        "feedback",
        "user_report_v2",
        "attachment",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly Func<SentryDsnDescriptor?> _resolveDsn;
    private readonly WindowsObservabilityIdentity _identity;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private bool _disposed;

    public SentryFeedbackSender(
        WindowsObservabilityIdentity identity,
        Func<ResolvedObservabilityKeys>? keyResolver = null,
        HttpMessageHandler? messageHandler = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        _identity = identity;
        var resolver = keyResolver ?? (() => ObservabilityKeyResolver.Resolve());
        _resolveDsn = () =>
        {
            var resolved = resolver();
            return resolved.HasSentry ? resolved.SentryDsn : null;
        };
        (_httpClient, _ownsHttpClient) = CreateHttpClient(messageHandler);
    }

    public SentryFeedbackSender(
        SentryDsnDescriptor? dsn,
        WindowsObservabilityIdentity identity,
        HttpMessageHandler? messageHandler = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        _identity = identity;
        _resolveDsn = () => dsn;
        (_httpClient, _ownsHttpClient) = CreateHttpClient(messageHandler);
    }

    public async Task<FeedbackTransportResult> SendAsync(
        FeedbackSubmission submission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var dsn = _resolveDsn();
        if (dsn is null)
        {
            return new FeedbackTransportResult(
                FeedbackTransportStatus.Unavailable,
                Detail: "Feedback delivery is not configured.");
        }

        var sentAt = DateTimeOffset.UtcNow;
        var envelope = BuildEnvelope(dsn, _identity, submission, sentAt);

        var authVersion = submission.IncludeDiagnostics && submission.DiagnosticsSnapshot is not null
            ? _identity.AppVersion
            : "0.0.0";
        using var request = new HttpRequestMessage(HttpMethod.Post, dsn.EnvelopeUri);
        request.Headers.TryAddWithoutValidation(
            "X-Sentry-Auth",
            SentryCrashClient.BuildSentryAuthHeader(dsn.PublicKey, authVersion));
        request.Content = new StringContent(
            envelope,
            Encoding.UTF8,
            new MediaTypeHeaderValue("application/x-sentry-envelope"));

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return ClassifyResponse(response, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return new FeedbackTransportResult(
                FeedbackTransportStatus.TransientFailure,
                Detail: "Network error while sending feedback.");
        }
    }

    public static FeedbackTransportResult ClassifyResponse(
        HttpResponseMessage response,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(response);

        var statusCode = (int)response.StatusCode;
        var retryAfterUtc = ResolveRetryAfterUtc(response, nowUtc);

        if (response.IsSuccessStatusCode)
        {
            return new FeedbackTransportResult(
                FeedbackTransportStatus.Sent,
                statusCode,
                RetryAfterUtc: retryAfterUtc);
        }

        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge)
        {
            return new FeedbackTransportResult(
                FeedbackTransportStatus.Rejected,
                statusCode,
                $"Feedback rejected by server ({statusCode}).",
                RetryAfterUtc: retryAfterUtc);
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            return new FeedbackTransportResult(
                FeedbackTransportStatus.Unavailable,
                statusCode,
                $"Feedback endpoint unavailable ({statusCode}).",
                RetryAfterUtc: retryAfterUtc);
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var effectiveHold = retryAfterUtc ?? nowUtc.AddSeconds(60);
            return new FeedbackTransportResult(
                FeedbackTransportStatus.TransientFailure,
                statusCode,
                "Feedback rate limited by server (429).",
                RetryAfterUtc: effectiveHold);
        }

        return new FeedbackTransportResult(
            FeedbackTransportStatus.TransientFailure,
            statusCode,
            $"Temporary server error ({statusCode}).",
            RetryAfterUtc: retryAfterUtc);
    }

    public static DateTimeOffset? ResolveRetryAfterUtc(
        HttpResponseMessage response,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(response);

        DateTimeOffset? longest = null;

        if (response.Headers.TryGetValues("X-Sentry-Rate-Limits", out var rateLimitValues))
        {
            foreach (var headerValue in rateLimitValues)
            {
                if (ParseLongestApplicableRateLimitSeconds(headerValue) is { } seconds)
                {
                    var candidate = nowUtc.AddSeconds(seconds);
                    if (longest is null || candidate > longest.Value)
                    {
                        longest = candidate;
                    }
                }
            }
        }

        if (response.Headers.RetryAfter is { } retryAfter)
        {
            DateTimeOffset? candidate = null;
            if (retryAfter.Delta is { } delta && delta > TimeSpan.Zero)
            {
                candidate = nowUtc.Add(delta);
            }
            else if (retryAfter.Date is { } date)
            {
                candidate = date.ToUniversalTime();
            }

            if (candidate is not null && (longest is null || candidate.Value > longest.Value))
            {
                longest = candidate.Value;
            }
        }

        return longest;
    }

    public static double? ParseLongestApplicableRateLimitSeconds(string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return null;
        }

        double? longest = null;
        foreach (var rawQuota in headerValue.Split(','))
        {
            var fields = rawQuota.Trim().Split(':', StringSplitOptions.None);
            if (fields.Length < 2)
            {
                continue;
            }

            if (!double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
                double.IsNaN(seconds) ||
                double.IsInfinity(seconds) ||
                seconds <= 0)
            {
                continue;
            }

            var categories = string.IsNullOrEmpty(fields[1])
                ? [string.Empty]
                : fields[1].Split(';');

            if (!categories.Any(category => LimitingRateCategories.Contains(category.Trim())))
            {
                continue;
            }

            longest = longest is null ? seconds : Math.Max(longest.Value, seconds);
        }

        return longest;
    }

    public static string BuildEnvelope(
        SentryDsnDescriptor dsn,
        WindowsObservabilityIdentity identity,
        FeedbackSubmission submission,
        DateTimeOffset sentAt)
    {
        ArgumentNullException.ThrowIfNull(dsn);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(submission);

        var eventId = submission.SubmissionId.ToString("N");
        var includeDiagnostics = submission.IncludeDiagnostics && submission.DiagnosticsSnapshot is not null;
        var envelopeHeader = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["event_id"] = eventId,
            ["sent_at"] = sentAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            ["dsn"] = dsn.RawDsn,
            ["sdk"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = SentryCrashClient.SdkName,
                ["version"] = includeDiagnostics
                    ? ObservabilitySanitizer.RedactString(identity.AppVersion)
                    : "0.0.0",
            },
        };

        var payload = BuildAndSanitizePayload(identity, submission);
        var payloadJson = JsonSerializer.Serialize(payload, JsonOptions);
        var payloadByteCount = Encoding.UTF8.GetByteCount(payloadJson);

        var feedbackItemHeader = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "feedback",
            ["content_type"] = "application/json",
            ["length"] = payloadByteCount,
        };

        var sb = new StringBuilder();
        sb.Append(JsonSerializer.Serialize(envelopeHeader, JsonOptions)).Append('\n');
        sb.Append(JsonSerializer.Serialize(feedbackItemHeader, JsonOptions)).Append('\n');
        sb.Append(payloadJson).Append('\n');

        if (submission.IncludeDiagnostics && submission.DiagnosticsSnapshot is { } snapshot)
        {
            var sanitizedDiagnostics = BuildSanitizedDiagnosticsDictionary(snapshot);
            var attachmentJson = JsonSerializer.Serialize(sanitizedDiagnostics, JsonOptions);
            var attachmentByteCount = Encoding.UTF8.GetByteCount(attachmentJson);
            var attachmentHeader = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "attachment",
                ["length"] = attachmentByteCount,
                ["filename"] = DiagnosticsAttachmentFilename,
                ["content_type"] = "application/json",
            };

            sb.Append(JsonSerializer.Serialize(attachmentHeader, JsonOptions)).Append('\n');
            sb.Append(attachmentJson).Append('\n');
        }

        return sb.ToString();
    }

    public static Dictionary<string, object?> BuildAndSanitizePayload(
        WindowsObservabilityIdentity identity,
        FeedbackSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(submission);

        var feedbackContext = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["message"] = ScrubFeedbackMessage(submission.Message),
            ["source"] = "custom",
        };

        // Keep explicit contact_email ONLY if the user typed one in the contact email field.
        if (!string.IsNullOrWhiteSpace(submission.ContactEmail))
        {
            feedbackContext["contact_email"] = submission.ContactEmail.Trim();
        }

        var includeDiagnostics = submission.IncludeDiagnostics && submission.DiagnosticsSnapshot is not null;

        var contexts = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["feedback"] = feedbackContext,
        };

        var tags = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["feedback.kind"] = FeedbackKinds.ToTagValue(submission.Kind),
            ["diagnostics.included"] = includeDiagnostics ? "true" : "false",
        };

        if (includeDiagnostics && submission.DiagnosticsSnapshot is { } snapshot)
        {
            contexts["app"] = ObservabilitySanitizer.RedactDictionary(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["app_version"] = identity.AppVersion,
            });
            contexts["os"] = ObservabilitySanitizer.RedactDictionary(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = WindowsObservabilityIdentity.OsName,
                ["version"] = identity.OsVersion,
                ["build"] = identity.OsBuild,
            });
            contexts["diagnostics"] = BuildSanitizedDiagnosticsDictionary(snapshot);

            tags["app.platform"] = WindowsObservabilityIdentity.PlatformIdentifier;
            tags["app.build_type"] = WindowsObservabilityIdentity.CompiledBuildType;
            tags["app.channel"] = identity.ReleaseChannel;
            tags["app.packaging"] = identity.Packaging;
            tags["runtime.asr_engine"] = snapshot.FinalAsrEngine;
            tags["runtime.polish_provider"] = snapshot.PolishProvider;
            tags["hardware.class"] = snapshot.HardwareClass;
        }

        var sanitizedTags = ObservabilitySanitizer.RedactStringDictionary(tags);
        var sdkVersion = includeDiagnostics
            ? ObservabilitySanitizer.RedactString(identity.AppVersion)
            : "0.0.0";

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["event_id"] = submission.SubmissionId.ToString("N"),
            ["type"] = "feedback",
            ["timestamp"] = submission.CreatedAtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            ["platform"] = WindowsObservabilityIdentity.SentryPlatformIdentifier,
            ["level"] = "error",
            ["sdk"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = SentryCrashClient.SdkName,
                ["version"] = sdkVersion,
                ["settings"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["infer_ip"] = "never",
                },
            },
            ["contexts"] = contexts,
            ["tags"] = sanitizedTags,
        };

        if (includeDiagnostics)
        {
            payload["release"] = ObservabilitySanitizer.RedactString(identity.SentryRelease);
            payload["environment"] = ObservabilitySanitizer.RedactString(identity.Environment);
        }

        payload.Remove("server_name");
        payload.Remove("user");
        return payload;
    }

    public static Dictionary<string, object?> BuildSanitizedDiagnosticsDictionary(
        FeedbackDiagnosticsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var raw = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["app_version"] = snapshot.AppVersion,
            ["os_version"] = snapshot.OsVersion,
            ["final_asr_engine"] = snapshot.FinalAsrEngine,
            ["polish_provider"] = snapshot.PolishProvider,
            ["live_preview_enabled"] = snapshot.LivePreviewEnabled,
            ["live_preview_engine"] = snapshot.LivePreviewEngine,
            ["recording_mode"] = snapshot.RecordingMode,
            ["auto_stop_enabled"] = snapshot.AutoStopEnabled,
            ["hardware_class"] = snapshot.HardwareClass,
            ["recent_event_codes"] = snapshot.RecentEventCodes,
        };

        return ObservabilitySanitizer.RedactDictionary(raw);
    }

    /// <summary>
    /// Scrubs user paths, accidental email addresses, API keys, and 32+ hex tokens from
    /// free-form feedback messages without truncating legitimate multi-sentence descriptions.
    /// </summary>
    public static string ScrubFeedbackMessage(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        var scrubbed = ObservabilitySanitizer.RedactUserPath(message);
        scrubbed = EmbeddedApiKeyRegex().Replace(scrubbed, ObservabilitySanitizer.RedactedPlaceholder);
        scrubbed = ContiguousHexRegex().Replace(scrubbed, ObservabilitySanitizer.RedactedPlaceholder);
        scrubbed = EmailRegex().Replace(scrubbed, ObservabilitySanitizer.RedactedPlaceholder);
        return scrubbed;
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
            _httpClient.Dispose();
        }
    }

    private static (HttpClient Client, bool OwnsClient) CreateHttpClient(HttpMessageHandler? messageHandler) =>
        (SharedHttpClients.CreateClient(
            messageHandler,
            HttpClientProfile.NoRedirect,
            TimeSpan.FromSeconds(10)), true);

    [GeneratedRegex(@"(?i)\b(?:sk-[A-Za-z0-9_\-]{17,}|phc_[A-Za-z0-9_\-]{16,}|sntrys_[A-Za-z0-9_\-]{13,}|key_[A-Za-z0-9_\-]{16,})", RegexOptions.CultureInvariant)]
    private static partial Regex EmbeddedApiKeyRegex();

    [GeneratedRegex(@"[0-9a-fA-F]{32,}", RegexOptions.CultureInvariant)]
    private static partial Regex ContiguousHexRegex();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();
}
