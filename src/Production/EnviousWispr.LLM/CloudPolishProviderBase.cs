using System.Diagnostics;
using System.Net;
using System.Text.Json;
using EnviousWispr.Core.Credentials;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.LLM;

public abstract class CloudPolishProviderBase : IPolishProvider, IMishearingAdvisor
{
    private static readonly TimeSpan[] RetryDelays =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)];

    private readonly IApiKeyStore _apiKeyStore;
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _requestTimeout;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private bool _disposed;

    protected CloudPolishProviderBase(
        IApiKeyStore apiKeyStore,
        CloudPolishOptions options,
        HttpMessageHandler? messageHandler = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(apiKeyStore);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ModelId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            options.EffectiveRequestTimeout,
            TimeSpan.Zero);
        _apiKeyStore = apiKeyStore;
        Options = options;
        _requestTimeout = options.EffectiveRequestTimeout;
        _httpClient = messageHandler is null
            ? new HttpClient()
            : new HttpClient(messageHandler, disposeHandler: false);
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        _delay = delay ?? Task.Delay;
    }

    protected CloudPolishOptions Options { get; }

    protected HttpClient HttpClient => _httpClient;

    public abstract string ProviderId { get; }

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
        ApiKeyReadResult keyResult;
        try
        {
            keyResult = _apiKeyStore.Read(Options.Provider);
        }
        catch (Exception exception) when (exception is not (StackOverflowException or OutOfMemoryException))
        {
            return Fallback(
                request.Input,
                PolishAttemptStatus.Unavailable,
                AppErrorCode.PolishCredentialUnreadable,
                timer,
                canRetry: false);
        }

        if (keyResult.Status != ApiKeyReadStatus.Found || string.IsNullOrWhiteSpace(keyResult.Value))
        {
            var code = keyResult.Status == ApiKeyReadStatus.Missing
                ? AppErrorCode.PolishCredentialMissing
                : AppErrorCode.PolishCredentialUnreadable;
            return Fallback(
                request.Input,
                PolishAttemptStatus.Unavailable,
                code,
                timer,
                canRetry: false);
        }

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutCancellation.CancelAfter(_requestTimeout);
        var wordCount = request.Input.Text.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries).Length;
        var systemPrompt = CloudPolishPrompt.BuildSystemPrompt(
            request.DetectedLanguage,
            wordCount);
        var userMessage = CloudPolishPrompt.BuildUserMessage(request.Input.Text);

        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var output = await SendOnceAsync(
                        keyResult.Value,
                        systemPrompt,
                        userMessage,
                        timeoutCancellation.Token).ConfigureAwait(false);
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
                catch (CloudPolishException exception) when (
                    exception.CanRetry && attempt < RetryDelays.Length)
                {
                    await _delay(RetryDelays[attempt], timeoutCancellation.Token)
                        .ConfigureAwait(false);
                }
                catch (HttpRequestException) when (attempt < RetryDelays.Length)
                {
                    await _delay(RetryDelays[attempt], timeoutCancellation.Token)
                        .ConfigureAwait(false);
                }
                catch (CloudPolishException exception)
                {
                    return Fallback(
                        request.Input,
                        StatusFor(exception.ErrorCode),
                        exception.ErrorCode,
                        timer,
                        exception.CanRetry);
                }
                catch (HttpRequestException)
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
        catch (Exception exception) when (exception is JsonException or IOException or InvalidOperationException)
        {
            return Fallback(
                request.Input,
                PolishAttemptStatus.Failed,
                AppErrorCode.PolishBadRequest,
                timer,
                canRetry: false);
        }
    }

    /// <summary>
    /// Refuses to ask a cloud model what a word is likely to be misheard as.
    /// </summary>
    /// <remarks>
    /// IT USED TO ASK, AND THAT SENT THE DICTIONARY OUT. The suggestion prompt carries the spelling
    /// somebody typed AND the aliases they have already saved for it, which is their custom
    /// dictionary by another name. The Permissions page promises "dictionaries, snippets, and history
    /// stay on this PC", and a button that quietly posts them to a vendor breaks that promise as
    /// squarely as the polish path did.
    ///
    /// IT SURVIVED THE FIRST FIX BECAUSE IT IS CALLED SOMETHING ELSE. The test holding the cloud path
    /// to that promise looked for the word "Vocabulary"; this route is named "mishearing advice". A
    /// guard written around one spelling of a thing guards the spelling.
    ///
    /// THE FEATURE IS NOT LOST, IT IS LOCAL. EG-1 and Ollama both implement this and both run on
    /// this machine, which is where a question about somebody's own dictionary belongs. A cloud
    /// provider answers NotSupported, which is the same answer the app already handles for any
    /// option that cannot do this.
    ///
    /// WHAT THE OLD IMPLEMENTATION EXPLAINED, KEPT BECAUSE IT IS STILL TRUE OF THE LOCAL ONES:
    /// it reuses <see cref="SendOnceAsync"/> and nothing else from the polish path. That method is
    /// the one seam that is genuinely about talking to this vendor - the URL, the request shape, the
    /// place the answer lives in the response - and every subclass already has it right. Everything
    /// above it is polish policy: fallback text, safety comparison against the original, retries
    /// tuned for a user who is mid-dictation. None of that applies to a button press.
    ///
    /// NO RETRIES HERE, ON PURPOSE. Polishing retries because the alternative is the user losing the
    /// benefit of words they have already spoken. Nothing is at stake in a suggestion, the user is
    /// looking at the screen, and the honest answer to a failed ask is to say so immediately rather
    /// than to sit for another four seconds first.
    ///
    /// EVERY FAILURE IS THE SAME FAILURE TO THE PERSON WAITING. A missing key, a refused connection,
    /// a rate limit and a timeout all mean the suggestion did not arrive, so they are caught
    /// together rather than sorted into messages nobody can act on differently.
    /// </remarks>
    public Task<MishearingAdvice> SuggestAsync(
        string spokenForm,
        IReadOnlyList<string> existing,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(MishearingAdvice.None(MishearingAdviceStatus.NotSupported));

    protected abstract Task<string?> SendOnceAsync(
        string apiKey,
        string systemPrompt,
        string userMessage,
        CancellationToken cancellationToken);

    protected static async Task<string> ReadErrorBodyAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        var body = await content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return body.Length <= 65_536 ? body : body[..65_536];
    }

    private protected static CloudPolishException ClassifyCommonStatus(
        HttpStatusCode statusCode,
        string body,
        bool ambiguousRateOrQuota = false)
    {
        var code = (int)statusCode;
        return code switch
        {
            401 => new(AppErrorCode.PolishApiKeyRejected, canRetry: false),
            403 => new(AppErrorCode.PolishAccessDenied, canRetry: false),
            404 => new(AppErrorCode.PolishModelUnavailable, canRetry: false),
            408 or 409 => new(AppErrorCode.PolishProviderServerError, canRetry: true),
            429 when !ambiguousRateOrQuota && body.Contains(
                "insufficient_quota",
                StringComparison.OrdinalIgnoreCase) =>
                new(AppErrorCode.PolishQuotaExceeded, canRetry: false),
            429 => new(AppErrorCode.PolishRateLimited, canRetry: true),
            >= 500 and <= 599 => new(AppErrorCode.PolishProviderServerError, canRetry: true),
            >= 400 and <= 499 => new(AppErrorCode.PolishBadRequest, canRetry: false),
            _ => new(AppErrorCode.PolishFailed, canRetry: false),
        };
    }

    protected static string? CleanOutput(string? content)
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
        AppErrorCode.PolishInputTooLarge => PolishAttemptStatus.InputTooLarge,
        AppErrorCode.PolishTimedOut => PolishAttemptStatus.TimedOut,
        AppErrorCode.PolishCredentialMissing or
            AppErrorCode.PolishCredentialUnreadable or
            AppErrorCode.PolishProviderUnavailable => PolishAttemptStatus.Unavailable,
        _ => PolishAttemptStatus.Failed,
    };

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
            new AppError(errorCode, AppErrorStage.CloudPolish, canRetry),
            timer.ElapsedMilliseconds);
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _httpClient.Dispose();
        }

        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}

internal sealed class CloudPolishException(AppErrorCode errorCode, bool canRetry) : Exception
{
    public AppErrorCode ErrorCode { get; } = errorCode;

    public bool CanRetry { get; } = canRetry;
}
