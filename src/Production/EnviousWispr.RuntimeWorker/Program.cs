using System.Diagnostics;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text.Json;
using EnviousWispr.ASR;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;

const int protocolVersion = 1;

var parentProcessId = ReadIntegerArgument(args, "--parent-pid");
var healthDelayMilliseconds = ReadIntegerArgument(args, "--health-delay-ms", defaultValue: 0);
// Test-only opt-in: exercise successful worker responses without loading a speech model.
var testTranscribeStub = args.Contains("--test-transcribe-stub", StringComparer.Ordinal);
// Test-only: a stubbed transcription that takes this long, so a wedged worker can be staged.
var transcribeDelayMilliseconds = ReadIntegerArgument(args, "--transcribe-delay-ms", defaultValue: 0);
// Test-only: a shutdown request that takes this long to honour, so a slow teardown can be staged.
var shutdownDelayMilliseconds = ReadIntegerArgument(args, "--shutdown-delay-ms", defaultValue: 0);
// Test-only: emit startup or decode progress heartbeats spaced by this delay without loading a model.
var testStartupProgressMilliseconds = ReadIntegerArgument(args, "--test-startup-progress-ms", defaultValue: 0);
var testDecodeProgressMilliseconds = ReadIntegerArgument(args, "--test-decode-progress-ms", defaultValue: 0);
if (parentProcessId <= 0 ||
    healthDelayMilliseconds < 0 ||
    transcribeDelayMilliseconds < 0 ||
    shutdownDelayMilliseconds < 0 ||
    testStartupProgressMilliseconds < 0 ||
    testDecodeProgressMilliseconds < 0)
{
    return 2;
}

Process parent;
try
{
    parent = Process.GetProcessById(parentProcessId);
}
catch (ArgumentException)
{
    return 3;
}

var phaseJournal = UatRuntimePhaseJournal.FromEnvironment(parentProcessId);
var writeGate = new SemaphoreSlim(1, 1);
TranscriptionEngineCreation? engineCreation = null;

try
{
    using (parent)
    {
        parent.EnableRaisingEvents = true;
        parent.Exited += (_, _) => Environment.Exit(24);

        while (await Console.In.ReadLineAsync() is { } line)
        {
            RuntimeWorkerRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<RuntimeWorkerRequest>(line);
            }
            catch (JsonException)
            {
                await WriteResponseAsync(
                    writeGate,
                    new RuntimeWorkerResponse(
                        protocolVersion,
                        RequestId: Guid.Empty,
                        Status: "invalid"));
                continue;
            }

            if (request is null ||
                request.ProtocolVersion != protocolVersion ||
                request.RequestId == Guid.Empty)
            {
                await WriteResponseAsync(
                    writeGate,
                    new RuntimeWorkerResponse(
                        protocolVersion,
                        request?.RequestId ?? Guid.Empty,
                        Status: "invalid"));
                continue;
            }

            if (string.Equals(request.Command, "health", StringComparison.Ordinal))
            {
                if (healthDelayMilliseconds > 0)
                {
                    await Task.Delay(healthDelayMilliseconds);
                }

                if (testTranscribeStub && testStartupProgressMilliseconds > 0)
                {
                    string[] stages = ["ModelInitStarted", "WeightsMapped", "ExecutionProviderReady"];
                    foreach (var stage in stages)
                    {
                        await WriteProgressAsync(writeGate, protocolVersion, request.RequestId, stage, progress: null);
                        await Task.Delay(testStartupProgressMilliseconds);
                    }
                }
                else if (!testTranscribeStub && engineCreation is null)
                {
                    try
                    {
                        engineCreation = await Task.Run(() => CreateTranscriptionEngine(
                            args,
                            stage => EmitProgressSync(writeGate, protocolVersion, request.RequestId, stage, null)));
                    }
                    catch (TranscriptionEngineException exception)
                    {
                        await WriteResponseAsync(
                            writeGate,
                            new RuntimeWorkerResponse(
                                protocolVersion,
                                request.RequestId,
                                Status: "failed",
                                Error: exception.Error));
                        return 4;
                    }
                }

                await WriteResponseAsync(
                    writeGate,
                    new RuntimeWorkerResponse(
                        protocolVersion,
                        request.RequestId,
                        Status: "ready"));
                continue;
            }

            if (string.Equals(request.Command, "transcribe", StringComparison.Ordinal))
            {
                if (testTranscribeStub && testDecodeProgressMilliseconds > 0)
                {
                    await WriteProgressAsync(writeGate, protocolVersion, request.RequestId, "DecodeStarted", 0.0);
                    await Task.Delay(testDecodeProgressMilliseconds);
                    await WriteProgressAsync(writeGate, protocolVersion, request.RequestId, "DecodeStep", 0.5);
                    await Task.Delay(testDecodeProgressMilliseconds);
                }
                else if (testTranscribeStub && transcribeDelayMilliseconds > 0)
                {
                    await Task.Delay(transcribeDelayMilliseconds);
                }

                var response = testTranscribeStub && request.Transcription is { } transcription
                    ? new RuntimeWorkerResponse(
                        protocolVersion,
                        request.RequestId,
                        Status: "complete",
                        new RuntimeWorkerTranscript(
                            transcription.SessionId,
                            string.Empty,
                            "test-transcribe-stub",
                            [],
                            UsedFallback: false,
                            // ECHOED, so a test can read which language the request carried.
                            RecognitionLanguage: transcription.Language))
                    : await TranscribeAsync(
                        request,
                        engineCreation,
                        phaseJournal,
                        (stage, progress) => EmitProgressSync(writeGate, protocolVersion, request.RequestId, stage, progress));
                try
                {
                    await WriteResponseAsync(writeGate, response);
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
                {
                    if (!testTranscribeStub)
                    {
                        phaseJournal?.Record(UatRuntimePhase.ResponsePublicationFailed, request.Transcription?.SessionId, request.RequestId);
                    }

                    throw;
                }

                if (!testTranscribeStub && engineCreation is not null && request.Transcription is { SampleCount: > 0 } published)
                {
                    phaseJournal?.Record(
                        response.Status == "complete" ? UatRuntimePhase.ResponsePublished : UatRuntimePhase.ResponseFailedPublished,
                        published.SessionId,
                        request.RequestId);
                }

                continue;
            }

            if (string.Equals(request.Command, "shutdown", StringComparison.Ordinal))
            {
                if (shutdownDelayMilliseconds > 0)
                {
                    await Task.Delay(shutdownDelayMilliseconds);
                }

                await WriteResponseAsync(
                    writeGate,
                    new RuntimeWorkerResponse(
                        protocolVersion,
                        request.RequestId,
                        Status: "stopping"));
                return 0;
            }

            await WriteResponseAsync(
                writeGate,
                new RuntimeWorkerResponse(
                    protocolVersion,
                    request.RequestId,
                    Status: "unsupported"));
        }
    }
}
finally
{
    (engineCreation?.Engine as IDisposable)?.Dispose();
}

return 0;

static TranscriptionEngineCreation? CreateTranscriptionEngine(
    string[] arguments,
    Action<string>? onStage = null)
{
    var modelDirectory = ReadStringArgument(arguments, "--asr-model-directory");
    if (modelDirectory is null)
    {
        return null;
    }

    var engineText = ReadStringArgument(arguments, "--asr-engine") ?? "parakeet";
    var providerText = ReadStringArgument(arguments, "--asr-provider") ?? "cpu";
    var modelPackText = ReadStringArgument(arguments, "--asr-model-pack") ?? "quantized";
    if (!FinalAsrEngineChoices.TryParse(engineText, out var engineKind) ||
        !Enum.TryParse<RuntimeProviderKind>(providerText, ignoreCase: true, out var provider))
    {
        throw new TranscriptionEngineException(new AppError(
            AppErrorCode.RuntimeProviderUnavailable,
            AppErrorStage.FinalAsr,
            CanRetry: true));
    }

    var intraOpThreads = ReadIntegerArgument(arguments, "--asr-intra-op-threads", defaultValue: 1);
    var interOpThreads = ReadIntegerArgument(arguments, "--asr-inter-op-threads", defaultValue: 1);
    var maximumTokensPerStep = ReadIntegerArgument(arguments, "--asr-maximum-tokens-per-step", defaultValue: 10);
    var fallbackThreads = ReadIntegerArgument(
        arguments,
        "--asr-cpu-fallback-threads",
        defaultValue: Math.Clamp(Environment.ProcessorCount / 4, 2, 8));
    var cudaRuntimeDirectory = ReadStringArgument(arguments, "--asr-cuda-runtime-directory");
    // BEFORE ANY ENGINE LOADS A NATIVE LIBRARY, and by the one mechanism a packaged process honours.
    // PATH is not it: a packaged process never searches PATH for a DLL. See NativeRuntimeSearchPath.
    var search = NativeRuntimeSearchPath.Configure(cudaRuntimeDirectory);
    // A CUDA FOLDER WAS GIVEN AND COULD NOT BE MADE VISIBLE, so the card cannot load its libraries: go
    // straight to the processor and say so the way an engine that failed to start on the card does
    // (UsedFallback with RuntimeProviderUnavailable), rather than start a card engine that cannot load.
    // No folder at all leaves the search as it was, and a card engine then finds its files beside the
    // executable or not at all, as before.
    var cardUnreachable = provider != RuntimeProviderKind.Cpu &&
        search is NativeRuntimeSearchPathOutcome.DirectoryMissing or NativeRuntimeSearchPathOutcome.Refused;
    if (engineKind == FinalAsrEngine.Whisper)
    {
        return CreateWhisperEngine(
            arguments,
            modelDirectory,
            provider,
            intraOpThreads,
            fallbackThreads,
            cardUnreachable,
            onStage);
    }

    if (engineKind != FinalAsrEngine.Parakeet ||
        !Enum.TryParse<ParakeetModelPack>(modelPackText, ignoreCase: true, out var modelPack))
    {
        throw new TranscriptionEngineException(new AppError(
            AppErrorCode.RuntimeProviderUnavailable,
            AppErrorStage.FinalAsr,
            CanRetry: true));
    }

    var allowFullPrecisionFallback = !arguments.Contains("--asr-disable-fp32-fallback", StringComparer.Ordinal);
    var primary = new ParakeetEngineOptions(
        modelDirectory,
        provider,
        modelPack,
        intraOpThreads,
        interOpThreads,
        maximumTokensPerStep,
        CudaRuntimeDirectory: cudaRuntimeDirectory,
        AllowFullPrecisionFallback: allowFullPrecisionFallback,
        OnInitStage: onStage);
    var fallback = provider == RuntimeProviderKind.Cpu
        ? null
        : new ParakeetEngineOptions(
            modelDirectory,
            RuntimeProviderKind.Cpu,
            ParakeetTranscriptionEngine.HasQuantizedModelPack(modelDirectory)
                ? ParakeetModelPack.Quantized
                : modelPack,
            fallbackThreads,
            InterOpThreads: 1,
            maximumTokensPerStep,
            AllowFullPrecisionFallback: allowFullPrecisionFallback,
            OnInitStage: onStage);
    if (cardUnreachable)
    {
        return OnTheProcessor(new ParakeetEngineFactory().Create(fallback!).Engine);
    }

    var creation = new ParakeetEngineFactory().Create(primary, fallback);
    return new TranscriptionEngineCreation(
        creation.Engine,
        creation.UsedFallback,
        creation.DegradedError);
}

static TranscriptionEngineCreation CreateWhisperEngine(
    string[] arguments,
    string modelDirectory,
    RuntimeProviderKind provider,
    int threadCount,
    int fallbackThreads,
    bool cardUnreachable,
    Action<string>? onStage = null)
{
    var packText = ReadStringArgument(arguments, "--asr-whisper-model-pack") ?? "quantized";
    if (!Enum.TryParse<WhisperModelPack>(packText, ignoreCase: true, out var pack))
    {
        throw new TranscriptionEngineException(new AppError(
            AppErrorCode.ModelPackUnavailable,
            AppErrorStage.FinalAsr,
            CanRetry: true));
    }

    var language = ReadStringArgument(arguments, "--asr-whisper-language");
    var primary = new WhisperEngineOptions(
        Path.Combine(modelDirectory, WhisperModelFileNames.For(pack)),
        provider,
        pack,
        threadCount,
        language,
        OnInitStage: onStage);
    WhisperEngineOptions? fallback = null;
    if (provider != RuntimeProviderKind.Cpu)
    {
        var quantizedPath = Path.Combine(modelDirectory, WhisperModelFileNames.Quantized);
        var fallbackPack = File.Exists(quantizedPath)
            ? WhisperModelPack.Quantized
            : pack;
        fallback = new WhisperEngineOptions(
            Path.Combine(modelDirectory, WhisperModelFileNames.For(fallbackPack)),
            RuntimeProviderKind.Cpu,
            fallbackPack,
            fallbackThreads,
            language,
            UseFlashAttention: false,
            OnInitStage: onStage);
    }

    if (cardUnreachable)
    {
        return OnTheProcessor(new WhisperEngineFactory().Create(fallback!).Engine);
    }

    var creation = new WhisperEngineFactory().Create(primary, fallback);
    return new TranscriptionEngineCreation(
        creation.Engine,
        creation.UsedFallback,
        creation.DegradedError);
}

static TranscriptionEngineCreation OnTheProcessor(ITranscriptionEngine engine) => new(
    engine,
    UsedFallback: true,
    new AppError(AppErrorCode.RuntimeProviderUnavailable, AppErrorStage.FinalAsr, CanRetry: true));

static async Task<RuntimeWorkerResponse> TranscribeAsync(
    RuntimeWorkerRequest request,
    TranscriptionEngineCreation? engineCreation,
    UatRuntimePhaseJournal? phaseJournal,
    Action<string, double?>? onProgress = null)
{
    if (engineCreation is null || request.Transcription is not { SampleCount: > 0 } transcription)
    {
        return Failure(request.RequestId, AppErrorCode.TranscriptionFailed);
    }

    try
    {
        var samples = new float[transcription.SampleCount];
        using var map = MemoryMappedFile.OpenExisting(
            transcription.MemoryMapName,
            MemoryMappedFileRights.Read);
        using var view = map.CreateViewStream(
            0,
            checked((long)transcription.SampleCount * sizeof(float)),
            MemoryMappedFileAccess.Read);
        view.ReadExactly(MemoryMarshal.AsBytes(samples.AsSpan()));
        var sessionId = new DictationSessionId(transcription.SessionId);
        using var requestPhase = phaseJournal?.BeginRequest(sessionId.Value, request.RequestId);
        phaseJournal?.Record(UatRuntimePhase.RealRequestAdmitted, sessionId.Value, request.RequestId);
        var audio = new CapturedAudio(
            sessionId,
            samples,
            ParakeetTranscriptionEngine.RequiredSampleRate,
            Channels: 1);
        // THE TAKE CARRIES ITS LANGUAGE (#241, #358). A request without one keeps the engine's
        // load-time setting; an unrecognized code is refused; Whisper runs 99+ languages and
        // Parakeet pins script selection across its 25 supported European languages.
        string? language = null;
        if (transcription.Language is { } requested &&
            !WhisperLanguageCodes.TryNormalize(requested, out language))
        {
            return Failure(request.RequestId, AppErrorCode.TranscriptionFailed);
        }

        onProgress?.Invoke("DecodeStarted", 0.0);
        var transcript = engineCreation.Engine is IProgressReportingTranscriptionEngine progressEngine
            ? await progressEngine.TranscribeAsync(audio, language, onProgress)
            : language is not null &&
              engineCreation.Engine is ILanguageSelectableTranscriptionEngine selectable
                ? await selectable.TranscribeAsync(audio, language)
                : await engineCreation.Engine.TranscribeAsync(audio);
        var usedFallback = engineCreation.UsedFallback || transcript.UsedFallback;
        var degradedError = transcript.DegradedError ?? engineCreation.DegradedError;
        return new RuntimeWorkerResponse(
            protocolVersion,
            request.RequestId,
            Status: "complete",
            new RuntimeWorkerTranscript(
                transcript.SessionId.Value,
                transcript.Text,
                transcript.EngineId,
                transcript.TokenTimings ?? [],
                usedFallback,
                degradedError,
                transcript.DetectedLanguage,
                transcript.RecognitionLanguage,
                transcript.DetectedLanguageConfidence,
                transcript.DetectedLanguageMargin));
    }
    catch (TranscriptionEngineException exception)
    {
        return new RuntimeWorkerResponse(
            protocolVersion,
            request.RequestId,
            Status: "failed",
            Error: exception.Error);
    }
    catch (Exception exception) when (
        exception is FileNotFoundException or IOException or UnauthorizedAccessException)
    {
        return Failure(request.RequestId, AppErrorCode.TranscriptionFailed);
    }
}

static RuntimeWorkerResponse Failure(Guid requestId, AppErrorCode code) => new(
    protocolVersion,
    requestId,
    Status: "failed",
    Error: new AppError(code, AppErrorStage.FinalAsr, CanRetry: true));

static int ReadIntegerArgument(string[] arguments, string name, int defaultValue = -1)
{
    var value = ReadStringArgument(arguments, name);
    return value is not null && int.TryParse(
        value,
        NumberStyles.None,
        CultureInfo.InvariantCulture,
        out var parsed)
        ? parsed
        : defaultValue;
}

static string? ReadStringArgument(string[] arguments, string name)
{
    var index = Array.IndexOf(arguments, name);
    return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
}

static void EmitProgressSync(
    SemaphoreSlim writeGate,
    int version,
    Guid requestId,
    string stage,
    double? progress)
{
    WriteProgressAsync(writeGate, version, requestId, stage, progress).GetAwaiter().GetResult();
}

static async Task WriteProgressAsync(
    SemaphoreSlim writeGate,
    int version,
    Guid requestId,
    string stage,
    double? progress)
{
    try
    {
        await WriteResponseAsync(
            writeGate,
            new RuntimeWorkerResponse(
                version,
                requestId,
                Status: "progress",
                Stage: stage,
                Progress: progress));
    }
    catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
    {
    }
}

static async Task WriteResponseAsync(SemaphoreSlim writeGate, RuntimeWorkerResponse response)
{
    await writeGate.WaitAsync().ConfigureAwait(false);
    try
    {
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(response)).ConfigureAwait(false);
        await Console.Out.FlushAsync().ConfigureAwait(false);
    }
    finally
    {
        writeGate.Release();
    }
}

internal sealed record TranscriptionEngineCreation(
    ITranscriptionEngine Engine,
    bool UsedFallback,
    AppError? DegradedError);
