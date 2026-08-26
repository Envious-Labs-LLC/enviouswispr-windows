using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Settings;
using EnviousWispr.Services.Settings;

namespace EnviousWispr.Architecture.Tests;

public sealed class JsonSettingsStoreTests
{
    [Fact]
    public async Task SaveThenLoadRestoresCurrentSettingsAndLeavesNoTemporaryFile()
    {
        await WithTestDirectoryAsync(async directory =>
        {
            var store = new JsonSettingsStore(Path.Combine(directory, "settings.json"));
            var expected = CreatePopulatedSettings();

            await store.SaveAsync(expected);
            var result = await store.LoadAsync();

            Assert.Equal(SettingsLoadStatus.Loaded, result.Status);
            Assert.Equal(expected, result.Settings);
            Assert.Null(result.Error);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        });
    }

    [Fact]
    public async Task LoadCorruptJsonReturnsTypedSafeDefaultsWithoutChangingSource()
    {
        await WithTestDirectoryAsync(async directory =>
        {
            var path = Path.Combine(directory, "settings.json");
            const string corruptJson = "not-json";
            await File.WriteAllTextAsync(path, corruptJson);
            var store = new JsonSettingsStore(path);

            var result = await store.LoadAsync();

            Assert.Equal(SettingsLoadStatus.Invalid, result.Status);
            Assert.Equal(AppSettings.Default, result.Settings);
            Assert.Equal(AppErrorCode.InvalidData, result.Error?.Code);
            Assert.Equal(corruptJson, await File.ReadAllTextAsync(path));
        });
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public async Task LoadInvalidLegacyLaunchCountReturnsSafeDefaults(int launchCount)
    {
        await WithTestDirectoryAsync(async directory =>
        {
            var path = Path.Combine(directory, "settings.json");
            await File.WriteAllTextAsync(path, LegacyJson(launchCount, hasCompletedOnboarding: false));
            var store = new JsonSettingsStore(path);

            var result = await store.LoadAsync();

            Assert.Equal(SettingsLoadStatus.Invalid, result.Status);
            Assert.Equal(AppSettings.Default, result.Settings);
        });
    }

    [Fact]
    public async Task LoadMigratesPhaseOneSchemaWithoutLosingKnownValues()
    {
        await WithTestDirectoryAsync(async directory =>
        {
            var path = Path.Combine(directory, "settings.json");
            var legacyJson = LegacyJson(launchCount: 9, hasCompletedOnboarding: true);
            await File.WriteAllTextAsync(path, legacyJson);
            var store = new JsonSettingsStore(path);

            var result = await store.LoadAsync();

            Assert.Equal(SettingsLoadStatus.Migrated, result.Status);
            Assert.Equal(1, result.SourceSchemaVersion);
            Assert.Equal(AppSettings.CurrentSchemaVersion, result.Settings.SchemaVersion);
            Assert.Equal(9, result.Settings.LaunchCount);
            Assert.True(result.Settings.HasCompletedOnboarding);
            Assert.Equal(UserPreferences.Default, result.Settings.Preferences);
            Assert.Equal(ReusableUserData.Empty, result.Settings.UserData);

            var persisted = await store.ResetAsync(result.Settings);
            Assert.True(persisted.PreservedPreviousData);
            Assert.Equal(legacyJson, await File.ReadAllTextAsync(path + ".previous"));
            Assert.Equal(SettingsLoadStatus.Loaded, (await store.LoadAsync()).Status);
        });
    }

    [Fact]
    public async Task LoadMigratesPhaseTwoSettingsAndAddsMacParityDefaults()
    {
        await WithTestDirectoryAsync(async directory =>
        {
            var path = Path.Combine(directory, "settings.json");
            const string legacyJson =
                """
                {
                  "schemaVersion": 2,
                  "launchCount": 12,
                  "hasCompletedOnboarding": true,
                  "preferences": {
                    "dictation": { "finalEngine": "Whisper", "pushToTalkGesture": "Ctrl+F8" },
                    "polish": { "provider": "None", "modelId": null },
                    "history": { "isEnabled": true, "retentionDays": 30 },
                    "theme": "Dark"
                  },
                  "userData": {
                    "customWords": [{ "spokenForm": "envy wisper", "replacement": "EnviousWispr" }],
                    "snippets": []
                  }
                }
                """;
            await File.WriteAllTextAsync(path, legacyJson);

            var result = await new JsonSettingsStore(path).LoadAsync();

            Assert.Equal(SettingsLoadStatus.Migrated, result.Status);
            Assert.Equal(2, result.SourceSchemaVersion);
            Assert.Equal(12, result.Settings.LaunchCount);
            Assert.Equal(AppSettings.CurrentSchemaVersion, result.Settings.SchemaVersion);
            Assert.True(result.Settings.Preferences.Dictation.WordCorrectionEnabled);
            Assert.True(result.Settings.Preferences.Dictation.FillerRemovalEnabled);
            Assert.True(result.Settings.Preferences.Dictation.EmojiFormatterEnabled);
            Assert.False(result.Settings.Preferences.Dictation.SpokenPunctuationEnabled);
            Assert.Single(result.Settings.UserData.CustomWords);
        });
    }

    [Fact]
    public async Task LoadRejectsFutureSchemaWithoutChangingSource()
    {
        await WithTestDirectoryAsync(async directory =>
        {
            var path = Path.Combine(directory, "settings.json");
            const string futureJson = "{\"schemaVersion\":999,\"futureValue\":true}";
            await File.WriteAllTextAsync(path, futureJson);
            var store = new JsonSettingsStore(path);

            var result = await store.LoadAsync();

            Assert.Equal(SettingsLoadStatus.NewerVersion, result.Status);
            Assert.Equal(999, result.SourceSchemaVersion);
            Assert.Equal(AppErrorCode.NewerSchema, result.Error?.Code);
            Assert.Equal(futureJson, await File.ReadAllTextAsync(path));
        });
    }

    [Fact]
    public async Task ResetPreservesPreviousBytesAndWritesValidatedDefaults()
    {
        await WithTestDirectoryAsync(async directory =>
        {
            var path = Path.Combine(directory, "settings.json");
            const string corruptJson = "{broken";
            await File.WriteAllTextAsync(path, corruptJson);
            var store = new JsonSettingsStore(path);

            var reset = await store.ResetAsync(AppSettings.Default);

            Assert.True(reset.PreservedPreviousData);
            Assert.Equal(corruptJson, await File.ReadAllTextAsync(path + ".previous"));
            var loaded = await store.LoadAsync();
            Assert.Equal(SettingsLoadStatus.Loaded, loaded.Status);
            Assert.Equal(AppSettings.Default, loaded.Settings);
        });
    }

    internal static AppSettings CreatePopulatedSettings() => AppSettings.Default with
    {
        LaunchCount = 7,
        HasCompletedOnboarding = true,
        Preferences = UserPreferences.Default with
        {
            Dictation = new DictationPreferences(
                FinalAsrEngine.Whisper,
                "Ctrl+F8",
                WordCorrectionEnabled: false,
                FillerRemovalEnabled: false,
                EmojiFormatterEnabled: false,
                SpokenPunctuationEnabled: true),
            Polish = new PolishPreferences(PolishProvider.Ollama, "qwen3:4b"),
            History = new HistoryPreferences(IsEnabled: false, RetentionDays: 14),
            Theme = AppTheme.Dark,
        },
        UserData = new ReusableUserData(
            [new CustomWordEntry("envy wisper", "EnviousWispr")],
            [new SnippetEntry("signature", "Kind regards")]),
    };

    internal static async Task WithTestDirectoryAsync(Func<string, Task> test)
    {
        var path = Path.Combine(Path.GetTempPath(), "EnviousWispr.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            await test(path);
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static string LegacyJson(int launchCount, bool hasCompletedOnboarding) =>
        $$"""
        {
          "schemaVersion": 1,
          "launchCount": {{launchCount}},
          "hasCompletedOnboarding": {{hasCompletedOnboarding.ToString().ToLowerInvariant()}}
        }
        """;
}
