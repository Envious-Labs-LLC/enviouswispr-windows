using EnviousWispr.Core.Feedback;

namespace EnviousWispr.Services.Feedback;

public enum FeedbackTransportStatus
{
    Sent,
    Unavailable,
    TransientFailure,
    Rejected,
}

public sealed record FeedbackTransportResult(
    FeedbackTransportStatus Status,
    int? HttpStatusCode = null,
    string? Detail = null,
    DateTimeOffset? RetryAfterUtc = null);

/// <summary>
/// Transport abstraction for sending feedback submissions to Sentry.
/// </summary>
public interface ISentryFeedbackTransport
{
    Task<FeedbackTransportResult> SendAsync(
        FeedbackSubmission submission,
        CancellationToken cancellationToken = default);
}
