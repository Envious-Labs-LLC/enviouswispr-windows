namespace EnviousWispr.Core.Runtime;

/// <summary>
/// What the product CALLS a transcription engine when it shows one to a person.
/// </summary>
/// <remarks>
/// An engine id is an internal address, not a name: a model id, the execution provider it ran
/// on, and for the isolated worker a third segment - "parakeet-tdt-0.6b-v3:cuda:isolated".
/// The Transcription page offers exactly two engines by name, "Parakeet" and "Whisper", so a
/// history row printing the raw id names the same thing twice in two vocabularies, only one of
/// which the person choosing it has ever seen.
///
/// An UNRECOGNISED id returns itself rather than a plausible-looking guess. That is deliberate
/// and it is deliberately ugly: a wrong engine name is unfalsifiable by looking at it, and a
/// raw id is not. Anything that starts naming engines wrongly should look broken immediately.
/// </remarks>
public static class TranscriptionEngineNames
{
    /// <summary>The name the Transcription page's own choice card uses.</summary>
    public const string Parakeet = "Parakeet";

    /// <summary>The name the Transcription page's own choice card uses.</summary>
    public const string Whisper = "Whisper";

    /// <summary>
    /// The engine's product name, or the id itself when it names no engine we ship.
    /// </summary>
    public static string DisplayName(string engineId)
    {
        ArgumentNullException.ThrowIfNull(engineId);

        var modelId = engineId.Split(':', 2)[0];

        if (string.Equals(modelId, ParakeetModelIds.Final, StringComparison.OrdinalIgnoreCase))
        {
            return Parakeet;
        }

        if (string.Equals(modelId, WhisperModelIds.Final, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(modelId, WhisperModelIds.Preview, StringComparison.OrdinalIgnoreCase))
        {
            return Whisper;
        }

        return engineId;
    }
}
