using EnviousWispr.Audio;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Credentials;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.History;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Settings;
using EnviousWispr.Presentation;

namespace EnviousWispr.App.Composition;

/// <summary>
/// Builds the window's presentation session the way the shell does: the shell's stores and services
/// handed in, the production microphone capture and device catalogue as the factories the session
/// opens on demand.
/// </summary>
/// <remarks>
/// THE FACTORIES ARE THE PRODUCTION ONES, NAMED HERE AND NOWHERE ELSE (plan-2 step 12). The window
/// used to construct <c>new WasapiAudioCapture()</c> for its microphone test and
/// <c>new WasapiDeviceCatalog()</c> for its microphone list where it first needed them, which no test
/// could see or replace. They are the composition's now: the session takes a factory, the app's
/// composition supplies the WASAPI one, a test supplies a fake, and a gate reads this file to hold the
/// production choice here.
/// </remarks>
public static class WindowComposition
{
    /// <summary>The production microphone-test capture: the WASAPI capture, opened fresh for each test.</summary>
    public static IMicrophoneTestCapture OpenMicrophoneTestCapture() => new WasapiAudioCapture();

    /// <summary>The production device catalogue: WASAPI's, opened once by the session and owned by it.</summary>
    public static IAudioDeviceCatalog OpenDeviceCatalog() => new WasapiDeviceCatalog();

    /// <summary>The session over the shell's stores and services and the production factories.</summary>
    public static WindowPresentationSession Compose(
        ISettingsStore settingsStore,
        AppSettings settings,
        IHistoryStore historyStore,
        IRecoveryTextStore recoveryStore,
        IApiKeyStore apiKeys,
        IPolishModelSource polishModels,
        IPortableProfileService profiles,
        IDiagnosticExportService diagnostics,
        IOllamaModelHost? ollamaModels = null) =>
        new(new WindowPresentationParts(
            settingsStore,
            settings,
            historyStore,
            recoveryStore,
            apiKeys,
            polishModels,
            profiles,
            diagnostics,
            OpenMicrophoneTestCapture,
            OpenDeviceCatalog,
            ollamaModels));
}
