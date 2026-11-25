using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using EnviousWispr.Core.Credentials;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Polish;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.ModelDelivery;
using EnviousWispr.Presentation;
using EnviousWispr.Services.Settings;

namespace EnviousWispr.Architecture.Tests;

public sealed class AiPolishEgOneParityTests
{
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public async Task LoadMigratesV22EgOneModelIdAndPortableProfileV16AndLeavesNonEgOneNull()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var egOnePath = Path.Combine(directory, "egone-v22.json");
            var v22EgOne = AppSettings.Default with
            {
                SchemaVersion = 22,
                Preferences = AppSettings.Default.Preferences with
                {
                    Polish = new PolishPreferences(PolishProvider.EgOne, "EG-1-MINI", null),
                },
            };
            var egOneNode = JsonNode.Parse(JsonSerializer.Serialize(v22EgOne, JsonSettingsStore.SerializerOptions))!.AsObject();
            egOneNode["preferences"]!["polish"]!.AsObject().Remove("egOneModelId");
            await File.WriteAllTextAsync(egOnePath, egOneNode.ToJsonString(JsonSettingsStore.SerializerOptions));

            var egOneLoaded = await new JsonSettingsStore(egOnePath).LoadAsync();

            Assert.Equal(SettingsLoadStatus.Migrated, egOneLoaded.Status);
            Assert.Equal(22, egOneLoaded.SourceSchemaVersion);
            Assert.Equal(AppSettings.CurrentSchemaVersion, egOneLoaded.Settings.SchemaVersion);
            Assert.Equal("eg-1-mini", egOneLoaded.Settings.Preferences.Polish.EgOneModelId);

            var openAiPath = Path.Combine(directory, "openai-v22.json");
            var v22OpenAi = AppSettings.Default with
            {
                SchemaVersion = 22,
                Preferences = AppSettings.Default.Preferences with
                {
                    Polish = new PolishPreferences(PolishProvider.OpenAI, "gpt-4o-mini", null),
                },
            };
            var openAiNode = JsonNode.Parse(JsonSerializer.Serialize(v22OpenAi, JsonSettingsStore.SerializerOptions))!.AsObject();
            openAiNode["preferences"]!["polish"]!.AsObject().Remove("egOneModelId");
            await File.WriteAllTextAsync(openAiPath, openAiNode.ToJsonString(JsonSettingsStore.SerializerOptions));

            var openAiLoaded = await new JsonSettingsStore(openAiPath).LoadAsync();

            Assert.Equal(SettingsLoadStatus.Migrated, openAiLoaded.Status);
            Assert.Null(openAiLoaded.Settings.Preferences.Polish.EgOneModelId);
            Assert.Equal("gpt-4o-mini", openAiLoaded.Settings.Preferences.Polish.ModelId);

            var profilePath = Path.Combine(directory, "profile-v16.enviouswispr.json");
            var v16Profile = AppSettings.Default.ToPortableProfile() with
            {
                SchemaVersion = 16,
                Preferences = AppSettings.Default.Preferences with
                {
                    Polish = new PolishPreferences(PolishProvider.EgOne, "EG-1-MICRO", null),
                },
            };
            var profileNode = JsonNode.Parse(JsonSerializer.Serialize(v16Profile, JsonSettingsStore.SerializerOptions))!.AsObject();
            profileNode["preferences"]!["polish"]!.AsObject().Remove("egOneModelId");
            await File.WriteAllTextAsync(profilePath, profileNode.ToJsonString(JsonSettingsStore.SerializerOptions));

            var importedProfile = await new JsonPortableProfileService().ImportAsync(profilePath);
            Assert.Equal(PortableProfileImportStatus.Imported, importedProfile.Status);
            Assert.Equal(PortableProfile.CurrentSchemaVersion, importedProfile.Profile?.SchemaVersion);
            Assert.Equal("eg-1-micro", importedProfile.Profile?.Preferences.Polish.EgOneModelId);
        });
    }

    [Fact]
    public void ValidatorRejectsUnknownEgOneModelIdAndAcceptsValidSizes()
    {
        foreach (var valid in new[] { null, "eg-1", "eg-1-mini", "eg-1-micro" })
        {
            var settings = AppSettings.Default with
            {
                Preferences = AppSettings.Default.Preferences with
                {
                    Polish = new PolishPreferences(PolishProvider.OpenAI, "gpt-4o-mini", null, valid),
                },
            };
            Assert.Null(AppSettingsValidator.Validate(settings, AppErrorStage.SettingsLoad));
        }

        foreach (var invalid in new[] { "", "   ", "gpt-4o-mini", "unknown-eg-1" })
        {
            var settings = AppSettings.Default with
            {
                Preferences = AppSettings.Default.Preferences with
                {
                    Polish = new PolishPreferences(PolishProvider.OpenAI, "gpt-4o-mini", null, invalid),
                },
            };
            Assert.NotNull(AppSettingsValidator.Validate(settings, AppErrorStage.SettingsLoad));
        }
    }

    [Fact]
    public async Task SwitchingProvidersPreservesSelectedEgOneModelIdAcrossRoundTrips()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var path = Path.Combine(directory, "settings.json");
            var store = new JsonSettingsStore(path);
            using var presenter = new SettingsPresenter(store, AppSettings.Default);

            var egOneMiniInput = CreateGeneralInput(polishProviderIndex: 1, polishModel: "eg-1-mini", egOneModelId: "eg-1-mini");
            var step1 = await presenter.SaveGeneralAsync(egOneMiniInput);
            Assert.True(step1.Saved);
            Assert.Equal("eg-1-mini", presenter.Current.Preferences.Polish.EgOneModelId);
            Assert.Equal("eg-1-mini", presenter.Current.Preferences.Polish.ModelId);

            var openAiInput = CreateGeneralInput(polishProviderIndex: 3, polishModel: "gpt-4o-mini", egOneModelId: "eg-1-mini");
            var step2 = await presenter.SaveGeneralAsync(openAiInput);
            Assert.True(step2.Saved);
            Assert.Equal(PolishProvider.OpenAI, presenter.Current.Preferences.Polish.Provider);
            Assert.Equal("gpt-4o-mini", presenter.Current.Preferences.Polish.ModelId);
            Assert.Equal("eg-1-mini", presenter.Current.Preferences.Polish.EgOneModelId);

            var backToEgOneInput = CreateGeneralInput(polishProviderIndex: 1, polishModel: "gpt-4o-mini", egOneModelId: "eg-1-mini");
            var step3 = await presenter.SaveGeneralAsync(backToEgOneInput);
            Assert.True(step3.Saved);
            Assert.Equal(PolishProvider.EgOne, presenter.Current.Preferences.Polish.Provider);
            Assert.Equal("eg-1-mini", presenter.Current.Preferences.Polish.EgOneModelId);
            Assert.Equal("eg-1-mini", presenter.Current.Preferences.Polish.ModelId);

            var reloaded = await store.LoadAsync();
            Assert.Equal("eg-1-mini", reloaded.Settings.Preferences.Polish.EgOneModelId);
        });
    }

    [Fact]
    public async Task CloudModelListingGatesBehindSavedApiKeyAndRefreshesImmediatelyWhenSaved()
    {
        var keys = new MemoryApiKeyStore();
        var models = new StubPolishModelSource(["gpt-4o-mini", "gpt-4o"]);
        var presenter = new ProviderSettingsPresenter(keys, models);

        var withoutKey = await presenter.ListModelsAsync(PolishProvider.OpenAI, ollamaEndpoint: null);
        Assert.NotNull(withoutKey);
        Assert.Empty(withoutKey!.Models);
        Assert.Equal(PolishModelDiscoveryStatus.MissingCredential, withoutKey.Discovery!.Status);
        Assert.Equal(0, models.DiscoveryCalls);

        Assert.Equal(ApiKeySaveOutcome.Saved, presenter.SaveKey(PolishProvider.OpenAI, "sk-test-key"));

        var withKey = await presenter.ListModelsAsync(PolishProvider.OpenAI, ollamaEndpoint: null);
        Assert.NotNull(withKey);
        Assert.Equal(["gpt-4o-mini", "gpt-4o"], withKey!.Models);
        Assert.Equal(PolishModelDiscoveryStatus.Ready, withKey.Discovery!.Status);
        Assert.Equal(1, models.DiscoveryCalls);

        var unlistedChoices = presenter.Choose(withKey, "gpt-custom-legacy", chooseDefault: false);
        var displayed = ProviderSettingsPresenter.IncludeActiveSelectionIfMissing(unlistedChoices, "gpt-custom-legacy");
        Assert.Equal(["gpt-custom-legacy", "gpt-4o-mini", "gpt-4o"], displayed.Models);
        Assert.Equal(0, displayed.SelectedIndex);
    }

    [Fact]
    public void DescribeEgOneCardCapturesHardwareBadgeSpecsStatesAndUnpublishedNotice()
    {
        var notInstalled = EnginePolishPresentation.DescribeEgOneCard(
            EgOneModelCatalog.MiniId,
            selectedModelId: EgOneModelCatalog.MiniId,
            recommendedModelId: EgOneModelCatalog.MiniId,
            state: new EgOneModelState(EgOneModelCatalog.MiniId, IsInstalled: false, CanRemove: false, HasBundledManifest: false),
            otherDownloadInFlight: false,
            serverExecutableExists: true);

        Assert.True(notInstalled.IsRecommended);
        Assert.True(notInstalled.IsSelected);
        Assert.Equal("Fast all-rounder", notInstalled.Tagline);
        Assert.Contains("~2.9 GB memory", notInstalled.SpecsSummary, StringComparison.Ordinal);
        Assert.Contains("~2.1 GB disk", notInstalled.SpecsSummary, StringComparison.Ordinal);
        Assert.True(notInstalled.ShowDownloadButton);
        Assert.False(notInstalled.ShowRemoveButton);
        Assert.Contains("not published in this build yet", notInstalled.StatusDetail, StringComparison.OrdinalIgnoreCase);

        var unpublishedAttempted = EnginePolishPresentation.DescribeEgOneCard(
            EgOneModelCatalog.MiniId,
            selectedModelId: EgOneModelCatalog.MiniId,
            recommendedModelId: EgOneModelCatalog.FullId,
            state: new EgOneModelState(
                EgOneModelCatalog.MiniId,
                IsInstalled: false,
                CanRemove: false,
                HasBundledManifest: false,
                DeliveryStage: SpeechModelDelivery.CannotDownload,
                StatusDetail: EnginePolishPresentation.DescribeEgOneNotPublishedNotice(EgOneModelCatalog.MiniId)),
            otherDownloadInFlight: false,
            serverExecutableExists: true);

        Assert.False(unpublishedAttempted.ShowDownloadButton);
        Assert.False(unpublishedAttempted.ShowProgress);
        Assert.Equal("Package not published", unpublishedAttempted.StatusText);
        Assert.Equal(
            "The Windows download package for EG-1 Mini is not published in this build yet.",
            unpublishedAttempted.StatusDetail);
        Assert.Null(BundledModelManifests.TryRead(EgOneModelCatalog.FullId));

        var installed = EnginePolishPresentation.DescribeEgOneCard(
            EgOneModelCatalog.FullId,
            selectedModelId: EgOneModelCatalog.FullId,
            recommendedModelId: EgOneModelCatalog.FullId,
            state: new EgOneModelState(EgOneModelCatalog.FullId, IsInstalled: true, CanRemove: true, HasBundledManifest: false),
            otherDownloadInFlight: false,
            serverExecutableExists: true);

        Assert.Equal("Installed · Ready", installed.StatusText);
        Assert.False(installed.ShowDownloadButton);
        Assert.True(installed.ShowRemoveButton);
    }

    [Fact]
    public async Task RemoveEgOneCleanerAbortsAndUnloadsRuntimeBeforeDeletingFiles()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var events = new List<string>();
            var modelId = EgOneModelCatalog.MiniId;
            var legacyDir = Path.Combine(directory, "models", modelId);
            Directory.CreateDirectory(legacyDir);
            var modelFile = Path.Combine(legacyDir, "active.gguf");
            await File.WriteAllBytesAsync(modelFile, [1, 2, 3, 4]);

            using var httpClient = new HttpClient();
            var store = new ModelStore(
                Path.Combine(directory, "models"),
                httpClient,
                new ModelManifestVerifier(new Dictionary<string, string>()),
                new Version(1, 0, 0));

            var outcome = await ModelPackageCleaner.RemoveAsync(
                store,
                modelId,
                legacyDir,
                unloadEnginesAsync: () =>
                {
                    Assert.True(File.Exists(modelFile), "Runtime abort/teardown must run before model files are removed.");
                    events.Add("abort-and-unload");
                    return Task.CompletedTask;
                });

            Assert.True(outcome.Succeeded);
            Assert.Equal(["abort-and-unload"], events);
            Assert.False(Directory.Exists(legacyDir));
        });
    }

    [Fact]
    public void MarkupAndCodeRemoveCustomIdFieldAndEnforceCloudKeyGatingAndSafeTeardown()
    {
        var root = FindRepositoryRoot();
        var xamlPath = Path.Combine(root, "src", "Production", "EnviousWispr.App", "MainWindow.xaml");
        var xamlText = File.ReadAllText(xamlPath);
        var document = XDocument.Parse(xamlText);

        Assert.DoesNotContain("PolishModelTextBox", xamlText, StringComparison.Ordinal);
        Assert.DoesNotContain("custom-ID", xamlText, StringComparison.OrdinalIgnoreCase);

        foreach (var removeName in new[] { "EgOneFullRemoveButton", "EgOneMiniRemoveButton", "EgOneMicroRemoveButton" })
        {
            var button = document.Descendants()
                .FirstOrDefault(e => (string?)e.Attribute(XName.Get("Name", XamlNamespace)) == removeName);
            Assert.NotNull(button);
            Assert.Equal("{StaticResource BrandDestructiveButtonStyle}", (string?)button!.Attribute("Style"));
        }

        var promptText = document.Descendants()
            .FirstOrDefault(e => (string?)e.Attribute(XName.Get("Name", XamlNamespace)) == "CloudModelsKeyRequiredText");
        Assert.NotNull(promptText);
        Assert.Equal("Save your API key to load available models", (string?)promptText!.Attribute("Text"));

        var mainWindowCode = File.ReadAllText(
            Path.Combine(root, "src", "Production", "EnviousWispr.App", "MainWindow.xaml.cs"));
        var ollamaCode = File.ReadAllText(
            Path.Combine(root, "src", "Production", "EnviousWispr.App", "MainWindow.OllamaModels.cs"));
        var deliveryCode = File.ReadAllText(
            Path.Combine(root, "src", "Production", "EnviousWispr.App", "App.ModelDelivery.cs"));
        var appCode = File.ReadAllText(
            Path.Combine(root, "src", "Production", "EnviousWispr.App", "App.xaml.cs"));

        Assert.DoesNotContain("PolishModelTextBox", mainWindowCode, StringComparison.Ordinal);
        Assert.DoesNotContain("PolishModelTextBox", ollamaCode, StringComparison.Ordinal);
        Assert.DoesNotContain("custom-ID", mainWindowCode, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PolishModelPicker_SelectionChanged", mainWindowCode, StringComparison.Ordinal);
        Assert.Contains("_selectedPolishModelId", mainWindowCode, StringComparison.Ordinal);
        Assert.Contains("_selectedEgOneModelId", mainWindowCode, StringComparison.Ordinal);
        Assert.Contains("TriggerAutoSaveAsync()", mainWindowCode, StringComparison.Ordinal);
        Assert.Contains("preferences.EgOneModelId", appCode, StringComparison.Ordinal);

        // Verify RemoveEgOneModelAsync unloads via TeardownPolishAsync (which invokes AbortPolishRuntime before DisposeAsync).
        var teardownStart = deliveryCode.IndexOf("private async Task TeardownPolishAsync()", StringComparison.Ordinal);
        var removeStart = deliveryCode.IndexOf("private async Task RemoveEgOneModelAsync(", StringComparison.Ordinal);
        Assert.True(teardownStart >= 0 && removeStart >= 0);
        var teardownBody = deliveryCode.Substring(teardownStart, 550);
        var removeBody = deliveryCode.Substring(removeStart, 1_500);
        Assert.True(
            teardownBody.IndexOf("AbortPolishRuntime();", StringComparison.Ordinal) <
            teardownBody.IndexOf("DisposeAsync()", StringComparison.Ordinal),
            "AbortPolishRuntime must run before disposing the polish provider.");
        Assert.Contains("IsSpeechEngineBusy", removeBody, StringComparison.Ordinal);
        Assert.Contains("ModelPackageCleaner.RemoveAsync", removeBody, StringComparison.Ordinal);
        Assert.Contains("TeardownPolishAsync", removeBody, StringComparison.Ordinal);
    }

    private sealed class MemoryApiKeyStore : IApiKeyStore
    {
        private readonly Dictionary<PolishProvider, string> _keys = [];

        public ApiKeyReadResult Read(PolishProvider provider) =>
            _keys.TryGetValue(provider, out var value)
                ? ApiKeyReadResult.Found(value)
                : ApiKeyReadResult.Missing;

        public void Store(PolishProvider provider, string value) => _keys[provider] = value;

        public void Delete(PolishProvider provider) => _keys.Remove(provider);
    }

    private sealed class StubPolishModelSource(IReadOnlyList<string> models) : IPolishModelSource
    {
        public int DiscoveryCalls { get; private set; }

        public string? RecommendedModel(PolishProvider provider) => models.Count > 0 ? models[0] : null;

        public bool ModelIdBelongsTo(string? modelId, PolishProvider provider) =>
            !string.IsNullOrWhiteSpace(modelId);

        public Task<PolishModelDiscovery> DiscoverAsync(
            PolishProvider provider,
            string? ollamaEndpoint,
            CancellationToken cancellationToken)
        {
            DiscoveryCalls++;
            return Task.FromResult(new PolishModelDiscovery(PolishModelDiscoveryStatus.Ready, models));
        }
    }

    private static GeneralSettingsInput CreateGeneralInput(
        int polishProviderIndex,
        string polishModel,
        string? egOneModelId) => new(
        RecordingShortcut: "F8",
        CancelShortcut: "Escape",
        QuickAddShortcut: "Ctrl+Alt+W",
        FinalEngineIndex: 0,
        WordCorrection: true,
        FillerRemoval: false,
        EmojiFormatter: true,
        SpokenPunctuation: false,
        WhisperLanguageIndex: 0,
        EnglishSpellingIndex: 0,
        RecordingModeIndex: 0,
        EscapeRecovery: false,
        AutoStop: false,
        AutoStopSeconds: 3.0,
        PolishProviderIndex: polishProviderIndex,
        PolishModel: polishModel,
        OllamaEndpoint: string.Empty,
        HistoryEnabled: true,
        HistoryRetentionDays: 30,
        ThemeIndex: 0,
        LivePreview: false,
        OverlayPositionIndex: 0,
        LevelRailPill: false,
        PlayRecordingSounds: false,
        RecordingSoundPairing: RecordingSoundPairing.WhisperTick,
        CopyInsteadOfPaste: false,
        LocalDiagnostics: true,
        DiagnosticRetentionDays: 30,
        ShareTelemetry: false,
        TelemetryAvailable: true,
        MicrophoneId: null,
        PasteLastShortcut: string.Empty,
        CopyLastShortcut: string.Empty,
        EgOneModelId: egOneModelId);

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
