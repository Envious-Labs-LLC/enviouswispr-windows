using System.Diagnostics;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Text.Json;
using EnviousWispr.ASR;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Runtime;

const int protocolVersion = 1;

var parentProcessId = ReadIntegerArgument(args, "--parent-pid");
var healthDelayMilliseconds = ReadIntegerArgument(args, "--health-delay-ms", defaultValue: 0);
if (parentProcessId <= 0 || healthDelayMilliseconds < 0)
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

ParakeetEngineCreationResult? engineCreation;
try
{
    engineCreation = CreateTranscriptionEngine(args);
}
catch (TranscriptionEngineException)
{
    return 4;
}

using (parent)
using (engineCreation?.Engine as IDisposable)
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
            await WriteResponseAsync(new RuntimeWorkerResponse(
                protocolVersion,
                RequestId: Guid.Empty,
                Status: "invalid"));
            continue;
        }

        if (request is null ||
            request.ProtocolVersion != protocolVersion ||
            request.RequestId == Guid.Empty)
        {
            await WriteResponseAsync(new RuntimeWorkerResponse(
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

            await WriteResponseAsync(new RuntimeWorkerResponse(
                protocolVersion,
                request.RequestId,
                Status: "ready"));
            continue;
        }

        if (string.Equals(request.Command, "transcribe", StringComparison.Ordinal))
        {
            await WriteResponseAsync(await TranscribeAsync(request, engineCreation));
            continue;
        }

        if (string.Equals(request.Command, "shutdown", StringComparison.Ordinal))
        {
            await WriteResponseAsync(new RuntimeWorkerResponse(
                protocolVersion,
                request.RequestId,
                Status: "stopping"));
            return 0;
        }

        await WriteResponseAsync(new RuntimeWorkerResponse(
            protocolVersion,
            request.RequestId,
            Status: "unsupported"));
    }
}

return 0;

static ParakeetEngineCreationResult? CreateTranscriptionEngine(string[] arguments)
{
    var modelDirectory = ReadStringArgument(arguments, "--asr-model-directory");
    if (modelDirectory is null)
    {
        return null;
    }

    var providerText = ReadStringArgument(arguments, "--asr-provider") ?? "cpu";
    var modelPackText = ReadStringArgument(arguments, "--asr-model-pack") ?? "quantized";
    if (!Enum.TryParse<RuntimeProviderKind>(providerText, ignoreCase: true, out var provider) ||
        !Enum.TryParse<ParakeetModelPack>(modelPackText, ignoreCase: true, out var modelPack))
    {
        throw new TranscriptionEngineException(new AppError(
            AppErrorCode.RuntimeProviderUnavailable,
            AppErrorStage.FinalAsr,
            CanRetry: true));
    }

    var intraOpThreads = ReadIntegerArgument(arguments, "--asr-intra-op-threads", defaultValue: 1);
    var interOpThreads = ReadIntegerArgument(arguments, "--asr-inter-op-threads", defaultValue: 1);
    var maximumTokensPerStep = ReadIntegerArgument(arguments, "--asr-maximum-tokens-per-step", defaultValue: 10);
    var cudaRuntimeDirectory = ReadStringArgument(arguments, "--asr-cuda-runtime-directory");
    var primary = new ParakeetEngineOptions(
        modelDirectory,
        provider,
        modelPack,
        intraOpThreads,
        interOpThreads,
        maximumTokensPerStep,
        CudaRuntimeDirectory: cudaRuntimeDirectory);
    var fallbackThreads = ReadIntegerArgument(
        arguments,
        "--asr-cpu-fallback-threads",
        defaultValue: Math.Clamp(Environment.ProcessorCount / 4, 2, 8));
    var fallback = provider == RuntimeProviderKind.Cpu
        ? null
        : new ParakeetEngineOptions(
            modelDirectory,
            RuntimeProviderKind.Cpu,
            ParakeetModelPack.Quantized,
            fallbackThreads,
            InterOpThreads: 1,
            maximumTokensPerStep);
    return new ParakeetEngineFactory().Create(primary, fallback);
}

static async Task<RuntimeWorkerResponse> TranscribeAsync(
    RuntimeWorkerRequest request,
    ParakeetEngineCreationResult? engineCreation)
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
        using var view = map.CreateViewAccessor(
            0,
            checked((long)transcription.SampleCount * sizeof(float)),
            MemoryMappedFileAccess.Read);
        view.ReadArray(0, samples, 0, samples.Length);
        var sessionId = new DictationSessionId(transcription.SessionId);
        var transcript = await engineCreation.Engine.TranscribeAsync(new CapturedAudio(
            sessionId,
            samples,
            ParakeetTranscriptionEngine.RequiredSampleRate,
            Channels: 1));
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
                degradedError));
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

static async Task WriteResponseAsync(RuntimeWorkerResponse response)
{
    await Console.Out.WriteLineAsync(JsonSerializer.Serialize(response));
    await Console.Out.FlushAsync();
}
