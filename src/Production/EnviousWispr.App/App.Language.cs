using EnviousWispr.Core.Settings;

namespace EnviousWispr.App;

/// <summary>The Whisper language as it stands now, read by the final engine and Live Preview at every take.</summary>
/// <remarks>
/// READ PER TAKE, NOT CAPTURED WHEN THE ENGINE WAS BUILT (#241). The engine and the preview used to be
/// handed the language once, at launch, after a model delivery or a graphics-runtime change, so a change
/// on the Transcription page or through the pill's Lock was saved, announced ("Recognition will use
/// French") and ignored until the app restarted. Both routes land in <see cref="_settings"/> through
/// <c>OnSettingsChanged</c>, and the workers are told the language with each request, so the next take
/// uses whatever was last saved. macOS reads the same setting per recording
/// (DictationSessionConfigFactory) and per preview pass (LivePreviewCoordinator).
/// </remarks>
public partial class App
{
    private string CurrentWhisperLanguage() => WhisperLanguageCodes.Current(
        _settings.Preferences.Dictation.WhisperLanguage,
        Environment.GetEnvironmentVariable("ENVIOUSWISPR_ASR_LANGUAGE"));
}
