using System.Text.Json;
using System.Text.Json.Nodes;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Settings;
using EnviousWispr.Services.Settings;

namespace EnviousWispr.Architecture.Tests;

public sealed class PortableProfileServiceTests
{
    [Fact]
    public async Task ExportThenImportRoundTripsPortableDataOnly()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var path = Path.Combine(directory, "profile.enviouswispr.json");
            var settings = JsonSettingsStoreTests.CreatePopulatedSettings();
            var service = new JsonPortableProfileService();

            var exportResult = await service.ExportAsync(settings.ToPortableProfile(), path);
            var importResult = await service.ImportAsync(path);

            Assert.True(exportResult.Succeeded);
            Assert.Equal(PortableProfileImportStatus.Imported, importResult.Status);
            Assert.Equal(settings.ToPortableProfile(), importResult.Profile);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var propertyNames = DescendantPropertyNames(document.RootElement).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("launchCount", propertyNames);
            Assert.DoesNotContain("hasCompletedOnboarding", propertyNames);
            Assert.DoesNotContain("preferredMicrophoneId", propertyNames);
            Assert.DoesNotContain("credential", propertyNames);
            Assert.DoesNotContain("apiKey", propertyNames);
            Assert.DoesNotContain("secret", propertyNames);
            Assert.DoesNotContain("transcript", propertyNames);
            Assert.DoesNotContain("historyEntries", propertyNames);
            Assert.DoesNotContain("clipboard", propertyNames);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        });
    }

    [Fact]
    public async Task ImportRejectsUnknownFieldsAndFutureVersions()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var service = new JsonPortableProfileService();
            var invalidPath = Path.Combine(directory, "invalid.json");
            await File.WriteAllTextAsync(
                invalidPath,
                "{\"schemaVersion\":1,\"apiKey\":\"must-not-import\"}");
            var futurePath = Path.Combine(directory, "future.json");
            await File.WriteAllTextAsync(futurePath, "{\"schemaVersion\":999}");

            var invalid = await service.ImportAsync(invalidPath);
            var future = await service.ImportAsync(futurePath);

            Assert.Equal(PortableProfileImportStatus.Invalid, invalid.Status);
            Assert.Equal(AppErrorCode.InvalidData, invalid.Error?.Code);
            Assert.Equal(PortableProfileImportStatus.NewerVersion, future.Status);
            Assert.Equal(AppErrorCode.NewerSchema, future.Error?.Code);
        });
    }

    [Fact]
    public async Task ImportMigratesPhaseTwoPortableProfileDefaults()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var path = Path.Combine(directory, "profile.enviouswispr.json");
            const string legacyJson =
                """
                {
                  "schemaVersion": 1,
                  "preferences": {
                    "dictation": { "finalEngine": "Parakeet", "pushToTalkGesture": "F8" },
                    "polish": { "provider": "None", "modelId": null },
                    "history": { "isEnabled": true, "retentionDays": 30 },
                    "theme": "System"
                  },
                  "userData": { "customWords": [], "snippets": [] }
                }
                """;
            await File.WriteAllTextAsync(path, legacyJson);

            var result = await new JsonPortableProfileService().ImportAsync(path);

            Assert.Equal(PortableProfileImportStatus.Imported, result.Status);
            Assert.Equal(PortableProfile.CurrentSchemaVersion, result.Profile?.SchemaVersion);
            Assert.True(result.Profile?.Preferences.Dictation.WordCorrectionEnabled);
            Assert.True(result.Profile?.Preferences.Dictation.FillerRemovalEnabled);
            Assert.True(result.Profile?.Preferences.Dictation.EmojiFormatterEnabled);
            Assert.False(result.Profile?.Preferences.Dictation.SpokenPunctuationEnabled);
        });
    }

    [Fact]
    public async Task ImportMigratesPhaseElevenProfileWithDefaultOllamaEndpoint()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var path = Path.Combine(directory, "profile.enviouswispr.json");
            const string legacyJson =
                """
                {
                  "schemaVersion": 2,
                  "preferences": {
                    "dictation": {
                      "finalEngine": "Parakeet",
                      "pushToTalkGesture": "F8",
                      "wordCorrectionEnabled": true,
                      "fillerRemovalEnabled": true,
                      "emojiFormatterEnabled": true,
                      "spokenPunctuationEnabled": false
                    },
                    "polish": { "provider": "Ollama", "modelId": "llama3.2" },
                    "history": { "isEnabled": true, "retentionDays": 30 },
                    "theme": "System"
                  },
                  "userData": { "customWords": [], "snippets": [] }
                }
                """;
            await File.WriteAllTextAsync(path, legacyJson);

            var result = await new JsonPortableProfileService().ImportAsync(path);

            Assert.Equal(PortableProfileImportStatus.Imported, result.Status);
            Assert.Equal(PortableProfile.CurrentSchemaVersion, result.Profile?.SchemaVersion);
            Assert.Equal("llama3.2", result.Profile?.Preferences.Polish.ModelId);
            Assert.Null(result.Profile?.Preferences.Polish.OllamaEndpoint);
        });
    }

    [Fact]
    public async Task ImportMigratesPhaseFourteenProfileWithAutomaticWhisperLanguage()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var path = Path.Combine(directory, "profile.enviouswispr.json");
            var current = JsonSettingsStoreTests.CreatePopulatedSettings().ToPortableProfile();
            var json = JsonSerializer.Serialize(
                current with { SchemaVersion = 3 },
                JsonSettingsStore.SerializerOptions);
            var root = JsonNode.Parse(json)!.AsObject();
            root["preferences"]!["dictation"]!.AsObject().Remove("whisperLanguage");
            await File.WriteAllTextAsync(path, root.ToJsonString(JsonSettingsStore.SerializerOptions));

            var result = await new JsonPortableProfileService().ImportAsync(path);

            Assert.Equal(PortableProfileImportStatus.Imported, result.Status);
            Assert.Equal(WhisperLanguagePreference.Automatic, result.Profile?.Preferences.Dictation.WhisperLanguage);
        });
    }

    [Fact]
    public async Task ImportMigratesPhaseTwentyThreeProfileWithMacAppearanceDefaults()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var path = Path.Combine(directory, "profile.enviouswispr.json");
            var current = JsonSettingsStoreTests.CreatePopulatedSettings().ToPortableProfile();
            var json = JsonSerializer.Serialize(
                current with { SchemaVersion = 4 },
                JsonSettingsStore.SerializerOptions);
            var root = JsonNode.Parse(json)!.AsObject();
            root["preferences"]!.AsObject().Remove("livePreviewEnabled");
            root["preferences"]!.AsObject().Remove("overlayPosition");
            await File.WriteAllTextAsync(path, root.ToJsonString(JsonSettingsStore.SerializerOptions));

            var result = await new JsonPortableProfileService().ImportAsync(path);

            Assert.Equal(PortableProfileImportStatus.Imported, result.Status);
            Assert.Equal(PortableProfile.CurrentSchemaVersion, result.Profile?.SchemaVersion);
            Assert.False(result.Profile?.Preferences.LivePreviewEnabled);
            Assert.Equal(OverlayPillPosition.Top, result.Profile?.Preferences.OverlayPosition);
        });
    }

    [Fact]
    public async Task ImportMigratesPhaseTwentyFourProfileWithPillAndSoundDefaults()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var path = Path.Combine(directory, "profile.enviouswispr.json");
            var current = JsonSettingsStoreTests.CreatePopulatedSettings().ToPortableProfile();
            var json = JsonSerializer.Serialize(
                current with { SchemaVersion = 5 },
                JsonSettingsStore.SerializerOptions);
            var root = JsonNode.Parse(json)!.AsObject();
            var preferences = root["preferences"]!.AsObject();
            preferences.Remove("pillDesignWithoutWords");
            preferences.Remove("pillDesignWithWords");
            preferences.Remove("playRecordingSounds");
            preferences.Remove("recordingSoundPairing");
            await File.WriteAllTextAsync(path, root.ToJsonString(JsonSettingsStore.SerializerOptions));

            var result = await new JsonPortableProfileService().ImportAsync(path);

            Assert.Equal(PortableProfileImportStatus.Imported, result.Status);
            Assert.Equal(PortableProfile.CurrentSchemaVersion, result.Profile?.SchemaVersion);
            Assert.Equal(RecordingPillDesign.Classic, result.Profile?.Preferences.PillDesignWithoutWords);
            Assert.Equal(RecordingPillDesign.ReadingWell, result.Profile?.Preferences.PillDesignWithWords);
            Assert.False(result.Profile?.Preferences.PlayRecordingSounds);
            Assert.Equal(RecordingSoundPairing.WhisperTick, result.Profile?.Preferences.RecordingSoundPairing);
        });
    }

    [Fact]
    public void ApplyingImportedProfilePreservesMachineLocalLifecycleState()
    {
        var current = AppSettings.Default with
        {
            LaunchCount = 42,
            HasCompletedOnboarding = true,
            PreferredMicrophoneId = "machine-local-microphone",
            Observability = new ObservabilityPreferences(
                LocalDiagnosticsEnabled: false,
                DiagnosticRetentionDays: 3,
                ShareAnonymousTelemetry: false),
        };
        var imported = JsonSettingsStoreTests.CreatePopulatedSettings().ToPortableProfile();

        var applied = current.Apply(imported);

        Assert.Equal(42, applied.LaunchCount);
        Assert.True(applied.HasCompletedOnboarding);
        Assert.Equal("machine-local-microphone", applied.PreferredMicrophoneId);
        Assert.Equal(current.Observability, applied.Observability);
        Assert.Equal(imported.Preferences, applied.Preferences);
        Assert.Equal(imported.UserData, applied.UserData);
    }

    private static IEnumerable<string> DescendantPropertyNames(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                yield return property.Name;
                foreach (var descendant in DescendantPropertyNames(property.Value))
                {
                    yield return descendant;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var descendant in DescendantPropertyNames(item))
                {
                    yield return descendant;
                }
            }
        }
    }
}
