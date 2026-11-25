using EnviousWispr.Core.Polish;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Presentation;

/// <summary>Normalized page and optional sub-tab destination within the main settings window.</summary>
public readonly record struct SettingsRoute(string PageTag, string? TabKey)
{
    public bool IsDictationEngine =>
        string.Equals(PageTag, "settings-dictation", StringComparison.Ordinal) &&
        (string.IsNullOrEmpty(TabKey) || string.Equals(TabKey, "engine", StringComparison.Ordinal));

    public bool IsAiPolish =>
        string.Equals(PageTag, "settings-ai-polish", StringComparison.Ordinal);

    public static SettingsRoute Normalize(string tag, string? tabKey = null)
    {
        var (resolvedTag, resolvedTab) = tag switch
        {
            "home" => ("history", (string?)null),
            "settings-transcription" => ("settings-dictation", "engine"),
            "settings-microphone" => ("settings-dictation", "microphone"),
            "settings-live-preview" => ("settings-dictation", "live-preview"),
            "settings-pill" => ("settings-dictation", "pill"),
            "settings-sounds" => ("settings-dictation", "sounds"),
            "settings-clipboard" => ("settings-dictation", "clipboard"),
            "settings-appearance" => ("settings-app", "appearance"),
            "settings-permissions" or "help-permissions" => ("settings-app", "permissions"),
            "settings-privacy" or "settings-history" => ("settings-app", "privacy"),
            "settings-profile" or "settings-backup" => ("settings-app", "backup"),
            "settings-updates" or "settings-licenses" or "help-updates" or "help-licenses" => ("settings-app", "updates-licenses"),
            _ => (tag, (string?)null),
        };

        var effectiveTab = tabKey ?? resolvedTab;
        if (string.Equals(resolvedTag, "settings-dictation", StringComparison.Ordinal) &&
            string.IsNullOrWhiteSpace(effectiveTab))
        {
            effectiveTab = "engine";
        }
        else if (string.Equals(resolvedTag, "settings-app", StringComparison.Ordinal) &&
            string.IsNullOrWhiteSpace(effectiveTab))
        {
            effectiveTab = "appearance";
        }

        return new SettingsRoute(resolvedTag, effectiveTab);
    }
}

public enum SetupLeaveDialogAction
{
    Stay,
    SetUpEngine,
    GoBackToEngine,
    GoBackToProvider,
    LeaveAnyway,
    Ok,
}

public sealed record SetupLeaveDialogButton(
    string Title,
    bool IsPrimary,
    SetupLeaveDialogAction Action);

public sealed record SetupLeaveDialogContent(
    string Title,
    string Message,
    IReadOnlyList<SetupLeaveDialogButton> Buttons,
    bool IsInformationalNotice);

/// <summary>Live installation and download facts for evaluating a speech engine leave guard.</summary>
public sealed record SpeechSetupFacts(
    FinalAsrEngine PickedEngine,
    bool? ParakeetInstalled,
    bool? WhisperInstalled,
    bool ParakeetDownloading,
    bool WhisperDownloading)
{
    public bool? IsInstalled(FinalAsrEngine engine) =>
        engine == FinalAsrEngine.Whisper ? WhisperInstalled : ParakeetInstalled;

    public bool IsDownloading(FinalAsrEngine engine) =>
        engine == FinalAsrEngine.Whisper ? WhisperDownloading : ParakeetDownloading;
}

public sealed record SpeechSetupLeaveRequest(
    SettingsRoute Target,
    FinalAsrEngine Engine,
    bool Downloading,
    FinalAsrEngine? GoBackEngine);

public enum SpeechSetupLeaveOutcomeKind
{
    Stay,
    SetUp,
    Navigate,
    RestoreAndNavigate,
}

public sealed record SpeechSetupLeaveOutcome(
    SpeechSetupLeaveOutcomeKind Kind,
    FinalAsrEngine? Engine = null,
    SettingsRoute? Target = null);

/// <summary>
/// Pure leave-guard policy for Dictation Settings > Engine when leaving with a picked engine
/// that is not installed yet (macOS SpeechSetupLeave parity).
/// </summary>
public static class SpeechSetupLeaveGuard
{
    public static SpeechSetupLeaveRequest? CreateRequest(
        SettingsRoute current,
        SettingsRoute target,
        SpeechSetupFacts facts,
        FinalAsrEngine? previousEngineWhenVisitBegan)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (!current.IsDictationEngine || target.IsDictationEngine)
        {
            return null;
        }

        var engine = facts.PickedEngine;
        if (facts.IsInstalled(engine) != false)
        {
            return null;
        }

        var downloading = facts.IsDownloading(engine);
        FinalAsrEngine? goBack = null;
        if (!downloading &&
            previousEngineWhenVisitBegan is { } previous &&
            previous != engine &&
            facts.IsInstalled(previous) == true)
        {
            goBack = previous;
        }

        return new SpeechSetupLeaveRequest(target, engine, downloading, goBack);
    }

    public static SetupLeaveDialogContent BuildContent(SpeechSetupLeaveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = FormatEngineName(request.Engine);
        const string untilThen = "Until then, dictation won't work.";

        if (request.Downloading)
        {
            return new SetupLeaveDialogContent(
                Title: "Speech engine still downloading",
                Message: $"{name} is still downloading. {untilThen}",
                Buttons:
                [
                    new SetupLeaveDialogButton("OK", IsPrimary: true, SetupLeaveDialogAction.Ok),
                ],
                IsInformationalNotice: true);
        }

        var buttons = new List<SetupLeaveDialogButton>
        {
            new("Set up model", IsPrimary: true, SetupLeaveDialogAction.SetUpEngine),
        };
        if (request.GoBackEngine is { } goBack)
        {
            buttons.Add(new($"Go back to {FormatEngineName(goBack)}", IsPrimary: false, SetupLeaveDialogAction.GoBackToEngine));
        }

        buttons.Add(new("Leave anyway", IsPrimary: false, SetupLeaveDialogAction.LeaveAnyway));
        return new SetupLeaveDialogContent(
            Title: "Finish speech engine setup",
            Message: $"{name} is not downloaded yet. {untilThen}",
            Buttons: buttons,
            IsInformationalNotice: false);
    }

    public static SpeechSetupLeaveOutcome Resolve(
        SetupLeaveDialogAction? action,
        SpeechSetupLeaveRequest request,
        SpeechSetupFacts currentFacts,
        FinalAsrEngine? previousEngineWhenVisitBegan)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(currentFacts);

        if (action is null or SetupLeaveDialogAction.Stay)
        {
            return new SpeechSetupLeaveOutcome(SpeechSetupLeaveOutcomeKind.Stay);
        }

        var live = CreateRequest(
            new SettingsRoute("settings-dictation", "engine"),
            request.Target,
            currentFacts,
            previousEngineWhenVisitBegan);

        if (live is null)
        {
            return action is SetupLeaveDialogAction.GoBackToEngine or SetupLeaveDialogAction.LeaveAnyway or SetupLeaveDialogAction.Ok
                ? new SpeechSetupLeaveOutcome(SpeechSetupLeaveOutcomeKind.Navigate, Target: request.Target)
                : new SpeechSetupLeaveOutcome(SpeechSetupLeaveOutcomeKind.Stay);
        }

        if (live.Engine != request.Engine || live.Downloading != request.Downloading)
        {
            return new SpeechSetupLeaveOutcome(SpeechSetupLeaveOutcomeKind.Stay);
        }

        return action switch
        {
            SetupLeaveDialogAction.SetUpEngine => new SpeechSetupLeaveOutcome(
                SpeechSetupLeaveOutcomeKind.SetUp,
                Engine: request.Engine),
            SetupLeaveDialogAction.GoBackToEngine when live.GoBackEngine is { } goBack =>
                new SpeechSetupLeaveOutcome(
                    SpeechSetupLeaveOutcomeKind.RestoreAndNavigate,
                    Engine: goBack,
                    Target: request.Target),
            SetupLeaveDialogAction.LeaveAnyway or SetupLeaveDialogAction.Ok =>
                new SpeechSetupLeaveOutcome(SpeechSetupLeaveOutcomeKind.Navigate, Target: request.Target),
            _ => new SpeechSetupLeaveOutcome(SpeechSetupLeaveOutcomeKind.Stay),
        };
    }

    public static string FormatEngineName(FinalAsrEngine engine) => engine switch
    {
        FinalAsrEngine.Whisper => "Whisper",
        _ => "Parakeet",
    };
}

public enum PolishSetupProblemKind
{
    None,
    CloudKeyMissing,
    CloudKeyUnsavedDraft,
    CloudKeyRejected,
    CloudNoModel,
    OllamaNotInstalled,
    OllamaNotRunning,
    OllamaNoModel,
    OllamaModelNotInstalled,
    OllamaModelDownloading,
    EgOneDownloading,
    EgOneActivating,
    EgOneNotDownloaded,
    EgOneDownloadPaused,
    EgOneDownloadFailed,
    EgOneUnavailable,
}

/// <summary>Live AI Polish readiness facts used to decide whether leaving needs confirmation.</summary>
public sealed record PolishSetupFacts(
    PolishProvider Provider,
    bool CloudHasStoredKey,
    bool CloudKeyRejected,
    bool CloudHasUnsavedKeyDraft,
    string SelectedModelId,
    bool OllamaSnapshotKnown,
    OllamaServerState? OllamaServer,
    bool OllamaFound,
    bool OllamaDownloading,
    bool OllamaSelectedModelInstalled,
    EgOneModelState? SelectedEgOneState,
    bool EgOneServerExecutableExists);

public sealed record PolishSetupLeaveRequest(
    SettingsRoute Target,
    PolishProvider Provider,
    PolishSetupProblemKind Problem,
    bool KeyNotSaved,
    string? EgOneModelId,
    PolishProvider? GoBackProvider);

public enum PolishSetupLeaveOutcomeKind
{
    Stay,
    Navigate,
    RestoreAndNavigate,
}

public sealed record PolishSetupLeaveOutcome(
    PolishSetupLeaveOutcomeKind Kind,
    PolishProvider? Provider = null,
    SettingsRoute? Target = null);

/// <summary>
/// Pure leave-guard policy for AI Polish when leaving while AI Polish is enabled with an
/// incomplete provider setup (macOS PolishSetupLeaveDialog parity).
/// </summary>
public static class PolishSetupLeaveGuard
{
    public static PolishSetupProblemKind DiagnoseProblem(PolishSetupFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        switch (facts.Provider)
        {
            case PolishProvider.None:
                return PolishSetupProblemKind.None;

            case PolishProvider.OpenAI:
            case PolishProvider.Anthropic:
            case PolishProvider.Gemini:
                if (facts.CloudKeyRejected)
                {
                    return PolishSetupProblemKind.CloudKeyRejected;
                }

                if (!facts.CloudHasStoredKey)
                {
                    return PolishSetupProblemKind.CloudKeyMissing;
                }

                if (facts.CloudHasUnsavedKeyDraft)
                {
                    return PolishSetupProblemKind.CloudKeyUnsavedDraft;
                }

                return string.IsNullOrWhiteSpace(facts.SelectedModelId)
                    ? PolishSetupProblemKind.CloudNoModel
                    : PolishSetupProblemKind.None;

            case PolishProvider.Ollama:
                if (facts.OllamaDownloading)
                {
                    return PolishSetupProblemKind.OllamaModelDownloading;
                }

                if (!facts.OllamaSnapshotKnown)
                {
                    return PolishSetupProblemKind.None;
                }

                if (facts.OllamaServer == OllamaServerState.NotListening && !facts.OllamaFound)
                {
                    return PolishSetupProblemKind.OllamaNotInstalled;
                }

                if (facts.OllamaServer is OllamaServerState.NotListening or OllamaServerState.NotResponding or OllamaServerState.EndpointInvalid or OllamaServerState.Starting)
                {
                    return PolishSetupProblemKind.OllamaNotRunning;
                }

                if (facts.OllamaServer == OllamaServerState.NoModels || string.IsNullOrWhiteSpace(facts.SelectedModelId))
                {
                    return PolishSetupProblemKind.OllamaNoModel;
                }

                return facts.OllamaSelectedModelInstalled
                    ? PolishSetupProblemKind.None
                    : PolishSetupProblemKind.OllamaModelNotInstalled;

            case PolishProvider.EgOne:
                var state = facts.SelectedEgOneState;
                if (state?.DeliveryStage == SpeechModelDelivery.Downloading)
                {
                    return PolishSetupProblemKind.EgOneDownloading;
                }

                if (state?.DeliveryStage == SpeechModelDelivery.Activating)
                {
                    return PolishSetupProblemKind.EgOneActivating;
                }

                if (state is not { IsInstalled: true })
                {
                    return state?.DeliveryStage switch
                    {
                        SpeechModelDelivery.Cancelled => PolishSetupProblemKind.EgOneDownloadPaused,
                        SpeechModelDelivery.Failed => PolishSetupProblemKind.EgOneDownloadFailed,
                        _ when state is null || !state.HasBundledManifest || state.DeliveryStage == SpeechModelDelivery.CannotDownload =>
                            PolishSetupProblemKind.EgOneUnavailable,
                        _ => PolishSetupProblemKind.EgOneNotDownloaded,
                    };
                }

                return facts.EgOneServerExecutableExists
                    ? PolishSetupProblemKind.None
                    : PolishSetupProblemKind.EgOneUnavailable;

            default:
                return PolishSetupProblemKind.None;
        }
    }

    public static bool IsInformationalProblem(PolishSetupProblemKind problem) => problem is
        PolishSetupProblemKind.OllamaModelDownloading or
        PolishSetupProblemKind.EgOneDownloading or
        PolishSetupProblemKind.EgOneActivating;

    public static PolishSetupLeaveRequest? CreateRequest(
        SettingsRoute current,
        SettingsRoute target,
        PolishSetupFacts facts,
        PolishProvider? previousProviderWhenVisitBegan,
        Func<PolishProvider, bool> isProviderReady)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(isProviderReady);

        if (!current.IsAiPolish || target.IsAiPolish || facts.Provider == PolishProvider.None)
        {
            return null;
        }

        var problem = DiagnoseProblem(facts);
        if (problem == PolishSetupProblemKind.None)
        {
            return null;
        }

        PolishProvider? goBack = null;
        if (!IsInformationalProblem(problem) &&
            previousProviderWhenVisitBegan is { } previous &&
            previous != PolishProvider.None &&
            previous != facts.Provider &&
            isProviderReady(previous))
        {
            goBack = previous;
        }

        var keyNotSaved = (problem is PolishSetupProblemKind.CloudKeyMissing or PolishSetupProblemKind.CloudKeyUnsavedDraft)
            && facts.CloudHasUnsavedKeyDraft;
        var egOneModelId = facts.Provider == PolishProvider.EgOne
            ? EnginePolishPresentation.NormalizeEgOneModelId(facts.SelectedEgOneState?.ModelId ?? facts.SelectedModelId)
            : null;

        return new PolishSetupLeaveRequest(
            target,
            facts.Provider,
            problem,
            keyNotSaved,
            egOneModelId,
            goBack);
    }

    public static SetupLeaveDialogContent BuildContent(PolishSetupLeaveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        const string untilThen = "Until then, text is pasted without AI polish.";
        var providerName = FormatProviderName(request.Provider);
        var modelName = request.Provider == PolishProvider.EgOne
            ? EnginePolishPresentation.DescribeEgOneTier(request.EgOneModelId).Title
            : providerName;

        if (IsInformationalProblem(request.Problem))
        {
            var title = request.Problem switch
            {
                PolishSetupProblemKind.EgOneActivating => $"Checking the downloaded {modelName}",
                PolishSetupProblemKind.EgOneDownloading => $"{modelName} is still downloading",
                _ => "Ollama model is still downloading",
            };
            var message = request.Problem == PolishSetupProblemKind.EgOneActivating
                ? "Until this check finishes, text is pasted without AI polish."
                : "The download is still in progress. Until setup finishes, text is pasted without AI polish.";
            return new SetupLeaveDialogContent(
                Title: title,
                Message: message,
                Buttons:
                [
                    new SetupLeaveDialogButton("OK", IsPrimary: true, SetupLeaveDialogAction.Ok),
                ],
                IsInformationalNotice: true);
        }

        var reason = request.Problem switch
        {
            PolishSetupProblemKind.CloudKeyMissing when request.KeyNotSaved =>
                $"Your {providerName} key is not saved yet.",
            PolishSetupProblemKind.CloudKeyUnsavedDraft =>
                $"Your new {providerName} key is not saved yet.",
            PolishSetupProblemKind.CloudKeyMissing =>
                $"Add your {providerName} API key to use AI polish.",
            PolishSetupProblemKind.CloudKeyRejected =>
                $"{providerName} did not accept your API key.",
            PolishSetupProblemKind.CloudNoModel =>
                $"Choose a {providerName} model to use AI polish.",
            PolishSetupProblemKind.OllamaNotInstalled =>
                "Install Ollama to use AI polish.",
            PolishSetupProblemKind.OllamaNotRunning =>
                "Start Ollama to use AI polish.",
            PolishSetupProblemKind.OllamaNoModel =>
                "Choose an Ollama model to use AI polish.",
            PolishSetupProblemKind.OllamaModelNotInstalled =>
                "Download the selected Ollama model, or choose another.",
            PolishSetupProblemKind.EgOneDownloadPaused =>
                $"The {modelName} download is paused.",
            PolishSetupProblemKind.EgOneDownloadFailed =>
                $"The {modelName} download did not finish.",
            PolishSetupProblemKind.EgOneUnavailable =>
                $"{modelName} is not available to run on this PC yet.",
            _ => $"{modelName} is not downloaded yet.",
        };

        var buttons = new List<SetupLeaveDialogButton>
        {
            new("Finish setup", IsPrimary: true, SetupLeaveDialogAction.Stay),
        };
        if (request.GoBackProvider is { } goBack)
        {
            buttons.Add(new($"Go back to {FormatProviderName(goBack)}", IsPrimary: false, SetupLeaveDialogAction.GoBackToProvider));
        }

        buttons.Add(new("Leave anyway", IsPrimary: false, SetupLeaveDialogAction.LeaveAnyway));
        var dialogMessage = request.Problem == PolishSetupProblemKind.CloudKeyUnsavedDraft
            ? $"{reason} Leaving now keeps your previously saved key and discards what you typed."
            : $"{reason} {untilThen}";
        return new SetupLeaveDialogContent(
            Title: "Finish AI polish setup",
            Message: dialogMessage,
            Buttons: buttons,
            IsInformationalNotice: false);
    }

    public static PolishSetupLeaveOutcome Resolve(
        SetupLeaveDialogAction? action,
        PolishSetupLeaveRequest request,
        PolishSetupFacts currentFacts,
        PolishProvider? previousProviderWhenVisitBegan,
        Func<PolishProvider, bool> isProviderReady)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(currentFacts);
        ArgumentNullException.ThrowIfNull(isProviderReady);

        if (action is null or SetupLeaveDialogAction.Stay)
        {
            return new PolishSetupLeaveOutcome(PolishSetupLeaveOutcomeKind.Stay);
        }

        var live = CreateRequest(
            new SettingsRoute("settings-ai-polish", null),
            request.Target,
            currentFacts,
            previousProviderWhenVisitBegan,
            isProviderReady);

        if (live is null)
        {
            return action is SetupLeaveDialogAction.GoBackToProvider or SetupLeaveDialogAction.LeaveAnyway or SetupLeaveDialogAction.Ok
                ? new PolishSetupLeaveOutcome(PolishSetupLeaveOutcomeKind.Navigate, Target: request.Target)
                : new PolishSetupLeaveOutcome(PolishSetupLeaveOutcomeKind.Stay);
        }

        if (live.Provider != request.Provider || live.Problem != request.Problem)
        {
            return new PolishSetupLeaveOutcome(PolishSetupLeaveOutcomeKind.Stay);
        }

        return action switch
        {
            SetupLeaveDialogAction.GoBackToProvider when live.GoBackProvider is { } goBack =>
                new PolishSetupLeaveOutcome(
                    PolishSetupLeaveOutcomeKind.RestoreAndNavigate,
                    Provider: goBack,
                    Target: request.Target),
            SetupLeaveDialogAction.LeaveAnyway or SetupLeaveDialogAction.Ok =>
                new PolishSetupLeaveOutcome(PolishSetupLeaveOutcomeKind.Navigate, Target: request.Target),
            _ => new PolishSetupLeaveOutcome(PolishSetupLeaveOutcomeKind.Stay),
        };
    }

    public static string FormatProviderName(PolishProvider provider) => provider switch
    {
        PolishProvider.EgOne => "EG-1",
        PolishProvider.Ollama => "Ollama",
        PolishProvider.OpenAI => "OpenAI",
        PolishProvider.Anthropic => "Claude",
        PolishProvider.Gemini => "Gemini",
        _ => "Off",
    };
}
