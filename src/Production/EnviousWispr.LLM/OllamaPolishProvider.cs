using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.LLM;

public sealed record OllamaPolishOptions(
    string? Endpoint,
    string ModelId,
    TimeSpan? RequestTimeout = null)
{
    public TimeSpan EffectiveRequestTimeout => RequestTimeout ?? TimeSpan.FromSeconds(15);
}

public sealed class OllamaPolishProvider : IPolishProvider, IMishearingAdvisor
{
    private static readonly TimeSpan[] RetryDelays =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)];

    private readonly OllamaApiClient _apiClient;
    private readonly OllamaPolishOptions _options;
    private readonly TimeSpan _requestTimeout;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly Lock _warmupLock = new();
    private CancellationTokenSource? _activeWarmupCts;
    private bool _disposed;

    public OllamaPolishProvider(
        OllamaPolishOptions options,
        HttpMessageHandler? messageHandler = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? readinessTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _requestTimeout = options.EffectiveRequestTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_requestTimeout, TimeSpan.Zero);
        _apiClient = new OllamaApiClient(
            options.Endpoint,
            messageHandler,
            readinessTimeout);
        _delay = delay ?? Task.Delay;
    }

    public string ProviderId => "ollama";

    public IModelCatalog ModelCatalog => _apiClient;

    public Task<OllamaDiscoveryResult> ProbeHealthAsync(
        CancellationToken cancellationToken = default) =>
        _apiClient.DiscoverAsync(cancellationToken);

    public Task<OllamaModelReadinessResult> ProbeModelAsync(
        CancellationToken cancellationToken = default) =>
        _apiClient.ProbeModelAsync(_options.ModelId, cancellationToken);

    public async Task<EnviousWispr.Core.Polish.OllamaWarmupOutcome> WarmUpAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationTokenSource? previous;
        lock (_warmupLock)
        {
            previous = _activeWarmupCts;
            _activeWarmupCts = linked;
        }

        TryCancel(previous);
        try
        {
            await _requestGate.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                return await _apiClient.WarmUpAsync(_options.ModelId, linked.Token).ConfigureAwait(false);
            }
            finally
            {
                _requestGate.Release();
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
            return EnviousWispr.Core.Polish.OllamaWarmupOutcome.Cancelled;
        }
        finally
        {
            lock (_warmupLock)
            {
                if (ReferenceEquals(_activeWarmupCts, linked))
                {
                    _activeWarmupCts = null;
                }
            }
        }
    }

    private void CancelActiveWarmup()
    {
        CancellationTokenSource? active;
        lock (_warmupLock)
        {
            active = _activeWarmupCts;
            _activeWarmupCts = null;
        }

        TryCancel(active);
    }

    private static void TryCancel(CancellationTokenSource? cts)
    {
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public async Task<PolishResult> TryPolishAsync(
        PolishRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Input);
        if (string.IsNullOrWhiteSpace(request.Input.Text))
        {
            return new PolishResult(request.Input, PolishAttemptStatus.Unchanged);
        }

        CancelActiveWarmup();
        var timer = Stopwatch.StartNew();
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var readiness = await _apiClient.ProbeModelAsync(
                _options.ModelId,
                cancellationToken).ConfigureAwait(false);
            if (readiness.Readiness != OllamaModelReadiness.Ready || readiness.Model is null)
            {
                return Fallback(
                    request.Input,
                    PolishAttemptStatus.Unavailable,
                    readiness.Error ?? new AppError(
                        AppErrorCode.PolishProviderUnavailable,
                        AppErrorStage.LocalPolish,
                        CanRetry: true),
                    timer);
            }

            var maxWords = readiness.Model.IsRemote
                ? PolishChunker.MaximumCloudWordsPerChunk
                : PolishChunker.MaximumLocalWordsPerChunk;
            var maxUtf8Bytes = PolishChunker.MaximumBytesForWords(maxWords);
            var pieces = PolishChunker.SplitByBudget(request.Input.Text, maxWords, maxUtf8Bytes);
            var expectedLanguage = request.DetectedLanguage ?? request.RecognitionLanguage;
            using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptTimeout.CancelAfter(_requestTimeout);

            if (pieces.Count <= 1)
            {
                var singleResult = await PolishSegmentAsync(
                    request.Input,
                    request.Vocabulary,
                    request.RecognitionLanguage,
                    request.TargetAppName,
                    expectedLanguage,
                    readiness.Model,
                    timer,
                    applyLeadingMarkerRepair: true,
                    attemptTimeout.Token,
                    cancellationToken).ConfigureAwait(false);
                timer.Stop();
                return singleResult;
            }

            var assembled = new System.Text.StringBuilder(request.Input.Text.Length);
            foreach (var piece in pieces)
            {
                if (string.IsNullOrWhiteSpace(piece.Text))
                {
                    assembled.Append(piece.Text).Append(piece.SeparatorAfter);
                    continue;
                }

                var pieceInput = new ProcessedText(request.Input.SessionId, piece.Text);
                var pieceResult = await PolishSegmentAsync(
                    pieceInput,
                    request.Vocabulary,
                    request.RecognitionLanguage,
                    request.TargetAppName,
                    expectedLanguage,
                    readiness.Model,
                    timer,
                    applyLeadingMarkerRepair: false,
                    attemptTimeout.Token,
                    cancellationToken).ConfigureAwait(false);
                if (pieceResult.UsedFallback)
                {
                    return Fallback(
                        request.Input,
                        pieceResult.Status,
                        pieceResult.Error?.Code ?? AppErrorCode.PolishFailed,
                        timer,
                        pieceResult.Error?.CanRetry ?? true);
                }

                assembled.Append(pieceResult.Output.Text).Append(piece.SeparatorAfter);
            }

            var finalText = LeadingMarkerRepair.Repair(
                request.Input.Text,
                assembled.ToString(),
                expectedLanguage);
            if (!EgOnePolishProvider.IsSafeOutput(request.Input.Text, finalText))
            {
                return Fallback(
                    request.Input,
                    PolishAttemptStatus.Failed,
                    AppErrorCode.PolishEmptyResponse,
                    timer,
                    canRetry: false);
            }

            timer.Stop();
            return new PolishResult(
                new ProcessedText(request.Input.SessionId, finalText),
                string.Equals(request.Input.Text, finalText, StringComparison.Ordinal)
                    ? PolishAttemptStatus.Unchanged
                    : PolishAttemptStatus.Polished,
                ElapsedMilliseconds: timer.ElapsedMilliseconds);
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private async Task<PolishResult> PolishSegmentAsync(
        ProcessedText segmentInput,
        IReadOnlyList<string>? vocabulary,
        string? recognitionLanguage,
        string? targetAppName,
        string? expectedLanguage,
        OllamaModelInfo model,
        Stopwatch timer,
        bool applyLeadingMarkerRepair,
        CancellationToken timeoutToken,
        CancellationToken callerCancellationToken)
    {
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var output = await SendOnceAsync(
                        segmentInput.Text,
                        vocabulary,
                        recognitionLanguage,
                        targetAppName,
                        model,
                        timeoutToken).ConfigureAwait(false);
                    if (LooksLikeCodeOutput(output))
                    {
                        return Fallback(
                            segmentInput,
                            PolishAttemptStatus.Failed,
                            AppErrorCode.PolishFailed,
                            timer,
                            canRetry: false);
                    }

                    output = CleanOutput(output);
                    if (output is null)
                    {
                        return Fallback(
                            segmentInput,
                            PolishAttemptStatus.Failed,
                            AppErrorCode.PolishEmptyResponse,
                            timer,
                            canRetry: false);
                    }

                    if (applyLeadingMarkerRepair)
                    {
                        output = LeadingMarkerRepair.Repair(segmentInput.Text, output, expectedLanguage);
                    }

                    if (!EgOnePolishProvider.IsSafeOutput(segmentInput.Text, output))
                    {
                        return Fallback(
                            segmentInput,
                            PolishAttemptStatus.Failed,
                            AppErrorCode.PolishEmptyResponse,
                            timer,
                            canRetry: false);
                    }

                    return new PolishResult(
                        new ProcessedText(segmentInput.SessionId, output),
                        string.Equals(segmentInput.Text, output, StringComparison.Ordinal)
                            ? PolishAttemptStatus.Unchanged
                            : PolishAttemptStatus.Polished,
                        ElapsedMilliseconds: timer.ElapsedMilliseconds);
                }
                catch (OllamaPolishException exception) when (
                    exception.CanRetry && attempt < RetryDelays.Length)
                {
                    await _delay(RetryDelays[attempt], timeoutToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is HttpRequestException or IOException &&
                    attempt < RetryDelays.Length)
                {
                    await _delay(RetryDelays[attempt], timeoutToken).ConfigureAwait(false);
                }
                catch (OllamaPolishException exception)
                {
                    return Fallback(
                        segmentInput,
                        StatusFor(exception.ErrorCode),
                        exception.ErrorCode,
                        timer,
                        exception.CanRetry);
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException)
                {
                    return Fallback(
                        segmentInput,
                        PolishAttemptStatus.Unavailable,
                        AppErrorCode.PolishProviderUnavailable,
                        timer);
                }
            }
        }
        catch (OperationCanceledException) when (!callerCancellationToken.IsCancellationRequested)
        {
            return Fallback(
                segmentInput,
                PolishAttemptStatus.TimedOut,
                AppErrorCode.PolishTimedOut,
                timer);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException)
        {
            return Fallback(
                segmentInput,
                PolishAttemptStatus.Failed,
                AppErrorCode.PolishBadRequest,
                timer,
                canRetry: false);
        }
    }

    /// <summary>Asks the local model what a word is likely to be misheard as.</summary>
    /// <remarks>
    /// The same shape as the cloud providers': probe, one call, parse, and no retries, because
    /// nothing is at stake in a suggestion and the user is looking at the screen while it runs.
    ///
    /// A model that is not installed or not running is reported as Failed rather than NotSupported.
    /// The distinction matters to the person reading the message - NotSupported means "this choice
    /// can never do this", which is a reason to switch, while Failed means "it did not work this
    /// time", which is a reason to check Ollama is running and press it again.
    /// </remarks>
    public async Task<MishearingAdvice> SuggestAsync(
        string spokenForm,
        IReadOnlyList<string> existing,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(existing);
        if (string.IsNullOrWhiteSpace(spokenForm))
        {
            return MishearingAdvice.None(MishearingAdviceStatus.NothingUsable);
        }

        CancelActiveWarmup();
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var readiness = await _apiClient.ProbeModelAsync(_options.ModelId, cancellationToken)
                .ConfigureAwait(false);
            if (readiness.Readiness != OllamaModelReadiness.Ready || readiness.Model is null)
            {
                return MishearingAdvice.None(MishearingAdviceStatus.Failed);
            }

            if (readiness.Model.IsRemote)
            {
                return MishearingAdvice.None(MishearingAdviceStatus.NotSupported);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_requestTimeout);

            string reply;
            try
            {
                reply = await SendOnceAsync(
                    spokenForm,
                    AliasSuggestionPrompt.SystemPrompt,
                    AliasSuggestionPrompt.BuildUserMessage(spokenForm, existing),
                    readiness.Model,
                    timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is OllamaPolishException or HttpRequestException or OperationCanceledException
                    or JsonException)
            {
                return MishearingAdvice.None(MishearingAdviceStatus.Failed);
            }

            var suggestions = AliasSuggestions.Parse(reply, spokenForm, existing);
            return suggestions.Count == 0
                ? MishearingAdvice.None(MishearingAdviceStatus.NothingUsable)
                : new MishearingAdvice(MishearingAdviceStatus.Suggested, suggestions);
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private async Task<string> SendOnceAsync(
        string transcript,
        IReadOnlyList<string>? vocabulary,
        string? recognitionLanguage,
        string? targetAppName,
        OllamaModelInfo model,
        CancellationToken cancellationToken)
    {
        if (model.IsRemote)
        {
            var wordCount = transcript.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries).Length;
            return await SendOnceAsync(
                transcript,
                CloudPolishPrompt.BuildSystemPrompt(recognitionLanguage, wordCount, targetAppName),
                CloudPolishPrompt.BuildUserMessage(transcript),
                model,
                cancellationToken).ConfigureAwait(false);
        }

        return await SendOnceAsync(
            transcript,
            OllamaLocalPrompt.BuildSystemPrompt(vocabulary, recognitionLanguage, targetAppName),
            OllamaLocalPrompt.BuildUserMessage(transcript, vocabulary),
            model,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One call to the local model, with the instruction supplied by the caller.
    /// </summary>
    /// <remarks>
    /// The prompt used to be read from a constant inside this method, which meant the only thing
    /// this model could ever be asked was to clean a transcript. Passing it in changes nothing about
    /// the polish path - it hands over exactly the constants that were hard-coded here - and lets
    /// the same connection answer a different question.
    ///
    /// <paramref name="transcript"/> stays a separate argument from the user message because it
    /// sizes the reply budget, and the two are not the same string once the caller is asking a
    /// question rather than submitting text to be rewritten.
    /// </remarks>
    private async Task<string> SendOnceAsync(
        string transcript,
        string systemPrompt,
        string userMessage,
        OllamaModelInfo model,
        CancellationToken cancellationToken)
    {
        var endpoint = _apiClient.Endpoint ??
            throw new OllamaPolishException(AppErrorCode.PolishEndpointInvalid, canRetry: false);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "api/chat"));
        var floor = model.SupportsThinking == false ? 256 : 2_048;
        var maximumOutputTokens = Math.Max(transcript.Length / 3 + 100, floor);
        var body = new Dictionary<string, object?>
        {
            ["model"] = model.Id,
            ["messages"] = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage },
            },
            ["stream"] = false,
            ["keep_alive"] = "60m",
            ["options"] = new
            {
                num_predict = maximumOutputTokens,
                temperature = 0,
            },
        };
        if (model.SupportsThinking == true)
        {
            body["think"] = "low";
        }

        request.Content = JsonContent.Create(body);
        using var response = await _apiClient.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw Classify(response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (root.TryGetProperty("done_reason", out var doneReason) &&
            doneReason.ValueKind == JsonValueKind.String &&
            !string.Equals(doneReason.GetString(), "stop", StringComparison.OrdinalIgnoreCase))
        {
            throw new OllamaPolishException(
                AppErrorCode.PolishOutputTruncated,
                canRetry: false);
        }

        if (!root.TryGetProperty("message", out var message) ||
            message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(content.GetString()))
        {
            throw new OllamaPolishException(AppErrorCode.PolishEmptyResponse, canRetry: false);
        }

        return content.GetString()!;
    }

    private static OllamaPolishException Classify(HttpStatusCode statusCode) =>
        (int)statusCode switch
        {
            401 => new(AppErrorCode.PolishApiKeyRejected, canRetry: false),
            402 => new(AppErrorCode.PolishQuotaExceeded, canRetry: false),
            403 => new(AppErrorCode.PolishAccessDenied, canRetry: false),
            404 => new(AppErrorCode.PolishModelUnavailable, canRetry: false),
            408 or 409 or 429 => new(AppErrorCode.PolishProviderServerError, canRetry: true),
            >= 500 and <= 599 => new(AppErrorCode.PolishProviderServerError, canRetry: true),
            >= 400 and <= 499 => new(AppErrorCode.PolishBadRequest, canRetry: false),
            _ => new(AppErrorCode.PolishFailed, canRetry: false),
        };

    internal static bool LooksLikeCodeOutput(string output)
    {
        var trimmed = output.TrimStart();
        return trimmed.StartsWith("```", StringComparison.Ordinal) ||
            trimmed.StartsWith("~~~", StringComparison.Ordinal);
    }

    internal static string? CleanOutput(string? content)
    {
        // PREAMBLE STRIPPING LIVES IN ONE PLACE NOW, AND IT IS NOT HERE. This ran before the shared
        // guard and without the person's own words, so it deleted a heading somebody actually
        // dictated - "Here is the plan:" - and nothing downstream could tell it had happened.
        // PolishOutputGuard.StripPreamble knows what was said and is the only place allowed to
        // remove a line because of how it looks.
        var result = content?.Trim();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private static PolishAttemptStatus StatusFor(AppErrorCode errorCode) => errorCode switch
    {
        AppErrorCode.PolishTimedOut => PolishAttemptStatus.TimedOut,
        AppErrorCode.PolishEndpointInvalid or
            AppErrorCode.PolishProviderUnavailable or
            AppErrorCode.PolishModelUnavailable or
            AppErrorCode.PolishRemoteModelDisallowed => PolishAttemptStatus.Unavailable,
        _ => PolishAttemptStatus.Failed,
    };

    private static PolishResult Fallback(
        ProcessedText input,
        PolishAttemptStatus status,
        AppError error,
        Stopwatch timer) =>
        Fallback(input, status, error.Code, timer, error.CanRetry);

    private static PolishResult Fallback(
        ProcessedText input,
        PolishAttemptStatus status,
        AppErrorCode errorCode,
        Stopwatch timer,
        bool canRetry = true)
    {
        timer.Stop();
        return new PolishResult(
            input,
            status,
            new AppError(errorCode, AppErrorStage.LocalPolish, canRetry),
            timer.ElapsedMilliseconds);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            CancelActiveWarmup();
            await _apiClient.DisposeAsync().ConfigureAwait(false);
        }

        GC.SuppressFinalize(this);
    }
}

internal sealed class OllamaPolishException(AppErrorCode errorCode, bool canRetry) : Exception
{
    public AppErrorCode ErrorCode { get; } = errorCode;

    public bool CanRetry { get; } = canRetry;
}
