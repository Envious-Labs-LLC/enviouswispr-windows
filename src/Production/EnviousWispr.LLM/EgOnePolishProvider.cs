using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
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

public sealed class EgOnePolishProvider : IPolishProvider, IMishearingAdvisor
{
    private readonly IEgOneRuntime _runtime;
    private readonly HttpClient _httpClient;
    private readonly string _modelId;
    private readonly TimeSpan _inferenceTimeout;
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
        _httpClient = messageHandler is null
            ? new HttpClient()
            : new HttpClient(messageHandler, disposeHandler: false);
    }

    public string ProviderId => "eg-one";

    public void TerminateRuntimeImmediately() => _runtime.TerminateImmediately();

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
        var endpoint = await _runtime.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        if (endpoint is null)
        {
            return Fallback(
                input,
                PolishAttemptStatus.Unavailable,
                AppErrorCode.PolishProviderUnavailable,
                timer);
        }

        var userMessage = EgOnePrompt.BuildUserMessage(input.Text);
        var maximumOutputTokens = Math.Max(input.Text.Length, 256);
        if (!FitsContext(endpoint.ContextTokens, userMessage, maximumOutputTokens))
        {
            return Fallback(
                input,
                PolishAttemptStatus.InputTooLarge,
                AppErrorCode.PolishInputTooLarge,
                timer,
                canRetry: false);
        }

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(_inferenceTimeout);
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (attempt > 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(750), timeoutCancellation.Token)
                        .ConfigureAwait(false);
                    endpoint = await _runtime.EnsureReadyAsync(timeoutCancellation.Token)
                        .ConfigureAwait(false);
                    if (endpoint is null)
                    {
                        break;
                    }
                }

                try
                {
                    var parsed = await SendAsync(
                        endpoint,
                        userMessage,
                        maximumOutputTokens,
                        timeoutCancellation.Token).ConfigureAwait(false);
                    if (parsed is null || !IsSafeOutput(input.Text, parsed))
                    {
                        return Fallback(
                            input,
                            PolishAttemptStatus.Failed,
                            AppErrorCode.PolishFailed,
                            timer);
                    }

                    timer.Stop();
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

    internal static bool FitsContext(int contextTokens, string userMessage, int outputTokens)
    {
        var promptCharacters = EgOnePrompt.SystemPrompt.Length + userMessage.Length;
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

        var endpoint = await _runtime.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        if (endpoint is null)
        {
            return MishearingAdvice.None(MishearingAdviceStatus.Failed);
        }

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutCancellation.CancelAfter(_inferenceTimeout);

        string? reply;
        try
        {
            reply = await SendAsync(
                endpoint,
                AliasSuggestionPrompt.SystemPrompt,
                AliasSuggestionPrompt.BuildUserMessage(spokenForm, existing),
                maximumOutputTokens: 256,
                timeoutCancellation.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or OperationCanceledException or JsonException)
        {
            return MishearingAdvice.None(MishearingAdviceStatus.Failed);
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

    private async Task<string?> SendAsync(
        EgOneEndpoint endpoint,
        string userMessage,
        int maximumOutputTokens,
        CancellationToken cancellationToken) =>
        await SendAsync(
            endpoint,
            EgOnePrompt.SystemPrompt,
            userMessage,
            maximumOutputTokens,
            cancellationToken).ConfigureAwait(false);

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
        CancellationToken cancellationToken)
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
        _httpClient.Dispose();
        await _runtime.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
