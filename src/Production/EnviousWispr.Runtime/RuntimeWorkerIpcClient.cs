using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Runtime;
using System.Diagnostics;
using System.Text.Json;

namespace EnviousWispr.Services.Runtime;

public sealed partial class RuntimeWorkerSupervisor
{
    private async Task<RuntimeWorkerResult> SendRequestAsync(
        string command,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var response = await SendRawRequestAsync(
            command,
            transcription: null,
            timeout,
            cancellationToken).ConfigureAwait(false);
        var expectedStatus = command == "shutdown" ? "stopping" : "ready";
        return response is not null &&
            string.Equals(response.Status, expectedStatus, StringComparison.Ordinal)
            ? Success(State)
            : Failure();
    }

    private async Task<RuntimeWorkerResponse?> SendRawRequestAsync(
        string command,
        RuntimeWorkerTranscriptionRequest? transcription,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        // ONE STABLE REFERENCE FOR THE WHOLE REQUEST. An abort that ends the generation while the
        // request is in flight closes the worker's stdout, and the read below ends with nothing -
        // the failed request the caller already handles - rather than a field read that finds a
        // different answer from one line to the next.
        var process = LiveProcess();
        if (process is null)
        {
            return null;
        }

        var requestId = transcription is not null && UatRuntimePhaseJournal.TryTakeCorrelatedRequestId(out var correlatedRequestId)
            ? correlatedRequestId
            : Guid.NewGuid();
        var request = new RuntimeWorkerRequest(ProtocolVersion, requestId, command, transcription);
        var observedGeneration = Volatile.Read(ref _generation);
        if (!ReferenceEquals(observedGeneration?.Process, process))
        {
            observedGeneration = null;
        }

        UatRequestIdentity? identity = null;
        if (_phaseJournal is not null && observedGeneration?.DiagnosticId is not null && transcription is not null)
        {
            identity = new UatRequestIdentity(transcription.SessionId, requestId);
            Volatile.Write(ref observedGeneration.LastTranscription, identity);
        }

        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request))
                .ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (identity is not null && observedGeneration is not null)
            {
                RecordGeneration(UatRuntimePhase.ParentRequestIssued, observedGeneration, process, identity);
            }

            var perStageTimeout = _stageStallTimeout;
            var allowProgressExtension = string.Equals(command, "health", StringComparison.Ordinal);
            var overallMaxTimeout = allowProgressExtension
                ? TimeSpan.FromTicks(Math.Max(timeout.Ticks, _maxProgressStartupTimeout.Ticks))
                : timeout;
            var deadline = DateTimeOffset.UtcNow + overallMaxTimeout;
            var sawProgress = false;

            while (true)
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    return null;
                }

                var waitBudget = TimeSpan.FromTicks(
                    Math.Min(sawProgress ? remaining.Ticks : timeout.Ticks, perStageTimeout.Ticks));

                var line = await process.StandardOutput.ReadLineAsync(cancellationToken)
                    .AsTask()
                    .WaitAsync(waitBudget, cancellationToken)
                    .ConfigureAwait(false);
                if (line is null)
                {
                    return null;
                }

                var response = JsonSerializer.Deserialize<RuntimeWorkerResponse>(line);
                if (response is null ||
                    response.ProtocolVersion != ProtocolVersion ||
                    response.RequestId != requestId)
                {
                    return null;
                }

                if (string.Equals(response.Status, "progress", StringComparison.Ordinal))
                {
                    sawProgress = true;
                    continue;
                }

                if (identity is not null && observedGeneration is not null)
                {
                    RecordGeneration(
                        ConsumptionPhase(response, identity.SessionId),
                        observedGeneration,
                        process,
                        identity);
                }

                return response;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (identity is not null && observedGeneration is not null)
            {
                RecordGeneration(UatRuntimePhase.CallerCancellationObserved, observedGeneration, process, identity);
            }

            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or JsonException or TimeoutException or ObjectDisposedException)
        {
            return null;
        }
    }

    private static async Task TryRequestShutdownAsync(Process process)
    {
        var request = new RuntimeWorkerRequest(ProtocolVersion, Guid.NewGuid(), "shutdown");
        try
        {
            await RequestAndWaitAsync(process, request)
                .WaitAsync(TimeSpan.FromMilliseconds(500), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or TimeoutException or ObjectDisposedException)
        {
            // The worker is already gone or wedged; process-tree kill is the fallback.
        }
    }

    private static async Task RequestAndWaitAsync(Process process, RuntimeWorkerRequest request)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request))
            .ConfigureAwait(false);
        await process.StandardInput.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
    }

    internal static UatRuntimePhase ConsumptionPhase(RuntimeWorkerResponse response, Guid expectedSessionId) =>
        response.Status == "complete" && response.Transcript is { } transcript && transcript.SessionId == expectedSessionId
            ? UatRuntimePhase.ResponseConsumed
            : UatRuntimePhase.ResponseFailureConsumed;

    private sealed record UatRequestIdentity(Guid SessionId, Guid RequestId);
}
