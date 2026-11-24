using System.Text.Json.Serialization;

namespace EnviousWispr.Core.Feedback;

/// <summary>
/// Outcome of attempting to deliver an in-app feedback submission.
/// </summary>
public enum FeedbackSendOutcome
{
    Sent,
    QueuedOffline,
    Unavailable,
    OutboxFull,
    Rejected,
}

/// <summary>
/// Result returned to the UI after submitting feedback.
/// </summary>
public sealed record FeedbackSendResult(
    FeedbackSendOutcome Outcome,
    Guid SubmissionId,
    string? Detail = null,
    int? HttpStatusCode = null)
{
    public bool IsAcceptedOrQueued =>
        Outcome is FeedbackSendOutcome.Sent or FeedbackSendOutcome.QueuedOffline;
}

/// <summary>
/// Validated, bounded in-app feedback submission payload.
/// </summary>
public sealed record FeedbackSubmission
{
    public const int MaxMessageLength = 4000;
    public const int MaxContactEmailLength = 254;

    [JsonConstructor]
    public FeedbackSubmission(
        Guid submissionId,
        DateTimeOffset createdAtUtc,
        FeedbackKind kind,
        string message,
        string? contactEmail,
        bool includeDiagnostics,
        FeedbackDiagnosticsSnapshot? diagnosticsSnapshot)
    {
        if (submissionId == Guid.Empty)
        {
            throw new ArgumentException("SubmissionId must be a non-empty GUID.", nameof(submissionId));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        var normalizedMessage = NormalizeMessage(message, out var messageError);
        if (normalizedMessage is null)
        {
            throw new ArgumentException(messageError, nameof(message));
        }

        var normalizedEmail = NormalizeContactEmail(contactEmail, out var emailError);
        if (emailError is not null)
        {
            throw new ArgumentException(emailError, nameof(contactEmail));
        }

        SubmissionId = submissionId;
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
        Kind = kind;
        Message = normalizedMessage;
        ContactEmail = normalizedEmail;
        IncludeDiagnostics = includeDiagnostics;
        DiagnosticsSnapshot = includeDiagnostics ? diagnosticsSnapshot : null;
    }

    public Guid SubmissionId { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public FeedbackKind Kind { get; }

    public string Message { get; }

    public string? ContactEmail { get; }

    public bool IncludeDiagnostics { get; }

    public FeedbackDiagnosticsSnapshot? DiagnosticsSnapshot { get; }

    public static FeedbackSubmission Create(
        FeedbackKind kind,
        string? message,
        string? contactEmail = null,
        bool includeDiagnostics = true,
        FeedbackDiagnosticsSnapshot? diagnosticsSnapshot = null,
        Guid? submissionId = null,
        DateTimeOffset? createdAtUtc = null)
    {
        if (!TryCreate(
            kind,
            message,
            contactEmail,
            includeDiagnostics,
            diagnosticsSnapshot,
            out var submission,
            out var validationError,
            submissionId,
            createdAtUtc))
        {
            throw new ArgumentException(validationError ?? "Invalid feedback submission.");
        }

        return submission!;
    }

    public static bool TryCreate(
        FeedbackKind kind,
        string? message,
        string? contactEmail,
        bool includeDiagnostics,
        FeedbackDiagnosticsSnapshot? diagnosticsSnapshot,
        out FeedbackSubmission? submission,
        out string? validationError,
        Guid? submissionId = null,
        DateTimeOffset? createdAtUtc = null)
    {
        submission = null;

        if (!Enum.IsDefined(kind))
        {
            validationError = "Please select a valid feedback category.";
            return false;
        }

        var resolvedId = submissionId ?? Guid.NewGuid();
        if (resolvedId == Guid.Empty)
        {
            validationError = "SubmissionId must be a non-empty GUID.";
            return false;
        }

        var normalizedMessage = NormalizeMessage(message, out var messageError);
        if (normalizedMessage is null)
        {
            validationError = messageError;
            return false;
        }

        var normalizedEmail = NormalizeContactEmail(contactEmail, out var emailError);
        if (emailError is not null)
        {
            validationError = emailError;
            return false;
        }

        submission = new FeedbackSubmission(
            resolvedId,
            (createdAtUtc ?? DateTimeOffset.UtcNow).ToUniversalTime(),
            kind,
            normalizedMessage,
            normalizedEmail,
            includeDiagnostics,
            includeDiagnostics ? diagnosticsSnapshot : null);
        validationError = null;
        return true;
    }

    public static bool IsValidOptionalEmail(string? rawEmail, out string? error)
    {
        _ = NormalizeContactEmail(rawEmail, out error);
        return error is null;
    }

    public static string? NormalizeContactEmail(string? rawEmail, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(rawEmail))
        {
            return null;
        }

        var trimmed = rawEmail.Trim();
        if (trimmed.Length > MaxContactEmailLength)
        {
            error = $"Email must be {MaxContactEmailLength} characters or fewer.";
            return null;
        }

        if (trimmed.Any(char.IsWhiteSpace) || trimmed.Any(char.IsControl))
        {
            error = "Enter a valid email address.";
            return null;
        }

        var atIndex = trimmed.IndexOf('@');
        if (atIndex <= 0 || atIndex != trimmed.LastIndexOf('@') || atIndex == trimmed.Length - 1)
        {
            error = "Enter a valid email address.";
            return null;
        }

        var domain = trimmed[(atIndex + 1)..];
        var dotIndex = domain.IndexOf('.');
        if (dotIndex <= 0 || dotIndex == domain.Length - 1 || domain.EndsWith('.'))
        {
            error = "Enter a valid email address.";
            return null;
        }

        return trimmed;
    }

    private static string? NormalizeMessage(string? rawMessage, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(rawMessage))
        {
            error = "Please enter a feedback message.";
            return null;
        }

        var trimmed = rawMessage.Trim();
        if (trimmed.Length > MaxMessageLength)
        {
            error = $"Feedback message must be {MaxMessageLength:N0} characters or fewer.";
            return null;
        }

        return trimmed;
    }
}
