using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace EnviousWispr.ASR;

public sealed partial class ParakeetTranscriptionEngine : ILanguageSelectableTranscriptionEngine, IDisposable
{
    public const string ModelId = ParakeetModelIds.Final;
    public const int RequiredSampleRate = 16_000;

    // The bundled nemo128 preprocessor reflects 256 samples on each side. Reflection needs
    // more input samples than that pad; this is not a minimum duration for recognizing speech.
    // The artifact digest is pinned by ParakeetInputContractTests so an update revisits this rule.
    internal const int MinimumPreprocessorInputSamples = 257;

    private const string PreprocessorFileName = "nemo128.onnx";
    private const string VocabularyFileName = "vocab.txt";
    private const string FullPrecisionEncoderFileName = "encoder-model.onnx";
    private const string FullPrecisionEncoderDataFileName = "encoder-model.onnx.data";
    private const string QuantizedEncoderFileName = "encoder-model.int8.onnx";
    private const string FullPrecisionDecoderFileName = "decoder_joint-model.onnx";
    private const string QuantizedDecoderFileName = "decoder_joint-model.int8.onnx";

    private const double MinimumVoicedFrameRms = 0.002;
    private const double StrongContinuousSpeechFrameRms = 0.015;
    private const int MinimumVoicedFrames = 8;
    private const int MinimumSustainedSegmentVoicedFrames = 4;
    private const int VoicedTrimPaddingSamples = 1_600;

    private static readonly string[] QuantizedRequiredFiles =
    [
        PreprocessorFileName,
        VocabularyFileName,
        QuantizedEncoderFileName,
        QuantizedDecoderFileName,
    ];

    private static readonly string[] FullPrecisionEncoderRequiredFiles =
    [
        FullPrecisionEncoderFileName,
        FullPrecisionEncoderDataFileName,
    ];

    private static readonly string[] PreprocessorOutputs = ["features", "features_lens"];
    private static readonly string[] EncoderOutputs = ["outputs", "encoded_lengths"];
    private static readonly string[] DecoderOutputs = ["outputs", "output_states_1", "output_states_2"];
    [GeneratedRegex(
        @"[ \t]+([.,!?;:\)\]\}\u2019\u201D]|'(?:s|t|re|ve|ll|d|m)\b)",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 500)]
    private static partial Regex SpaceBeforeClosingPunctuationPattern();

    [GeneratedRegex(@"[ \t]+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 500)]
    private static partial Regex HorizontalWhitespacePattern();

    private static readonly HashSet<string> LowercaseProtectedAbbreviations = new(StringComparer.Ordinal)
    {
        "mr", "mrs", "ms", "dr", "prof", "jr", "sr", "st", "ave", "blvd", "dept", "apt",
        "vs", "eg", "ie", "etc", "inc", "ltd", "fig", "vol", "no", "est", "approx",
    };

    private static readonly HashSet<string> SentenceStarterRightWords = new(StringComparer.Ordinal)
    {
        "I", "I'm", "I\u2019m", "I've", "I\u2019ve", "I'll", "I\u2019ll", "I'd", "I\u2019d",
        "You", "You're", "You\u2019re", "You've", "You\u2019ve", "You'll", "You\u2019ll", "You'd", "You\u2019d",
        "He", "He's", "He\u2019s", "He'll", "He\u2019ll", "He'd", "He\u2019d",
        "She", "She's", "She\u2019s", "She'll", "She\u2019ll", "She'd", "She\u2019d",
        "It", "It's", "It\u2019s", "It'll", "It\u2019ll", "It'd", "It\u2019d",
        "We", "We're", "We\u2019re", "We've", "We\u2019ve", "We'll", "We\u2019ll", "We'd", "We\u2019d",
        "They", "They're", "They\u2019re", "They've", "They\u2019ve", "They'll", "They\u2019ll", "They'd", "They\u2019d",
        "Is", "Isn't", "Isn\u2019t", "Are", "Aren't", "Aren\u2019t", "Was", "Wasn't", "Wasn\u2019t",
        "Were", "Weren't", "Weren\u2019t", "Am",
        "Do", "Don't", "Don\u2019t", "Does", "Doesn't", "Doesn\u2019t", "Did", "Didn't", "Didn\u2019t",
        "Can", "Can't", "Can\u2019t", "Cannot", "Could", "Couldn't", "Couldn\u2019t", "Could've", "Could\u2019ve",
        "Will", "Won't", "Won\u2019t", "Would", "Wouldn't", "Wouldn\u2019t", "Would've", "Would\u2019ve",
        "Should", "Shouldn't", "Shouldn\u2019t", "Should've", "Should\u2019ve", "Shall",
        "May", "Might", "Must", "Mustn't", "Mustn\u2019t",
        "Have", "Haven't", "Haven\u2019t", "Has", "Hasn't", "Hasn\u2019t", "Had", "Hadn't", "Hadn\u2019t",
        "The", "This", "That", "That's", "That\u2019s", "These", "Those",
        "There", "There's", "There\u2019s", "Here", "Here's", "Here\u2019s",
        "What", "What's", "What\u2019s", "When", "Where", "Where's", "Where\u2019s",
        "Why", "How", "How's", "How\u2019s", "Who", "Who's", "Who\u2019s", "Which",
        "If", "But", "And", "Or", "So", "Because", "Although", "Unless", "Until", "While",
        "However", "Actually", "Anyway", "Also", "Then", "Now", "Please", "Thanks", "Sorry",
        "Yes", "Yeah", "No", "Well", "Sure", "Okay", "Let's", "Let\u2019s",
        "My", "Your", "Our", "Their", "His", "Her", "Its",
    };

    private readonly IParakeetBackend _backend;
    private readonly ParakeetVocabulary _vocabulary;
    private readonly ParakeetTdtRecipe _recipe;
    private readonly SemaphoreSlim _transcriptionGate = new(1, 1);
    private bool _disposed;

    public ParakeetTranscriptionEngine(ParakeetEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        Provider = options.Provider;
        ModelPack = options.ModelPack;
        EngineId = $"{ModelId}:{Provider.ToString().ToLowerInvariant()}";

        ConfigureCudaRuntime(options);
        var (backend, vocabulary) = OnnxParakeetBackend.Create(options);
        _backend = backend;
        _vocabulary = vocabulary;
        _recipe = new ParakeetTdtRecipe(backend, vocabulary, options.MaximumTokensPerStep);
    }

    internal ParakeetTranscriptionEngine(
        ParakeetEngineOptions options,
        IParakeetBackend backend,
        ParakeetVocabulary vocabulary)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(vocabulary);
        ValidateOptions(options);
        Provider = options.Provider;
        ModelPack = options.ModelPack;
        EngineId = $"{ModelId}:{Provider.ToString().ToLowerInvariant()}";
        _backend = backend;
        _vocabulary = vocabulary;
        _recipe = new ParakeetTdtRecipe(backend, vocabulary, options.MaximumTokensPerStep);
    }

    public string EngineId { get; }

    public RuntimeProviderKind Provider { get; }

    public ParakeetModelPack ModelPack { get; }

    public static bool HasQuantizedModelPack(string modelDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
        return AllNonEmpty(Path.GetFullPath(modelDirectory), QuantizedRequiredFiles);
    }

    public static bool HasFullPrecisionEncoder(string modelDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
        return AllNonEmpty(Path.GetFullPath(modelDirectory), FullPrecisionEncoderRequiredFiles);
    }

    public Task<Transcript> TranscribeAsync(
        CapturedAudio audio,
        CancellationToken cancellationToken = default) =>
        TranscribeWithLanguageAsync(
            audio,
            allowedTokenMask: null,
            blockedTokenMask: null,
            recognitionLanguage: null,
            cancellationToken);

    /// <summary>Transcribes audio while optionally pinning TDT token selection to the requested European writing script.</summary>
    /// <remarks>
    /// SCRIPT PINNING WITHOUT LANGUAGE REPORTING. When one of Parakeet's 25 European languages is requested,
    /// non-blank decoder steps whose top-1 token belongs to a different writing script fall back to the
    /// highest-logit script-compatible token; unsupported or "auto" requests run unpinned, and DetectedLanguage
    /// always stays null so a forced hint is never reported as detected speech.
    /// </remarks>
    public Task<Transcript> TranscribeAsync(
        CapturedAudio audio,
        string? language,
        CancellationToken cancellationToken = default)
    {
        var effectiveLanguage = LanguageCatalog.EffectiveRecognitionCode(FinalAsrEngine.Parakeet, language);
        var script = TokenLanguageFilter.ScriptForLanguage(effectiveLanguage);
        return TranscribeWithLanguageAsync(
            audio,
            _vocabulary.MaskFor(script),
            _vocabulary.FrenchBlocklistMaskFor(effectiveLanguage),
            effectiveLanguage,
            cancellationToken);
    }

    private async Task<Transcript> TranscribeWithLanguageAsync(
        CapturedAudio audio,
        bool[]? allowedTokenMask,
        bool[]? blockedTokenMask,
        string? recognitionLanguage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (audio.Outcome == AudioCaptureOutcome.Cancelled)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (audio.SampleRate != RequiredSampleRate || audio.Channels != 1)
        {
            throw CreateFailure(AppErrorCode.AudioFormatUnsupported);
        }

        if (audio.Samples.IsEmpty)
        {
            return new Transcript(
                audio.SessionId,
                string.Empty,
                EngineId,
                [],
                DetectedLanguage: null,
                RecognitionLanguage: recognitionLanguage);
        }

        ValidateInputLength(audio.Samples.Length, cancellationToken);
        await _transcriptionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(
                () => TranscribeCore(audio, allowedTokenMask, blockedTokenMask, recognitionLanguage, cancellationToken),
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _transcriptionGate.Release();
        }
    }

    internal static void ValidateInputLength(int sampleCount, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(sampleCount);
        if (sampleCount is > 0 and < MinimumPreprocessorInputSamples)
        {
            // The same input cannot be retried. A streaming caller may instead submit the whole
            // recording. No native work has been admitted when this particular error is returned.
            throw new TranscriptionEngineException(new AppError(
                AppErrorCode.TranscriptionInputTooShort,
                AppErrorStage.FinalAsr,
                CanRetry: false));
        }
    }

    internal static VoicedSpeechAnalysis AnalyzeVoicedSpeech(ReadOnlySpan<float> samples)
    {
        var frameLength = RequiredSampleRate * SpeechSegmenter.FrameMilliseconds / 1000;
        if (frameLength <= 0 || samples.Length < frameLength)
        {
            return new VoicedSpeechAnalysis(false, 0, samples.Length);
        }

        var frameCount = samples.Length / frameLength;
        var frameRms = new double[frameCount];
        var voicedFrames = 0;
        for (var frame = 0; frame < frameCount; frame++)
        {
            var rms = RootMeanSquare(samples.Slice(frame * frameLength, frameLength));
            frameRms[frame] = rms;
            if (rms >= MinimumVoicedFrameRms)
            {
                voicedFrames++;
            }
        }

        if (voicedFrames < MinimumVoicedFrames)
        {
            return new VoicedSpeechAnalysis(false, 0, samples.Length);
        }

        var segments = new SpeechSegmenter(RequiredSampleRate, TimeSpan.FromMilliseconds(200)).Segment(samples);
        SpeechSegment? firstSpeech = null;
        SpeechSegment? lastSpeech = null;
        foreach (var segment in segments)
        {
            if (!segment.IsSpeech)
            {
                continue;
            }

            var startFrame = Math.Clamp(segment.StartSample / frameLength, 0, frameCount);
            var endFrame = Math.Clamp(segment.EndSample / frameLength, startFrame, frameCount);
            var segmentVoicedFrames = 0;
            for (var frame = startFrame; frame < endFrame; frame++)
            {
                if (frameRms[frame] >= MinimumVoicedFrameRms)
                {
                    segmentVoicedFrames++;
                }
            }

            // Ignore brief hotkey or microphone clicks (<80 ms voiced) so a leading or trailing
            // transient cannot pin the trim window to the full recording or masquerade as speech.
            if (segmentVoicedFrames < MinimumSustainedSegmentVoicedFrames)
            {
                continue;
            }

            firstSpeech ??= segment;
            lastSpeech = segment;
        }

        if (firstSpeech is { } first && lastSpeech is { } last)
        {
            var trimStart = Math.Max(0, first.StartSample - VoicedTrimPaddingSamples);
            var trimEnd = Math.Min(samples.Length, last.EndSample + VoicedTrimPaddingSamples);
            return new VoicedSpeechAnalysis(true, trimStart, trimEnd - trimStart);
        }

        int? firstStrongRunStart = null;
        var lastStrongRunEnd = 0;
        var currentStrongRunStart = 0;
        var currentStrongRunLength = 0;
        for (var frame = 0; frame < frameCount; frame++)
        {
            if (frameRms[frame] >= StrongContinuousSpeechFrameRms)
            {
                if (currentStrongRunLength == 0)
                {
                    currentStrongRunStart = frame;
                }

                currentStrongRunLength++;
                if (currentStrongRunLength >= MinimumSustainedSegmentVoicedFrames)
                {
                    firstStrongRunStart ??= currentStrongRunStart;
                    lastStrongRunEnd = frame + 1;
                }
            }
            else
            {
                currentStrongRunLength = 0;
            }
        }

        if (firstStrongRunStart is { } strongStartFrame)
        {
            var rawStart = strongStartFrame * frameLength;
            var rawEnd = lastStrongRunEnd == frameCount ? samples.Length : lastStrongRunEnd * frameLength;
            var trimStart = Math.Max(0, rawStart - VoicedTrimPaddingSamples);
            var trimEnd = Math.Min(samples.Length, rawEnd + VoicedTrimPaddingSamples);
            return new VoicedSpeechAnalysis(true, trimStart, trimEnd - trimStart);
        }

        return new VoicedSpeechAnalysis(false, 0, samples.Length);
    }

    private Transcript TranscribeCore(
        CapturedAudio audio,
        bool[]? allowedTokenMask,
        bool[]? blockedTokenMask,
        string? recognitionLanguage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var runContext = _backend.CreateRunContext(cancellationToken);

        var samples = MemoryMarshal.TryGetArray(audio.Samples, out var backing) &&
            backing.Array is { } array &&
            backing.Offset == 0 &&
            array.Length == audio.Samples.Length
            ? array
            : audio.Samples.ToArray();

        var recipeResult = _recipe.Transcribe(
            samples,
            allowedTokenMask,
            blockedTokenMask,
            runContext,
            cancellationToken,
            Provider == RuntimeProviderKind.Cpu && ModelPack == ParakeetModelPack.Quantized
                ? RescueSinglePassBlankOutput
                : null);

        var text = _recipe.TokensToText(recipeResult.Tokens);
        var fullDuration = TimeSpan.FromSeconds(samples.Length / (double)RequiredSampleRate);
        var timings = ParakeetTdtRecipe.BuildTimings(
            recipeResult.Tokens,
            _vocabulary.Tokens,
            fullDuration,
            recipeResult.StartSampleOffset);
        return new Transcript(
            audio.SessionId,
            text,
            EngineId,
            timings,
            DetectedLanguage: null,
            RecognitionLanguage: recognitionLanguage);
    }

    // The bundled istupakov INT8 encoder uses per-tensor DynamicQuantizeLinear + ConvInteger across
    // FastConformer layers, which can collapse all TDT steps to <blk> on voiced utterances framed
    // by silence or activation outliers (#268). When voiced speech produced zero tokens on CPU INT8,
    // rescue first with the QDQ-free FP32 encoder on the already-computed mel features when present,
    // or retry once on the silence-trimmed voiced span when only the INT8 pack is installed.
    private ParakeetTdtRecipe.SinglePassRescueResult RescueSinglePassBlankOutput(
        float[] originalSamples,
        float[] primaryFeatures,
        long primaryFeatureLength,
        bool[]? allowedTokenMask,
        bool[]? blockedTokenMask,
        IParakeetRunContext runContext,
        CancellationToken cancellationToken)
    {
        var speech = AnalyzeVoicedSpeech(originalSamples);
        if (!speech.HasVoicedSpeech)
        {
            return new ParakeetTdtRecipe.SinglePassRescueResult([], 0);
        }

        var alignedSampleCount = originalSamples.Length % ParakeetTdtRecipe.FrameSamples == 0
            ? originalSamples.Length
            : originalSamples.Length + ParakeetTdtRecipe.FrameSamples - (originalSamples.Length % ParakeetTdtRecipe.FrameSamples);

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (_backend.TryEncodeWithFullPrecisionFallback(
                    primaryFeatures,
                    primaryFeatureLength,
                    runContext,
                    cancellationToken,
                    out var fp32EncodedRaw,
                    out var fp32EncodedLength))
            {
                var fp32Decoded = _recipe.DecodeEncoded(
                    fp32EncodedRaw,
                    fp32EncodedLength,
                    realSampleCount: alignedSampleCount,
                    contextSamples: 0,
                    offsetSample: 0,
                    isLast: true,
                    padToWindow: false,
                    allowedTokenMask,
                    blockedTokenMask,
                    runContext,
                    cancellationToken);
                if (fp32Decoded.Count > 0)
                {
                    return new ParakeetTdtRecipe.SinglePassRescueResult(fp32Decoded, StartSampleOffset: 0);
                }
            }
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        catch (Exception exception) when (
            exception is not (OperationCanceledException or StackOverflowException or OutOfMemoryException))
        {
            // Rescue is best-effort: a faulty optional sidecar or decode step must not turn a
            // completed zero-token pass into a failed dictation, and trimmed retry can still run.
        }

        if (speech.TrimmedLength >= MinimumPreprocessorInputSamples &&
            speech.TrimmedLength < originalSamples.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var trimmedSamples = originalSamples.AsSpan(speech.TrimmedStartSample, speech.TrimmedLength).ToArray();
                var (trimmedFeatures, trimmedFeatureLength) = _backend.Preprocess(
                    trimmedSamples,
                    trimmedSamples.Length,
                    runContext,
                    cancellationToken);
                var (trimmedEncodedRaw, trimmedEncodedLength) = _backend.Encode(
                    trimmedFeatures,
                    trimmedFeatureLength,
                    runContext,
                    cancellationToken);
                var trimmedDecoded = _recipe.DecodeEncoded(
                    trimmedEncodedRaw,
                    trimmedEncodedLength,
                    realSampleCount: trimmedSamples.Length,
                    contextSamples: 0,
                    offsetSample: 0,
                    isLast: true,
                    padToWindow: false,
                    allowedTokenMask,
                    blockedTokenMask,
                    runContext,
                    cancellationToken);
                if (trimmedDecoded.Count > 0)
                {
                    return new ParakeetTdtRecipe.SinglePassRescueResult(
                        trimmedDecoded,
                        StartSampleOffset: speech.TrimmedStartSample);
                }
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
            catch (Exception exception) when (
                exception is not (OperationCanceledException or StackOverflowException or OutOfMemoryException))
            {
                // Leave the original empty transcript intact when the best-effort retry fails.
            }
        }

        return new ParakeetTdtRecipe.SinglePassRescueResult([], 0);
    }

    internal static string NormalizeDecodedTokenText(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return string.Empty;
        }

        var strippedBeforeClosing = SpaceBeforeClosingPunctuationPattern().Replace(rawText, "$1");
        var collapsed = HorizontalWhitespacePattern().Replace(strippedBeforeClosing, " ").Trim();
        return RepairPostPeriodBoundarySpacing(collapsed);
    }

    internal static string RepairPostPeriodBoundarySpacing(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('.') < 0)
        {
            return text;
        }

        StringBuilder? repaired = null;
        var lastCopied = 0;

        for (var i = 1; i < text.Length - 1; i++)
        {
            if (text[i] != '.' || !ShouldInsertSpaceAfterPeriod(text, i))
            {
                continue;
            }

            repaired ??= new StringBuilder(text.Length + 8);
            repaired.Append(text, lastCopied, i + 1 - lastCopied);
            repaired.Append(' ');
            lastCopied = i + 1;
        }

        if (repaired is null)
        {
            return text;
        }

        repaired.Append(text, lastCopied, text.Length - lastCopied);
        return repaired.ToString();
    }

    private static bool ShouldInsertSpaceAfterPeriod(string text, int dotIndex)
    {
        if (text[dotIndex - 1] == '.' || text[dotIndex + 1] == '.' || !char.IsUpper(text[dotIndex + 1]))
        {
            return false;
        }

        var tokenStart = dotIndex;
        while (tokenStart > 0 && !char.IsWhiteSpace(text[tokenStart - 1]))
        {
            tokenStart--;
        }

        var tokenEnd = dotIndex + 1;
        while (tokenEnd < text.Length && !char.IsWhiteSpace(text[tokenEnd]))
        {
            tokenEnd++;
        }

        var token = text.AsSpan(tokenStart, tokenEnd - tokenStart);
        if (token.Contains('@') ||
            token.Contains('/') ||
            token.Contains('\\') ||
            token.Contains('_') ||
            token.IndexOf("://", StringComparison.Ordinal) >= 0)
        {
            return false;
        }

        // Validate left word directly before the period: allow leading opening quotes/parens only,
        // no preceding dots/digits/uppercase (protects PascalCase, camelCase, versions, dotted initials).
        var leftStart = dotIndex;
        var leftLetters = 0;
        while (leftStart > tokenStart)
        {
            var prev = text[leftStart - 1];
            if (char.IsLetter(prev))
            {
                if (!char.IsLower(prev))
                {
                    return false;
                }

                leftLetters++;
                leftStart--;
            }
            else if (prev is '\'' or '\u2019' && leftStart - 1 > tokenStart && char.IsLetter(text[leftStart - 2]))
            {
                leftStart--;
            }
            else
            {
                break;
            }
        }

        if (leftLetters < 2)
        {
            return false;
        }

        for (var k = tokenStart; k < leftStart; k++)
        {
            if (text[k] is not ('(' or '[' or '{' or '"' or '\'' or '\u201C' or '\u2018'))
            {
                return false;
            }
        }

        var leftWord = text[leftStart..dotIndex];
        if (LowercaseProtectedAbbreviations.Contains(leftWord))
        {
            return false;
        }

        // Validate right word: initial uppercase letter followed by lowercase word (or contraction like I'm),
        // not PascalCase/ALLCAPS/chain/method invocation (e.g. WriteLine, IO, U.S.A, Run()), and only split
        // high-confidence English sentence starters so dictated member accesses like "user.Name" remain intact.
        var wordEnd = dotIndex + 2;
        while (wordEnd < tokenEnd && char.IsLetter(text[wordEnd]))
        {
            if (char.IsUpper(text[wordEnd]))
            {
                return false;
            }

            wordEnd++;
        }

        if (wordEnd < tokenEnd)
        {
            var next = text[wordEnd];
            if (next is '.' or '(' or '[' or '<' or '_' || char.IsDigit(next))
            {
                return false;
            }

            if (next is '\'' or '\u2019')
            {
                var afterApostrophe = wordEnd + 1;
                if (afterApostrophe >= tokenEnd || !char.IsLetter(text[afterApostrophe]))
                {
                    return false;
                }

                while (afterApostrophe < tokenEnd && char.IsLetter(text[afterApostrophe]))
                {
                    if (char.IsUpper(text[afterApostrophe]))
                    {
                        return false;
                    }

                    afterApostrophe++;
                }

                if (afterApostrophe < tokenEnd && text[afterApostrophe] is '.' or '(' or '[' or '<' or '_' ||
                    (afterApostrophe < tokenEnd && char.IsDigit(text[afterApostrophe])))
                {
                    return false;
                }

                wordEnd = afterApostrophe;
            }
        }

        var rightWord = text[(dotIndex + 1)..wordEnd];
        return SentenceStarterRightWords.Contains(rightWord);
    }

    private static double RootMeanSquare(ReadOnlySpan<float> frame)
    {
        double sumSquares = 0;
        foreach (var sample in frame)
        {
            sumSquares += (double)sample * sample;
        }

        return Math.Sqrt(sumSquares / frame.Length);
    }

    private static bool AllNonEmpty(string directory, IEnumerable<string> fileNames) =>
        fileNames.All(fileName =>
        {
            var path = Path.Combine(directory, fileName);
            return File.Exists(path) && new FileInfo(path).Length > 0;
        });

    private static SessionOptions CreateSessionOptions(ParakeetEngineOptions options)
    {
        var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = options.IntraOpThreads,
            InterOpNumThreads = options.InterOpThreads,
        };
        if (options.Provider == RuntimeProviderKind.Cuda)
        {
            sessionOptions.AppendExecutionProvider_CUDA(options.CudaDeviceId);
        }

        return sessionOptions;
    }

    private static void ConfigureCudaRuntime(ParakeetEngineOptions options)
    {
        if (options.Provider != RuntimeProviderKind.Cuda ||
            string.IsNullOrWhiteSpace(options.CudaRuntimeDirectory))
        {
            return;
        }

        // THE SAME MECHANISM THE WORKER USES, NOT ENVIRONMENT VARIABLES, which a packaged process never searches for a DLL.
        // Kept here as well so a harness that runs this engine in its own process gets the same search.
        if (NativeRuntimeSearchPath.Configure(options.CudaRuntimeDirectory) != NativeRuntimeSearchPathOutcome.Added)
        {
            throw CreateFailure(AppErrorCode.RuntimeProviderUnavailable);
        }
    }

    private static void ValidateOptions(ParakeetEngineOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ModelDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.IntraOpThreads, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.InterOpThreads, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumTokensPerStep, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(options.CudaDeviceId);
        if (options.Provider == RuntimeProviderKind.DirectMl)
        {
            throw CreateFailure(AppErrorCode.RuntimeProviderIncompatible);
        }

        if (options.Provider == RuntimeProviderKind.Cuda &&
            options.ModelPack != ParakeetModelPack.FullPrecision)
        {
            throw CreateFailure(AppErrorCode.RuntimeProviderIncompatible);
        }
    }

    private static bool IsModelOrRuntimeFailure(Exception exception) =>
        exception is OnnxRuntimeException or IOException or UnauthorizedAccessException or
            KeyNotFoundException or InvalidDataException;

    internal static TranscriptionEngineException CreateFailure(
        AppErrorCode code,
        Exception? innerException = null) =>
        CreateFailure(code, AppErrorStage.FinalAsr, innerException);

    internal static TranscriptionEngineException CreateFailure(
        AppErrorCode code,
        AppErrorStage stage,
        Exception? innerException = null) => new(
        new AppError(code, stage, CanRetry: true),
        innerException);

    internal static TranscriptionEngineException CreateStageFailure(
        ParakeetOnnxStage stage,
        Exception? innerException = null) =>
        stage switch
        {
            ParakeetOnnxStage.Preprocessor => CreateFailure(
                AppErrorCode.TranscriptionPreprocessorFailed,
                AppErrorStage.AsrPreprocessor,
                innerException),
            ParakeetOnnxStage.Encoder => CreateFailure(
                AppErrorCode.TranscriptionEncoderFailed,
                AppErrorStage.AsrEncoder,
                innerException),
            ParakeetOnnxStage.Decoder => CreateFailure(
                AppErrorCode.TranscriptionDecoderFailed,
                AppErrorStage.AsrDecoder,
                innerException),
            _ => CreateFailure(AppErrorCode.TranscriptionFailed, AppErrorStage.FinalAsr, innerException),
        };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _backend.Dispose();
        _transcriptionGate.Dispose();
        GC.SuppressFinalize(this);
    }

    internal readonly record struct VoicedSpeechAnalysis(
        bool HasVoicedSpeech,
        int TrimmedStartSample,
        int TrimmedLength);

    internal readonly record struct ParakeetDecoderStepResult(
        int Token,
        int Step);

    internal interface IParakeetRunContext : IDisposable;

    internal interface IParakeetBackend : IDisposable
    {
        int EncoderDimension { get; }

        int StateLeadingDimension { get; }

        int StateDimension { get; }

        IParakeetRunContext CreateRunContext(CancellationToken cancellationToken);

        (float[] Features, long FeatureLength) Preprocess(
            float[] samples,
            IParakeetRunContext runContext,
            CancellationToken cancellationToken);

        (float[] Features, long FeatureLength) Preprocess(
            float[] samples,
            int validSampleCount,
            IParakeetRunContext runContext,
            CancellationToken cancellationToken) =>
            Preprocess(samples, runContext, cancellationToken);

        (float[] EncodedChannelFirst, int EncodedLength) Encode(
            float[] features,
            long featureLength,
            IParakeetRunContext runContext,
            CancellationToken cancellationToken);

        bool TryEncodeWithFullPrecisionFallback(
            float[] features,
            long featureLength,
            IParakeetRunContext runContext,
            CancellationToken cancellationToken,
            out float[] encodedChannelFirst,
            out int encodedLength);

        ParakeetDecoderStepResult DecodeStep(
            float[] encoderFrame,
            int[] target,
            int[] targetLength,
            float[] state1,
            float[] state2,
            int vocabularySize,
            int blankIndex,
            IParakeetRunContext runContext,
            CancellationToken cancellationToken);

        ParakeetDecoderStepResult DecodeStep(
            float[] encoderFrame,
            int[] target,
            int[] targetLength,
            float[] state1,
            float[] state2,
            int vocabularySize,
            int blankIndex,
            bool[]? allowedTokenMask,
            IParakeetRunContext runContext,
            CancellationToken cancellationToken) =>
            DecodeStep(
                encoderFrame,
                target,
                targetLength,
                state1,
                state2,
                vocabularySize,
                blankIndex,
                runContext,
                cancellationToken);

        ParakeetDecoderStepResult DecodeStep(
            float[] encoderFrame,
            int[] target,
            int[] targetLength,
            float[] state1,
            float[] state2,
            int vocabularySize,
            int blankIndex,
            bool[]? allowedTokenMask,
            bool[]? blockedTokenMask,
            IParakeetRunContext runContext,
            CancellationToken cancellationToken) =>
            DecodeStep(
                encoderFrame,
                target,
                targetLength,
                state1,
                state2,
                vocabularySize,
                blankIndex,
                allowedTokenMask,
                runContext,
                cancellationToken);
    }

    private sealed class OnnxParakeetBackend : IParakeetBackend
    {
        private const int MelChannelCount = 128;

        private readonly ParakeetEngineOptions _options;
        private readonly string _modelDirectory;
        private readonly InferenceSession _preprocessor;
        private readonly InferenceSession _encoder;
        private readonly InferenceSession _decoder;
        private InferenceSession? _fp32FallbackEncoder;
        private bool _fp32FallbackUnavailable;

        private OnnxParakeetBackend(
            ParakeetEngineOptions options,
            string modelDirectory,
            InferenceSession preprocessor,
            InferenceSession encoder,
            InferenceSession decoder,
            int encoderDimension,
            int stateLeadingDimension,
            int stateDimension)
        {
            _options = options;
            _modelDirectory = modelDirectory;
            _preprocessor = preprocessor;
            _encoder = encoder;
            _decoder = decoder;
            EncoderDimension = encoderDimension;
            StateLeadingDimension = stateLeadingDimension;
            StateDimension = stateDimension;
        }

        public int EncoderDimension { get; }

        public int StateLeadingDimension { get; }

        public int StateDimension { get; }

        public static (OnnxParakeetBackend Backend, ParakeetVocabulary Vocabulary) Create(ParakeetEngineOptions options)
        {
            InferenceSession? preprocessor = null;
            InferenceSession? encoder = null;
            InferenceSession? decoder = null;
            try
            {
                using var sessionOptions = CreateSessionOptions(options);
                var modelDirectory = Path.GetFullPath(options.ModelDirectory);
                var encoderFile = options.ModelPack == ParakeetModelPack.FullPrecision
                    ? FullPrecisionEncoderFileName
                    : QuantizedEncoderFileName;
                var decoderFile = options.ModelPack == ParakeetModelPack.FullPrecision
                    ? FullPrecisionDecoderFileName
                    : QuantizedDecoderFileName;

                preprocessor = new InferenceSession(Path.Combine(modelDirectory, PreprocessorFileName), sessionOptions);
                encoder = new InferenceSession(Path.Combine(modelDirectory, encoderFile), sessionOptions);
                decoder = new InferenceSession(Path.Combine(modelDirectory, decoderFile), sessionOptions);
                var vocabulary = ParakeetVocabulary.Load(Path.Combine(modelDirectory, VocabularyFileName));

                var decoderInputs = decoder.InputMetadata;
                var encoderDimension = checked((int)decoderInputs["encoder_outputs"].Dimensions[1]);
                var stateLeadingDimension = checked((int)decoderInputs["input_states_1"].Dimensions[0]);
                var stateDimension = checked((int)decoderInputs["input_states_1"].Dimensions[2]);

                return (
                    new OnnxParakeetBackend(
                        options,
                        modelDirectory,
                        preprocessor,
                        encoder,
                        decoder,
                        encoderDimension,
                        stateLeadingDimension,
                        stateDimension),
                    vocabulary);
            }
            catch (Exception exception) when (IsModelOrRuntimeFailure(exception))
            {
                preprocessor?.Dispose();
                encoder?.Dispose();
                decoder?.Dispose();
                throw CreateFailure(
                    options.Provider == RuntimeProviderKind.Cpu
                        ? AppErrorCode.TranscriptionFailed
                        : AppErrorCode.RuntimeProviderUnavailable,
                    exception);
            }
        }

        public IParakeetRunContext CreateRunContext(CancellationToken cancellationToken) =>
            new OnnxRunContext(cancellationToken);

        public (float[] Features, long FeatureLength) Preprocess(
            float[] samples,
            IParakeetRunContext runContext,
            CancellationToken cancellationToken) =>
            Preprocess(samples, samples.Length, runContext, cancellationToken);

        public (float[] Features, long FeatureLength) Preprocess(
            float[] samples,
            int validSampleCount,
            IParakeetRunContext runContext,
            CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(validSampleCount);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(validSampleCount, samples.Length);

            var options = ((OnnxRunContext)runContext).Options;
            try
            {
                using var preprocessorPhase = UatRuntimePhaseJournal.BeginNativeCall(UatNativeOperation.Preprocessor);
                using var preprocessorResult = _preprocessor.Run(
                    [
                        NamedOnnxValue.CreateFromTensor(
                            "waveforms",
                            new DenseTensor<float>(samples.AsMemory(), [1, samples.Length])),
                        NamedOnnxValue.CreateFromTensor(
                            "waveforms_lens",
                            new DenseTensor<long>(new long[] { validSampleCount }, [1])),
                    ],
                    PreprocessorOutputs,
                    options);
                preprocessorPhase.Returned();
                cancellationToken.ThrowIfCancellationRequested();

                var features = TensorArray<float>(preprocessorResult[0]);
                var featureLength = TensorSingle<long>(preprocessorResult[1]);
                return (features, featureLength);
            }
            catch (OnnxRuntimeException exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(
                    "Local transcription was cancelled.",
                    exception,
                    cancellationToken);
            }
            catch (OnnxRuntimeException exception)
            {
                throw CreateStageFailure(ParakeetOnnxStage.Preprocessor, exception);
            }
        }

        public (float[] EncodedChannelFirst, int EncodedLength) Encode(
            float[] features,
            long featureLength,
            IParakeetRunContext runContext,
            CancellationToken cancellationToken)
        {
            var options = ((OnnxRunContext)runContext).Options;
            if (!TryGetFeatureTimeSteps(features, featureLength, out var featureTimeSteps))
            {
                throw CreateStageFailure(ParakeetOnnxStage.Encoder);
            }

            try
            {
                return RunEncoderSession(_encoder, features, featureLength, featureTimeSteps, options, cancellationToken);
            }
            catch (OnnxRuntimeException exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(
                    "Local transcription was cancelled.",
                    exception,
                    cancellationToken);
            }
            catch (OnnxRuntimeException exception)
            {
                throw CreateStageFailure(ParakeetOnnxStage.Encoder, exception);
            }
        }

        public bool TryEncodeWithFullPrecisionFallback(
            float[] features,
            long featureLength,
            IParakeetRunContext runContext,
            CancellationToken cancellationToken,
            out float[] encodedChannelFirst,
            out int encodedLength)
        {
            encodedChannelFirst = [];
            encodedLength = 0;
            if (_fp32FallbackUnavailable ||
                !_options.AllowFullPrecisionFallback ||
                _options.Provider != RuntimeProviderKind.Cpu ||
                _options.ModelPack != ParakeetModelPack.Quantized ||
                !HasFullPrecisionEncoder(_modelDirectory) ||
                !TryGetFeatureTimeSteps(features, featureLength, out var featureTimeSteps))
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var options = ((OnnxRunContext)runContext).Options;
            try
            {
                if (_fp32FallbackEncoder is null)
                {
                    using var sessionOptions = CreateSessionOptions(_options);
                    _fp32FallbackEncoder = new InferenceSession(
                        Path.Combine(_modelDirectory, FullPrecisionEncoderFileName),
                        sessionOptions);
                }

                cancellationToken.ThrowIfCancellationRequested();
                (encodedChannelFirst, encodedLength) = RunEncoderSession(
                    _fp32FallbackEncoder,
                    features,
                    featureLength,
                    featureTimeSteps,
                    options,
                    cancellationToken);
                if (encodedLength <= 0 ||
                    encodedChannelFirst.Length < (long)EncoderDimension * encodedLength ||
                    encodedChannelFirst.Length % EncoderDimension != 0)
                {
                    _fp32FallbackEncoder.Dispose();
                    _fp32FallbackEncoder = null;
                    _fp32FallbackUnavailable = true;
                    encodedChannelFirst = [];
                    encodedLength = 0;
                    return false;
                }

                return true;
            }
            catch (OnnxRuntimeException exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(
                    "Local transcription was cancelled.",
                    exception,
                    cancellationToken);
            }
            catch (Exception exception) when (
                IsModelOrRuntimeFailure(exception) ||
                exception is InvalidOperationException or OverflowException)
            {
                _fp32FallbackEncoder?.Dispose();
                _fp32FallbackEncoder = null;
                _fp32FallbackUnavailable = true;
                encodedChannelFirst = [];
                encodedLength = 0;
                return false;
            }
        }

        private static bool TryGetFeatureTimeSteps(float[] features, long featureLength, out int featureTimeSteps)
        {
            featureTimeSteps = 0;
            if (featureLength <= 0 || features.Length == 0 || features.Length % MelChannelCount != 0)
            {
                return false;
            }

            var steps = features.Length / MelChannelCount;
            if (steps < featureLength)
            {
                return false;
            }

            featureTimeSteps = steps;
            return true;
        }

        private static (float[] EncodedChannelFirst, int EncodedLength) RunEncoderSession(
            InferenceSession session,
            float[] features,
            long featureLength,
            int featureTimeSteps,
            RunOptions options,
            CancellationToken cancellationToken)
        {
            using var encoderPhase = UatRuntimePhaseJournal.BeginNativeCall(UatNativeOperation.Encoder);
            using var encoderResult = session.Run(
                [
                    NamedOnnxValue.CreateFromTensor(
                        "audio_signal",
                        new DenseTensor<float>(features.AsMemory(), [1, MelChannelCount, featureTimeSteps])),
                    NamedOnnxValue.CreateFromTensor(
                        "length",
                        new DenseTensor<long>(new long[] { featureLength }, [1])),
                ],
                EncoderOutputs,
                options);
            encoderPhase.Returned();
            cancellationToken.ThrowIfCancellationRequested();

            var encoded = TensorArray<float>(encoderResult[0]);
            var encodedLength = checked((int)TensorSingle<long>(encoderResult[1]));
            return (encoded, encodedLength);
        }

        public ParakeetDecoderStepResult DecodeStep(
            float[] encoderFrame,
            int[] target,
            int[] targetLength,
            float[] state1,
            float[] state2,
            int vocabularySize,
            int blankIndex,
            IParakeetRunContext runContext,
            CancellationToken cancellationToken) =>
            DecodeStep(
                encoderFrame,
                target,
                targetLength,
                state1,
                state2,
                vocabularySize,
                blankIndex,
                allowedTokenMask: null,
                blockedTokenMask: null,
                runContext,
                cancellationToken);

        public ParakeetDecoderStepResult DecodeStep(
            float[] encoderFrame,
            int[] target,
            int[] targetLength,
            float[] state1,
            float[] state2,
            int vocabularySize,
            int blankIndex,
            bool[]? allowedTokenMask,
            IParakeetRunContext runContext,
            CancellationToken cancellationToken) =>
            DecodeStep(
                encoderFrame,
                target,
                targetLength,
                state1,
                state2,
                vocabularySize,
                blankIndex,
                allowedTokenMask,
                blockedTokenMask: null,
                runContext,
                cancellationToken);

        public ParakeetDecoderStepResult DecodeStep(
            float[] encoderFrame,
            int[] target,
            int[] targetLength,
            float[] state1,
            float[] state2,
            int vocabularySize,
            int blankIndex,
            bool[]? allowedTokenMask,
            bool[]? blockedTokenMask,
            IParakeetRunContext runContext,
            CancellationToken cancellationToken)
        {
            var options = ((OnnxRunContext)runContext).Options;
            try
            {
                using var decoderPhase = UatRuntimePhaseJournal.BeginNativeCall(UatNativeOperation.Decoder);
                using var result = _decoder.Run(
                    [
                        NamedOnnxValue.CreateFromTensor(
                            "encoder_outputs",
                            new DenseTensor<float>(encoderFrame.AsMemory(), [1, EncoderDimension, 1])),
                        NamedOnnxValue.CreateFromTensor(
                            "targets",
                            new DenseTensor<int>(target.AsMemory(), [1, 1])),
                        NamedOnnxValue.CreateFromTensor(
                            "target_length",
                            new DenseTensor<int>(targetLength.AsMemory(), [1])),
                        NamedOnnxValue.CreateFromTensor(
                            "input_states_1",
                            new DenseTensor<float>(
                                state1.AsMemory(),
                                [StateLeadingDimension, 1, StateDimension])),
                        NamedOnnxValue.CreateFromTensor(
                            "input_states_2",
                            new DenseTensor<float>(
                                state2.AsMemory(),
                                [StateLeadingDimension, 1, StateDimension])),
                    ],
                    DecoderOutputs,
                    options);
                decoderPhase.Returned();

                var output = TensorSpan<float>(result[0]);
                var token = ParakeetVocabulary.SelectToken(
                    output[..vocabularySize],
                    blankIndex,
                    allowedTokenMask,
                    blockedTokenMask);
                var step = ParakeetVocabulary.IndexOfMaximum(output[vocabularySize..]);
                if (token != blankIndex)
                {
                    TensorSpan<float>(result[1])[..state1.Length].CopyTo(state1);
                    TensorSpan<float>(result[2])[..state2.Length].CopyTo(state2);
                }

                return new ParakeetDecoderStepResult(token, step);
            }
            catch (OnnxRuntimeException exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(
                    "Local transcription was cancelled.",
                    exception,
                    cancellationToken);
            }
            catch (OnnxRuntimeException exception)
            {
                throw CreateStageFailure(ParakeetOnnxStage.Decoder, exception);
            }
        }

        public void Dispose()
        {
            _preprocessor.Dispose();
            _encoder.Dispose();
            _fp32FallbackEncoder?.Dispose();
            _decoder.Dispose();
        }

        private static ReadOnlySpan<T> TensorSpan<T>(DisposableNamedOnnxValue value) =>
            value.Value is DenseTensor<T> dense
                ? dense.Buffer.Span
                : value.AsEnumerable<T>().ToArray();

        private static T[] TensorArray<T>(DisposableNamedOnnxValue value) =>
            value.Value is DenseTensor<T> dense
                ? dense.Buffer.Span.ToArray()
                : value.AsEnumerable<T>().ToArray();

        private static T TensorSingle<T>(DisposableNamedOnnxValue value)
        {
            if (value.Value is DenseTensor<T> dense && dense.Buffer.Length == 1)
            {
                return dense.Buffer.Span[0];
            }

            return value.AsEnumerable<T>().Single();
        }

        private sealed class OnnxRunContext : IParakeetRunContext
        {
            private readonly CancellationTokenRegistration _registration;

            public OnnxRunContext(CancellationToken cancellationToken)
            {
                Options = new RunOptions();
                _registration = cancellationToken.Register(
                    static state => ((RunOptions)state!).Terminate = true,
                    Options);
            }

            public RunOptions Options { get; }

            public void Dispose()
            {
                _registration.Dispose();
                Options.Dispose();
            }
        }
    }
}

public enum ParakeetOnnxStage
{
    Preprocessor,
    Encoder,
    Decoder,
}
