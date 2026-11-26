using System.Text;
using System.Text.RegularExpressions;

namespace EnviousWispr.Core.Learning;

/// <summary>
/// Canonical lookup key for an ordered <c>(original -> corrected)</c> edit pair.
/// Case-, Unicode-normalization- and internal-whitespace-folded so the same fix
/// matches identically across LearnMemory, CandidateFilter, and Coordinator.
/// </summary>
public static partial class CorrectionPairKey
{
    public static string Normalise(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var composed = value.Trim().Normalize(NormalizationForm.FormC);
        var collapsed = WhitespaceRegex().Replace(composed, " ");
        return collapsed.ToLowerInvariant();
    }

    public static string Make(string original, string corrected) =>
        $"{Normalise(original)}\0{Normalise(corrected)}";

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
