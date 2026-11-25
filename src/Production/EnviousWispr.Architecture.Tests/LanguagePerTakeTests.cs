using EnviousWispr.ASR;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;
using EnviousWispr.Services.Runtime;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EnviousWispr.Architecture.Tests;

/// <summary>A language changed in the running app reaches the next take and the next preview pass, with no restart (#241).</summary>
public sealed class LanguagePerTakeTests
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task EveryTakeCarriesTheLanguageAsItStandsAndAChangeBetweenTakesIsHonoured()
    {
        // THE REAL WORKER, ITS STUB ECHOING THE LANGUAGE THE REQUEST CARRIED: what crossed the process
        // boundary, not what the adapter meant to send.
        var current = "auto";
        await using var engine = new RuntimeWorkerTranscriptionEngine(
            new RuntimeWorkerSupervisor(WorkerPath(), ["--test-transcribe-stub"], maximumRestarts: 1),
            "test:isolated",
            RequestTimeout,
            RequestTimeout,
            language: () => current);
        Assert.True((await engine.StartAsync()).Succeeded);

        var told = new List<string?>();
        foreach (var language in new[] { "auto", "fr", "fr", "es", "auto" })
        {
            current = language;
            told.Add((await engine.TranscribeAsync(Audio())).RecognitionLanguage);
        }

        Assert.Equal(["auto", "fr", "fr", "es", "auto"], told);
    }

    [Fact]
    public async Task LivePreviewFollowsALanguageChangeBetweenPasses()
    {
        var current = "auto";
        using var arbiter = new RuntimeResourceArbiter();
        await using var preview = new RuntimeWorkerLivePreviewEngine(
            new RuntimeWorkerTranscriptionEngine(
                new RuntimeWorkerSupervisor(WorkerPath(), ["--test-transcribe-stub"], maximumRestarts: 1),
                "test:isolated",
                RequestTimeout,
                RequestTimeout,
                language: () => current),
            arbiter,
            RuntimeResourceKind.Cpu);
        Assert.True((await preview.StartAsync()).Succeeded);

        var before = await preview.PreviewAsync(Snapshot(), sequence: 0);
        current = "de";
        var after = await preview.PreviewAsync(Snapshot(), sequence: 1);
        await preview.StopAsync();

        Assert.True(before.Succeeded);
        Assert.Equal("auto", before.RecognitionLanguage);
        Assert.True(after.Succeeded);
        Assert.Equal("de", after.RecognitionLanguage);
    }

    [Fact]
    public void TheOptionsSendTheCurrentLanguageForBothWhisperAndParakeet()
    {
        var current = "auto";
        var whisper = RuntimeWorkerTranscriptionEngine.LanguageFor(Options(FinalAsrEngine.Whisper, "auto", () => current));
        var parakeet = RuntimeWorkerTranscriptionEngine.LanguageFor(Options(FinalAsrEngine.Parakeet, "auto", () => current));
        Assert.Equal("auto", whisper());
        Assert.Equal("auto", parakeet());
        current = "fr";
        Assert.Equal("fr", whisper());
        Assert.Equal("fr", parakeet());

        // NOTHING READS IT: the language the worker loaded with, every take.
        Assert.Equal("es", RuntimeWorkerTranscriptionEngine.LanguageFor(Options(FinalAsrEngine.Whisper, "es", null))());
        Assert.Equal("ru", RuntimeWorkerTranscriptionEngine.LanguageFor(Options(FinalAsrEngine.Parakeet, "ru", null))());
    }

    [Fact]
    public async Task TheFallbackEngineHandsTheTakesLanguageToWhicheverEngineRunsIt()
    {
        var primary = new LanguageEngine("primary", fail: true);
        var fallback = new LanguageEngine("fallback", fail: false);
        using var engine = new FallbackTranscriptionEngine(primary, () => fallback);

        var first = await engine.TranscribeAsync(Audio(), "fr");
        var second = await engine.TranscribeAsync(Audio(), "de");
        var unspecified = await engine.TranscribeAsync(Audio());

        Assert.Equal(["fr"], primary.Told);
        Assert.Equal(["fr", "de", null], fallback.Told);
        Assert.Equal("fr", first.RecognitionLanguage);
        Assert.Equal("de", second.RecognitionLanguage);
        Assert.Null(unspecified.RecognitionLanguage);
    }

    [Theory]
    [InlineData("auto", null, "auto")]
    [InlineData("fr", null, "fr")]
    [InlineData("fr", "", "fr")]
    [InlineData("fr", "xx", "fr")]
    [InlineData("es", "de-DE", "de")]
    [InlineData("de", "auto", "auto")]
    [InlineData("ja", "haw", "haw")]
    public void TheCurrentLanguageIsTheSettingUnlessAValidOverridePinsIt(
        string setting,
        string? environmentOverride,
        string expected) =>
        Assert.Equal(expected, WhisperLanguageCodes.Current(setting, environmentOverride));

    [Theory]
    [InlineData(null, null)]
    [InlineData(" ", null)]
    [InlineData("auto", DiagnosticRecognitionLanguage.Automatic)]
    [InlineData("en", DiagnosticRecognitionLanguage.English)]
    [InlineData("fr", DiagnosticRecognitionLanguage.French)]
    [InlineData("de", DiagnosticRecognitionLanguage.German)]
    [InlineData("es", DiagnosticRecognitionLanguage.Spanish)]
    [InlineData("ja", DiagnosticRecognitionLanguage.Other)]
    public void TheLogCarriesTheLanguageAsACategoryNeverACode(string? code, DiagnosticRecognitionLanguage? expected) =>
        Assert.Equal(expected, DiagnosticRecognitionLanguages.From(code));

    [Fact]
    public void EveryWhisperWorkerTheShellBuildsReadsTheLanguageAtEachTake()
    {
        // THE ROUTES ARE COUNTED FROM THE SOURCE, NOT REMEMBERED: the final engine on the card, the one on
        // the processor, and Live Preview. A Whisper worker built without the reader would keep the language
        // it loaded with until a restart, which is the defect.
        var app = Path.Combine(FindRepositoryRoot(), "src", "Production", "EnviousWispr.App");
        var creations = Directory.EnumerateFiles(app, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot()
                .DescendantNodes()
                .OfType<ObjectCreationExpressionSyntax>()
                .Where(creation => creation.Type.ToString() == "RuntimeWorkerTranscriptionOptions"))
            .Where(creation => creation.ArgumentList!.Arguments.Any(argument =>
                argument.NameColon?.Name.Identifier.ValueText == "Engine" &&
                argument.Expression.ToString() == "FinalAsrEngine.Whisper"))
            .ToArray();

        Assert.Equal(3, creations.Length);
        Assert.All(creations, creation => Assert.Contains(
            creation.ArgumentList!.Arguments,
            argument => argument.NameColon?.Name.Identifier.ValueText == "CurrentLanguage" &&
                argument.Expression.ToString() == "CurrentWhisperLanguage"));

        var parakeetCreations = Directory.EnumerateFiles(app, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot()
                .DescendantNodes()
                .OfType<ObjectCreationExpressionSyntax>()
                .Where(creation => creation.Type.ToString() == "RuntimeWorkerTranscriptionOptions"))
            .Where(creation => creation.ArgumentList!.Arguments.All(argument =>
                argument.NameColon?.Name.Identifier.ValueText != "Engine" ||
                argument.Expression.ToString() == "FinalAsrEngine.Parakeet"))
            .ToArray();

        Assert.Equal(2, parakeetCreations.Length);
        Assert.All(parakeetCreations, creation => Assert.Contains(
            creation.ArgumentList!.Arguments,
            argument => argument.NameColon?.Name.Identifier.ValueText == "CurrentLanguage" &&
                argument.Expression.ToString() == "CurrentWhisperLanguage"));

        var reader = File.ReadAllText(Path.Combine(app, "App.Language.cs"));
        Assert.Contains("_settings.Preferences.Dictation.WhisperLanguage", reader, StringComparison.Ordinal);

        var transcription = File.ReadAllText(Path.Combine(app, "App.Transcription.cs"));
        Assert.Contains("CreateParakeetPreviewOptions(", transcription, StringComparison.Ordinal);
        Assert.Contains("CurrentWhisperLanguage", transcription, StringComparison.Ordinal);
        var parakeetPreviewCall = CSharpSyntaxTree.ParseText(transcription).GetRoot()
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .FirstOrDefault(invocation => invocation.Expression.ToString().Contains("CreateParakeetPreviewOptions", StringComparison.Ordinal));
        Assert.NotNull(parakeetPreviewCall);
        Assert.Contains(
            parakeetPreviewCall!.ArgumentList.Arguments,
            argument => argument.Expression.ToString() == "CurrentWhisperLanguage");
    }

    [Fact]
    public async Task DeterministicCleanupUsesPinnedRecognitionLanguageWhenParakeetReportsNoDetectedLanguage()
    {
        var pipeline = new DeterministicTextPipeline();
        var germanTranscript = new Transcript(
            DictationSessionId.Create(),
            "er sagt ja.",
            ParakeetModelIds.Final + ":cpu",
            DetectedLanguage: null,
            RecognitionLanguage: "de");
        var options = new DeterministicTextOptions(
            WordCorrectionEnabled: false,
            FillerRemovalEnabled: true,
            EmojiFormatterEnabled: false,
            SpokenPunctuationEnabled: true,
            EnglishSpelling: EnglishSpelling.British,
            CasualModeEnabled: false);

        var result = await pipeline.ProcessAsync(
            new DeterministicTextRequest(
                germanTranscript,
                Array.Empty<CustomWordEntry>(),
                options));

        Assert.Equal("er sagt ja.", result.DeterministicText);
    }

    private static RuntimeWorkerTranscriptionOptions Options(
        FinalAsrEngine engine,
        string language,
        Func<string?>? current) => new(
        "worker.exe",
        "models",
        RuntimeProviderKind.Cpu,
        ParakeetModelPack.Quantized,
        IntraOpThreads: 1,
        Engine: engine,
        Language: language,
        CurrentLanguage: current);

    private static CapturedAudio Audio() => new(
        DictationSessionId.Create(),
        new float[1_600],
        16_000,
        Channels: 1);

    private static AudioSnapshot Snapshot() => new(
        DictationSessionId.Create(),
        new float[1_600],
        16_000,
        Channels: 1);

    private static string WorkerPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "EnviousWispr.RuntimeWorker.exe");
        Assert.True(File.Exists(path), $"Worker apphost missing: {path}");
        return path;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EnviousWispr.Windows.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }

    private sealed class LanguageEngine(string id, bool fail) : ILanguageSelectableTranscriptionEngine
    {
        public List<string?> Told { get; } = [];

        public string EngineId => id;

        public Task<Transcript> TranscribeAsync(CapturedAudio audio, CancellationToken cancellationToken = default) =>
            Answer(audio, null);

        public Task<Transcript> TranscribeAsync(CapturedAudio audio, string? language, CancellationToken cancellationToken = default) =>
            Answer(audio, language);

        private Task<Transcript> Answer(CapturedAudio audio, string? language)
        {
            Told.Add(language);
            if (fail)
            {
                throw new TranscriptionEngineException(new EnviousWispr.Core.Errors.AppError(
                    EnviousWispr.Core.Errors.AppErrorCode.RuntimeProviderUnavailable,
                    EnviousWispr.Core.Errors.AppErrorStage.FinalAsr,
                    CanRetry: true));
            }

            return Task.FromResult(new Transcript(audio.SessionId, string.Empty, id, RecognitionLanguage: language));
        }
    }
}
