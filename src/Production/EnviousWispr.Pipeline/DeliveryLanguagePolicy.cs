using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.PostProcessing;

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
        return EngineDetectsLanguage(transcript)
            ? transcript.DetectedLanguage
            : null;
    }

    public static bool EngineDetectsLanguage(Transcript transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        return !IsParakeetEngineId(transcript.EngineId);
    }

    public static string? LockedLanguageFor(Transcript transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        var trimmed = transcript.RecognitionLanguage?.Trim();
        if (string.IsNullOrEmpty(trimmed) ||
            string.Equals(trimmed, LanguageCatalog.AutomaticCode, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return trimmed;
    }

    public static DictationLanguageResolution ResolveFor(
        Transcript transcript,
        string text,
        string surroundingText = "",
        IOsLanguageDetector? osDetector = null,
        LanguageIdentifier? identifier = null)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        return DictationLanguageResolver.Resolve(
            new LanguageEvidence(
                LockedLanguageFor(transcript),
                EngineDetectsLanguage(transcript),
                transcript.DetectedLanguage),
            text,
            surroundingText,
            identifyTextOnAllPaths: false,
            osDetector: osDetector,
            identifier: identifier);
    }

    private static bool IsParakeetEngineId(string engineId)
    {
        var modelId = engineId.Split(':', 2)[0];
        return string.Equals(modelId, ParakeetModelIds.Final, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(modelId, ParakeetModelIds.LegacyV3, StringComparison.OrdinalIgnoreCase) ||
            modelId.StartsWith("parakeet", StringComparison.OrdinalIgnoreCase);
    }
}
