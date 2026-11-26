using System.Text.RegularExpressions;

namespace EnviousWispr.Core.Learning;

/// <summary>One changed run the user made inside a pasted dictation.</summary>
public sealed record CorrectionCandidate(int Id, string Original, string Replacement);

/// <summary>The mutually exclusive verdicts a correction judge assigns to a candidate.</summary>
public enum CorrectionJudgeClass
{
    NotCorrection,
    CorrectionButUnsafe,
    CorrectionAndSafe,
}

public static class CorrectionJudgeClassExtensions
{
    public static bool IsVocabularyCorrection(this CorrectionJudgeClass verdict) =>
        verdict != CorrectionJudgeClass.NotCorrection;

    public static bool IsSafeAlias(this CorrectionJudgeClass verdict) =>
        verdict == CorrectionJudgeClass.CorrectionAndSafe;

    /// <summary>
    /// Converts model boolean outputs to <see cref="CorrectionJudgeClass"/>.
    /// Rejects the impossible <c>(vocabularyCorrection: false, safeAlias: true)</c> pair.
    /// </summary>
    public static bool TryFromBooleans(
        bool vocabularyCorrection,
        bool safeAlias,
        out CorrectionJudgeClass verdict)
    {
        switch ((vocabularyCorrection, safeAlias))
        {
            case (false, false):
                verdict = CorrectionJudgeClass.NotCorrection;
                return true;
            case (true, false):
                verdict = CorrectionJudgeClass.CorrectionButUnsafe;
                return true;
            case (true, true):
                verdict = CorrectionJudgeClass.CorrectionAndSafe;
                return true;
            default:
                verdict = CorrectionJudgeClass.NotCorrection;
                return false;
        }
    }
}

public sealed record CorrectionJudgeDecision(
    int Id,
    CorrectionJudgeClass Verdict,
    double? Probability = null);

public enum CorrectionJudgeBypass
{
    Unavailable,
    NotGranted,
    Deadline,
    Cancelled,
    Malformed,
}

public abstract record CorrectionJudgeOutcome
{
    private CorrectionJudgeOutcome() { }

    public sealed record Verdict(
        IReadOnlyList<CorrectionJudgeDecision> Decisions,
        string? ProviderId = null) : CorrectionJudgeOutcome;

    public sealed record Bypass(CorrectionJudgeBypass Reason) : CorrectionJudgeOutcome;

    public static CorrectionJudgeOutcome Validated(
        IReadOnlyList<CorrectionJudgeDecision> decisions,
        CorrectionJudgeRequest request,
        string? providerId = null)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(request);

        var expected = request.Candidates.Select(c => c.Id).ToHashSet();
        if (decisions.Count != expected.Count)
        {
            return new Bypass(CorrectionJudgeBypass.Malformed);
        }

        var seen = new HashSet<int>();
        foreach (var decision in decisions)
        {
            if (!expected.Contains(decision.Id) || !seen.Add(decision.Id))
            {
                return new Bypass(CorrectionJudgeBypass.Malformed);
            }
        }

        return new Verdict(decisions.OrderBy(d => d.Id).ToArray(), providerId);
    }
}

public sealed record CorrectionJudgeCapabilities(
    bool CanRunOnThisDevice,
    IReadOnlyDictionary<string, string> ExecutionIdentity);

/// <summary>Validated request passed to a correction judge.</summary>
public sealed class CorrectionJudgeRequest
{
    public const int MaxCandidates = 4;
    public const int MaxContextUtf16 = 600;

    private static readonly char[] TrimmedInvisibleChars = ['\u200B', '\uFEFF'];

    public CorrectionJudgeRequest(
        IReadOnlyList<CorrectionCandidate> candidates,
        string context,
        string? language)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(context);
        if (candidates.Count == 0)
        {
            throw new ArgumentException("At least one candidate is required.", nameof(candidates));
        }

        if (candidates.Count > MaxCandidates)
        {
            throw new ArgumentOutOfRangeException(nameof(candidates), $"At most {MaxCandidates} candidates are allowed.");
        }

        var seen = new HashSet<int>();
        foreach (var candidate in candidates)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            if (candidate.Id is < 1 or > MaxCandidates)
            {
                throw new ArgumentOutOfRangeException(nameof(candidates), $"Candidate id {candidate.Id} is out of range.");
            }

            if (!seen.Add(candidate.Id))
            {
                throw new ArgumentException($"Duplicate candidate id {candidate.Id}.", nameof(candidates));
            }

            var orig = RunText(candidate.Original);
            var repl = RunText(candidate.Replacement);
            if (orig.Length == 0 || repl.Length == 0)
            {
                throw new ArgumentException($"Candidate {candidate.Id} has empty text.", nameof(candidates));
            }

            if (string.Equals(orig, repl, StringComparison.Ordinal))
            {
                throw new ArgumentException($"Candidate {candidate.Id} is unchanged.", nameof(candidates));
            }
        }

        if (context.Length > MaxContextUtf16)
        {
            throw new ArgumentOutOfRangeException(nameof(context), $"Context exceeds {MaxContextUtf16} UTF-16 chars.");
        }

        Candidates = candidates.OrderBy(c => c.Id).ToArray();
        Context = context;
        Language = language;
    }

    public IReadOnlyList<CorrectionCandidate> Candidates { get; }
    public string Context { get; }
    public string? Language { get; }

    public static string RunText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text.Trim().Trim(TrimmedInvisibleChars).Trim();
    }
}

public interface ICorrectionJudge
{
    ValueTask<CorrectionJudgeCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default);

    Task<CorrectionJudgeOutcome> JudgeAsync(CorrectionJudgeRequest request, CancellationToken cancellationToken = default);
}
