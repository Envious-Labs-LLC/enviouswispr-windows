using System.Text.Json;
using System.Text.Json.Nodes;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;
using EnviousWispr.PostProcessing;
using EnviousWispr.Services.Settings;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// Casual mode: leaves off the full stop at the very end of each dictation. Ref: macOS #3737.
/// </summary>
public sealed class CasualEndingTests
{
    [Theory]
    [InlineData("Sounds good, see you at three.", "Sounds good, see you at three")]
    [InlineData("Hi. See you soon.", "Hi. See you soon")]
    [InlineData("Done.\n", "Done\n")]
    [InlineData("on my way. ", "on my way ")]
    [InlineData("The price is 3.5.", "The price is 3.5")]
    [InlineData("See you at 5.", "See you at 5")]
    [InlineData("**done**.", "**done**")]
    [InlineData("今天很好。", "今天很好")]
    [InlineData("यह ठीक है।", "यह ठीक है")]
    public void RemovesFinalFullStopAndPreservesTrailingWhitespace(string input, string expected)
    {
        var result = CasualEnding.RemoveFinalFullStop(input, spokenFinalFullStop: false, languageCode: "en");
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Are you free?")]
    [InlineData("That is great!")]
    [InlineData("Wait...")]
    [InlineData("Wait…")]
    [InlineData("Wait.。")]
    [InlineData("Wait.।")]
    [InlineData("Meet me at 9 a.m.")]
    [InlineData("I live in the U.S.")]
    [InlineData("Bring snacks, e.g.")]
    [InlineData("She has a Ph.D.")]
    [InlineData("(see above.)")]
    [InlineData("\"hi.\"")]
    [InlineData("Thanks. 👍")]
    [InlineData(".")]
    [InlineData("  .  ")]
    [InlineData("")]
    [InlineData("no ending")]
    public void KeepsEveryOtherEnding(string input)
    {
        var result = CasualEnding.RemoveFinalFullStop(input, spokenFinalFullStop: false, languageCode: "en");
        Assert.Equal(input, result);
    }

    [Theory]
    [InlineData("Apple Inc.", "Apple Inc")]
    [InlineData("Call Dr.", "Call Dr")]
    [InlineData("Bring snacks etc.", "Bring snacks etc")]
    public void IntentionalCasualRegisterRemovesSinglePieceAbbreviationDot(string input, string expected)
    {
        var result = CasualEnding.RemoveFinalFullStop(input, spokenFinalFullStop: false, languageCode: "en");
        Assert.Equal(expected, result);
    }

    [Fact]
    public void OrdinalDotOutsideEnglishIsPreserved()
    {
        Assert.Equal("Wir treffen uns am 3.", CasualEnding.RemoveFinalFullStop("Wir treffen uns am 3.", spokenFinalFullStop: false, "de"));
        Assert.Equal("Wir treffen uns am 3.", CasualEnding.RemoveFinalFullStop("Wir treffen uns am 3.", spokenFinalFullStop: false, "de-DE"));
        Assert.Equal("Wir treffen uns am 3.", CasualEnding.RemoveFinalFullStop("Wir treffen uns am 3.", spokenFinalFullStop: false, null));
        Assert.Equal("See you at 3", CasualEnding.RemoveFinalFullStop("See you at 3.", spokenFinalFullStop: false, "en-GB"));
        Assert.Equal("Bis morgen", CasualEnding.RemoveFinalFullStop("Bis morgen.", spokenFinalFullStop: false, "de"));
    }

    [Fact]
    public void SpokenFinalFullStopIsKept()
    {
        Assert.Equal("See you.", CasualEnding.RemoveFinalFullStop("See you.", spokenFinalFullStop: true, "en"));
        Assert.Equal("See you", CasualEnding.RemoveFinalFullStop("See you.", spokenFinalFullStop: false, "en"));
    }

    [Theory]
    [InlineData("see you period", true)]
    [InlineData("see you full stop", true)]
    [InlineData("see you period.", true)]
    [InlineData("see you period..", true)]
    [InlineData("see you Period", true)]
    [InlineData("see you period new paragraph", true)]
    [InlineData("see you period new line.", true)]
    [InlineData("see you comma okay period", true)]
    [InlineData("see you period?", true)]
    [InlineData("see you period!", true)]
    [InlineData("the grace period expires", false)]
    [InlineData("period, see you", false)]
    [InlineData("hello period?.", false)]
    [InlineData("hello period new line new paragraph.", false)]
    [InlineData("see you period comma", false)]
    [InlineData("see you.", false)]
    [InlineData("see you", false)]
    public void ItnSpokenFinalFullStopProvenanceMatchesContract(string input, bool expected)
    {
        var normalized = InverseTextNormalizer.Normalize(input, spokenPunctuation: true, out var spokenFinalFullStop);
        Assert.NotNull(normalized);
        Assert.Equal(expected, spokenFinalFullStop);
        Assert.Equal(expected, InverseTextNormalizer.HasSpokenFinalFullStop(input, spokenPunctuation: true));
    }

    [Fact]
    public void ItnSpokenFinalFullStopIsFalseWhenSpokenPunctuationIsOff()
    {
        var normalized = InverseTextNormalizer.Normalize("see you period", spokenPunctuation: false, out var spokenFinalFullStop);
        Assert.Equal("see you period", normalized);
        Assert.False(spokenFinalFullStop);
        Assert.False(InverseTextNormalizer.HasSpokenFinalFullStop("see you period", spokenPunctuation: false));
    }

    [Fact]
    public async Task PipelineCasualModeOffLeavesFinalFullStopIntact()
    {
        var pipeline = new DeterministicTextPipeline();
        var request = new DeterministicTextRequest(
            new Transcript(DictationSessionId.Create(), "See you tomorrow.", "whisper", DetectedLanguage: "en"),
            CustomWords: [],
            Options: new DeterministicTextOptions(
                WordCorrectionEnabled: false,
                FillerRemovalEnabled: false,
                EmojiFormatterEnabled: false,
                SpokenPunctuationEnabled: false,
                CasualModeEnabled: false));

        var result = await pipeline.ProcessAsync(request);
        Assert.Equal("See you tomorrow.", result.Output.Text);
        var casualReceipt = result.Receipts.Single(r => r.Stage == DeterministicTextStage.CasualEnding);
        Assert.Equal(DeterministicStageStatus.Skipped, casualReceipt.Status);
    }

    [Fact]
    public async Task PipelineCasualModeOnStripsDeterministicAndPolishedText()
    {
        var pipeline = new DeterministicTextPipeline();
        var request = new DeterministicTextRequest(
            new Transcript(DictationSessionId.Create(), "see you at three.", "whisper", DetectedLanguage: "en"),
            CustomWords: [],
            Options: new DeterministicTextOptions(
                WordCorrectionEnabled: false,
                FillerRemovalEnabled: false,
                EmojiFormatterEnabled: false,
                SpokenPunctuationEnabled: false,
                CasualModeEnabled: true));

        var result = await pipeline.ProcessAsync(request);
        Assert.Equal("see you at three", result.Output.Text);
        Assert.Equal("see you at three", result.DeterministicText);

        var polishedResult = await pipeline.ApplyPolishedTextAsync(request, result, "See you at three.");
        Assert.Equal("See you at three", polishedResult.Output.Text);
    }

    [Fact]
    public async Task PipelinePreservesSpokenFullStopOnBothOutputs()
    {
        var pipeline = new DeterministicTextPipeline();
        var request = new DeterministicTextRequest(
            new Transcript(DictationSessionId.Create(), "see you period", "whisper", DetectedLanguage: "en"),
            CustomWords: [],
            Options: new DeterministicTextOptions(
                WordCorrectionEnabled: false,
                FillerRemovalEnabled: false,
                EmojiFormatterEnabled: false,
                SpokenPunctuationEnabled: true,
                CasualModeEnabled: true));

        var result = await pipeline.ProcessAsync(request);
        Assert.Equal("see you.", result.Output.Text);
        Assert.True(result.SpokenFinalFullStop);

        var polishedResult = await pipeline.ApplyPolishedTextAsync(request, result, "See you.");
        Assert.Equal("See you.", polishedResult.Output.Text);
    }

    [Fact]
    public async Task PipelinePreservesSnippetEndingIntact()
    {
        var pipeline = new DeterministicTextPipeline();
        var sentinel = "EWSNIP0123456789abcdef0123456789abcdef";
        var request = new DeterministicTextRequest(
            new Transcript(DictationSessionId.Create(), $"Thanks {sentinel}", "whisper", DetectedLanguage: "en"),
            CustomWords: [],
            Options: new DeterministicTextOptions(
                WordCorrectionEnabled: false,
                FillerRemovalEnabled: false,
                EmojiFormatterEnabled: false,
                SpokenPunctuationEnabled: false,
                CasualModeEnabled: true));

        var result = await pipeline.ProcessAsync(request);
        // The sentinel is not a full stop, so it was not stripped
        Assert.Equal($"Thanks {sentinel}", result.Output.Text);

        // Later snippet resolution restores the snippet's ending full stop
        var records = new[]
        {
            new SnippetExpansionRecord(sentinel, "Best regards, Sam.", SuppressFollowingSentenceEnding: false),
        };
        var finalized = SnippetFinalizer.Resolve(result.DeterministicText, null, records);
        Assert.Equal("Thanks Best regards, Sam.", finalized.Text);
    }

    [Fact]
    public void CasualEndingIsTheLastStepInTheDefaultChain()
    {
        var steps = DeterministicTextPipeline.DefaultSteps();
        Assert.Equal(DeterministicTextStage.CasualEnding, steps[^1].Stage);
    }

    [Fact]
    public async Task TranscribeFileExplicitlyForcesCasualModeDisabled()
    {
        var pipeline = new DeterministicTextPipeline();
        // Even if options have CasualModeEnabled = true, TranscribeFile forces CasualModeEnabled = false
        var baseOptions = new DeterministicTextOptions(
            WordCorrectionEnabled: false,
            FillerRemovalEnabled: false,
            EmojiFormatterEnabled: false,
            SpokenPunctuationEnabled: false,
            CasualModeEnabled: true);

        var fileOptions = baseOptions with { CasualModeEnabled = false };
        var request = new DeterministicTextRequest(
            new Transcript(DictationSessionId.Create(), "Meeting recorded.", "whisper", DetectedLanguage: "en"),
            CustomWords: [],
            Options: fileOptions);

        var result = await pipeline.ProcessAsync(request);
        Assert.Equal("Meeting recorded.", result.Output.Text);
    }

    [Fact]
    public async Task JsonSettingsStoreRoundTripsCasualModeEnabled()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var path = Path.Combine(directory, "settings.json");
            var store = new JsonSettingsStore(path);
            var initial = AppSettings.Default with
            {
                Preferences = UserPreferences.Default with
                {
                    Dictation = DictationPreferences.Default with
                    {
                        CasualModeEnabled = true,
                    },
                },
            };

            await store.SaveAsync(initial);
            var loaded = await store.LoadAsync();

            Assert.Equal(SettingsLoadStatus.Loaded, loaded.Status);
            Assert.True(loaded.Settings.Preferences.Dictation.CasualModeEnabled);
        });
    }

    [Fact]
    public async Task JsonSettingsStoreMigratesV18SettingsToDefaultCasualModeFalse()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var path = Path.Combine(directory, "settings.json");
            var v18Settings = JsonSettingsStoreTests.CreatePopulatedSettings() with { SchemaVersion = 18 };
            var json = JsonSerializer.Serialize(v18Settings, JsonSettingsStore.SerializerOptions);
            var root = JsonNode.Parse(json)!.AsObject();
            Assert.True(root["preferences"]!["dictation"]!.AsObject().Remove("casualModeEnabled"));
            await File.WriteAllTextAsync(path, root.ToJsonString(JsonSettingsStore.SerializerOptions));

            var result = await new JsonSettingsStore(path).LoadAsync();

            Assert.Equal(SettingsLoadStatus.Migrated, result.Status);
            Assert.Equal(18, result.SourceSchemaVersion);
            Assert.Equal(AppSettings.CurrentSchemaVersion, result.Settings.SchemaVersion);
            Assert.False(result.Settings.Preferences.Dictation.CasualModeEnabled);
        });
    }

    [Fact]
    public void CasualEndingReceiptEmitsInMainAndRestorationPasses()
    {
        var logger = new RecordingLogger();
        var effects = new EnviousWispr.App.Composition.TranscriptFinalizationEffects(logger, null!);
        DeterministicStageReceipt[] receipts =
        [
            new(DeterministicTextStage.InverseTextNormalization, DeterministicStageStatus.Completed, false, 1),
            new(DeterministicTextStage.EnglishSpelling, DeterministicStageStatus.Completed, true, 1),
            new(DeterministicTextStage.CasualEnding, DeterministicStageStatus.Completed, true, 1),
            new(DeterministicTextStage.EnglishSpellingAfterPolish, DeterministicStageStatus.Completed, true, 1),
            new(DeterministicTextStage.EmojiRestoration, DeterministicStageStatus.Completed, false, 1),
        ];

        effects.EmitStageReceipts(receipts, emojiRestorationOnly: false);
        var main = logger.Entries.Select(entry => entry.Stage).ToArray();
        effects.EmitStageReceipts(receipts, emojiRestorationOnly: true);
        var polish = logger.Entries.Skip(main.Length).Select(entry => entry.Stage).ToArray();

        Assert.Equal([DeterministicTextStage.InverseTextNormalization, DeterministicTextStage.EnglishSpelling, DeterministicTextStage.CasualEnding], main);
        Assert.Equal([DeterministicTextStage.CasualEnding, DeterministicTextStage.EnglishSpellingAfterPolish, DeterministicTextStage.EmojiRestoration], polish);
    }

    [Fact]
    public async Task ParakeetUnknownLanguagePreservesNumericOrdinalFullStop()
    {
        var pipeline = new DeterministicTextPipeline();
        var transcript = new Transcript(
            DictationSessionId.Create(),
            "Wir treffen uns am 3.",
            "parakeet-v3",
            DetectedLanguage: null);
        var request = new DeterministicTextRequest(
            transcript,
            [],
            new DeterministicTextOptions(
                WordCorrectionEnabled: false,
                FillerRemovalEnabled: false,
                EmojiFormatterEnabled: false,
                SpokenPunctuationEnabled: false,
                EnglishSpelling: EnglishSpelling.American,
                CasualModeEnabled: true));

        var result = await pipeline.ProcessAsync(request);
        Assert.Equal("Wir treffen uns am 3.", result.Output.Text);
    }
}
