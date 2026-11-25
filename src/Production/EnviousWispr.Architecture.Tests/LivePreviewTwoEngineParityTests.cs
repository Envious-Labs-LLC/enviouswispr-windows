using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using EnviousWispr.ASR;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Preview;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;
using EnviousWispr.Presentation;
using EnviousWispr.Services.Runtime;
using EnviousWispr.Services.Settings;

namespace EnviousWispr.Architecture.Tests;

public sealed class LivePreviewTwoEngineParityTests
{
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void UserPreferencesDefaultsToParakeetLivePreviewEngine()
    {
        Assert.Equal(LivePreviewEngineChoice.Parakeet, UserPreferences.Default.LivePreviewEngine);
        Assert.Equal("Parakeet", LivePreviewEngineChoices.ToPersistedValue(LivePreviewEngineChoice.Parakeet));
        Assert.Equal("Whisper", LivePreviewEngineChoices.ToPersistedValue(LivePreviewEngineChoice.Whisper));
    }

    [Theory]
    [InlineData(null, LivePreviewEngineChoice.Parakeet)]
    [InlineData("", LivePreviewEngineChoice.Parakeet)]
    [InlineData("   ", LivePreviewEngineChoice.Parakeet)]
    [InlineData("Parakeet", LivePreviewEngineChoice.Parakeet)]
    [InlineData("parakeet", LivePreviewEngineChoice.Parakeet)]
    [InlineData("Whisper", LivePreviewEngineChoice.Whisper)]
    [InlineData("whisper", LivePreviewEngineChoice.Whisper)]
    [InlineData("Universal", LivePreviewEngineChoice.Whisper)]
    [InlineData("multilingual", LivePreviewEngineChoice.Whisper)]
    [InlineData("unknown-future-engine", LivePreviewEngineChoice.Parakeet)]
    public void LivePreviewEngineChoicesParseOrDefaultFallsBackSafelyToParakeet(
        string? raw,
        LivePreviewEngineChoice expected)
    {
        Assert.Equal(expected, LivePreviewEngineChoices.ParseOrDefault(raw));
    }

    [Theory]
    [InlineData(LivePreviewEngineChoice.Parakeet)]
    [InlineData(LivePreviewEngineChoice.Whisper)]
    public async Task JsonSettingsStoreRoundTripsLivePreviewEngineChoice(LivePreviewEngineChoice choice)
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var path = Path.Combine(directory, "settings.json");
            var store = new JsonSettingsStore(path);
            var settings = AppSettings.Default with
            {
                Preferences = UserPreferences.Default with
                {
                    LivePreviewEnabled = true,
                    LivePreviewEngine = choice,
                },
            };

            await store.SaveAsync(settings);
            var loaded = await store.LoadAsync();

            Assert.Equal(SettingsLoadStatus.Loaded, loaded.Status);
            Assert.Equal(choice, loaded.Settings.Preferences.LivePreviewEngine);
            var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            Assert.Equal(
                LivePreviewEngineChoices.ToPersistedValue(choice),
                root["preferences"]!["livePreviewEngine"]!.GetValue<string>());
        });
    }

    [Fact]
    public async Task JsonSettingsStoreMigratesV19SettingsToParakeetLivePreviewDefault()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var path = Path.Combine(directory, "settings.json");
            var v19 = JsonSettingsStoreTests.CreatePopulatedSettings() with { SchemaVersion = 19 };
            var json = JsonSerializer.Serialize(v19, JsonSettingsStore.SerializerOptions);
            var root = JsonNode.Parse(json)!.AsObject();
            Assert.True(root["preferences"]!.AsObject().Remove("livePreviewEngine"));
            await File.WriteAllTextAsync(path, root.ToJsonString(JsonSettingsStore.SerializerOptions));

            var result = await new JsonSettingsStore(path).LoadAsync();

            Assert.Equal(SettingsLoadStatus.Migrated, result.Status);
            Assert.Equal(19, result.SourceSchemaVersion);
            Assert.Equal(AppSettings.CurrentSchemaVersion, result.Settings.SchemaVersion);
            Assert.Equal(LivePreviewEngineChoice.Parakeet, result.Settings.Preferences.LivePreviewEngine);
        });
    }

    [Fact]
    public async Task JsonSettingsStoreFallsBackToParakeetOnUnknownPersistedLivePreviewEngineString()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var path = Path.Combine(directory, "settings.json");
            var current = JsonSettingsStoreTests.CreatePopulatedSettings();
            var json = JsonSerializer.Serialize(current, JsonSettingsStore.SerializerOptions);
            var root = JsonNode.Parse(json)!.AsObject();
            root["preferences"]!["livePreviewEngine"] = "LegacyOrUnknownEngine";
            await File.WriteAllTextAsync(path, root.ToJsonString(JsonSettingsStore.SerializerOptions));

            var result = await new JsonSettingsStore(path).LoadAsync();

            Assert.Equal(SettingsLoadStatus.Loaded, result.Status);
            Assert.Equal(LivePreviewEngineChoice.Parakeet, result.Settings.Preferences.LivePreviewEngine);
        });
    }

    [Fact]
    public async Task PortableProfileMigratesV13ProfileToParakeetLivePreviewDefault()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var path = Path.Combine(directory, "profile.enviouswispr.json");
            var current = JsonSettingsStoreTests.CreatePopulatedSettings().ToPortableProfile();
            var json = JsonSerializer.Serialize(current with { SchemaVersion = 13 }, JsonSettingsStore.SerializerOptions);
            var root = JsonNode.Parse(json)!.AsObject();
            Assert.True(root["preferences"]!.AsObject().Remove("livePreviewEngine"));
            await File.WriteAllTextAsync(path, root.ToJsonString(JsonSettingsStore.SerializerOptions));

            var result = await new JsonPortableProfileService().ImportAsync(path);

            Assert.Equal(PortableProfileImportStatus.Imported, result.Status);
            Assert.Equal(PortableProfile.CurrentSchemaVersion, result.Profile?.SchemaVersion);
            Assert.Equal(LivePreviewEngineChoice.Parakeet, result.Profile?.Preferences.LivePreviewEngine);
        });
    }

    [Fact]
    public void RequiredPreviewModelIdMatchesSelectedPreviewEngine()
    {
        Assert.Equal(ParakeetModelIds.Final, LivePreviewRuntimeResolver.RequiredModelId(LivePreviewEngineChoice.Parakeet));
        Assert.Equal(WhisperModelIds.Preview, LivePreviewRuntimeResolver.RequiredModelId(LivePreviewEngineChoice.Whisper));
    }

    [Fact]
    public void LivePreviewRuntimeResolverBuildsParakeetWorkhorseCpuInt8OptionsWithFp32FallbackDisabled()
    {
        var hardware = CreateHardware(physicalCores: 6, logicalProcessors: 12);

        var plan = LivePreviewRuntimeResolver.Resolve(
            LivePreviewEngineChoice.Parakeet,
            workerExecutable: "EnviousWispr.RuntimeWorker.exe",
            hardware,
            parakeetModelDirectory: @"C:\models\parakeet",
            parakeetInt8Complete: true,
            whisperPreviewModelDirectory: null,
            whisperPreviewComplete: false,
            whisperProvider: RuntimeProviderKind.Cpu);

        Assert.True(plan.Succeeded);
        Assert.Equal(LivePreviewEngineChoice.Parakeet, plan.RequestedEngine);
        Assert.Equal(LivePreviewEngineChoice.Parakeet, plan.EffectiveEngine);
        Assert.False(plan.IsStandingIn);
        Assert.NotNull(plan.Options);
        Assert.Equal(FinalAsrEngine.Parakeet, plan.Options!.Engine);
        Assert.Equal(RuntimeProviderKind.Cpu, plan.Options.Provider);
        Assert.Equal(ParakeetModelPack.Quantized, plan.Options.ModelPack);
        Assert.Equal(6, plan.Options.IntraOpThreads);
        Assert.Equal(6, plan.Options.CpuFallbackThreads);
        Assert.Equal(ProcessPriorityClass.BelowNormal, plan.Options.WorkerPriority);
        Assert.Equal(0, plan.Options.MaximumWorkerRestarts);
        Assert.False(plan.Options.AllowFullPrecisionFallback);
        Assert.Equal(TimeSpan.FromSeconds(15), plan.Options.StartupTimeout);
        Assert.Equal(TimeSpan.FromSeconds(15), plan.Options.TranscriptionTimeout);
    }

    [Fact]
    public void LivePreviewRuntimeResolverStandsInWithParakeetWhenWhisperPreviewIsNotYetInstalled()
    {
        var hardware = CreateHardware(physicalCores: 8, logicalProcessors: 16);

        var standIn = LivePreviewRuntimeResolver.Resolve(
            LivePreviewEngineChoice.Whisper,
            workerExecutable: "EnviousWispr.RuntimeWorker.exe",
            hardware,
            parakeetModelDirectory: @"C:\models\parakeet",
            parakeetInt8Complete: true,
            whisperPreviewModelDirectory: null,
            whisperPreviewComplete: false,
            whisperProvider: RuntimeProviderKind.Cuda,
            whisperLanguage: "auto");

        Assert.True(standIn.Succeeded);
        Assert.Equal(LivePreviewEngineChoice.Whisper, standIn.RequestedEngine);
        Assert.Equal(LivePreviewEngineChoice.Parakeet, standIn.EffectiveEngine);
        Assert.True(standIn.IsStandingIn);
        Assert.NotNull(standIn.Options);
        Assert.Equal(FinalAsrEngine.Parakeet, standIn.Options!.Engine);
        Assert.Equal(RuntimeProviderKind.Cpu, standIn.Options.Provider);
        Assert.False(standIn.Options.AllowFullPrecisionFallback);

        var whisperReady = LivePreviewRuntimeResolver.Resolve(
            LivePreviewEngineChoice.Whisper,
            workerExecutable: "EnviousWispr.RuntimeWorker.exe",
            hardware,
            parakeetModelDirectory: @"C:\models\parakeet",
            parakeetInt8Complete: true,
            whisperPreviewModelDirectory: @"C:\models\whisper-small",
            whisperPreviewComplete: true,
            whisperProvider: RuntimeProviderKind.Cuda,
            whisperLanguage: "fr");

        Assert.True(whisperReady.Succeeded);
        Assert.Equal(LivePreviewEngineChoice.Whisper, whisperReady.EffectiveEngine);
        Assert.False(whisperReady.IsStandingIn);
        Assert.NotNull(whisperReady.Options);
        Assert.Equal(FinalAsrEngine.Whisper, whisperReady.Options!.Engine);
        Assert.Equal(WhisperModelPack.PreviewSmall, whisperReady.Options.WhisperPack);
        Assert.Equal(RuntimeProviderKind.Cuda, whisperReady.Options.Provider);
        Assert.Equal("fr", whisperReady.Options.Language);
    }

    [Fact]
    public void RuntimeWorkerLivePreviewEngineAcceptsBothParakeetInt8AndWhisperSmallAndRejectsInvalidPacks()
    {
        var parakeetOptions = new RuntimeWorkerTranscriptionOptions(
            "worker.exe",
            "models/parakeet",
            RuntimeProviderKind.Cpu,
            ParakeetModelPack.Quantized,
            IntraOpThreads: 4,
            Engine: FinalAsrEngine.Parakeet);
        var normalizedParakeet = RuntimeWorkerLivePreviewEngine.NormalizePreviewOptions(parakeetOptions);
        Assert.Equal(ProcessPriorityClass.BelowNormal, normalizedParakeet.WorkerPriority);
        Assert.Equal(0, normalizedParakeet.MaximumWorkerRestarts);
        Assert.False(normalizedParakeet.AllowFullPrecisionFallback);

        var whisperOptions = new RuntimeWorkerTranscriptionOptions(
            "worker.exe",
            "models/whisper-small",
            RuntimeProviderKind.Cuda,
            ParakeetModelPack.Quantized,
            IntraOpThreads: 4,
            Engine: FinalAsrEngine.Whisper,
            WhisperPack: WhisperModelPack.PreviewSmall);
        var normalizedWhisper = RuntimeWorkerLivePreviewEngine.NormalizePreviewOptions(whisperOptions);
        Assert.Equal(ProcessPriorityClass.BelowNormal, normalizedWhisper.WorkerPriority);
        Assert.Equal(0, normalizedWhisper.MaximumWorkerRestarts);

        Assert.Throws<ArgumentException>(() =>
            RuntimeWorkerLivePreviewEngine.NormalizePreviewOptions(parakeetOptions with
            {
                ModelPack = ParakeetModelPack.FullPrecision,
            }));
        Assert.Throws<ArgumentException>(() =>
            RuntimeWorkerLivePreviewEngine.NormalizePreviewOptions(parakeetOptions with
            {
                Provider = RuntimeProviderKind.Cuda,
            }));
        Assert.Throws<ArgumentException>(() =>
            RuntimeWorkerLivePreviewEngine.NormalizePreviewOptions(whisperOptions with
            {
                WhisperPack = WhisperModelPack.Quantized,
            }));
    }

    [Fact]
    public void LivePreviewTextBoundKeepsWordAlignedTailWithin2000CharactersAndHandlesUnspacedScripts()
    {
        const string shortText = "the quick brown fox";
        Assert.Equal(shortText, LivePreviewTextBound.Apply(shortText));

        var longWords = string.Concat(Enumerable.Repeat("alpha ", 1_000));
        var boundedWords = LivePreviewTextBound.Apply(longWords);
        Assert.True(boundedWords.Length <= LivePreviewTextBound.MaxCharacters);
        Assert.True(boundedWords.Length > 0);
        Assert.EndsWith(boundedWords, longWords, StringComparison.Ordinal);
        Assert.StartsWith("alpha", boundedWords, StringComparison.Ordinal);
        Assert.Equal(boundedWords, LivePreviewTextBound.Apply(boundedWords));

        var unspacedCjk = new string('語', 5_000);
        var boundedCjk = LivePreviewTextBound.Apply(unspacedCjk);
        Assert.Equal(LivePreviewTextBound.MaxCharacters, boundedCjk.Length);
        Assert.EndsWith(boundedCjk, unspacedCjk, StringComparison.Ordinal);

        var unspacedWithTrailingSpace = new string('語', 5_000) + " ";
        var boundedTrailingSpace = LivePreviewTextBound.Apply(unspacedWithTrailingSpace);
        Assert.Equal(LivePreviewTextBound.MaxCharacters, boundedTrailingSpace.Length);
        Assert.False(string.IsNullOrWhiteSpace(boundedTrailingSpace));
    }

    [Fact]
    public async Task ProducerSidePreviewAsyncBoundsTranscriptTextTo2000Characters()
    {
        using var arbiter = new RuntimeResourceArbiter();
        var longTranscript = string.Concat(Enumerable.Repeat("word ", 900));
        var runtime = new StubTextRuntime(longTranscript);
        await using var preview = new RuntimeWorkerLivePreviewEngine(runtime, arbiter, RuntimeResourceKind.Cpu);
        Assert.True((await preview.StartAsync()).Succeeded);

        var update = await preview.PreviewAsync(
            new AudioSnapshot(DictationSessionId.Create(), new float[1], 16_000, 1),
            sequence: 1);

        Assert.True(update.Succeeded);
        Assert.True(update.Text.Length <= LivePreviewTextBound.MaxCharacters);
        Assert.StartsWith("word ", update.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void LivePreviewTextFormatterAppliesCustomWordsBritishSpellingAnd2000CharBound()
    {
        CustomWordEntry[] customWords =
        [
            new("envy wisper", "EnviousWispr"),
            new("Kennedy Center", "Kennedy Center"),
        ];

        var formatted = LivePreviewTextFormatter.Format(
            "envy wisper adds color and flavor at the Kennedy Center",
            wordCorrectionEnabled: true,
            customWords,
            EnglishSpelling.British,
            detectedLanguage: "en",
            engineId: $"{ParakeetModelIds.Final}:cpu:isolated");

        Assert.Equal("EnviousWispr adds colour and flavour at the Kennedy Center", formatted);

        // Non-English detected speech must not be respelled by the British table ("color" in Spanish).
        var spanish = LivePreviewTextFormatter.Format(
            "el color por favor",
            wordCorrectionEnabled: true,
            customWords,
            EnglishSpelling.British,
            detectedLanguage: "es",
            engineId: $"{WhisperModelIds.Preview}:cpu:isolated");
        Assert.Equal("el color por favor", spanish);

        // Re-bounded after British expansion ("color" -> "colour") exceeds 2,000 characters.
        var nearLimit = string.Concat(Enumerable.Repeat("color ", 340));
        var formattedLong = LivePreviewTextFormatter.Format(
            nearLimit,
            wordCorrectionEnabled: false,
            customWords: null,
            EnglishSpelling.British,
            detectedLanguage: "en");
        Assert.True(formattedLong.Length <= LivePreviewTextBound.MaxCharacters);
        Assert.Contains("colour", formattedLong, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LivePreviewControllerFormatsDisplayFramesWithoutLeakingTextToLogs()
    {
        var clock = new ManualPreviewClock();
        var log = new CapturingLogger();
        var engine = new StubPreviewEngine("envy wisper favorite color");
        var effects = new FormattingPreviewEffects(engine)
        {
            WordCorrectionEnabled = true,
            CustomWords = [new CustomWordEntry("envy wisper", "EnviousWispr")],
            EnglishSpelling = EnglishSpelling.British,
        };

        await using var controller = new LivePreviewController(effects, log, clock);
        var sessionId = effects.RecordingSessionId!.Value;
        await controller.StartAsync(sessionId);
        await effects.FirstFrameShown.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await controller.StopAsync();

        Assert.Contains("EnviousWispr favourite colour", effects.ShownTexts);
        foreach (var entry in log.Entries)
        {
            var serialized = JsonSerializer.Serialize(entry);
            Assert.DoesNotContain("EnviousWispr", serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("favourite", serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("colour", serialized, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void LivePreviewSettingsPresentationMapsReadinessAndStandInStatesAccurately()
    {
        var off = LivePreviewSettingsPresentation.Present(
            isEnabled: false,
            LivePreviewEngineChoice.Parakeet,
            parakeetInstalled: true,
            whisperPreviewInstalled: false);
        Assert.Equal(LivePreviewReadinessKind.Off, off.ReadinessKind);
        Assert.Equal("Off", off.StatusBadgeText);
        Assert.True(off.ParakeetCard.IsSelected);
        Assert.False(off.WhisperCard.IsSelected);

        var parakeetReady = LivePreviewSettingsPresentation.Present(
            isEnabled: true,
            LivePreviewEngineChoice.Parakeet,
            parakeetInstalled: true,
            whisperPreviewInstalled: false);
        Assert.Equal(LivePreviewReadinessKind.Ready, parakeetReady.ReadinessKind);
        Assert.Equal("Ready", parakeetReady.StatusBadgeText);

        var downloadingWithStandIn = LivePreviewSettingsPresentation.Present(
            isEnabled: true,
            LivePreviewEngineChoice.Whisper,
            parakeetInstalled: true,
            whisperPreviewInstalled: false,
            SpeechModelDelivery.Downloading,
            whisperDownloadPercent: 42.5);
        Assert.Equal(LivePreviewReadinessKind.DownloadingWithParakeetStandIn, downloadingWithStandIn.ReadinessKind);
        Assert.Equal("Using Parakeet while Multilingual downloads", downloadingWithStandIn.StatusBadgeText);
        Assert.Equal(42.5, downloadingWithStandIn.WhisperCard.ProgressPercent);
        Assert.True(downloadingWithStandIn.WhisperCard.CanCancel);
        Assert.False(downloadingWithStandIn.WhisperCard.CanDownload);

        var standingInNeedsDownload = LivePreviewSettingsPresentation.Present(
            isEnabled: true,
            LivePreviewEngineChoice.Whisper,
            parakeetInstalled: true,
            whisperPreviewInstalled: false,
            SpeechModelDelivery.Missing);
        Assert.Equal(LivePreviewReadinessKind.StandingInWithParakeet, standingInNeedsDownload.ReadinessKind);
        Assert.Equal("Using Parakeet", standingInNeedsDownload.StatusBadgeText);
        Assert.True(standingInNeedsDownload.WhisperCard.CanDownload);

        var whisperReady = LivePreviewSettingsPresentation.Present(
            isEnabled: true,
            LivePreviewEngineChoice.Whisper,
            parakeetInstalled: true,
            whisperPreviewInstalled: true,
            SpeechModelDelivery.Installed,
            canRemoveWhisperModel: true);
        Assert.Equal(LivePreviewReadinessKind.Ready, whisperReady.ReadinessKind);
        Assert.Equal("Ready", whisperReady.StatusBadgeText);
        Assert.Equal("Installed · Active", whisperReady.WhisperCard.StatusHeadline);
        Assert.Equal("99+ languages", whisperReady.WhisperCard.BadgeText);
        Assert.Equal("190 MB", whisperReady.WhisperCard.DownloadSizeText);
        Assert.Equal(WhisperModelIds.Preview, whisperReady.WhisperCard.ModelName);
        Assert.True(whisperReady.WhisperCard.CanRemove);
        Assert.False(whisperReady.WhisperCard.CanDownload);

        var removal = LivePreviewSettingsPresentation.DescribeWhisperRemovalDialog(
            whisperSelected: true,
            livePreviewEnabled: true);
        Assert.Equal("Remove Whisper model", removal.ConfirmButtonText);
        Assert.Contains("Parakeet", removal.Message, StringComparison.Ordinal);

        var removalWithoutFallback = LivePreviewSettingsPresentation.DescribeWhisperRemovalDialog(
            whisperSelected: true,
            livePreviewEnabled: true,
            parakeetInstalled: false);
        Assert.DoesNotContain("falls back to Parakeet", removalWithoutFallback.Message, StringComparison.Ordinal);
        Assert.Contains("unavailable", removalWithoutFallback.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LivePreviewSectionMarkupCarriesAccessibleTwoEngineCardsAndModelDeliveryControls()
    {
        var root = FindRepositoryRoot();
        var document = XDocument.Load(Path.Combine(
            root,
            "src",
            "Production",
            "EnviousWispr.App",
            "MainWindow.xaml"));

        var section = FindNamed(document, "LivePreviewSection");
        var cardsGrid = FindNamed(section, "LivePreviewEngineCardsPanel");
        Assert.Equal("Grid", cardsGrid.Name.LocalName);
        Assert.Equal("LivePreviewEngineCardsPanel_SizeChanged", (string?)cardsGrid.Attribute("SizeChanged"));
        var columns = cardsGrid.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "Grid.ColumnDefinitions")?
            .Elements()
            .ToList();
        Assert.NotNull(columns);
        Assert.Equal(2, columns!.Count);

        var parakeetContainer = FindNamed(section, "LivePreviewParakeetCardContainer");
        var whisperContainer = FindNamed(section, "LivePreviewWhisperCardContainer");
        Assert.Equal("0", (string?)parakeetContainer.Attribute("Grid.Column"));
        Assert.Equal("1", (string?)whisperContainer.Attribute("Grid.Column"));

        var parakeetCard = FindNamed(section, "LivePreviewParakeetEngineCard");
        var whisperCard = FindNamed(section, "LivePreviewWhisperEngineCard");
        Assert.Equal("RadioButton", parakeetCard.Name.LocalName);
        Assert.Equal("RadioButton", whisperCard.Name.LocalName);
        Assert.Equal("LivePreviewEngine", (string?)parakeetCard.Attribute("GroupName"));
        Assert.Equal("LivePreviewEngine", (string?)whisperCard.Attribute("GroupName"));

        var parakeetBadge = FindNamed(section, "LivePreviewParakeetEngineBadge");
        var whisperBadge = FindNamed(section, "LivePreviewWhisperEngineBadge");
        Assert.Equal("{StaticResource BrandAccentChipStyle}", (string?)parakeetBadge.Attribute("Style"));
        Assert.Equal("{StaticResource BrandAccentChipStyle}", (string?)whisperBadge.Attribute("Style"));
        Assert.Null(parakeetBadge.Attribute("CornerRadius"));
        Assert.Null(whisperBadge.Attribute("CornerRadius"));

        var parakeetName = (string?)parakeetCard.Attribute("AutomationProperties.Name");
        var whisperName = (string?)whisperCard.Attribute("AutomationProperties.Name");
        Assert.False(string.IsNullOrWhiteSpace(parakeetName));
        Assert.False(string.IsNullOrWhiteSpace(whisperName));
        Assert.Contains("Parakeet", parakeetName!, StringComparison.Ordinal);
        Assert.Contains("25 languages", parakeetName!, StringComparison.Ordinal);
        Assert.Contains("Whisper", whisperName!, StringComparison.Ordinal);
        Assert.Contains("99 languages", whisperName!, StringComparison.Ordinal);

        Assert.NotNull(FindNamed(section, "LivePreviewReadyBadge"));
        Assert.NotNull(FindNamed(section, "LivePreviewWarningBadge"));
        Assert.NotNull(FindNamed(section, "LivePreviewErrorBadge"));
        Assert.NotNull(FindNamed(section, "LivePreviewOffBadge"));
        Assert.NotNull(FindNamed(section, "LivePreviewStatusDetailText"));
        Assert.NotNull(FindNamed(section, "LivePreviewPrivacyText"));
        Assert.NotNull(FindNamed(section, "DownloadLivePreviewWhisperButton"));
        Assert.NotNull(FindNamed(section, "CancelLivePreviewWhisperDownloadButton"));

        var removeButton = FindNamed(section, "RemoveLivePreviewWhisperButton");
        Assert.Equal("Remove Whisper model", (string?)removeButton.Attribute("Content"));
        Assert.Equal("{StaticResource BrandDestructiveButtonStyle}", (string?)removeButton.Attribute("Style"));
    }

    private static XElement FindNamed(XContainer container, string name) =>
        container.Descendants().Single(element =>
            string.Equals(
                (string?)element.Attribute(XName.Get("Name", XamlNamespace)),
                name,
                StringComparison.Ordinal));

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "EnviousWispr.Windows.slnx")))
        {
            current = current.Parent;
        }

        Assert.NotNull(current);
        return current.FullName;
    }

    private static HardwareSnapshot CreateHardware(int physicalCores, int logicalProcessors) => new(
        HardwareProbeStatus.Complete,
        ProcessorArchitectureKind.X64,
        ProcessorVendor.Amd,
        PhysicalCoreCount: physicalCores,
        LogicalProcessorCount: logicalProcessors,
        TotalPhysicalMemoryBytes: 32UL * 1024 * 1024 * 1024,
        GraphicsAdapters: [],
        IsDirectMlRuntimeAvailable: true,
        new CudaDriverCapability(IsDriverAvailable: true, DeviceCount: 1, DriverVersion: 12_040),
        IsOnnxRuntimeCudaDependencySetAvailable: true,
        IsWhisperCudaDependencySetAvailable: true);

    private sealed class StubTextRuntime(string text) : IWorkerTranscriptionRuntime
    {
        public string EngineId => $"{ParakeetModelIds.Final}:cpu:isolated";

        public int? WorkerProcessId => 1234;

        public Task<RuntimeWorkerResult> StartAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RuntimeWorkerResult(true, RuntimeWorkerState.Ready));

        public Task<RuntimeWorkerResult> StopAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RuntimeWorkerResult(true, RuntimeWorkerState.Stopped));

        public Task<RuntimeWorkerAbortResult> AbortAsync(TimeSpan deadline) =>
            Task.FromResult(new RuntimeWorkerAbortResult(RuntimeWorkerAbortOutcome.Exited, 1234));

        public Task<Transcript> TranscribeAsync(CapturedAudio audio, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Transcript(audio.SessionId, text, EngineId));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubPreviewEngine(string text) : ILivePreviewEngine
    {
        public string EngineId => $"{ParakeetModelIds.Final}:cpu:isolated";

        public TaskCompletionSource FirstPassCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<RuntimeWorkerResult> StartAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RuntimeWorkerResult(true, RuntimeWorkerState.Ready));

        public Task<LivePreviewUpdate> PreviewAsync(
            AudioSnapshot snapshot,
            long sequence,
            CancellationToken cancellationToken = default)
        {
            FirstPassCompleted.TrySetResult();
            return Task.FromResult(new LivePreviewUpdate(
                snapshot.SessionId.Value,
                sequence,
                Succeeded: true,
                text,
                DetectedLanguage: "en"));
        }

        public Task<RuntimeWorkerResult> StopAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RuntimeWorkerResult(true, RuntimeWorkerState.Stopped));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FormattingPreviewEffects(ILivePreviewEngine engine) : ILivePreviewEffects, IAudioSnapshotSource
    {
        private readonly float[] _samples = new float[16_000];

        public bool Enabled => true;

        public ILivePreviewEngine? Engine => engine;

        public AppErrorCode? EngineUnavailableReason => null;

        public IAudioSnapshotSource? Audio => this;

        public DictationSessionId? RecordingSessionId { get; } = DictationSessionId.Create();

        public bool WordCorrectionEnabled { get; init; }

        public IReadOnlyList<CustomWordEntry> CustomWords { get; init; } = [];

        public EnglishSpelling EnglishSpelling { get; init; } = EnglishSpelling.American;

        public List<string> ShownTexts { get; } = [];

        public TaskCompletionSource FirstFrameShown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ShowPreview(LivePreviewFrame frame)
        {
            if (frame.IsCurrent())
            {
                ShownTexts.Add(frame.Text);
                FirstFrameShown.TrySetResult();
            }
        }

        public void ClearPreview()
        {
        }

        public AudioSnapshot? GetSnapshot(TimeSpan maximumDuration) =>
            new(RecordingSessionId!.Value, _samples, 16_000, 1);
    }

    private sealed class CapturingLogger : IAppLogger
    {
        public List<AppLogEntry> Entries { get; } = [];

        public void Write(AppLogEntry entry) => Entries.Add(entry);
    }

    private sealed class ManualPreviewClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 24, 17, 0, 0, TimeSpan.Zero);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            new InertTimer();

        private sealed class InertTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
