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

        var timer = Stopwatch.StartNew();
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

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var output = await SendOnceAsync(
                        request.Input.Text,
                        readiness.Model,
                        timeout.Token).ConfigureAwait(false);
                    if (LooksLikeCodeOutput(output))
                    {
                        return Fallback(
                            request.Input,
                            PolishAttemptStatus.Failed,
                            AppErrorCode.PolishFailed,
                            timer,
                            canRetry: false);
                    }

                    output = CleanOutput(output);
                    if (output is null || !EgOnePolishProvider.IsSafeOutput(request.Input.Text, output))
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
                        new ProcessedText(request.Input.SessionId, output),
                        string.Equals(request.Input.Text, output, StringComparison.Ordinal)
                            ? PolishAttemptStatus.Unchanged
                            : PolishAttemptStatus.Polished,
                        ElapsedMilliseconds: timer.ElapsedMilliseconds);
                }
                catch (OllamaPolishException exception) when (
                    exception.CanRetry && attempt < RetryDelays.Length)
                {
                    await _delay(RetryDelays[attempt], timeout.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is HttpRequestException or IOException &&
                    attempt < RetryDelays.Length)
                {
                    await _delay(RetryDelays[attempt], timeout.Token).ConfigureAwait(false);
                }
                catch (OllamaPolishException exception)
                {
                    return Fallback(
                        request.Input,
                        StatusFor(exception.ErrorCode),
                        exception.ErrorCode,
                        timer,
                        exception.CanRetry);
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException)
                {
                    return Fallback(
                        request.Input,
                        PolishAttemptStatus.Unavailable,
                        AppErrorCode.PolishProviderUnavailable,
                        timer);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fallback(
                request.Input,
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
                request.Input,
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

        var readiness = await _apiClient.ProbeModelAsync(_options.ModelId, cancellationToken)
            .ConfigureAwait(false);
        if (readiness.Readiness != OllamaModelReadiness.Ready || readiness.Model is null)
        {
            return MishearingAdvice.None(MishearingAdviceStatus.Failed);
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

    private async Task<string> SendOnceAsync(
        string transcript,
        OllamaModelInfo model,
        CancellationToken cancellationToken) =>
        await SendOnceAsync(
            transcript,
            OllamaLocalPrompt.SystemPrompt,
            OllamaLocalPrompt.BuildUserMessage(transcript),
            model,
            cancellationToken).ConfigureAwait(false);

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
        var result = content?.Trim();
        if (string.IsNullOrWhiteSpace(result))
        {
            return null;
        }

        var firstNewline = result.IndexOf('\n');
        var firstLine = firstNewline >= 0 ? result[..firstNewline].Trim() : result;
        var lower = firstLine.ToLowerInvariant();
        var preamble = firstLine.Length < 100 && firstLine.EndsWith(':') &&
            (lower.StartsWith("here", StringComparison.Ordinal) ||
             lower.StartsWith("below", StringComparison.Ordinal) ||
             lower.StartsWith("the corrected", StringComparison.Ordinal) ||
             lower.StartsWith("the cleaned", StringComparison.Ordinal) ||
             lower.StartsWith("the polished", StringComparison.Ordinal) ||
             lower.StartsWith("corrected version", StringComparison.Ordinal));
        return preamble && firstNewline >= 0
            ? result[(firstNewline + 1)..].Trim()
            : result;
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
