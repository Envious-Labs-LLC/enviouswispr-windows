using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Polish;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.LLM;
using EnviousWispr.Pipeline;
using EnviousWispr.Presentation;
using EnviousWispr.Services.Distribution;

namespace EnviousWispr.App;

/// <summary>AI Polish provider selection, local EG-1/Ollama setup and warmup, and mishearing suggestions.</summary>
public partial class App
{
    private IPolishProvider? _polishProvider;

    /// <summary>The model Ollama polish uses this launch, or null; the page will not remove it from under the provider.</summary>
    private string? _ollamaActiveModel;
    private RuntimeResourceKind _polishResource = RuntimeResourceKind.Cpu;
    private bool _polishUsesLocalRuntime;
    private CloudPolishConsent? _cloudPolishConsent;
    private string? _localPolishNotice;
    private readonly CancellationTokenSource _polishLifetime = new();
    private readonly Lock _polishWarmupLock = new();
    private CancellationTokenSource? _polishWarmupCancellation;
    private readonly Lock _polishKeepAwakeLock = new();
    private CancellationTokenSource? _polishKeepAwakeCancellation;
    private Task? _polishWarmup;
    private string? _configuredEgOneModelPath;
    private PolishModelSource? _polishModelSource;
    private bool _polishSetupCardShownThisLaunch;

    /// <summary>
    /// Asks the user's chosen polish model what a word is likely to be misheard as.
    /// </summary>
    /// <remarks>
    /// THE PROVIDER IS WHATEVER POLISH IS SET TO, AND THAT IS THE POINT. The user has already chosen
    /// a model and, for a cloud one, already provided a key. Asking them to configure a second thing
    /// for a convenience button would mean almost nobody ever sees it work.
    ///
    /// PRIVACY: THE WORD GOES WHERE THEIR POLISHED TEXT ALREADY GOES. It travels to the provider
    /// they chose, under their own key, with no Envious Labs endpoint in the path - the same
    /// boundary cloud polish already sits on. Nothing about this reaches us.
    ///
    /// A PROVIDER THAT CANNOT BE ASKED SAYS SO RATHER THAN FAILING QUIETLY. Every provider that
    /// ships today can answer, and a test enumerates them from the type system so a fourth cannot
    /// arrive without one. This branch is what the user would see if one ever did.
    /// </remarks>
    private async void OnMishearingSuggestionsRequested(string term, IReadOnlyList<string> existing)
    {
        if (_polishProvider is not IMishearingAdvisor advisor)
        {
            _window?.SetAliasSuggestions(
                term,
                MishearingAdvice.None(MishearingAdviceStatus.NotSupported));
            return;
        }

        // ADMITTED LIKE A PRESENTER'S OPERATION: the request is inside the polish provider, which the
        // exit disposes; the lease is what the drain joins before that, its token is what the exit
        // cancels the request with, and a request refused because the drain has begun is not made.
        if (_presentation is not { } presentation || !presentation.TryEnter(out var lease))
        {
            return;
        }

        MishearingAdvice advice;
        using (lease)
        {
            IAsyncDisposable? resourceLease = null;
            if (_polishUsesLocalRuntime && _polishProvider is { } localProvider)
            {
                RuntimeResourceAcquireResult acquired;
                try
                {
                    acquired = await _resourceArbiter.AcquireAsync(
                        EffectivePolishResource(localProvider),
                        RuntimeWorkloadKind.LocalPolish,
                        TimeSpan.FromSeconds(2),
                        lease.Closing).ConfigureAwait(true);
                }
                catch (OperationCanceledException) when (lease.Closing.IsCancellationRequested)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                if (!acquired.Succeeded || acquired.Lease is null)
                {
                    if (!lease.Closing.IsCancellationRequested)
                    {
                        _window?.SetAliasSuggestions(
                            term,
                            MishearingAdvice.None(MishearingAdviceStatus.Failed));
                    }

                    return;
                }

                resourceLease = acquired.Lease;
            }

            try
            {
                try
                {
                    advice = await advisor.SuggestAsync(term, existing, lease.Closing).ConfigureAwait(true);
                }
                catch (Exception exception) when (
                    exception is not (StackOverflowException or OutOfMemoryException))
                {
                    advice = MishearingAdvice.None(MishearingAdviceStatus.Failed);
                }
            }
            finally
            {
                if (resourceLease is not null)
                {
                    await resourceLease.DisposeAsync().ConfigureAwait(true);
                }
            }

            if (lease.Closing.IsCancellationRequested)
            {
                return;
            }
        }

        _window?.SetAliasSuggestions(term, advice);
    }

    private void CancelPolishWarmup()
    {
        CancellationTokenSource? active;
        lock (_polishWarmupLock)
        {
            active = _polishWarmupCancellation;
        }

        if (active is null)
        {
            return;
        }

        try
        {
            active.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void AbortPolishRuntime()
    {
        StopPolishKeepAwake();
        CancelPolishWarmup();
        (_polishProvider as EgOnePolishProvider)?.TerminateRuntimeImmediately();
    }

    private void StopPolishKeepAwake()
    {
        CancellationTokenSource? previous;
        lock (_polishKeepAwakeLock)
        {
            previous = _polishKeepAwakeCancellation;
            _polishKeepAwakeCancellation = null;
        }

        if (previous is not null)
        {
            previous.Cancel();
            previous.Dispose();
        }
    }

    private void SetPolishSessionActive(bool active)
    {
        if (active)
        {
            CancelPolishWarmup();
            _resourceArbiter.PreemptBackgroundWork();
        }

        if (!active || Leaving || _polishProvider is not EgOnePolishProvider egOneProvider)
        {
            StopPolishKeepAwake();
            return;
        }

        if (EgOnePolishProvider.ResolveKeepAwakeInterval(egOneProvider.SleepIdleSeconds) is not { } interval)
        {
            return;
        }

        CancellationTokenSource next;
        lock (_polishKeepAwakeLock)
        {
            _polishKeepAwakeCancellation?.Cancel();
            _polishKeepAwakeCancellation?.Dispose();
            next = CancellationTokenSource.CreateLinkedTokenSource(_polishLifetime.Token);
            _polishKeepAwakeCancellation = next;
        }

        var token = next.Token;
        _ = KeepPolishAwakeDuringSessionAsync(egOneProvider, interval, token);
    }

    private static async Task KeepPolishAwakeDuringSessionAsync(
        EgOnePolishProvider provider,
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Run(() => provider.TryPreWakeAsync(cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await provider.TryPreWakeAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void StartPolishWarmup()
    {
        CancellationTokenSource? previous;
        CancellationTokenSource? next = null;
        lock (_polishWarmupLock)
        {
            previous = _polishWarmupCancellation;
            _polishWarmupCancellation = null;
            if (_polishProvider is EgOnePolishProvider or OllamaPolishProvider)
            {
                next = CancellationTokenSource.CreateLinkedTokenSource(_polishLifetime.Token);
                _polishWarmupCancellation = next;
            }
        }

        if (previous is not null)
        {
            try
            {
                previous.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            previous.Dispose();
        }

        if (next is null)
        {
            return;
        }

        if (_polishProvider is EgOnePolishProvider polishProvider)
        {
            _polishWarmup = WarmPolishRuntimeAsync(polishProvider, next.Token);
        }
        else if (_polishProvider is OllamaPolishProvider ollamaProvider)
        {
            _polishWarmup = ProbeOllamaRuntimeAsync(ollamaProvider, next.Token);
        }
    }

    private async Task<string> ResolveEgOneConfiguredModelFileAsync(
        string modelId,
        string? environmentOverride,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(environmentOverride))
        {
            return environmentOverride.Trim();
        }

        var normalizedId = EnginePolishPresentation.NormalizeEgOneModelId(modelId);
        var active = await Task.Run(
            () => OfflineModelStore.OpenActiveOfflineAsync(normalizedId, cancellationToken),
            cancellationToken).ConfigureAwait(true);
        if (active.Succeeded &&
            EgOneRuntimeLocator.TryResolveInstalledGgufInDirectory(active.Installed?.DirectoryPath, normalizedId) is { } storeFile)
        {
            return storeFile;
        }

        return EgOneRuntimeLocator.ResolveModelFile(_dataDirectory, normalizedId);
    }

    private async Task RefreshEgOneConfiguredProviderFromStoreAsync(
        PolishPreferences preferences,
        CancellationToken cancellationToken = default)
    {
        if (EffectivePolishProvider(preferences) != PolishProvider.EgOne ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ENVIOUSWISPR_EG1_MODEL_PATH")))
        {
            return;
        }

        var modelId = ConfiguredEgOneModelId(preferences);
        var active = await Task.Run(
            () => OfflineModelStore.OpenActiveOfflineAsync(modelId, cancellationToken),
            cancellationToken).ConfigureAwait(true);
        if (Leaving ||
            !active.Succeeded ||
            EgOneRuntimeLocator.TryResolveInstalledGgufInDirectory(active.Installed?.DirectoryPath, modelId) is not { } storeFile ||
            string.Equals(storeFile, _configuredEgOneModelPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (IsSpeechEngineBusy)
        {
            _polishReconfigurePending = true;
            return;
        }

        var attempt = _sessionCoordinator?.TryHold(SessionHolder.SpeechEngineSwitch)
            ?? new SessionHoldAttempt(NoScope.Instance);
        if (attempt.Hold is not { } hold)
        {
            if (attempt.Refusal != SessionHoldRefusal.Closed)
            {
                _polishReconfigurePending = true;
            }

            return;
        }

        using (hold)
        {
            await _engineReconfiguration.WaitAsync(cancellationToken).ConfigureAwait(true);
            try
            {
                if (Leaving || IsSpeechEngineBusy)
                {
                    return;
                }

                if (string.Equals(storeFile, _configuredEgOneModelPath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                await TeardownPolishAsync().ConfigureAwait(true);
                if (Leaving)
                {
                    return;
                }

                await ConfigurePolishAsync(preferences, modelFileOverride: storeFile, cancellationToken).ConfigureAwait(true);
                StartPolishWarmup();
            }
            finally
            {
                _engineReconfiguration.Release();
            }
        }
    }

    private static string ConfiguredEgOneModelId(PolishPreferences preferences)
    {
        var requested = EnginePolishPresentation.IsKnownEgOneModelId(preferences.EgOneModelId)
            ? preferences.EgOneModelId
            : preferences.ModelId;
        return EnginePolishPresentation.NormalizeEgOneModelId(requested);
    }

    private void ConfigurePolish(PolishPreferences preferences) =>
        ApplyPolishConfiguration(preferences, resolvedEgOneModelFile: null);

    private async Task ConfigurePolishAsync(
        PolishPreferences preferences,
        string? modelFileOverride = null,
        CancellationToken cancellationToken = default)
    {
        string? resolvedEgOneFile = modelFileOverride;
        if (resolvedEgOneFile is null && EffectivePolishProvider(preferences) == PolishProvider.EgOne)
        {
            resolvedEgOneFile = await ResolveEgOneConfiguredModelFileAsync(
                ConfiguredEgOneModelId(preferences),
                Environment.GetEnvironmentVariable("ENVIOUSWISPR_EG1_MODEL_PATH"),
                cancellationToken).ConfigureAwait(true);
        }

        ApplyPolishConfiguration(preferences, resolvedEgOneFile);
    }

    private static PolishProvider EffectivePolishProvider(PolishPreferences preferences)
    {
        var provider = preferences.Provider;
        var environmentProvider = Environment.GetEnvironmentVariable(
            "ENVIOUSWISPR_POLISH_PROVIDER");
        if (Enum.TryParse<PolishProvider>(environmentProvider, ignoreCase: true, out var parsedProvider))
        {
            return parsedProvider;
        }

        return provider;
    }

    private void ApplyPolishConfiguration(PolishPreferences preferences, string? resolvedEgOneModelFile)
    {
        _cloudPolishConsent = null;
        _localPolishNotice = null;
        _ollamaActiveModel = null;
        _polishUsesLocalRuntime = false;
        _polishResource = RuntimeResourceKind.Cpu;
        _configuredEgOneModelPath = null;

        var provider = EffectivePolishProvider(preferences);

        if (provider is PolishProvider.OpenAI or PolishProvider.Anthropic or PolishProvider.Gemini)
        {
            var configuredModel = CloudPolishOptions.ModelIdLooksLikeProvider(
                preferences.ModelId,
                provider)
                ? preferences.ModelId!
                : CloudPolishOptions.DefaultModel(provider);
            _polishProvider = provider switch
            {
                PolishProvider.OpenAI => new OpenAiPolishProvider(_credentialStore, configuredModel),
                PolishProvider.Anthropic => new AnthropicPolishProvider(_credentialStore, configuredModel),
                PolishProvider.Gemini => new GeminiPolishProvider(_credentialStore, configuredModel),
                _ => null,
            };
            _cloudPolishConsent = CloudPolishConsent.For(provider);
            return;
        }

        if (provider == PolishProvider.Ollama)
        {
            var endpoint = Environment.GetEnvironmentVariable("ENVIOUSWISPR_OLLAMA_ENDPOINT");
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                endpoint = preferences.OllamaEndpoint;
            }

            var model = Environment.GetEnvironmentVariable("ENVIOUSWISPR_OLLAMA_MODEL");
            if (string.IsNullOrWhiteSpace(model))
            {
                model = preferences.ModelId ?? string.Empty;
            }

            var isHostedModel = EnviousWispr.Core.Polish.OllamaModelCatalog.LooksLikeHostedCloudModel(model);
            _ollamaActiveModel = string.IsNullOrWhiteSpace(model) ? null : model;
            _polishProvider = new OllamaPolishProvider(new OllamaPolishOptions(endpoint, model));
            _polishUsesLocalRuntime = !isHostedModel;
            _cloudPolishConsent = isHostedModel ? CloudPolishConsent.ForOllamaHosted(model) : null;
            _localPolishNotice = OllamaEndpointPolicy.TryNormalize(endpoint, out var normalized)
                ? isHostedModel
                    ? $"Ollama polish uses {normalized} with hosted model {model.Trim()} on Ollama's servers."
                    : $"Ollama polish uses {normalized}. Local models stay on this PC; hosted models run on Ollama's servers."
                : "Ollama polish is disabled until its endpoint is a loopback HTTP or HTTPS address.";
            return;
        }

        if (provider != PolishProvider.EgOne)
        {
            _polishProvider = null;
            return;
        }

        var serverExecutable = EgOneRuntimeLocator.ResolveServerExecutable(
            AppContext.BaseDirectory,
            _dataDirectory,
            _updateService.IsStoreInstalled,
            Environment.GetEnvironmentVariable("ENVIOUSWISPR_EG1_SERVER_EXE"));

        var modelId = ConfiguredEgOneModelId(preferences);
        var contextTokens = EgOneModelCatalog.DefaultContextTokens(modelId);
        var modelFile = resolvedEgOneModelFile ?? EgOneRuntimeLocator.ResolveModelFile(
            _dataDirectory,
            modelId,
            Environment.GetEnvironmentVariable("ENVIOUSWISPR_EG1_MODEL_PATH"));

        int? gpuLayers = null;
        var allowCpuFallback = false;
        var hardwareProfile = EnviousWispr.Services.Diagnostics.HardwareProfileSummary.GetCurrent();
        if (int.TryParse(
                Environment.GetEnvironmentVariable("ENVIOUSWISPR_EG1_GPU_LAYERS"),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsedGpuLayers))
        {
            gpuLayers = parsedGpuLayers;
            if (parsedGpuLayers > 0)
            {
                _polishResource = RuntimeResourceKind.Accelerator;
            }
        }
        else if (!string.IsNullOrWhiteSpace(serverExecutable))
        {
            var runtimeDirectory = Path.GetDirectoryName(serverExecutable) ?? string.Empty;
            var hasCudaBackend = File.Exists(Path.Combine(runtimeDirectory, "ggml-cuda.dll")) &&
                                 hardwareProfile.HasNvidiaGpu;
            var hasVulkanBackend = File.Exists(Path.Combine(runtimeDirectory, "ggml-vulkan.dll")) &&
                                   hardwareProfile.HasVulkanGpu;
            if (hasCudaBackend || hasVulkanBackend)
            {
                gpuLayers = 99;
                allowCpuFallback = true;
                _polishResource = RuntimeResourceKind.Accelerator;
            }
        }

        var (defaultThreads, defaultBatchThreads) =
            EnviousWispr.Services.Diagnostics.HardwareProfileSummary.ChooseCpuThreadCounts();
        int? threads = defaultThreads;
        int? batchThreads = defaultBatchThreads;
        if (int.TryParse(
                Environment.GetEnvironmentVariable("ENVIOUSWISPR_EG1_THREADS"),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsedThreads) &&
            parsedThreads > 0)
        {
            threads = parsedThreads;
            batchThreads = Math.Max(parsedThreads, defaultBatchThreads);
        }

        var sleepIdleSeconds = EgOneIdleSleepPolicy.SleepIdleSeconds(preferences.UnloadPolicy);
        if (int.TryParse(
                Environment.GetEnvironmentVariable("ENVIOUSWISPR_EG1_SLEEP_IDLE_SECONDS"),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsedSleepSeconds))
        {
            sleepIdleSeconds = parsedSleepSeconds > 0 ? parsedSleepSeconds : null;
        }

        _configuredEgOneModelPath = modelFile;
        _polishProvider = new EgOnePolishProvider(new EgOnePolishOptions(
            new EgOneServerOptions(
                serverExecutable,
                modelFile,
                ContextTokens: contextTokens,
                GpuLayers: gpuLayers,
                Threads: threads,
                AllowCpuFallback: allowCpuFallback,
                BatchThreads: batchThreads,
                SleepIdleSeconds: sleepIdleSeconds),
            modelId));
        _polishUsesLocalRuntime = true;
    }

    private async Task ProbeOllamaRuntimeAsync(
        OllamaPolishProvider provider,
        CancellationToken cancellationToken)
    {
        _logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            AppEventCode.PolishRuntimeStarted,
            Provider: DiagnosticProviderIds.FromProviderId(provider.ProviderId)));
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var health = await provider.ProbeHealthAsync(cancellationToken).ConfigureAwait(false);
        var modelProbe = health.Health == OllamaHealth.Ready
            ? await provider.ProbeModelAsync(cancellationToken).ConfigureAwait(false)
            : null;
        timer.Stop();
        if (modelProbe?.Model is { } probedModel)
        {
            _polishUsesLocalRuntime = !probedModel.IsRemote;
            _cloudPolishConsent = probedModel.IsRemote
                ? CloudPolishConsent.ForOllamaHosted(probedModel.Id)
                : null;
        }
        _logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            health.Health == OllamaHealth.Ready
                ? AppEventCode.PolishRuntimeReady
                : AppEventCode.PolishRuntimeDegraded,
            health.Health == OllamaHealth.Ready
                ? AppFailureCategory.None
                : AppFailureCategory.LocalPolish,
            timer.ElapsedMilliseconds,
            DiagnosticProviderIds.FromProviderId(provider.ProviderId),
            health.Error?.Code));
        PostToLiveWindow(window =>
        {
            window.SetCloudPolishNotice(_cloudPolishConsent?.Notice);
            if (health.Health != OllamaHealth.Ready)
            {
                window.SetSessionStatus(OllamaHealthStatus(health.Health));
            }
        });
        if (health.Health == OllamaHealth.Ready &&
            !Leaving &&
            !IsSpeechEngineBusy &&
            _cloudPolishConsent is null)
        {
            CancellationTokenSource? linked = null;
            RuntimeResourceAcquireResult acquired;
            try
            {
                linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _resourceArbiter.CurrentBackgroundToken);
                acquired = await _resourceArbiter.AcquireAsync(
                    RuntimeResourceKind.Cpu,
                    RuntimeWorkloadKind.LocalPolishBackground,
                    TimeSpan.Zero,
                    linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                linked?.Dispose();
                return;
            }
            catch (ObjectDisposedException)
            {
                linked?.Dispose();
                return;
            }

            using (linked)
            {
                if (acquired.Succeeded && acquired.Lease is not null)
                {
                    await using (acquired.Lease.ConfigureAwait(false))
                    {
                        if (!Leaving && !IsSpeechEngineBusy)
                        {
                            try
                            {
                                _ = await provider.WarmUpAsync(linked.Token).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (linked.IsCancellationRequested)
                            {
                            }
                        }
                    }
                }
            }
        }
    }

    private async Task WarmPolishRuntimeAsync(
        EgOnePolishProvider provider,
        CancellationToken cancellationToken)
    {
        if (Leaving || IsSpeechEngineBusy)
        {
            return;
        }

        CancellationTokenSource? linked = null;
        RuntimeResourceAcquireResult acquired;
        try
        {
            linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _resourceArbiter.CurrentBackgroundToken);
            acquired = await _resourceArbiter.AcquireAsync(
                EffectivePolishResource(provider),
                RuntimeWorkloadKind.LocalPolishBackground,
                TimeSpan.Zero,
                linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            linked?.Dispose();
            return;
        }
        catch (ObjectDisposedException)
        {
            linked?.Dispose();
            return;
        }

        using (linked)
        {
            if (!acquired.Succeeded || acquired.Lease is null)
            {
                return;
            }

            await using (acquired.Lease.ConfigureAwait(false))
            {
                if (Leaving || IsSpeechEngineBusy)
                {
                    return;
                }

                _logger.Write(new AppLogEntry(
                    DateTimeOffset.UtcNow,
                    AppEventCode.PolishRuntimeStarted,
                    Provider: DiagnosticProviderIds.FromProviderId(provider.ProviderId)));
                var timer = System.Diagnostics.Stopwatch.StartNew();
                EgOneHealthResult health;
                try
                {
                    health = await provider.ProbeHealthAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested)
                {
                    return;
                }

                timer.Stop();
                _logger.Write(new AppLogEntry(
                    DateTimeOffset.UtcNow,
                    health.Health == EgOneHealth.Green
                        ? AppEventCode.PolishRuntimeReady
                        : AppEventCode.PolishRuntimeDegraded,
                    health.Health == EgOneHealth.Green
                        ? AppFailureCategory.None
                        : AppFailureCategory.LocalPolish,
                    timer.ElapsedMilliseconds,
                    DiagnosticProviderIds.FromProviderId(provider.ProviderId)));
            }
        }
    }

    /// <summary>The polish provider in force, and how it is hosted, or null when there is none.</summary>
    private PolishSetup? CurrentPolishSetup()
    {
        if (_polishProvider is not { } provider)
        {
            return null;
        }

        var modelId = provider switch
        {
            EgOnePolishProvider egOne => egOne.ModelId,
            OllamaPolishProvider => _ollamaActiveModel,
            CloudPolishProviderBase cloud => cloud.ModelId,
            _ => null,
        };
        return new PolishSetup(provider, _polishUsesLocalRuntime, EffectivePolishResource(provider), modelId);
    }

    internal void MaybeShowPolishSetupCard(DeliveryReport delivered, MainWindow window)
    {
        if (_polishSetupCardShownThisLaunch ||
            delivered.Landing != DeliveryLanding.Landed ||
            CurrentPolishSetup() is not null ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ENVIOUSWISPR_UAT_OVERLAY_STATE")))
        {
            return;
        }

        var provider = EffectivePolishProvider(_settings.Preferences.Polish);
        if (provider == PolishProvider.None)
        {
            return;
        }

        var (title, button) = provider switch
        {
            PolishProvider.Ollama => ("Choose a model for Ollama.", "Open AI polish settings"),
            PolishProvider.EgOne => ("Local AI polish needs setup.", "Set up AI polish"),
            _ => ("Finish AI polish setup.", "Set up AI polish"),
        };
        if (window.TryShowPolishSetupCard(title, button))
        {
            _polishSetupCardShownThisLaunch = true;
        }
    }

    private RuntimeResourceKind EffectivePolishResource(IPolishProvider provider) =>
        provider is EgOnePolishProvider { UsesAccelerator: { } usesAccelerator }
            ? (usesAccelerator ? RuntimeResourceKind.Accelerator : RuntimeResourceKind.Cpu)
            : _polishResource;

    /// <summary>The button an advisory about the cleanup provider carries.</summary>
    /// <remarks>
    /// ONE INSTANCE RATHER THAN ONE PER ROW. Four sentences send the user to the same page, and
    /// four copies of the same two words is how one of them ends up saying something else.
    /// </remarks>
    private static readonly PillAction OpenPolish =
        new("Open settings", PillActionKind.OpenPolishSettings, "Open AI polish settings");

    private static DictationStatus OllamaHealthStatus(OllamaHealth health) => health switch
    {
        // EVERY UNHEALTHY OLLAMA ROW IS A SETUP PROBLEM THE USER CAN FIX, so it is an advisory
        // rather than an error or, as it was, nothing at all. A user whose polish provider is
        // switched off currently gets a silently plainer result and no pill saying why.
        OllamaHealth.EndpointInvalid =>
            DictationStatus.Advisory("Ollama endpoint must point to this PC", OpenPolish),
        OllamaHealth.ServerUnavailable =>
            DictationStatus.Advisory(
                "Ollama is offline. Cleaned text will still be preserved", OpenPolish),
        OllamaHealth.ServerUnhealthy =>
            DictationStatus.Advisory("Ollama did not return a usable health response", OpenPolish),
        OllamaHealth.NoLocalModels =>
            DictationStatus.Advisory(
                "Ollama is running, but no local model is installed", OpenPolish),
        _ => DictationStatus.Quiet("Ollama is ready"),
    };
}
