using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Pipeline;
using EnviousWispr.PostProcessing;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// #3594: with Spoken punctuation on, the engine's own comma or period beside a mark the user speaks is
/// removed, and a command the engine wrote twice becomes one.
/// Ref: macOS RecognizerMarksTests.swift.
/// </summary>
public sealed class RecognizerMarksTests
{
    [Theory]
    [InlineData("Hello. Period", "Hello.")]
    [InlineData("hello period", "hello.")]
    [InlineData("period hello", ". Hello")]
    [InlineData("Mr. period", "Mr.")]
    [InlineData("3. period", "3.")]
    [InlineData("U.S. comma", "U.S.,")]
    [InlineData("Dr. question mark", "Dr.?")]
    [InlineData("e.g. comma", "e.g.,")]
    [InlineData("p.m. comma", "p.m.,")]
    [InlineData("end.) comma", "end),")]
    [InlineData("\"quote.\" period", "\"quote\".")]
    [InlineData("period.period", ".")]
    [InlineData("comma , comma", ",")]
    [InlineData("colon: colon", ":")]
    [InlineData("period. period. period.", ".")]
    [InlineData("period period", "..")]
    [InlineData("wait... period", "wait....")]
    [InlineData("example dot com period", "example.com.")]
    [InlineData("Meet John Jr. comma and Jane", "Meet John Jr., and Jane")]
    [InlineData("Use tools, etc. comma and supplies", "Use tools, etc., and supplies")]
    [InlineData("Ask J. comma next", "Ask J., next")]
    [InlineData("Maple Ave. comma", "Maple Ave.,")]
    [InlineData("Acme Inc. comma", "Acme Inc.,")]
    [InlineData("I said no. comma but", "I said no, but")]
    [InlineData("Tell Ed. comma now", "Tell Ed, now")]
    public void EnglishBoundaryRowsAbsorbRecognizerMarksBesideSpokenCommands(string input, string expected)
    {
        var result = InverseTextNormalizer.Normalize(input, spokenPunctuation: true);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Step4LiveParakeetTakeWithModelPunctuationNormalizesToSingleFullStop()
    {
        // Bug reported in UAT Step 4: Parakeet attaches model punctuation "Sounds good. See you tomorrow period."
        // Prior to absorption, ApplyPunctuation turned this into "Sounds good. See you tomorrow.."
        const string rawTake = "Sounds good. See you tomorrow period.";
        var normalized = InverseTextNormalizer.Normalize(rawTake, spokenPunctuation: true, out var spokenFinalFullStop);

        Assert.Equal("Sounds good. See you tomorrow.", normalized);
        Assert.True(spokenFinalFullStop);
    }

    [Fact]
    public void Step4LiveParakeetTakeWithPauseCommaAndDoubleDotNormalizesCleanly()
    {
        // Pause comma before spoken command: "Sounds good. See you tomorrow, period."
        var withComma = InverseTextNormalizer.Normalize("Sounds good. See you tomorrow, period.", spokenPunctuation: true, out var spoken1);
        Assert.Equal("Sounds good. See you tomorrow.", withComma);
        Assert.True(spoken1);

        // Trailing double dot: "Sounds good. See you tomorrow period.."
        var withDoubleDot = InverseTextNormalizer.Normalize("Sounds good. See you tomorrow period..", spokenPunctuation: true, out var spoken2);
        Assert.Equal("Sounds good. See you tomorrow.", withDoubleDot);
        Assert.True(spoken2);
    }

    [Fact]
    public async Task Step4DeterministicPipelineCasualModeKeepsSingleFullStopOnSpokenPeriodTake()
    {
        var pipeline = new DeterministicTextPipeline();
        var request = new DeterministicTextRequest(
            new Transcript(DictationSessionId.Create(), "Sounds good. See you tomorrow period.", "whisper", DetectedLanguage: "en"),
            CustomWords: [],
            Options: new DeterministicTextOptions(
                WordCorrectionEnabled: false,
                FillerRemovalEnabled: false,
                EmojiFormatterEnabled: false,
                SpokenPunctuationEnabled: true,
                CasualModeEnabled: true));

        var result = await pipeline.ProcessAsync(request);

        // In Casual Mode with spoken punctuation, user explicitly spoke "period", so the single period is preserved.
        Assert.Equal("Sounds good. See you tomorrow.", result.Output.Text);
        var casualReceipt = result.Receipts.Single(r => r.Stage == DeterministicTextStage.CasualEnding);
        Assert.Equal(DeterministicStageStatus.Completed, casualReceipt.Status);
        Assert.False(casualReceipt.Changed);
    }

    [Fact]
    public void RecognizerMarksAndPostProcessingUseSourceGeneratedRegexWithoutRuntimeCompiledOption()
    {
        InverseTextNormalizer.Warm();

        var assembly = typeof(InverseTextNormalizer).Assembly;
        var compiledOffenders = new List<string>();
        var generatedRecognizerRegexCount = 0;

        foreach (var type in assembly.GetTypes())
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            foreach (var field in type.GetFields(flags))
            {
                if (field.FieldType == typeof(Regex) && field.GetValue(null) is Regex regex)
                {
                    if ((regex.Options & RegexOptions.Compiled) != 0)
                    {
                        compiledOffenders.Add($"{type.Name}.{field.Name}");
                    }
                }
            }

            foreach (var method in type.GetMethods(flags))
            {
                if (method.ReturnType == typeof(Regex) && method.GetParameters().Length == 0)
                {
                    var regex = (Regex)method.Invoke(null, null)!;
                    if ((regex.Options & RegexOptions.Compiled) != 0)
                    {
                        compiledOffenders.Add($"{type.Name}.{method.Name}()");
                    }

                    if (type == typeof(InverseTextNormalizer))
                    {
                        generatedRecognizerRegexCount++;
                        Assert.NotEqual(Regex.InfiniteMatchTimeout, regex.MatchTimeout);
                        Assert.InRange(regex.MatchTimeout, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(2));
                    }
                }
            }
        }

        var patternsField = typeof(InverseTextNormalizer).GetField(
            "Patterns",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(patternsField);
        var patterns = (ConcurrentDictionary<(string Pattern, bool IgnoreCase), Regex>)patternsField.GetValue(null)!;
        Assert.NotEmpty(patterns);
        foreach (var pair in patterns)
        {
            if ((pair.Value.Options & RegexOptions.Compiled) != 0)
            {
                compiledOffenders.Add($"Patterns[{pair.Key.Pattern}]");
            }
        }

        Assert.True(
            generatedRecognizerRegexCount >= 9,
            $"Expected all recognizer-mark patterns on InverseTextNormalizer to be [GeneratedRegex] methods, found {generatedRecognizerRegexCount}.");
        Assert.Empty(compiledOffenders);
    }

    [Fact]
    public async Task WarmAndRecognizerMarkReconciliationSucceedUnderConcurrentCallsAndUsePublicationOnlyLazy()
    {
        var sourcePath = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "EnviousWispr.PostProcessing",
            "InverseTextNormalizer.cs");
        if (File.Exists(sourcePath))
        {
            var source = await File.ReadAllTextAsync(sourcePath);
            Assert.Contains("LazyThreadSafetyMode.PublicationOnly", source, StringComparison.Ordinal);
        }

        var tasks = Enumerable.Range(0, 16).Select(index => Task.Run(() =>
        {
            InverseTextNormalizer.Warm();
            var reconciled = InverseTextNormalizer.ReconcileEnglishRecognizerMarks(
                " Mr. comma J. comma U.S. comma hello. comma, comma period. period new line. wait, new paragraph. ");
            Assert.Equal(
                " Mr. comma J. comma U.S. comma hello comma period new line wait, new paragraph ",
                reconciled);
            var absorbed = InverseTextNormalizer.AbsorbRecognizerMarks(
                " Sounds good. See you tomorrow, period.. ");
            Assert.Equal(" Sounds good. See you tomorrow period ", absorbed);
            var normalized = InverseTextNormalizer.Normalize(
                $"Sounds good. See you tomorrow, period.. Take {index + 1} is ready, comma thanks.",
                spokenPunctuation: true,
                out var spokenFinalFullStop);
            Assert.Equal($"Sounds good. See you tomorrow. Take {index + 1} is ready, thanks.", normalized);
            Assert.False(spokenFinalFullStop);
        }));

        await Task.WhenAll(tasks);
    }
}
