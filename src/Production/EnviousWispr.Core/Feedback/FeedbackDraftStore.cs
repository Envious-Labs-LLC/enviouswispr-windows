using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using EnviousWispr.Core.IO;

namespace EnviousWispr.Core.Feedback;

/// <summary>
/// Persisted unsent feedback draft stored in the isolated data directory.
/// </summary>
public sealed record FeedbackDraft(
    FeedbackKind Kind,
    string Message,
    string? ContactEmail,
    bool IncludeDiagnostics);

/// <summary>
/// Thread-safe, atomic JSON persistence for unsent feedback drafts (<c>feedback-draft.json</c>).
/// </summary>
public sealed class FeedbackDraftStore
{
    public const string DefaultFileName = "feedback-draft.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Lock _lock = new();
    private readonly string _directory;

    public FeedbackDraftStore(string dataDirectory, string fileName = DefaultFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        _directory = Path.GetFullPath(dataDirectory);
        FilePath = Path.Combine(_directory, fileName);
    }

    public string FilePath { get; }

    public FeedbackDraft? Load()
    {
        lock (_lock)
        {
            return LoadUnlocked();
        }
    }

    public void Save(FeedbackDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        lock (_lock)
        {
            AtomicFileWriter.WriteJson(FilePath, draft, SerializerOptions);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            TryDeleteFile(FilePath);
        }
    }

    public bool ClearIfMatches(FeedbackKind kind, string message, string? contactEmail)
    {
        lock (_lock)
        {
            var current = LoadUnlocked();
            if (current is null)
            {
                return false;
            }

            var currentMessage = current.Message?.Trim() ?? string.Empty;
            var targetMessage = message?.Trim() ?? string.Empty;
            var currentEmail = string.IsNullOrWhiteSpace(current.ContactEmail) ? null : current.ContactEmail.Trim();
            var targetEmail = string.IsNullOrWhiteSpace(contactEmail) ? null : contactEmail.Trim();

            if (current.Kind != kind ||
                !string.Equals(currentMessage, targetMessage, StringComparison.Ordinal) ||
                !string.Equals(currentEmail, targetEmail, StringComparison.Ordinal))
            {
                return false;
            }

            TryDeleteFile(FilePath);
            return true;
        }
    }

    private FeedbackDraft? LoadUnlocked()
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(FilePath);
            var loaded = JsonSerializer.Deserialize<FeedbackDraft>(json, SerializerOptions);
            if (loaded is null)
            {
                return null;
            }

            var kind = Enum.IsDefined(loaded.Kind) ? loaded.Kind : FeedbackKind.Bug;
            var message = loaded.Message ?? string.Empty;
            if (message.Length > FeedbackSubmission.MaxMessageLength)
            {
                message = message[..FeedbackSubmission.MaxMessageLength];
            }

            var email = loaded.ContactEmail;
            if (email is not null && email.Length > FeedbackSubmission.MaxContactEmailLength)
            {
                email = email[..FeedbackSubmission.MaxContactEmailLength];
            }

            return new FeedbackDraft(kind, message, email, loaded.IncludeDiagnostics);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or SecurityException)
        {
            return null;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Clearing a draft is best-effort and must not fail submission or dialog close.
        }
    }
}
