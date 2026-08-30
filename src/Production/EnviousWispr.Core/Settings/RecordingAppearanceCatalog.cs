namespace EnviousWispr.Core.Settings;

public static class RecordingPillCatalog
{
    public static string DisplayName(RecordingPillDesign design) => design switch
    {
        RecordingPillDesign.Classic => "Capsule",
        RecordingPillDesign.ReadingWell => "Reading Well",
        RecordingPillDesign.LevelRail => "Level Rail",
        _ => throw new ArgumentOutOfRangeException(nameof(design)),
    };

    public static string Summary(RecordingPillDesign design) => design switch
    {
        RecordingPillDesign.Classic =>
            "A small capsule with the rainbow mark and a timer. The pill EnviousWispr has always shown.",
        RecordingPillDesign.ReadingWell =>
            "A wide panel that shows your words as you speak, growing a line at a time.",
        RecordingPillDesign.LevelRail =>
            "A wider capsule with a live rainbow meter of your voice beside the timer.",
        _ => throw new ArgumentOutOfRangeException(nameof(design)),
    };

    public static bool CanHoldWords(RecordingPillDesign design) =>
        design is RecordingPillDesign.ReadingWell;

    public static RecordingPillDesign Resolve(
        bool livePreviewEnabled,
        RecordingPillDesign withoutWords,
        RecordingPillDesign withWords)
    {
        var selected = livePreviewEnabled ? withWords : withoutWords;
        if (livePreviewEnabled && !CanHoldWords(selected))
        {
            return RecordingPillDesign.ReadingWell;
        }

        if (!livePreviewEnabled && CanHoldWords(selected))
        {
            return RecordingPillDesign.Classic;
        }

        return selected;
    }
}

public sealed record RecordingSoundChoice(
    RecordingSoundPairing Pairing,
    string DisplayName,
    string Description);

public static class RecordingSoundCatalog
{
    public static IReadOnlyList<RecordingSoundChoice> Choices { get; } =
    [
        new(RecordingSoundPairing.DustMote, "Dust Mote", "Soft filtered air, no tone."),
        new(RecordingSoundPairing.VelvetHush, "Velvet Hush", "Two close tones, gentle warmth."),
        new(RecordingSoundPairing.MutedConfirm, "Muted Confirm", "Same pitch both ways, plain."),
        new(RecordingSoundPairing.WhisperTick, "Whisper Tick", "Barely-there tick."),
        new(RecordingSoundPairing.RoundPebble, "Round Pebble", "Rounded, no edge."),
        new(RecordingSoundPairing.PaperTap, "Paper Tap", "Soft paper-like tap."),
        new(RecordingSoundPairing.SoftHush, "Soft Hush", "Slow fade, like a breath."),
        new(RecordingSoundPairing.LowNod, "Low Nod", "Low, warm, unhurried."),
        new(RecordingSoundPairing.CloudPop, "Cloud Pop", "Tiny filtered-air pop."),
        new(RecordingSoundPairing.VelvetTap, "Velvet Tap", "Muted, compact tap."),
        new(RecordingSoundPairing.SatinShift, "Satin Shift", "Smooth two-tone shift."),
        new(RecordingSoundPairing.AirGlint, "Air Glint", "Clean, airy glint."),
    ];

    public static RecordingSoundChoice Find(RecordingSoundPairing pairing) =>
        Choices.First(choice => choice.Pairing == pairing);
}
