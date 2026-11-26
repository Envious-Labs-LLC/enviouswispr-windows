using EnviousWispr.Core.Input;

namespace EnviousWispr.Core.Learning;

public enum PastedRegionEndReason
{
    Settled,
    TextboxEmptied,
    RegionRemoved,
    DictatedTextNotFound,
    AnchorAmbiguous,
    FocusChanged,
    ElementDestroyed,
    NextDictationStarted,
    EditDistanceExceeded,
    CeilingElapsed,
    CaptureUnsupported,
    AppTerminated,
}

public enum SettleEvidence
{
    Strong,
    Weak,
}

public static class PastedRegionTiming
{
    public const int SettleMs = 1500;
    public const int PollMs = 750;
    public const int FlushMinQuietMs = 500;
    public const int FocusGraceMs = 1500;
    public const int CaretCapMs = 10_000;
    public const int CeilingMs = 60_000;
    public const int CaptureGraceMs = 1500;
    public const int CaptureRetryIntervalMs = 150;
    public const int MaxValueUtf16 = 20_000;
    public const int AnchorUtf16 = 64;
    public const double EditDistanceLimitFraction = 0.5;
    public const int EditDistanceLimitFloor = 12;
    public const int MaxConsecutiveReadFailures = 3;

    public static bool FlushesPendingEdit(this PastedRegionEndReason reason) => reason switch
    {
        PastedRegionEndReason.Settled or
        PastedRegionEndReason.DictatedTextNotFound or
        PastedRegionEndReason.CaptureUnsupported => false,
        _ => true,
    };

    public static SettleEvidence PendingEvidence(this PastedRegionEndReason reason) => reason switch
    {
        PastedRegionEndReason.FocusChanged or PastedRegionEndReason.CeilingElapsed => SettleEvidence.Weak,
        _ => SettleEvidence.Strong,
    };

    public static int MinimumPendingEditAgeMs(this PastedRegionEndReason reason) =>
        reason == PastedRegionEndReason.FocusChanged ? FlushMinQuietMs : 0;
}

public sealed record PastedRegionAnchors(string Before, string After);

public sealed record PastedRegionTarget(
    string PastedText,
    PastedRegionAnchors Anchors,
    int ProcessId,
    long WindowHandle,
    string? ProcessName);

public abstract record PastedRegionEvent
{
    private PastedRegionEvent() { }

    public sealed record Changed(string Region) : PastedRegionEvent;

    public sealed record Settled(string Region, SettleEvidence Evidence) : PastedRegionEvent;

    public sealed record Ended(PastedRegionEndReason Reason) : PastedRegionEvent;
}

public sealed record PasteCompletedEvent(
    string PastedText,
    TargetWindowId Target,
    string? LanguageCode,
    DateTimeOffset PastedAtUtc);

public interface IPastedRegionObserver
{
    bool IsObserving { get; }

    Task StartObservationAsync(
        PasteCompletedEvent paste,
        Action<PastedRegionEvent> onEvent,
        Action<string> onSkipped,
        CancellationToken cancellationToken = default);

    void Finish(PastedRegionEndReason reason);

    void CancelObservation();
}
