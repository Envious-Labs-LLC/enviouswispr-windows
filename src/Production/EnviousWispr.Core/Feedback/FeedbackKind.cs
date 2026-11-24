namespace EnviousWispr.Core.Feedback;

/// <summary>
/// Categorizes an in-app user feedback submission.
/// </summary>
public enum FeedbackKind
{
    Bug,
    Feature,
    General,
}

/// <summary>
/// Display labels and stable wire identifiers for <see cref="FeedbackKind"/>.
/// </summary>
public static class FeedbackKinds
{
    public static string ToDisplayName(FeedbackKind kind) => kind switch
    {
        FeedbackKind.Bug => "Bug report",
        FeedbackKind.Feature => "Feature idea",
        FeedbackKind.General => "General",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static string ToTagValue(FeedbackKind kind) => kind switch
    {
        FeedbackKind.Bug => "bug",
        FeedbackKind.Feature => "feature",
        FeedbackKind.General => "general",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
