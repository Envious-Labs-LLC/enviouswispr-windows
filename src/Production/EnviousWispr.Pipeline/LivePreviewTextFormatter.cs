using EnviousWispr.Core.Preview;
using EnviousWispr.Core.Settings;
using EnviousWispr.PostProcessing;

using EnviousWispr.Core.Dictation;

namespace EnviousWispr.Pipeline;

/// <summary>
/// Formats display-only live preview text with Custom Words correction, British English respelling,
/// and 2,000-character tail bounding (macOS #1988, #3124; Windows #127).
/// </summary>
/// <remarks>
/// DISPLAY-ONLY. Output from this formatter is handed exclusively to <see cref="LivePreviewFrame"/>
/// for the recording overlay and main window preview surface; it never enters final ASR, deterministic
/// finalization, history, clipboard delivery, or diagnostic logs.
/// </remarks>
public static class LivePreviewTextFormatter
{
    public static string Format(
        string? rawText,
        bool wordCorrectionEnabled,
        IReadOnlyList<CustomWordEntry>? customWords,
        EnglishSpelling englishSpelling,
        string? detectedLanguage = null,
        string? recognitionLanguage = null,
        string? engineId = null,
        BritishSpellingConverter? britishConverter = null,
        IReadOnlyList<string>? enabledVocabularyPackIds = null,
        bool excludeLearnedAliases = false)
    {
        var cleaned = WhisperNonSpeechFilter.Clean(rawText);
        if (cleaned.Length == 0)
        {
            return string.Empty;
        }

        var bounded = LivePreviewTextBound.Apply(cleaned);
        if (bounded.Length == 0)
        {
            return string.Empty;
        }

        var activeCustomWords = wordCorrectionEnabled && customWords is { Count: > 0 }
            ? (excludeLearnedAliases ? customWords : ExcludeBuiltinCheckerAliases(customWords))
            : [];
        var activePackWords = wordCorrectionEnabled
            ? VocabularyPacks.ResolveEntries(enabledVocabularyPackIds)
            : [];
        if (activeCustomWords.Count > 0 || activePackWords.Count > 0)
        {
            bounded = LivePreviewTextBound.Apply(
                CustomWordCorrector.Correct(
                    bounded,
                    activeCustomWords,
                    activePackWords,
                    excludeLearnedAliases: excludeLearnedAliases).Text);
        }

        if (englishSpelling == EnglishSpelling.British &&
            IsEnglishPreview(detectedLanguage, recognitionLanguage, engineId))
        {
            var converter = britishConverter ?? BritishSpellingConverter.Shared;
            if (converter is not null)
            {
                var protectedCustomWords = excludeLearnedAliases
                    ? activeCustomWords.Where(entry => !entry.IsPureAutoLearned)
                    : activeCustomWords;
                var protectedWords = activeCustomWords.Count > 0 || activePackWords.Count > 0
                    ? BritishSpellingConverter.ProtectedWords(
                        protectedCustomWords.Select(entry => entry.Replacement)
                            .Concat(activePackWords.Select(entry => entry.Replacement)))
                    : null;
                bounded = LivePreviewTextBound.Apply(
                    converter.Convert(bounded, protectedWords).Text);
            }
        }

        return bounded;
    }

    private static IReadOnlyList<CustomWordEntry> ExcludeBuiltinCheckerAliases(IReadOnlyList<CustomWordEntry> entries)
    {
        var hasBuiltinChecker = false;
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].Origin == CustomWordOrigin.Builtin && entries[i].HasCheckerAliases)
            {
                hasBuiltinChecker = true;
                break;
            }
        }

        if (!hasBuiltinChecker)
        {
            return entries;
        }

        var filtered = new List<CustomWordEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry.Origin != CustomWordOrigin.Builtin || !entry.HasCheckerAliases)
            {
                filtered.Add(entry);
                continue;
            }

            var checkerSet = entry.EffectiveLearnedAliases().ToHashSet(StringComparer.OrdinalIgnoreCase);
            var remainingAliases = entry.Aliases.Where(alias => !checkerSet.Contains(alias)).ToArray();
            filtered.Add(new CustomWordEntry(
                remainingAliases,
                entry.Replacement,
                entry.Strictness,
                entry.Category,
                entry.Id,
                origin: entry.Origin));
        }

        return filtered.ToArray();
    }

    public static bool IsEnglishPreview(
        string? detectedLanguage,
        string? recognitionLanguage = null,
        string? engineId = null)
    {
        var detected = detectedLanguage?.Trim();
        if (!string.IsNullOrEmpty(detected))
        {
            return IsEnglishCode(detected);
        }

        var requested = recognitionLanguage?.Trim();
        if (!string.IsNullOrEmpty(requested) &&
            !requested.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return IsEnglishCode(requested);
        }

        // Parakeet reports no language; choosing British spelling on Parakeet (or an engine without
        // a non-English language tag) applies British spelling as documented in pipeline.md.
        return engineId is null || !engineId.Contains("whisper", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEnglishCode(string code) =>
        code.Equals("en", StringComparison.OrdinalIgnoreCase) ||
        code.StartsWith("en-", StringComparison.OrdinalIgnoreCase) ||
        code.StartsWith("en_", StringComparison.OrdinalIgnoreCase);
}
