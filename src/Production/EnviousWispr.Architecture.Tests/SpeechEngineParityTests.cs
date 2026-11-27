using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.ModelDelivery;
using EnviousWispr.Presentation;
using EnviousWispr.Services.Settings;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace EnviousWispr.Architecture.Tests;

public sealed class SpeechEngineParityTests
{
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private const string XNamespaceUri = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void AutomaticPreferenceValueIsRemovedFromEnumAndDefaultsToParakeet()
    {
        var values = Enum.GetValues<FinalAsrEngine>();

        Assert.Equal([FinalAsrEngine.Parakeet, FinalAsrEngine.Whisper], values);
        Assert.Equal(FinalAsrEngine.Parakeet, DictationPreferences.Default.FinalEngine);
    }

    [Theory]
    [InlineData("Parakeet", true, FinalAsrEngine.Parakeet)]
    [InlineData(" parakeet ", true, FinalAsrEngine.Parakeet)]
    [InlineData("Automatic", true, FinalAsrEngine.Parakeet)]
    [InlineData("automatic", true, FinalAsrEngine.Parakeet)]
    [InlineData("Whisper", true, FinalAsrEngine.Whisper)]
    [InlineData(" WHISPER ", true, FinalAsrEngine.Whisper)]
    [InlineData("", false, FinalAsrEngine.Parakeet)]
    [InlineData("UnknownEngine", false, FinalAsrEngine.Parakeet)]
    public void FinalAsrEngineChoicesParsesLegacyAutomaticAndRejectsUnknown(
        string? raw,
        bool expectedParsed,
        FinalAsrEngine expectedEngine)
    {
        var parsed = FinalAsrEngineChoices.TryParse(raw, out var actual);

        Assert.Equal(expectedParsed, parsed);
        Assert.Equal(expectedEngine, actual);
    }

    [Fact]
    public async Task SettingsStoreNormalizesLegacyAutomaticToParakeetOnLoadAndWritesParakeetOnSave()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var path = Path.Combine(directory, "settings.json");
            var baselineJson = JsonSerializer.Serialize(AppSettings.Default, JsonSettingsStore.SerializerOptions);
            var legacyAutomaticJson = baselineJson.Replace(
                "\"finalEngine\": \"Parakeet\"",
                "\"finalEngine\": \"Automatic\"",
                StringComparison.Ordinal);
            Assert.Contains("\"finalEngine\": \"Automatic\"", legacyAutomaticJson, StringComparison.Ordinal);
            await File.WriteAllTextAsync(path, legacyAutomaticJson);

            var store = new JsonSettingsStore(path);
            var loaded = await store.LoadAsync();

            Assert.Equal(SettingsLoadStatus.Loaded, loaded.Status);
            Assert.Equal(FinalAsrEngine.Parakeet, loaded.Settings.Preferences.Dictation.FinalEngine);

            await store.SaveAsync(loaded.Settings);

            var savedJson = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("\"finalEngine\": \"Automatic\"", savedJson, StringComparison.Ordinal);
            Assert.Contains("\"finalEngine\": \"Parakeet\"", savedJson, StringComparison.Ordinal);

            await File.WriteAllTextAsync(
                path,
                baselineJson.Replace("\"finalEngine\": \"Parakeet\"", "\"finalEngine\": \"NotAnEngine\"", StringComparison.Ordinal));
            var invalid = await store.LoadAsync();
            Assert.Equal(SettingsLoadStatus.Invalid, invalid.Status);

            // Legacy numeric enum forms (Automatic = 0, Parakeet = 1, Whisper = 2) upgrade cleanly.
            foreach (var (rawNumber, expectedEngine) in new[] { (0, FinalAsrEngine.Parakeet), (1, FinalAsrEngine.Parakeet), (2, FinalAsrEngine.Whisper) })
            {
                await File.WriteAllTextAsync(
                    path,
                    baselineJson.Replace("\"finalEngine\": \"Parakeet\"", $"\"finalEngine\": {rawNumber}", StringComparison.Ordinal));
                var numeric = await store.LoadAsync();
                Assert.Equal(SettingsLoadStatus.Loaded, numeric.Status);
                Assert.Equal(expectedEngine, numeric.Settings.Preferences.Dictation.FinalEngine);
            }

            await File.WriteAllTextAsync(
                path,
                baselineJson.Replace("\"finalEngine\": \"Parakeet\"", "\"finalEngine\": 99", StringComparison.Ordinal));
            Assert.Equal(SettingsLoadStatus.Invalid, (await store.LoadAsync()).Status);
        });
    }

    [Fact]
    public async Task PortableProfileImportNormalizesLegacyAutomaticToParakeet()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var path = Path.Combine(directory, "profile.json");
            var profile = new PortableProfile(PortableProfile.CurrentSchemaVersion, UserPreferences.Default, ReusableUserData.Empty);
            var profileJson = JsonSerializer.Serialize(profile, JsonSettingsStore.SerializerOptions).Replace(
                "\"finalEngine\": \"Parakeet\"",
                "\"finalEngine\": \"Automatic\"",
                StringComparison.Ordinal);
            await File.WriteAllTextAsync(path, profileJson);

            var service = new JsonPortableProfileService();
            var imported = await service.ImportAsync(path);

            Assert.Equal(PortableProfileImportStatus.Imported, imported.Status);
            Assert.NotNull(imported.Profile);
            Assert.Equal(FinalAsrEngine.Parakeet, imported.Profile!.Preferences.Dictation.FinalEngine);
        });
    }

    [Fact]
    public void BundledManifestBundleSizesMatchParakeet640MbAndWhisper548Mb()
    {
        var parakeet = BundledModelManifests.Load(
            ParakeetModelIds.Final,
            new ModelManifestVerifier(new Dictionary<string, string>()));
        var whisper = BundledModelManifests.Load(
            WhisperModelIds.Final,
            new ModelManifestVerifier(new Dictionary<string, string>()));

        Assert.True(parakeet.Succeeded);
        Assert.True(whisper.Succeeded);

        static string FormatMb(VerifiedModelManifest manifest) =>
            $"{(manifest.Payload.Files.Sum(f => f.SizeBytes) + (1024 * 1024) - 1) / (1024 * 1024)} MB";

        Assert.Equal(EnginePolishPresentation.ParakeetBundleSizeLabel, FormatMb(parakeet.Manifest!));
        Assert.Equal(EnginePolishPresentation.WhisperBundleSizeLabel, FormatMb(whisper.Manifest!));
        Assert.Equal("640 MB", EnginePolishPresentation.ParakeetBundleSizeLabel);
        Assert.Equal("548 MB", EnginePolishPresentation.WhisperBundleSizeLabel);
    }

    [Fact]
    public void EngineCardsPresentSpecsInstallDownloadRemoveAndRetryStatesWithoutAutomatic()
    {
        var missingState = new SpeechEngineModelState(
            IsInstalled: false,
            CanRemove: false,
            CanDownload: true,
            DeliveryStage: SpeechModelDelivery.Missing,
            BundleSizeText: "640 MB");

        var parakeetMissing = EnginePolishPresentation.DescribeEngineCard(
            FinalAsrEngine.Parakeet,
            FinalAsrEngine.Parakeet,
            missingState,
            otherDownloadInFlight: false);
        Assert.True(parakeetMissing.IsSelected);
        Assert.True(parakeetMissing.ShowDownloadButton);
        Assert.Equal("Download (640 MB)", parakeetMissing.DownloadButtonText);
        Assert.False(parakeetMissing.ShowRemoveButton);
        Assert.Equal("25 languages", parakeetMissing.LanguagesText);
        Assert.Equal(ParakeetModelIds.Final, parakeetMissing.ModelName);

        var downloadingWhisper = EnginePolishPresentation.DescribeEngineCard(
            FinalAsrEngine.Whisper,
            FinalAsrEngine.Parakeet,
            new SpeechEngineModelState(
                IsInstalled: false,
                CanRemove: false,
                CanDownload: true,
                DeliveryStage: SpeechModelDelivery.Downloading,
                DownloadPercent: 45.5,
                StatusDetail: "Downloading… 250 of 548 MB",
                BundleSizeText: "548 MB"),
            otherDownloadInFlight: false);
        Assert.False(downloadingWhisper.IsSelected);
        Assert.True(downloadingWhisper.ShowProgress);
        Assert.Equal(45.5, downloadingWhisper.DownloadPercent);
        Assert.True(downloadingWhisper.ShowCancelButton);
        Assert.False(downloadingWhisper.ShowDownloadButton);
        Assert.False(downloadingWhisper.ShowRemoveButton);
        Assert.Equal("99+ languages", downloadingWhisper.LanguagesText);
        Assert.Equal(WhisperModelIds.Final, downloadingWhisper.ModelName);

        var siblingWhileOtherDownloads = EnginePolishPresentation.DescribeEngineCard(
            FinalAsrEngine.Parakeet,
            FinalAsrEngine.Parakeet,
            new SpeechEngineModelState(
                IsInstalled: true,
                CanRemove: true,
                CanDownload: true,
                DeliveryStage: SpeechModelDelivery.Installed,
                BundleSizeText: "640 MB"),
            otherDownloadInFlight: true);
        Assert.True(siblingWhileOtherDownloads.ShowRemoveButton);
        Assert.False(siblingWhileOtherDownloads.RemoveButtonEnabled);
        Assert.False(siblingWhileOtherDownloads.DownloadButtonEnabled);

        var failedRetry = EnginePolishPresentation.DescribeEngineCard(
            FinalAsrEngine.Whisper,
            FinalAsrEngine.Whisper,
            new SpeechEngineModelState(
                IsInstalled: false,
                CanRemove: false,
                CanDownload: true,
                DeliveryStage: SpeechModelDelivery.Failed,
                StatusDetail: "Network error",
                BundleSizeText: "548 MB"),
            otherDownloadInFlight: false);
        Assert.True(failedRetry.ShowDownloadButton);
        Assert.Equal("Retry (548 MB)", failedRetry.DownloadButtonText);
    }

    [Fact]
    public void ActiveEngineRemovalDialogWarnsAboutActiveUsageAndParakeetLivePreviewImpact()
    {
        var parakeetConfirm = EnginePolishPresentation.DescribeActiveEngineRemovalDialog(
            FinalAsrEngine.Parakeet,
            livePreviewUsesParakeet: true);
        Assert.Equal("Remove Parakeet model?", parakeetConfirm.Title);
        Assert.Contains("Parakeet Live Preview will also become unavailable", parakeetConfirm.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Automatic", parakeetConfirm.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("Automatic", parakeetConfirm.Message, StringComparison.Ordinal);

        var whisperConfirm = EnginePolishPresentation.DescribeActiveEngineRemovalDialog(
            FinalAsrEngine.Whisper,
            livePreviewUsesParakeet: false);
        Assert.Equal("Remove Whisper model?", whisperConfirm.Title);
        Assert.Contains("currently selected for dictation", whisperConfirm.Message, StringComparison.Ordinal);

        var inactiveParakeetPreviewOnly = EnginePolishPresentation.DescribeActiveEngineRemovalDialog(
            FinalAsrEngine.Parakeet,
            livePreviewUsesParakeet: true,
            isSelectedFinalEngine: false);
        Assert.Contains("currently powering Live Preview", inactiveParakeetPreviewOnly.Message, StringComparison.Ordinal);
        Assert.Contains("keeps Whisper active for dictation", inactiveParakeetPreviewOnly.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("selected for dictation", inactiveParakeetPreviewOnly.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelPackageCleanerUnloadsEnginesBeforeRemovingStoreAndLegacyFiles()
    {
        var root = Directory.CreateTempSubdirectory("EnviousWispr.EngineRemoval.").FullName;
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            var payloadBytes = Encoding.UTF8.GetBytes("synthetic-parakeet-model-payload");
            var payload = new ModelManifestPayload(
                ModelManifestVerifier.CurrentManifestSchemaVersion,
                ParakeetModelIds.Final,
                "1.0.0",
                "1.0.0",
                new ModelLicenseNotice(
                    "Fixture license",
                    new Uri("https://example.invalid/license"),
                    "Test weights only."),
                [
                    new ModelArtifact(
                        "encoder-model.int8.onnx",
                        payloadBytes.Length,
                        Convert.ToHexString(SHA256.HashData(payloadBytes)).ToLowerInvariant(),
                        [new Uri($"https://example.invalid/{ParakeetModelIds.Final}/1.0.0/encoder-model.int8.onnx")]),
                ]);
            var envelope = ModelManifestSigning.CreateEnvelope(payload, "test-2026", key);
            var verifier = new ModelManifestVerifier(new Dictionary<string, string>
            {
                ["test-2026"] = ModelManifestSigning.ExportPublicKeyPem(key),
            });

            using var handler = new StaticPayloadHandler(payloadBytes);
            using var httpClient = new HttpClient(handler, disposeHandler: false);
            var store = new ModelStore(root, httpClient, verifier, new Version(1, 0, 0));

            var installed = await store.InstallAsync(envelope);
            Assert.True(installed.Succeeded);
            var active = await store.OpenActiveOfflineAsync(ParakeetModelIds.Final);
            Assert.True(active.Succeeded);

            // Hold an exclusive handle inside the active version directory (simulating ONNX worker lock),
            // proving ModelPackageCleaner unloads engines strictly BEFORE attempting store/directory cleanup.
            var lockedPath = Path.Combine(active.Installed!.DirectoryPath, "encoder-model.int8.onnx");
            var handle = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None);
            var events = new List<string>();
            var legacyModelDirectory = Path.Combine(root, ParakeetModelIds.Final);
            File.WriteAllText(Path.Combine(legacyModelDirectory, "legacy-leftover.txt"), "legacy");

            var outcome = await ModelPackageCleaner.RemoveAsync(
                store,
                ParakeetModelIds.Final,
                legacyModelDirectory,
                unloadEnginesAsync: () =>
                {
                    events.Add("unload");
                    handle.Dispose();
                    return Task.CompletedTask;
                });

            Assert.True(outcome.Succeeded);
            Assert.Equal(["unload"], events);
            Assert.Empty(await store.ListInstalledAsync(ParakeetModelIds.Final));
            Assert.False((await store.OpenActiveOfflineAsync(ParakeetModelIds.Final)).Succeeded);
            Assert.False(Directory.Exists(legacyModelDirectory));

            // If removal encounters a locked file (both FileShare.Read as held by workers and FileShare.None)
            // and fails, active.json and payload files must remain intact so the model stays usable.
            Assert.True((await store.InstallAsync(envelope)).Succeeded);
            var activeAgain = await store.OpenActiveOfflineAsync(ParakeetModelIds.Final);
            Assert.True(activeAgain.Succeeded);
            var stillLocked = Path.Combine(activeAgain.Installed!.DirectoryPath, "encoder-model.int8.onnx");
            foreach (var shareMode in new[] { FileShare.Read, FileShare.None })
            {
                using (new FileStream(stillLocked, FileMode.Open, FileAccess.Read, shareMode))
                {
                    var failedRemoval = await ModelPackageCleaner.RemoveAsync(
                        store,
                        ParakeetModelIds.Final,
                        legacyModelDirectory,
                        unloadEnginesAsync: () => Task.CompletedTask);
                    Assert.False(failedRemoval.Succeeded);
                    Assert.Equal(ModelDeliveryFailure.StorageUnavailable, failedRemoval.Failure);
                }

                Assert.True((await store.OpenActiveOfflineAsync(ParakeetModelIds.Final)).Succeeded);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void SideBySideEngineCardsAndLiveSwitchContractsMatchDesignAndLifecycleRules()
    {
        var root = FindRepositoryRoot();
        var xamlText = AppXamlSources.ReadMainWindowAndPagesText(root);
        var mainWindowCode = File.ReadAllText(Path.Combine(root, "src", "Production", "EnviousWispr.App", "MainWindow.SpeechEngine.cs"));
        var appCode = File.ReadAllText(Path.Combine(root, "src", "Production", "EnviousWispr.App", "App.xaml.cs"));
        var deliveryCode = File.ReadAllText(Path.Combine(root, "src", "Production", "EnviousWispr.App", "App.ModelDelivery.cs"));

        var document = AppXamlSources.LoadMainWindowAndPagesDocument(root);
        var grid = FindNamed(document, "EngineCardsGrid");
        var cols = grid.Element(XName.Get("Grid.ColumnDefinitions", XamlNamespace))!.Elements().ToArray();
        Assert.Equal(2, cols.Length);

        var parakeetContainer = FindNamed(document, "ParakeetEngineCardContainer");
        var whisperContainer = FindNamed(document, "WhisperEngineCardContainer");
        Assert.Equal("0", (string?)parakeetContainer.Attribute("Grid.Column"));
        Assert.Equal("1", (string?)whisperContainer.Attribute("Grid.Column"));

        foreach (var name in new[]
        {
            "ParakeetEngineCard",
            "WhisperEngineCard",
            "ParakeetDownloadButton",
            "ParakeetCancelButton",
            "ParakeetRemoveButton",
            "WhisperDownloadButton",
            "WhisperCancelButton",
            "WhisperRemoveButton",
            "SpeechEngineNoticeBanner",
            "SpeechEngineNoticeText",
        })
        {
            Assert.NotNull(FindNamed(document, name));
        }

        var whisperLanguageRow = FindNamed(document, "WhisperLanguageRow");
        Assert.Equal("Visible", (string?)whisperLanguageRow.Attribute("Visibility"));

        // Automatic must never be displayed as a transcription engine name in MainWindow markup.
        Assert.DoesNotContain("Text=\"Automatic\"", xamlText, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"Automatic\"", xamlText, StringComparison.Ordinal);
        Assert.Contains("HandleSpeechEngineRemoveClickAsync", mainWindowCode, StringComparison.Ordinal);
        Assert.Contains("previousFinalEngine != settings.Preferences.Dictation.FinalEngine", appCode, StringComparison.Ordinal);
        Assert.Contains("_transcriptionReconfigurePending = true;", appCode, StringComparison.Ordinal);

        // App.ModelDelivery must unload active engines via TeardownTranscriptionAsync before removal and refresh afterwards.
        var deliveryTree = CSharpSyntaxTree.ParseText(deliveryCode).GetRoot();
        var removeMethod = deliveryTree.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "RemoveSpeechEngineModelAsync");
        var removeBody = removeMethod.ToString();
        Assert.Contains("IsSpeechEngineBusy", removeBody, StringComparison.Ordinal);
        Assert.Contains("SessionHolder.SpeechEngineSwitch", removeBody, StringComparison.Ordinal);
        Assert.Contains("ModelPackageCleaner.RemoveAsync", removeBody, StringComparison.Ordinal);
        Assert.Contains("TeardownTranscriptionAsync", removeBody, StringComparison.Ordinal);
        Assert.Contains("ReconfigureTranscriptionAsync", removeBody, StringComparison.Ordinal);
        Assert.Contains("PresentModelDeliveryAsync", removeBody, StringComparison.Ordinal);
        Assert.Contains("SessionHolder.SpeechEngineSwitch", deliveryCode, StringComparison.Ordinal);
        Assert.Contains("TrySchedulePendingModelRefresh", appCode, StringComparison.Ordinal);
        Assert.True(
            removeBody.IndexOf("ModelPackageCleaner.RemoveAsync", StringComparison.Ordinal)
                < removeBody.IndexOf("ReconfigureTranscriptionAsync", StringComparison.Ordinal),
            "Model files must be removed after teardown and before engine reconfiguration.");
    }

    private static XElement FindNamed(XDocument document, string name)
    {
        var element = document.Descendants()
            .FirstOrDefault(node => (string?)node.Attribute(XName.Get("Name", XNamespaceUri)) == name);
        Assert.True(element is not null, $"Missing XAML element '{name}'.");
        return element!;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EnviousWispr.Windows.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class StaticPayloadHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes),
            });
    }
}
