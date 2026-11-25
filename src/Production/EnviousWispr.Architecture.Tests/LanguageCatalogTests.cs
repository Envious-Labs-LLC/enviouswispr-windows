using System.Text.Json;
using System.Text.Json.Nodes;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Settings;
using EnviousWispr.Services.Settings;

namespace EnviousWispr.Architecture.Tests;

public sealed class LanguageCatalogTests
{
    [Fact]
    public void CatalogExposesFullWhisperCatalogWithEnglishAndEnglishUkAdjacent()
    {
        Assert.Equal(100, LanguageCatalog.All.Count);
        Assert.Equal(101, LanguageCatalog.PickerEntries.Count);

        var pickerCodes = LanguageCatalog.PickerEntries.Select(entry => entry.Code).ToList();
        var enIndex = pickerCodes.IndexOf("en");
        var enGbIndex = pickerCodes.IndexOf("en-gb");
        Assert.True(enIndex >= 0);
        Assert.Equal(enIndex + 1, enGbIndex);

        Assert.Contains("haw", pickerCodes);
        Assert.Contains("yue", pickerCodes);
        Assert.Contains("ja", pickerCodes);
        Assert.Contains("zh", pickerCodes);
    }

    [Fact]
    public void PickerSelectionAppliesBritishAndAmericanSpellingOnEnglishWithoutClobberingOtherLanguages()
    {
        var (ukLang, ukSpell) = LanguageCatalog.ApplyPickerSelection("en-gb", EnglishSpelling.American);
        Assert.Equal("en", ukLang);
        Assert.Equal(EnglishSpelling.British, ukSpell);
        Assert.Equal("en-gb", LanguageCatalog.ResolveSelection(ukLang, ukSpell)?.Code);
        Assert.Equal("English (UK)", LanguageCatalog.DisplayNameFor(ukLang, ukSpell));

        var (usLang, usSpell) = LanguageCatalog.ApplyPickerSelection("en", EnglishSpelling.British);
        Assert.Equal("en", usLang);
        Assert.Equal(EnglishSpelling.American, usSpell);
        Assert.Equal("en", LanguageCatalog.ResolveSelection(usLang, usSpell)?.Code);

        var (jaLang, jaSpell) = LanguageCatalog.ApplyPickerSelection("ja", EnglishSpelling.British);
        Assert.Equal("ja", jaLang);
        Assert.Equal(EnglishSpelling.British, jaSpell);
    }

    [Fact]
    public void ParakeetSupportsExactTwentyFiveEuropeanLanguagesAndFallsBackNonEuropeanToAuto()
    {
        var expected25 = new[]
        {
            "en", "es", "fr", "de", "it", "pt", "ro", "nl", "da", "sv", "fi", "hu",
            "et", "lv", "lt", "mt", "pl", "cs", "sk", "sl", "hr",
            "ru", "uk", "bg", "el",
        };

        Assert.Equal(25, LanguageCatalog.ParakeetSupportedCodes.Count);
        Assert.Equal(expected25.ToHashSet(StringComparer.Ordinal), LanguageCatalog.ParakeetSupportedCodes);
        Assert.Equal(26, LanguageCatalog.EntriesForEngine(FinalAsrEngine.Parakeet).Count);

        foreach (var code in expected25)
        {
            Assert.True(LanguageCatalog.IsSupported(FinalAsrEngine.Parakeet, code));
            Assert.Equal(code, LanguageCatalog.EffectiveRecognitionCode(FinalAsrEngine.Parakeet, code));
        }

        foreach (var unsupported in new[] { "ja", "ar", "hi", "ko", "sr", "bs", "be" })
        {
            Assert.False(LanguageCatalog.IsSupported(FinalAsrEngine.Parakeet, unsupported));
            Assert.True(LanguageCatalog.IsSupported(FinalAsrEngine.Whisper, unsupported));
            Assert.Equal(LanguageCatalog.AutomaticCode, LanguageCatalog.EffectiveRecognitionCode(FinalAsrEngine.Parakeet, unsupported));
            Assert.Equal(unsupported, LanguageCatalog.EffectiveRecognitionCode(FinalAsrEngine.Whisper, unsupported));
        }
    }

    [Fact]
    public void RecentLanguagesDeduplicateCapAtFiveAndFilterUnsupportedWhenParakeetIsActive()
    {
        IReadOnlyList<string> recents = [];
        foreach (var code in new[] { "fr", "ja", "en-gb", "es", "de", "ru", "ja", "auto" })
        {
            recents = LanguageCatalog.RecordRecentLanguage(recents, code);
        }

        Assert.Equal(["ja", "ru", "de", "es", "en-gb"], recents);
        Assert.Equal(["ja", "ru", "de", "es", "en-gb"], LanguageCatalog.ResolveRecentEntries(recents, FinalAsrEngine.Whisper).Select(e => e.Code));
        Assert.Equal(["ru", "de", "es", "en-gb"], LanguageCatalog.ResolveRecentEntries(recents, FinalAsrEngine.Parakeet).Select(e => e.Code));
    }

    [Theory]
    [InlineData("\"Automatic\"", "auto")]
    [InlineData("\"English\"", "en")]
    [InlineData("\"French\"", "fr")]
    [InlineData("\"German\"", "de")]
    [InlineData("\"Spanish\"", "es")]
    [InlineData("\"ja\"", "ja")]
    [InlineData("\"haw\"", "haw")]
    [InlineData("\"yue\"", "yue")]
    [InlineData("0", "auto")]
    [InlineData("2", "fr")]
    public async Task SettingsStoreMigratesLegacyV23EnumAndIsoLanguageValues(string rawJsonValue, string expectedCode)
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var path = Path.Combine(directory, "settings.json");
            var v23 = JsonSettingsStoreTests.CreatePopulatedSettings() with { SchemaVersion = 23 };
            var serialized = JsonSerializer.Serialize(v23, JsonSettingsStore.SerializerOptions);
            var root = JsonNode.Parse(serialized)!.AsObject();
            root["preferences"]!["dictation"]!["whisperLanguage"] = JsonNode.Parse(rawJsonValue);
            await File.WriteAllTextAsync(path, root.ToJsonString(JsonSettingsStore.SerializerOptions));

            var loaded = await new JsonSettingsStore(path).LoadAsync();

            Assert.Equal(SettingsLoadStatus.Migrated, loaded.Status);
            Assert.Equal(23, loaded.SourceSchemaVersion);
            Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.Settings.SchemaVersion);
            Assert.Equal(expectedCode, loaded.Settings.Preferences.Dictation.WhisperLanguage);
        });
    }

    [Fact]
    public async Task PortableProfileRoundTripsNonEfigsAndMigratesLegacyEnumNames()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var service = new JsonPortableProfileService();
            var exportedPath = Path.Combine(directory, "export.enviouswispr.json");
            var original = JsonSettingsStoreTests.CreatePopulatedSettings() with
            {
                RecentLanguages = ["ja", "en-gb"],
                Preferences = UserPreferences.Default with
                {
                    Dictation = DictationPreferences.Default with
                    {
                        WhisperLanguage = "ja",
                        EnglishSpelling = EnglishSpelling.British,
                    },
                },
            };

            Assert.True((await service.ExportAsync(original.ToPortableProfile(), exportedPath)).Succeeded);
            var imported = await service.ImportAsync(exportedPath);
            Assert.Equal(PortableProfileImportStatus.Imported, imported.Status);
            Assert.Equal("ja", imported.Profile?.Preferences.Dictation.WhisperLanguage);
            Assert.Equal(EnglishSpelling.British, imported.Profile?.Preferences.Dictation.EnglishSpelling);

            var root = JsonNode.Parse(await File.ReadAllTextAsync(exportedPath))!.AsObject();
            root["preferences"]!["dictation"]!["whisperLanguage"] = "German";
            var legacyPath = Path.Combine(directory, "legacy.enviouswispr.json");
            await File.WriteAllTextAsync(legacyPath, root.ToJsonString(JsonSettingsStore.SerializerOptions));
            var legacyImported = await service.ImportAsync(legacyPath);
            Assert.Equal(PortableProfileImportStatus.Imported, legacyImported.Status);
            Assert.Equal("de", legacyImported.Profile?.Preferences.Dictation.WhisperLanguage);
        });
    }
}
