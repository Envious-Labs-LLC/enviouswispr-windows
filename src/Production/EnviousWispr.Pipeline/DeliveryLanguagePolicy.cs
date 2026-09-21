using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Runtime;

namespace EnviousWispr.Pipeline;

/// <summary>Which language, if any, the delivery route is told a transcript is in.</summary>
/// <remarks>
/// PARAKEET'S DETECTED LANGUAGE IS NOT TRUSTED FOR INSERTION. The final Parakeet model reports a
/// language it did not detect, so the delivery route is told nothing rather than something wrong;
/// a Whisper transcript carries the language Whisper detected. This was the shell's rule, applied
/// through the finalisation's effects; it is a decision about the transcript, so it lives beside
/// the finalisation.
/// </remarks>
public static class DeliveryLanguagePolicy
{
    public static string? For(Transcript transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        return transcript.EngineId.StartsWith(ParakeetModelIds.Final, StringComparison.OrdinalIgnoreCase)
            ? null
            : transcript.DetectedLanguage;
    }
}
