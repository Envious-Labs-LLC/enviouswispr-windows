using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Networking;
using EnviousWispr.Core.Polish;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.LLM;

public enum EgOneHealth
{
    Green,
    Yellow,
    Red,
}

public sealed record EgOneHealthResult(EgOneHealth Health, string Reason, long ElapsedMilliseconds);

public sealed record EgOnePolishOptions(
    EgOneServerOptions Server,
    string ModelId = "eg-1",
    TimeSpan? InferenceTimeout = null)
{
    public TimeSpan EffectiveInferenceTimeout => InferenceTimeout ?? TimeSpan.FromSeconds(15);
}

internal enum EgOneJudgeCallStatus
{
    Completed,
    Unavailable,
    NotGranted,
    Deadline,
    Cancelled,
    Failed,
}

internal readonly record struct EgOneJudgeCallResult(
    EgOneJudgeCallStatus Status,
    string? Content);

public sealed class EgOnePolishProvider : IPolishProvider, IMishearingAdvisor
{
    private readonly IEgOneRuntime _runtime;
    private readonly HttpClient _httpClient;
    private readonly string _modelId;
    private readonly TimeSpan _inferenceTimeout;
    private readonly SemaphoreSlim _slotGate = new(1, 1);
    private readonly Lock _judgeSlotLock = new();
    private CancellationTokenSource? _activeJudgeCts;
    private int _polishInFlight;
    private bool _disposed;

    public EgOnePolishProvider(
        EgOnePolishOptions options,
        HttpMessageHandler? messageHandler = null)
        : this(
            new EgOneServerManager(options?.Server ?? throw new ArgumentNullException(nameof(options))),
            options,
            messageHandler)
    {
    }

    internal EgOnePolishProvider(
        IEgOneRuntime runtime,
        EgOnePolishOptions options,
        HttpMessageHandler? messageHandler = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ModelId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            options.EffectiveInferenceTimeout,
            TimeSpan.Zero);
        _runtime = runtime;
        _modelId = options.ModelId;
        _inferenceTimeout = options.EffectiveInferenceTimeout;
        _httpClient = SharedHttpClients.CreateClient(messageHandler);
    }

    public string ProviderId => "eg-one";

    public string ModelId => _modelId;

    public bool IsLocalRuntimeReady => !_disposed && _runtime.TryGetReadyEndpoint() is not null;

    public EgOneServerActivity ServerActivity => _disposed ? EgOneServerActivity.Inactive : _runtime.ServerActivity;

    public event EventHandler? ServerActivityChanged
    {
        add => _runtime.ServerActivityChanged += value;
        remove => _runtime.ServerActivityChanged -= value;
    }

    public int? OwnedProcessId => _runtime.OwnedProcessId;

    public int? SleepIdleSeconds => _runtime.SleepIdleSeconds;

    public bool? UsesAccelerator => (_runtime as EgOneServerManager)?.IsUsingAccelerator;

    public static TimeSpan? ResolveKeepAwakeInterval(int? sleepIdleSeconds)
    {
        if (sleepIdleSeconds is not > 0)
        {
            return null;
        }

        var intervalSeconds = Math.Clamp(sleepIdleSeconds.Value / 2, 2, 30);
        return TimeSpan.FromSeconds(intervalSeconds);
    }

    public void TerminateRuntimeImmediately() => _runtime.TerminateImmediately();

    public Task<bool> TryPreWakeAsync(CancellationToken cancellationToken = default) =>
        _disposed
            ? Task.FromResult(false)
            : _runtime.TryPreWakeAsync(cancellationToken);

    internal static TimeSpan ResolveInferenceTimeout(TimeSpan configuredTimeout, int characterCount)
    {
        if (configuredTimeout < TimeSpan.FromSeconds(15) || characterCount <= 0)
        {
            return configuredTimeout;
        }

        var baseSeconds = configuredTimeout.TotalSeconds;
        var effectiveCap = Math.Max(baseSeconds, 90.0);
        var scaledSeconds = Math.Clamp(baseSeconds + (characterCount / 200.0), baseSeconds, effectiveCap);
        return TimeSpan.FromSeconds(scaledSeconds);
    }

    public async Task<PolishResult> TryPolishAsync(
        PolishRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Input);
        var input = request.Input;
        if (string.IsNullOrWhiteSpace(input.Text))
        {
            return new PolishResult(input, PolishAttemptStatus.Unchanged);
        }

        var timer = Stopwatch.StartNew();
        EnterCriticalSlotPriority();
        try
        {
            var endpoint = await _runtime.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
            if (endpoint is null)
            {
                return Fallback(
                    input,
                    PolishAttemptStatus.Unavailable,
                    AppErrorCode.PolishProviderUnavailable,
                    timer);
            }

            var expectedLanguage = request.DetectedLanguage ?? request.RecognitionLanguage;
            var systemPrompt = EgOnePrompt.BuildSystemPrompt(expectedLanguage);
            var userMessage = EgOnePrompt.BuildUserMessage(input.Text);
            var maximumOutputTokens = Math.Max(input.Text.Length, 256);
            if (FitsContext(endpoint.ContextTokens, systemPrompt, userMessage, maximumOutputTokens) &&
                PolishChunker.CountWords(input.Text.AsSpan()) <= PolishChunker.MaximumLocalWordsPerChunk &&
                System.Text.Encoding.UTF8.GetByteCount(input.Text) <=
                    PolishChunker.MaximumBytesForWords(PolishChunker.MaximumLocalWordsPerChunk))
            {
                var single = await PolishSingleWithSlotAsync(
                    endpoint,
                    input,
                    systemPrompt,
                    userMessage,
                    maximumOutputTokens,
                    timer,
                    cancellationToken).ConfigureAwait(false);
                if (!single.UsedFallback)
                {
                    var repaired = LeadingMarkerRepair.Repair(input.Text, single.Output.Text, expectedLanguage);
                    if (!string.Equals(repaired, single.Output.Text, StringComparison.Ordinal) &&
                        IsSafeOutput(input.Text, repaired))
                    {
                        single = new PolishResult(
                            new ProcessedText(input.SessionId, repaired),
                            string.Equals(input.Text, repaired, StringComparison.Ordinal)
                                ? PolishAttemptStatus.Unchanged
                                : PolishAttemptStatus.Polished,
                            ElapsedMilliseconds: timer.ElapsedMilliseconds);
                    }
                }

                timer.Stop();
                return single;
            }

            var chunkBudget = ResolveChunkCharacterBudget(endpoint.ContextTokens, systemPrompt.Length);
            if (chunkBudget <= 0)
            {
                return Fallback(
                    input,
                    PolishAttemptStatus.InputTooLarge,
                    AppErrorCode.PolishInputTooLarge,
                    timer,
                    canRetry: false);
            }

            var pieces = PolishChunker.Split(
                input.Text,
                chunkBudget,
                PolishChunker.MaximumLocalWordsPerChunk,
                PolishChunker.MaximumBytesForWords(PolishChunker.MaximumLocalWordsPerChunk));
            if (pieces.Count <= 1 && !FitsContext(endpoint.ContextTokens, systemPrompt, userMessage, maximumOutputTokens))
            {
                return Fallback(
                    input,
                    PolishAttemptStatus.InputTooLarge,
                    AppErrorCode.PolishInputTooLarge,
                    timer,
                    canRetry: false);
            }

            var assembled = new System.Text.StringBuilder(input.Text.Length);
            foreach (var piece in pieces)
            {
                if (string.IsNullOrWhiteSpace(piece.Text))
                {
                    assembled.Append(piece.Text).Append(piece.SeparatorAfter);
                    continue;
                }

                var pieceMessage = EgOnePrompt.BuildUserMessage(piece.Text);
                var pieceOutputTokens = Math.Max(piece.Text.Length, 256);
                if (!FitsContext(endpoint.ContextTokens, systemPrompt, pieceMessage, pieceOutputTokens))
                {
                    return Fallback(
                        input,
                        PolishAttemptStatus.InputTooLarge,
                        AppErrorCode.PolishInputTooLarge,
                        timer,
                        canRetry: false);
                }

                var pieceInput = new ProcessedText(input.SessionId, piece.Text);
                var pieceResult = await PolishSingleWithSlotAsync(
                    endpoint,
                    pieceInput,
                    systemPrompt,
                    pieceMessage,
                    pieceOutputTokens,
                    timer,
                    cancellationToken).ConfigureAwait(false);
                if (pieceResult.UsedFallback)
                {
                    return Fallback(
                        input,
                        pieceResult.Status,
                        pieceResult.Error?.Code ?? AppErrorCode.PolishFailed,
                        timer,
                        pieceResult.Error?.CanRetry ?? true);
                }

                assembled.Append(pieceResult.Output.Text).Append(piece.SeparatorAfter);
            }

            timer.Stop();
            var finalText = LeadingMarkerRepair.Repair(input.Text, assembled.ToString(), expectedLanguage);
            if (!IsSafeOutput(input.Text, finalText))
            {
                return Fallback(
                    input,
                    PolishAttemptStatus.Failed,
                    AppErrorCode.PolishFailed,
                    timer,
                    canRetry: false);
            }

            var overallStatus = string.Equals(input.Text, finalText, StringComparison.Ordinal)
                ? PolishAttemptStatus.Unchanged
                : PolishAttemptStatus.Polished;
            return new PolishResult(
                new ProcessedText(input.SessionId, finalText),
                overallStatus,
                ElapsedMilliseconds: timer.ElapsedMilliseconds);
        }
        finally
        {
            Interlocked.Decrement(ref _polishInFlight);
        }
    }

    internal static int ResolveChunkCharacterBudget(int contextTokens, int systemPromptCharacters)
    {
        if (contextTokens < 4_096)
        {
            return 0;
        }

        // Leave room for system prompt tokens, <TRANSCRIPT> wrapper (~32 chars), and fixed 256 margin.
        var reservedTokens = ((systemPromptCharacters + 32 + 3) / 4) + 256;
        var availableTokens = contextTokens - reservedTokens;
        if (availableTokens < 1_024)
        {
            return 0;
        }

        // Output tokens budget 1 per character + prompt tokens ~0.25 per character => ~1.25 tokens/char.
        return Math.Clamp((int)(availableTokens / 1.3), 512, 5_000);
    }

    private async Task<PolishResult> PolishSingleWithSlotAsync(
        EgOneEndpoint endpoint,
        ProcessedText input,
        string systemPrompt,
        string userMessage,
        int maximumOutputTokens,
        Stopwatch timer,
        CancellationToken cancellationToken)
    {
        var slotAcquired = false;
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(ResolveInferenceTimeout(_inferenceTimeout, input.Text.Length));
        try
        {
            await _slotGate.WaitAsync(timeoutCancellation.Token).ConfigureAwait(false);
            slotAcquired = true;

            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (attempt > 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(750), timeoutCancellation.Token)
                        .ConfigureAwait(false);
                    var refreshed = await _runtime.EnsureReadyAsync(timeoutCancellation.Token)
                        .ConfigureAwait(false);
                    if (refreshed is null)
                    {
                        break;
                    }

                    endpoint = refreshed;
                }

                try
                {
                    _runtime.BeginModelRequest();
                    string? parsed;
                    try
                    {
                        parsed = await SendAsync(
                            endpoint,
                            systemPrompt,
                            userMessage,
                            maximumOutputTokens,
                            timeoutCancellation.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        _runtime.EndModelRequest();
                    }
                    if (parsed is null || !IsSafeOutput(input.Text, parsed))
                    {
                        return Fallback(
                            input,
                            PolishAttemptStatus.Failed,
                            AppErrorCode.PolishFailed,
                            timer);
                    }

                    var status = string.Equals(input.Text, parsed, StringComparison.Ordinal)
                        ? PolishAttemptStatus.Unchanged
                        : PolishAttemptStatus.Polished;
                    return new PolishResult(
                        new ProcessedText(input.SessionId, parsed),
                        status,
                        ElapsedMilliseconds: timer.ElapsedMilliseconds);
                }
                catch (HttpRequestException) when (attempt == 0)
                {
                    // One retry covers a single owned-server crash or a transient loopback reset.
                }
            }

            return Fallback(
                input,
                PolishAttemptStatus.Failed,
                AppErrorCode.PolishFailed,
                timer);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fallback(
                input,
                PolishAttemptStatus.TimedOut,
                AppErrorCode.PolishTimedOut,
                timer);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or JsonException or IOException or InvalidOperationException)
        {
            return Fallback(
                input,
                PolishAttemptStatus.Failed,
                AppErrorCode.PolishFailed,
                timer);
        }
        finally
        {
            if (slotAcquired)
            {
                _slotGate.Release();
            }
        }
    }

    public async Task<EgOneHealthResult> ProbeHealthAsync(
        CancellationToken cancellationToken = default)
    {
        var sessionId = DictationSessionId.Create();
        const string probe = "so um move the meeting to thursday no wait friday";
        var result = await TryPolishAsync(
            new PolishRequest(new ProcessedText(sessionId, probe), "en"),
            cancellationToken).ConfigureAwait(false);
        if (result.UsedFallback)
        {
            return new EgOneHealthResult(EgOneHealth.Red, "probe_failed", result.ElapsedMilliseconds);
        }

        var output = result.Output.Text;
        var transformed = output.Contains("friday", StringComparison.OrdinalIgnoreCase) &&
            !output.Contains("thursday", StringComparison.OrdinalIgnoreCase) &&
            !output.Contains("no wait", StringComparison.OrdinalIgnoreCase) &&
            !ContainsWholeWord(output, "um");
        if (!transformed)
        {
            return new EgOneHealthResult(
                EgOneHealth.Yellow,
                "probe_output_unexpected",
                result.ElapsedMilliseconds);
        }

        return result.ElapsedMilliseconds > 5_000
            ? new EgOneHealthResult(EgOneHealth.Yellow, "probe_slow", result.ElapsedMilliseconds)
            : new EgOneHealthResult(EgOneHealth.Green, "ready", result.ElapsedMilliseconds);
    }

    internal static bool FitsContext(int contextTokens, string userMessage, int outputTokens) =>
        FitsContext(contextTokens, EgOnePrompt.SystemPrompt, userMessage, outputTokens);

    internal static bool FitsContext(int contextTokens, string systemPrompt, string userMessage, int outputTokens)
    {
        var promptCharacters = systemPrompt.Length + userMessage.Length;
        var estimatedPromptTokens = (promptCharacters + 3) / 4;
        return estimatedPromptTokens + outputTokens + 256 <= contextTokens;
    }

    internal static bool IsSafeOutput(string input, string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        var maximumLength = Math.Max(input.Length * 4L, input.Length + 512L);
        if (output.Length > maximumLength)
        {
            return false;
        }

        return input.Length < 80 || output.Length >= input.Length / 5;
    }

    /// <summary>Asks the built-in model what a word is likely to be misheard as.</summary>
    /// <remarks>
    /// The built-in model gets this too, and that decides whether the feature exists for most
    /// people. It is the default polish choice, so leaving it out would have shipped a button that
    /// says "not available with this option" to the majority of users while technically counting as
    /// done.
    ///
    /// One attempt, no retry loop, and no context-size check. A word and a short instruction cannot
    /// approach the context limit, and the polish path's retry exists because a user has already
    /// spoken and would otherwise lose the benefit of it. Nothing is lost here but a button press.
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

        EnterCriticalSlotPriority();
        var slotAcquired = false;
        string? reply;
        try
        {
            var endpoint = await _runtime.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
            if (endpoint is null)
            {
                return MishearingAdvice.None(MishearingAdviceStatus.Failed);
            }

            using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeoutCancellation.CancelAfter(_inferenceTimeout);

            await _slotGate.WaitAsync(timeoutCancellation.Token).ConfigureAwait(false);
            slotAcquired = true;

            _runtime.BeginModelRequest();
            try
            {
                reply = await SendAsync(
                    endpoint,
                    AliasSuggestionPrompt.SystemPrompt,
                    AliasSuggestionPrompt.BuildUserMessage(spokenForm, existing),
                    maximumOutputTokens: 256,
                    timeoutCancellation.Token,
                    cachePrompt: false).ConfigureAwait(false);
            }
            finally
            {
                _runtime.EndModelRequest();
            }
        }
        catch (Exception exception) when (
            exception is HttpRequestException or OperationCanceledException or JsonException)
        {
            return MishearingAdvice.None(MishearingAdviceStatus.Failed);
        }
        finally
        {
            if (slotAcquired)
            {
                _slotGate.Release();
            }

            Interlocked.Decrement(ref _polishInFlight);
        }

        if (reply is null)
        {
            return MishearingAdvice.None(MishearingAdviceStatus.Failed);
        }

        var suggestions = AliasSuggestions.Parse(reply, spokenForm, existing);
        return suggestions.Count == 0
            ? MishearingAdvice.None(MishearingAdviceStatus.NothingUsable)
            : new MishearingAdvice(MishearingAdviceStatus.Suggested, suggestions);
    }

    internal async Task<EgOneJudgeCallResult> TryCompleteJudgeJsonAsync(
        string systemPrompt,
        string userMessage,
        object responseFormat,
        int maximumOutputTokens,
        TimeSpan deadline,
        CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return new EgOneJudgeCallResult(EgOneJudgeCallStatus.Unavailable, null);
        }

        if (Volatile.Read(ref _polishInFlight) > 0)
        {
            return new EgOneJudgeCallResult(EgOneJudgeCallStatus.NotGranted, null);
        }

        if (_runtime.TryGetReadyEndpoint() is not { } endpoint)
        {
            return new EgOneJudgeCallResult(EgOneJudgeCallStatus.Unavailable, null);
        }

        if (!_slotGate.Wait(0, CancellationToken.None))
        {
            return new EgOneJudgeCallResult(EgOneJudgeCallStatus.NotGranted, null);
        }

        CancellationTokenSource? timeoutCts = null;
        try
        {
            timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (_judgeSlotLock)
            {
                if (_disposed || Volatile.Read(ref _polishInFlight) > 0)
                {
                    return new EgOneJudgeCallResult(EgOneJudgeCallStatus.NotGranted, null);
                }

                _activeJudgeCts = timeoutCts;
            }

            timeoutCts.CancelAfter(deadline);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.ChatCompletionsUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.AuthToken);
            request.Content = JsonContent.Create(new
            {
                model = _modelId,
                messages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userMessage },
                },
                max_tokens = maximumOutputTokens,
                temperature = 0,
                cache_prompt = false,
                id_slot = 0,
                response_format = responseFormat,
            });
            _runtime.BeginModelRequest();
            try
            {
                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutCts.Token).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    return new EgOneJudgeCallResult(EgOneJudgeCallStatus.Failed, null);
                }

                await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token)
                    .ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(
                    stream,
                    cancellationToken: timeoutCts.Token).ConfigureAwait(false);
                if (!document.RootElement.TryGetProperty("choices", out var choices) ||
                    choices.ValueKind != JsonValueKind.Array ||
                    choices.GetArrayLength() == 0)
                {
                    return new EgOneJudgeCallResult(EgOneJudgeCallStatus.Failed, null);
                }

                var choice = choices[0];
                if (choice.TryGetProperty("finish_reason", out var finishReason) &&
                    string.Equals(finishReason.GetString(), "length", StringComparison.Ordinal))
                {
                    return new EgOneJudgeCallResult(EgOneJudgeCallStatus.Failed, null);
                }

                if (!choice.TryGetProperty("message", out var message) ||
                    !message.TryGetProperty("content", out var content))
                {
                    return new EgOneJudgeCallResult(EgOneJudgeCallStatus.Failed, null);
                }

                var raw = content.GetString()?.Trim();
                return string.IsNullOrWhiteSpace(raw)
                    ? new EgOneJudgeCallResult(EgOneJudgeCallStatus.Failed, null)
                    : new EgOneJudgeCallResult(EgOneJudgeCallStatus.Completed, raw);
            }
            finally
            {
                _runtime.EndModelRequest();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new EgOneJudgeCallResult(EgOneJudgeCallStatus.Cancelled, null);
        }
        catch (OperationCanceledException) when (Volatile.Read(ref _polishInFlight) > 0)
        {
            return new EgOneJudgeCallResult(EgOneJudgeCallStatus.NotGranted, null);
        }
        catch (OperationCanceledException)
        {
            return new EgOneJudgeCallResult(EgOneJudgeCallStatus.Deadline, null);
        }
        catch (ObjectDisposedException)
        {
            return new EgOneJudgeCallResult(EgOneJudgeCallStatus.Unavailable, null);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or JsonException or IOException or InvalidOperationException)
        {
            return new EgOneJudgeCallResult(EgOneJudgeCallStatus.Failed, null);
        }
        finally
        {
            lock (_judgeSlotLock)
            {
                if (timeoutCts is not null && ReferenceEquals(_activeJudgeCts, timeoutCts))
                {
                    _activeJudgeCts = null;
                }
            }

            timeoutCts?.Dispose();
            _slotGate.Release();
        }
    }

    private void EnterCriticalSlotPriority()
    {
        Interlocked.Increment(ref _polishInFlight);
        CancelActiveJudge();
    }

    private void CancelActiveJudge()
    {
        CancellationTokenSource? judgeCts;
        lock (_judgeSlotLock)
        {
            judgeCts = _activeJudgeCts;
        }

        if (judgeCts is not null)
        {
            try
            {
                judgeCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>
    /// One call to the built-in model, with the instruction supplied by the caller.
    /// </summary>
    /// <remarks>
    /// The instruction used to be read from a constant inside this method, so the only thing the
    /// model could be asked was to clean a transcript. Passing it in changes nothing on the polish
    /// path, which hands over the same constant that was hard-coded here.
    /// </remarks>
    private async Task<string?> SendAsync(
        EgOneEndpoint endpoint,
        string systemPrompt,
        string userMessage,
        int maximumOutputTokens,
        CancellationToken cancellationToken,
        bool cachePrompt = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.ChatCompletionsUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.AuthToken);
        request.Content = JsonContent.Create(new
        {
            model = _modelId,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage },
            },
            max_tokens = maximumOutputTokens,
            temperature = 0,
            cache_prompt = cachePrompt,
            id_slot = 0,
        });
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            return null;
        }

        var choice = choices[0];
        if (choice.TryGetProperty("finish_reason", out var finishReason) &&
            string.Equals(finishReason.GetString(), "length", StringComparison.Ordinal))
        {
            return null;
        }

        if (!choice.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content))
        {
            return null;
        }

        return CleanOutput(content.GetString());
    }

    internal static string? CleanOutput(string? content)
    {
        var cleaned = content?.Trim();
        if (string.IsNullOrEmpty(cleaned))
        {
            return null;
        }

        foreach (var (opening, closing) in new[]
        {
            ("<TRANSCRIPT>", "</TRANSCRIPT>"),
            ("<transcript>", "</transcript>"),
        })
        {
            if (cleaned.StartsWith(opening, StringComparison.Ordinal) &&
                cleaned.EndsWith(closing, StringComparison.Ordinal))
            {
                cleaned = cleaned[opening.Length..^closing.Length].Trim();
            }
        }

        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    private static bool ContainsWholeWord(string text, string word)
    {
        var index = text.IndexOf(word, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var beforeBoundary = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
            var after = index + word.Length;
            var afterBoundary = after == text.Length || !char.IsLetterOrDigit(text[after]);
            if (beforeBoundary && afterBoundary)
            {
                return true;
            }

            index = text.IndexOf(word, index + 1, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

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
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelActiveJudge();
        _httpClient.Dispose();
        await _runtime.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
