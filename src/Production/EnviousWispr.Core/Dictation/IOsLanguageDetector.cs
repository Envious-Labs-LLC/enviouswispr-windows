namespace EnviousWispr.Core.Dictation;

/// <summary>
/// Optional operating-system text language identifier (such as Windows Extended Linguistic Services
/// in <c>EnviousWispr.Services</c>) used as secondary agreement evidence beside the bundled pure-C# classifier.
/// </summary>
public interface IOsLanguageDetector
{
    /// <summary>Returns ordered BCP-47 base language tags detected in <paramref name="text"/>, most likely first.</summary>
    IReadOnlyList<string> DetectBaseTags(string text);
}
