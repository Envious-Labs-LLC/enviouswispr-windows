using EnviousWispr.Core.Polish;
using EnviousWispr.Core.Settings;
using EnviousWispr.Presentation;

namespace EnviousWispr.Architecture.Tests;

public sealed class SetupLeaveGuardTests
{
    [Fact]
    public void SettingsRouteNormalizesLegacyAliasesAndSubTabs()
    {
        var transcription = SettingsRoute.Normalize("settings-transcription");
        Assert.True(transcription.IsDictationEngine);
        Assert.False(transcription.IsAiPolish);
        Assert.Equal("settings-dictation", transcription.PageTag);
        Assert.Equal("engine", transcription.TabKey);

        var dictationDefault = SettingsRoute.Normalize("settings-dictation");
        Assert.True(dictationDefault.IsDictationEngine);

        var livePreview = SettingsRoute.Normalize("settings-live-preview");
        Assert.False(livePreview.IsDictationEngine);
        Assert.Equal("live-preview", livePreview.TabKey);

        var aiPolish = SettingsRoute.Normalize("settings-ai-polish");
        Assert.True(aiPolish.IsAiPolish);
        Assert.False(aiPolish.IsDictationEngine);
    }

    [Fact]
    public void SpeechSetupLeaveGuardTriggersAndResolvesActionablyWhenEngineMissing()
    {
        var engineRoute = new SettingsRoute("settings-dictation", "engine");
        var livePreviewRoute = new SettingsRoute("settings-dictation", "live-preview");
        var historyRoute = new SettingsRoute("history", null);

        // Staying inside Dictation > Engine never prompts.
        Assert.Null(SpeechSetupLeaveGuard.CreateRequest(
            engineRoute,
            new SettingsRoute("settings-dictation", null),
            new SpeechSetupFacts(FinalAsrEngine.Whisper, ParakeetInstalled: true, WhisperInstalled: false, false, false),
            FinalAsrEngine.Parakeet));

        // Unknown installation status never prompts.
        Assert.Null(SpeechSetupLeaveGuard.CreateRequest(
            engineRoute,
            historyRoute,
            new SpeechSetupFacts(FinalAsrEngine.Whisper, ParakeetInstalled: true, WhisperInstalled: null, false, false),
            FinalAsrEngine.Parakeet));

        // Installed engine never prompts.
        Assert.Null(SpeechSetupLeaveGuard.CreateRequest(
            engineRoute,
            livePreviewRoute,
            new SpeechSetupFacts(FinalAsrEngine.Whisper, ParakeetInstalled: true, WhisperInstalled: true, false, false),
            FinalAsrEngine.Parakeet));

        // Missing Whisper with Parakeet installed offers Go back to Parakeet.
        var facts = new SpeechSetupFacts(FinalAsrEngine.Whisper, ParakeetInstalled: true, WhisperInstalled: false, false, false);
        var request = SpeechSetupLeaveGuard.CreateRequest(engineRoute, livePreviewRoute, facts, FinalAsrEngine.Parakeet);
        Assert.NotNull(request);
        Assert.Equal(FinalAsrEngine.Whisper, request!.Engine);
        Assert.Equal(FinalAsrEngine.Parakeet, request.GoBackEngine);
        Assert.False(request.Downloading);

        var content = SpeechSetupLeaveGuard.BuildContent(request);
        Assert.Equal("Finish speech engine setup", content.Title);
        Assert.False(content.IsInformationalNotice);
        Assert.Equal(["Set up model", "Go back to Parakeet", "Leave anyway"], content.Buttons.Select(b => b.Title).ToArray());

        // Escape stays without starting download.
        var escaped = SpeechSetupLeaveGuard.Resolve(null, request, facts, FinalAsrEngine.Parakeet);
        Assert.Equal(SpeechSetupLeaveOutcomeKind.Stay, escaped.Kind);

        // Set up model stays and starts downloading Whisper.
        var setUp = SpeechSetupLeaveGuard.Resolve(SetupLeaveDialogAction.SetUpEngine, request, facts, FinalAsrEngine.Parakeet);
        Assert.Equal(SpeechSetupLeaveOutcomeKind.SetUp, setUp.Kind);
        Assert.Equal(FinalAsrEngine.Whisper, setUp.Engine);

        // Go back restores Parakeet and continues navigation.
        var restore = SpeechSetupLeaveGuard.Resolve(SetupLeaveDialogAction.GoBackToEngine, request, facts, FinalAsrEngine.Parakeet);
        Assert.Equal(SpeechSetupLeaveOutcomeKind.RestoreAndNavigate, restore.Kind);
        Assert.Equal(FinalAsrEngine.Parakeet, restore.Engine);
        Assert.Equal(livePreviewRoute, restore.Target);

        // Leave anyway navigates without changing selection.
        var leave = SpeechSetupLeaveGuard.Resolve(SetupLeaveDialogAction.LeaveAnyway, request, facts, FinalAsrEngine.Parakeet);
        Assert.Equal(SpeechSetupLeaveOutcomeKind.Navigate, leave.Kind);
        Assert.Equal(livePreviewRoute, leave.Target);
    }

    [Fact]
    public void SpeechSetupLeaveGuardShowsDownloadNoticeWhenInFlight()
    {
        var request = SpeechSetupLeaveGuard.CreateRequest(
            new SettingsRoute("settings-dictation", "engine"),
            new SettingsRoute("settings-app", "appearance"),
            new SpeechSetupFacts(FinalAsrEngine.Parakeet, ParakeetInstalled: false, WhisperInstalled: true, ParakeetDownloading: true, WhisperDownloading: false),
            previousEngineWhenVisitBegan: FinalAsrEngine.Whisper);

        Assert.NotNull(request);
        Assert.True(request!.Downloading);
        Assert.Null(request.GoBackEngine);

        var content = SpeechSetupLeaveGuard.BuildContent(request);
        Assert.Equal("Speech engine still downloading", content.Title);
        Assert.True(content.IsInformationalNotice);
        Assert.Equal(["OK"], content.Buttons.Select(b => b.Title).ToArray());
    }

    [Fact]
    public void PolishSetupLeaveGuardCoversCloudUnsavedDraftOllamaAndEgOneStates()
    {
        var aiPolish = new SettingsRoute("settings-ai-polish", null);
        var target = new SettingsRoute("history", null);

        // Disabled AI Polish never prompts.
        Assert.Null(PolishSetupLeaveGuard.CreateRequest(
            aiPolish,
            target,
            new PolishSetupFacts(PolishProvider.None, false, false, false, string.Empty, false, null, false, false, false, null, false),
            previousProviderWhenVisitBegan: null,
            isProviderReady: _ => false));

        // Unsaved cloud API key draft triggers key-not-saved message + Go back to ready provider.
        var cloudDraftFacts = new PolishSetupFacts(
            Provider: PolishProvider.OpenAI,
            CloudHasStoredKey: false,
            CloudKeyRejected: false,
            CloudHasUnsavedKeyDraft: true,
            SelectedModelId: string.Empty,
            OllamaSnapshotKnown: false,
            OllamaServer: null,
            OllamaFound: false,
            OllamaDownloading: false,
            OllamaSelectedModelInstalled: false,
            SelectedEgOneState: null,
            EgOneServerExecutableExists: false);
        var cloudRequest = PolishSetupLeaveGuard.CreateRequest(
            aiPolish,
            target,
            cloudDraftFacts,
            previousProviderWhenVisitBegan: PolishProvider.Anthropic,
            isProviderReady: provider => provider == PolishProvider.Anthropic);
        Assert.NotNull(cloudRequest);
        Assert.Equal(PolishSetupProblemKind.CloudKeyMissing, cloudRequest!.Problem);
        Assert.True(cloudRequest.KeyNotSaved);
        Assert.Equal(PolishProvider.Anthropic, cloudRequest.GoBackProvider);

        var cloudContent = PolishSetupLeaveGuard.BuildContent(cloudRequest);
        Assert.Equal("Finish AI polish setup", cloudContent.Title);
        Assert.Contains("Your OpenAI key is not saved yet.", cloudContent.Message, StringComparison.Ordinal);
        Assert.Equal(["Finish setup", "Go back to Claude", "Leave anyway"], cloudContent.Buttons.Select(b => b.Title).ToArray());

        // Replacement draft on top of an already-stored cloud key still warns before discarding the draft.
        var replacementDraftRequest = PolishSetupLeaveGuard.CreateRequest(
            aiPolish,
            target,
            cloudDraftFacts with { CloudHasStoredKey = true, SelectedModelId = "gpt-4o-mini" },
            previousProviderWhenVisitBegan: PolishProvider.OpenAI,
            isProviderReady: _ => true);
        Assert.NotNull(replacementDraftRequest);
        Assert.Equal(PolishSetupProblemKind.CloudKeyUnsavedDraft, replacementDraftRequest!.Problem);
        Assert.True(replacementDraftRequest.KeyNotSaved);
        var replacementDraftContent = PolishSetupLeaveGuard.BuildContent(replacementDraftRequest);
        Assert.Contains("Your new OpenAI key is not saved yet.", replacementDraftContent.Message, StringComparison.Ordinal);
        Assert.Equal(["Finish setup", "Leave anyway"], replacementDraftContent.Buttons.Select(b => b.Title).ToArray());

        // Ollama missing installed model prompts cleanly.
        var ollamaFacts = new PolishSetupFacts(
            Provider: PolishProvider.Ollama,
            CloudHasStoredKey: true,
            CloudKeyRejected: false,
            CloudHasUnsavedKeyDraft: false,
            SelectedModelId: "qwen2.5:7b",
            OllamaSnapshotKnown: true,
            OllamaServer: OllamaServerState.Ready,
            OllamaFound: true,
            OllamaDownloading: false,
            OllamaSelectedModelInstalled: false,
            SelectedEgOneState: null,
            EgOneServerExecutableExists: false);
        var ollamaRequest = PolishSetupLeaveGuard.CreateRequest(aiPolish, target, ollamaFacts, null, _ => false);
        Assert.NotNull(ollamaRequest);
        Assert.Equal(PolishSetupProblemKind.OllamaModelNotInstalled, ollamaRequest!.Problem);
        Assert.Contains("Download the selected Ollama model", PolishSetupLeaveGuard.BuildContent(ollamaRequest).Message, StringComparison.Ordinal);

        // EG-1 unavailable/missing model prompts with Finish setup and Leave anyway.
        var egOneUnavailable = new PolishSetupFacts(
            Provider: PolishProvider.EgOne,
            CloudHasStoredKey: true,
            CloudKeyRejected: false,
            CloudHasUnsavedKeyDraft: false,
            SelectedModelId: EgOneModelCatalog.MiniId,
            OllamaSnapshotKnown: false,
            OllamaServer: null,
            OllamaFound: false,
            OllamaDownloading: false,
            OllamaSelectedModelInstalled: false,
            SelectedEgOneState: new EgOneModelState(EgOneModelCatalog.MiniId, IsInstalled: false, CanRemove: false, HasBundledManifest: false),
            EgOneServerExecutableExists: true);
        var egOneRequest = PolishSetupLeaveGuard.CreateRequest(aiPolish, target, egOneUnavailable, PolishProvider.OpenAI, p => p == PolishProvider.OpenAI);
        Assert.NotNull(egOneRequest);
        Assert.Equal(PolishSetupProblemKind.EgOneUnavailable, egOneRequest!.Problem);
        Assert.Equal(PolishProvider.OpenAI, egOneRequest.GoBackProvider);
        var egOneContent = PolishSetupLeaveGuard.BuildContent(egOneRequest);
        Assert.Contains("EG-1 Mini is not available to run on this PC yet.", egOneContent.Message, StringComparison.Ordinal);
        Assert.Equal(["Finish setup", "Go back to OpenAI", "Leave anyway"], egOneContent.Buttons.Select(b => b.Title).ToArray());

        // Ready EG-1 (installed + runtime found) never prompts.
        var egOneReady = egOneUnavailable with
        {
            SelectedEgOneState = new EgOneModelState(EgOneModelCatalog.FullId, IsInstalled: true, CanRemove: true, HasBundledManifest: false),
            EgOneServerExecutableExists = true,
        };
        Assert.Null(PolishSetupLeaveGuard.CreateRequest(aiPolish, target, egOneReady, null, _ => false));

        // Downloading EG-1 raises informational notice.
        var egOneDownloading = egOneUnavailable with
        {
            SelectedEgOneState = new EgOneModelState(EgOneModelCatalog.FullId, IsInstalled: false, CanRemove: false, HasBundledManifest: true, DeliveryStage: SpeechModelDelivery.Downloading, DownloadPercent: 35),
        };
        var downloadingRequest = PolishSetupLeaveGuard.CreateRequest(aiPolish, target, egOneDownloading, PolishProvider.OpenAI, _ => true);
        Assert.NotNull(downloadingRequest);
        Assert.Null(downloadingRequest!.GoBackProvider);
        var downloadingContent = PolishSetupLeaveGuard.BuildContent(downloadingRequest);
        Assert.True(downloadingContent.IsInformationalNotice);
        Assert.Equal("EG-1 is still downloading", downloadingContent.Title);
        Assert.Equal(["OK"], downloadingContent.Buttons.Select(b => b.Title).ToArray());
    }

    [Fact]
    public void MainWindowWiresLeaveGuardsAcrossAllExitRoutesWithoutRecursiveNavigationLoops()
    {
        var root = FindRepositoryRoot();
        var mainWindow = AppXamlSources.ReadMainWindowShellCodeText(root);
        var shell = File.ReadAllText(Path.Combine(root, "src", "Production", "EnviousWispr.App", "MainWindow.Shell.cs"));
        var leaveGuards = File.ReadAllText(Path.Combine(root, "src", "Production", "EnviousWispr.App", "MainWindow.SetupLeaveGuards.cs"));

        Assert.Contains("if (_isHandlingGuardedNavigation ||", mainWindow, StringComparison.Ordinal);
        Assert.Contains("TryBeginGuardedLeave(targetRoute, revertSidebarSelection: true)", mainWindow, StringComparison.Ordinal);
        Assert.Contains("TryBeginGuardedLeave(targetRoute, revertSidebarSelection: false)", mainWindow, StringComparison.Ordinal);
        Assert.Contains("NavigateToDictationTab(tag);", mainWindow, StringComparison.Ordinal);
        Assert.Contains("NavigateToDictationTab(\"live-preview\");", shell, StringComparison.Ordinal);
        Assert.Contains("SelectNavigationItemWithoutNavigation", leaveGuards, StringComparison.Ordinal);
        Assert.Contains("_isLeaveDialogOpen", leaveGuards, StringComparison.Ordinal);
        Assert.Contains("_pendingSupersededRoute", leaveGuards, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "EnviousWispr.Windows.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
