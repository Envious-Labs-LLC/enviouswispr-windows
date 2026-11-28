using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Settings;
using EnviousWispr.PostProcessing;

namespace EnviousWispr.Pipeline;

public sealed record LanguageEvidence(
    string? LockedLanguage,
    bool EngineDetectsLanguage,
    string? EngineReportedLanguage)
{
    public static LanguageEvidence None { get; } = new(null, false, null);

    public static LanguageEvidence Locked(string language) =>
        new(language, EngineDetectsLanguage: false, EngineReportedLanguage: null);
}

public enum DictationLanguageSource
{
    Locked,
    Engine,
    Dictation,
    Document,
    None,
}

public enum DictationConfidenceBucket
{
    None,
    Lt50,
    From50To70,
    From70To90,
    Ge90,
}

public sealed record DictationLanguageResolution(
    string? Language,
    string? LearnLanguage,
    DictationLanguageSource Source,
    DictationConfidenceBucket ConfidenceBucket,
    bool EnglishVeto = false,
    string? TextLanguage = null,
    bool UsedOsAgreement = false);

/// <summary>
/// Resolves dictation language across the 4-rung precedence ladder:
/// user lock &gt; detecting engine &gt; L4 Hybrid text identifier &gt; surrounding document non-English veto.
/// </summary>
public static class DictationLanguageResolver
{
    public static DictationLanguageResolution Resolve(
        LanguageEvidence evidence,
        string text,
        string surroundingText = "",
        bool identifyTextOnAllPaths = false,
        IOsLanguageDetector? osDetector = null,
        LanguageIdentifier? identifier = null) =>
        Resolve(
            evidence,
            text,
            surroundingText,
            identifyTextOnAllPaths,
            osDetector,
            identifier,
            identifyOverride: null);

    internal static DictationLanguageResolution Resolve(
        LanguageEvidence evidence,
        string text,
        string surroundingText,
        bool identifyTextOnAllPaths,
        IOsLanguageDetector? osDetector,
        LanguageIdentifier? identifier,
        Func<string, IOsLanguageDetector?, LanguageIdentificationResult>? identifyOverride)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var activeIdentifier = identifier ?? LanguageIdentifier.Shared;

        LanguageIdentificationResult IdentifyText(string sample)
        {
            if (identifyOverride is not null)
            {
                return identifyOverride(sample, osDetector);
            }

            return activeIdentifier is not null
                ? activeIdentifier.IdentifyHybrid(sample, osDetector)
                : IdentifyScriptOnly(sample);
        }

        var locked = NormalizeExplicitLanguage(evidence.LockedLanguage);
        if (locked is not null)
        {
            var textLang = identifyTextOnAllPaths
                ? IdentifyText(text).AcceptedLanguage
                : null;
            return new DictationLanguageResolution(
                Language: locked,
                LearnLanguage: locked,
                Source: DictationLanguageSource.Locked,
                ConfidenceBucket: DictationConfidenceBucket.None,
                EnglishVeto: false,
                TextLanguage: textLang);
        }

        var engineReported = NormalizeExplicitLanguage(evidence.EngineReportedLanguage);
        if (evidence.EngineDetectsLanguage && engineReported is not null)
        {
            var textLang = identifyTextOnAllPaths
                ? IdentifyText(text).AcceptedLanguage
                : null;
            return new DictationLanguageResolution(
                Language: engineReported,
                LearnLanguage: engineReported,
                Source: DictationLanguageSource.Engine,
                ConfidenceBucket: DictationConfidenceBucket.None,
                EnglishVeto: false,
                TextLanguage: textLang);
        }

        var fromDictation = string.IsNullOrWhiteSpace(text) ? default : IdentifyText(text);
        var dictationBucket = BucketFor(fromDictation.TopConfidence);
        if (!string.IsNullOrEmpty(fromDictation.AcceptedLanguage) &&
            double.IsFinite(fromDictation.AcceptedConfidence))
        {
            return new DictationLanguageResolution(
                Language: fromDictation.AcceptedLanguage,
                LearnLanguage: fromDictation.AcceptedLanguage,
                Source: DictationLanguageSource.Dictation,
                ConfidenceBucket: BucketFor(fromDictation.AcceptedConfidence),
                EnglishVeto: false,
                TextLanguage: fromDictation.AcceptedLanguage,
                UsedOsAgreement: fromDictation.UsedOsAgreement);
        }

        if (!string.IsNullOrWhiteSpace(surroundingText))
        {
            var combined = string.IsNullOrWhiteSpace(text)
                ? surroundingText
                : string.Concat(surroundingText, " ", text);
            var fromDocument = IdentifyText(combined);
            if (!string.IsNullOrEmpty(fromDocument.AcceptedLanguage) &&
                double.IsFinite(fromDocument.AcceptedConfidence))
            {
                var docBase = SeamCasingPolicies.NormalizeBaseCode(fromDocument.AcceptedLanguage);
                if (!string.Equals(docBase, "en", StringComparison.Ordinal))
                {
                    return new DictationLanguageResolution(
                        Language: fromDocument.AcceptedLanguage,
                        LearnLanguage: null,
                        Source: DictationLanguageSource.Document,
                        ConfidenceBucket: BucketFor(fromDocument.AcceptedConfidence),
                        EnglishVeto: false,
                        UsedOsAgreement: fromDocument.UsedOsAgreement);
                }
            }
        }

        var topBase = SeamCasingPolicies.NormalizeBaseCode(fromDictation.TopHypothesis);
        var englishVeto = topBase is not null &&
            !string.Equals(topBase, "en", StringComparison.Ordinal) &&
            PermitsEnglishVeto(dictationBucket);

        return new DictationLanguageResolution(
            Language: null,
            LearnLanguage: englishVeto ? null : fromDictation.TopHypothesis,
            Source: DictationLanguageSource.None,
            ConfidenceBucket: dictationBucket,
            EnglishVeto: englishVeto);
    }

    public static IReadOnlyList<string> BuildProtectedWords(
        IEnumerable<CustomWordEntry>? customWords,
        IEnumerable<string>? enabledVocabularyPackIds)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        void AddIfProtected(string? spelling)
        {
            var trimmed = spelling?.Trim();
            if (string.IsNullOrEmpty(trimmed) || !ContainsUppercase(trimmed))
            {
                return;
            }

            if (seen.Add(trimmed))
            {
                result.Add(trimmed);
            }
        }

        if (customWords is not null)
        {
            foreach (var entry in customWords)
            {
                AddIfProtected(entry.Replacement);
            }
        }

        if (enabledVocabularyPackIds is not null)
        {
            var packIds = enabledVocabularyPackIds as IReadOnlyList<string> ?? enabledVocabularyPackIds.ToArray();
            foreach (var entry in VocabularyPacks.ResolveEntries(packIds))
            {
                AddIfProtected(entry.Replacement);
            }
        }

        return result;
    }

    public static DictationConfidenceBucket BucketFor(double confidence)
    {
        if (!double.IsFinite(confidence) || confidence <= 0)
        {
            return DictationConfidenceBucket.None;
        }

        if (confidence < 0.50) return DictationConfidenceBucket.Lt50;
        if (confidence < 0.70) return DictationConfidenceBucket.From50To70;
        if (confidence < 0.90) return DictationConfidenceBucket.From70To90;
        return DictationConfidenceBucket.Ge90;
    }

    public static bool PermitsEnglishVeto(DictationConfidenceBucket bucket) =>
        bucket is DictationConfidenceBucket.From50To70
            or DictationConfidenceBucket.From70To90
            or DictationConfidenceBucket.Ge90;

    private static string? NormalizeExplicitLanguage(string? code)
    {
        var trimmed = code?.Trim();
        if (string.IsNullOrEmpty(trimmed) ||
            string.Equals(trimmed, LanguageCatalog.AutomaticCode, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return trimmed;
    }

    private static bool ContainsUppercase(string value)
    {
        foreach (var ch in value)
        {
            if (char.IsUpper(ch))
            {
                return true;
            }
        }

        return false;
    }

    private static LanguageIdentificationResult IdentifyScriptOnly(string text)
    {
        var script = ScriptLanguageGate.DetectByScript(text);
        return script is { } matched
            ? new LanguageIdentificationResult(matched.Language, matched.Confidence, matched.Language, matched.Confidence)
            : default;
    }
}
