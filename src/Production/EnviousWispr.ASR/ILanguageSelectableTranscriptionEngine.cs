using EnviousWispr.Core.Dictation;

namespace EnviousWispr.ASR;

/// <summary>An engine told the language with each take rather than once when it loaded.</summary>
/// <remarks>
/// PER TAKE, LIKE macOS (DictationSessionConfigFactory reads the setting for every recording). A language
/// fixed when the model loaded kept the old choice until the app restarted, while the pill and the picker
/// both said the new one was in use (#241). whisper.cpp takes the language as a decoding parameter, so a
/// change costs no reload.
/// </remarks>
public interface ILanguageSelectableTranscriptionEngine : ITranscriptionEngine
{
    /// <param name="language">A Whisper code, or "auto" (or nothing) for detection.</param>
    Task<Transcript> TranscribeAsync(
        CapturedAudio audio,
        string? language,
        CancellationToken cancellationToken = default);
}
