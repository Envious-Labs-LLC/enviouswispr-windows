using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Services.Diagnostics;

/// <summary>
/// Coordinates PostHog usage metrics and Sentry crash/error reporting on Windows.
/// Observes lifecycle transitions, pipeline diagnostic records, settings changes, worker abnormal exits,
/// and unhandled exception hooks without touching user content.
/// </summary>
public sealed class WindowsObservabilityService : IAsyncDisposable
{
    private readonly PostHogTelemetryClient _postHog;
    private readonly SentryCrashClient _sentry;
    private readonly WindowsObservabilityIdentity _identity;
    private readonly Lock _lock = new();

    private AppSettings _currentSettings = AppSettings.Default;
    private bool _processHandlersAttached;
    private bool _disposed;

    public WindowsObservabilityService(
        PostHogTelemetryClient postHog,
        SentryCrashClient sentry,
        WindowsObservabilityIdentity identity,
        AppSettings? initialSettings = null)
    {
        ArgumentNullException.ThrowIfNull(postHog);
        ArgumentNullException.ThrowIfNull(sentry);
        ArgumentNullException.ThrowIfNull(identity);

        _postHog = postHog;
        _sentry = sentry;
        _identity = identity;
        if (initialSettings is not null)
        {
            _currentSettings = initialSettings;
        }
    }

    public PostHogTelemetryClient PostHog => _postHog;

    public SentryCrashClient Sentry => _sentry;

    public WindowsObservabilityIdentity Identity => _identity;

    public bool LaunchedCrashReports => _sentry.LaunchedCrashReports;

    public bool NeedsCrashReportsRestart(bool storedSendCrashReports) =>
        _sentry.NeedsRestart(storedSendCrashReports);

    public static WindowsObservabilityService CreateFromEnvironment(
        string dataDirectory,
        WindowsObservabilityIdentity identity,
        ObservabilityPreferences preferences,
        HttpMessageHandler? postHogHandler = null,
        HttpMessageHandler? sentryHandler = null,
        bool startBackgroundFlushLoop = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(preferences);

        var keys = ObservabilityKeyResolver.Resolve();
        var postHog = new PostHogTelemetryClient(
            apiKey: keys.PostHogApiKey,
            host: keys.PostHogHost,
            dataDirectory: dataDirectory,
            identity: identity,
            usageMetricsEnabled: preferences.ShareAnonymousTelemetry,
            messageHandler: postHogHandler,
            startBackgroundFlushLoop: startBackgroundFlushLoop);

        var sentry = new SentryCrashClient(
            dsn: keys.SentryDsn,
            identity: identity,
            sendCrashReportsAtLaunch: preferences.SendCrashReports,
            usageMetricsEnabled: preferences.ShareAnonymousTelemetry,
            postHogDistinctId: postHog.DistinctId,
            messageHandler: sentryHandler);

        return new WindowsObservabilityService(postHog, sentry, identity);
    }

    /// <summary>
    /// Attaches <see cref="AppDomain.CurrentDomain.UnhandledException"/> and
    /// <see cref="TaskScheduler.UnobservedTaskException"/> hooks once per instance.
    /// </summary>
    public void AttachProcessExceptionHandlers()
    {
        lock (_lock)
        {
            if (_processHandlersAttached || _disposed)
            {
                return;
            }

            _processHandlersAttached = true;
        }

        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnTaskSchedulerUnobservedTaskException;
    }

    public void ApplySettings(AppSettings settings, bool emitSettingsChangedEvent)
    {
        ArgumentNullException.ThrowIfNull(settings);
        AppSettings previous;
        lock (_lock)
        {
            previous = _currentSettings;
            _currentSettings = settings;
        }

        var observability = settings.Observability ?? ObservabilityPreferences.Default;
        _postHog.SetUsageMetricsEnabled(observability.ShareAnonymousTelemetry);
        _sentry.UpdateUsageMetricsState(observability.ShareAnonymousTelemetry, _postHog.DistinctId);

        var engineTag = settings.Preferences.Dictation.FinalEngine.ToString().ToLowerInvariant();
        _sentry.SetTag("runtime.asr_engine", engineTag);

        if (emitSettingsChangedEvent && !Equals(previous.Preferences, settings.Preferences))
        {
            RecordSettingsChanged(settings.Preferences);
        }
    }

    public bool RecordAppLaunched(
        HardwareSnapshot? hardware,
        bool isFreshInstall,
        bool aiCapable,
        bool aiEnabled,
        string? speechModelsInstalled = null,
        string? runtimeAcceleration = null)
    {
        var gpuVendors = FormatGpuVendors(hardware);
        var acceleration = !string.IsNullOrWhiteSpace(runtimeAcceleration)
            ? runtimeAcceleration!
            : DeriveAcceleration(hardware);
        var asrEngine = _currentSettings.Preferences.Dictation.FinalEngine.ToString().ToLowerInvariant();

        _sentry.UpdateRuntimeScope(asrEngine, acceleration, gpuVendors);
        _sentry.AddBreadcrumb("lifecycle", "Application launched", "info", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["fresh_install"] = isFreshInstall,
            ["acceleration"] = acceleration,
            ["gpu_vendors"] = gpuVendors,
        });

        var props = BuildAppLaunchedProperties(
            _identity,
            hardware,
            isFreshInstall,
            aiCapable,
            aiEnabled,
            speechModelsInstalled,
            acceleration,
            gpuVendors);
        return _postHog.Capture("app.launched", props);
    }

    public static Dictionary<string, object?> BuildAppLaunchedProperties(
        WindowsObservabilityIdentity identity,
        HardwareSnapshot? hardware,
        bool isFreshInstall,
        bool aiCapable,
        bool aiEnabled,
        string? speechModelsInstalled = null,
        string? runtimeAcceleration = null,
        string? gpuVendorsOverride = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var gpuVendors = gpuVendorsOverride ?? FormatGpuVendors(hardware);
        var acceleration = runtimeAcceleration ?? DeriveAcceleration(hardware);
        var cudaAvailable = hardware is not null && hardware.Cuda.IsDriverAvailable && hardware.Cuda.DeviceCount > 0;
        var directMlAvailable = hardware?.IsDirectMlRuntimeAvailable == true;

        var props = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["version"] = identity.AppVersion,
            ["build"] = identity.OsBuild,
            ["os_version"] = identity.OsVersion,
            ["hardware"] = FormatArchitecture(hardware?.Architecture),
            ["cpu_vendor"] = FormatCpuVendor(hardware?.ProcessorVendor),
            ["physical_core_bucket"] = FormatCoreBucket(hardware?.PhysicalCoreCount),
            ["ram_gb_bucket"] = FormatRamBucket(hardware?.TotalPhysicalMemoryBytes),
            ["gpu_vendors"] = gpuVendors,
            ["cuda_available"] = cudaAvailable,
            ["directml_available"] = directMlAvailable,
            ["runtime_acceleration"] = acceleration,
            ["is_fresh_install"] = isFreshInstall,
            ["ai_capable"] = aiCapable,
            ["ai_enabled"] = aiEnabled,
        };

        if (!string.IsNullOrWhiteSpace(speechModelsInstalled))
        {
            props["speech_models_installed"] = speechModelsInstalled;
        }

        return props;
    }

    public bool RecordDictationCompleted(
        string inputMode,
        string asrBackend,
        string llmProvider,
        string deliveryOutcome,
        long? elapsedMs = null,
        string? recognitionLanguage = null)
    {
        var props = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["result"] = "success",
            ["input_mode"] = inputMode,
            ["asr_backend"] = asrBackend,
            ["llm_provider"] = llmProvider,
            ["delivery_outcome"] = deliveryOutcome,
        };

        if (elapsedMs is { } ms and >= 0)
        {
            props["elapsed_ms"] = ms;
            props["e2e_seconds"] = ms / 1000.0;
        }

        if (!string.IsNullOrWhiteSpace(recognitionLanguage))
        {
            props["recognition_language"] = recognitionLanguage;
        }

        _sentry.AddBreadcrumb("dictation", "Dictation completed", "info", props);
        return _postHog.Capture("dictation.completed", props);
    }

    public bool RecordDictationCanceled(
        string inputMode,
        string asrBackend,
        long? elapsedMs = null)
    {
        var props = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["input_mode"] = inputMode,
            ["asr_backend"] = asrBackend,
        };

        if (elapsedMs is { } ms and >= 0)
        {
            props["elapsed_ms"] = ms;
        }

        _sentry.AddBreadcrumb("dictation", "Dictation canceled", "info", props);
        return _postHog.Capture("dictation.canceled", props);
    }

    public bool RecordPipelineFailed(
        string stage,
        AppFailureCategory failureCategory,
        AppErrorCode? errorCode = null,
        string? asrBackend = null,
        string? provider = null,
        long? elapsedMs = null)
    {
        var props = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["stage"] = stage,
            ["failure_category"] = failureCategory.ToString(),
        };

        if (errorCode is { } code)
        {
            props["error_code"] = code.ToString();
        }

        if (!string.IsNullOrWhiteSpace(asrBackend))
        {
            props["asr_backend"] = asrBackend;
        }

        if (!string.IsNullOrWhiteSpace(provider))
        {
            props["provider"] = provider;
        }

        if (elapsedMs is { } ms and >= 0)
        {
            props["elapsed_ms"] = ms;
        }

        _sentry.AddBreadcrumb(stage, $"Pipeline failed: {failureCategory}", "error", props);
        return _postHog.Capture("pipeline.failed", props);
    }

    public bool RecordLlmPolishCompleted(
        string provider,
        long? elapsedMs = null,
        string result = "success")
    {
        var props = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["provider"] = provider,
            ["result"] = result,
        };

        if (elapsedMs is { } ms and >= 0)
        {
            props["elapsed_ms"] = ms;
            props["latency_seconds"] = ms / 1000.0;
        }

        _sentry.AddBreadcrumb("polish", "LLM polish completed", "info", props);
        return _postHog.Capture("llm.polish_completed", props);
    }

    public bool RecordLlmPolishFailed(
        string provider,
        AppFailureCategory failureCategory,
        AppErrorCode? errorCode = null,
        long? elapsedMs = null)
    {
        var props = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["provider"] = provider,
            ["result"] = "failed",
            ["failure_category"] = failureCategory.ToString(),
        };

        if (errorCode is { } code)
        {
            props["error_code"] = code.ToString();
        }

        if (elapsedMs is { } ms and >= 0)
        {
            props["elapsed_ms"] = ms;
            props["latency_seconds"] = ms / 1000.0;
        }

        _sentry.AddBreadcrumb("polish", "LLM polish failed", "warning", props);
        return _postHog.Capture("llm.polish_failed", props);
    }

    public bool RecordSettingsChanged(UserPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var props = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["final_engine"] = preferences.Dictation.FinalEngine.ToString().ToLowerInvariant(),
            ["recording_mode"] = preferences.Dictation.RecordingMode.ToString().ToLowerInvariant(),
            ["live_preview_enabled"] = preferences.LivePreviewEnabled,
            ["live_preview_engine"] = preferences.LivePreviewEngine.ToString().ToLowerInvariant(),
            ["polish_provider"] = preferences.Polish.Provider.ToString().ToLowerInvariant(),
            ["english_spelling"] = preferences.Dictation.EnglishSpelling.ToString().ToLowerInvariant(),
            ["whisper_language"] = preferences.Dictation.WhisperLanguage.ToString().ToLowerInvariant(),
            ["history_enabled"] = preferences.History.IsEnabled,
            ["casual_mode_enabled"] = preferences.Dictation.CasualModeEnabled,
            ["escape_recovery_enabled"] = preferences.Dictation.EscapeRecoveryEnabled,
            ["auto_stop_enabled"] = preferences.Dictation.AutoStopEnabled,
            ["copy_instead_of_paste"] = preferences.CopyInsteadOfPaste,
        };

        _sentry.AddBreadcrumb("settings", "Settings changed", "info", props);
        return _postHog.Capture("settings.changed", props);
    }

    public async Task<Guid?> RecordRuntimeWorkerFailedAsync(
        string asrBackend,
        int? exitCode = null,
        AppErrorCode? errorCode = null,
        CancellationToken cancellationToken = default)
    {
        var props = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["role"] = "runtime_worker",
            ["asr_backend"] = asrBackend,
            ["failure_category"] = AppFailureCategory.RuntimeWorker.ToString(),
        };

        if (exitCode is { } code)
        {
            props["exit_code"] = code;
        }

        if (errorCode is { } err)
        {
            props["error_code"] = err.ToString();
        }

        _postHog.Capture("runtime_worker.failed", props);
        _sentry.AddBreadcrumb("runtime_worker", "Owned runtime worker failed", "warning", props);

        return await _sentry.CaptureEventAsync(
            new SentryCaptureEvent(
                Level: "warning",
                Message: "Owned runtime worker process failed; underlying cause unknown",
                Tags: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["process.role"] = "runtime_worker",
                    ["diagnostic.kind"] = "child_exit",
                    ["error.category"] = "child_process_failed",
                },
                Extra: props,
                Fingerprint: ["handled_error", "child_process_failed", "runtime_worker", _identity.Environment]),
            cancellationToken).ConfigureAwait(false);
    }

    public Task<Guid?> CaptureInterruptedDictationNoteAsync(
        bool previousRunWasDictating,
        CancellationToken cancellationToken = default)
    {
        var category = previousRunWasDictating ? "transcription_interrupted" : "previous_run_interrupted";
        _sentry.AddBreadcrumb("recovery", $"Prior run interrupted (was_dictating={previousRunWasDictating})", "warning");
        return _sentry.CaptureEventAsync(
            new SentryCaptureEvent(
                Level: previousRunWasDictating ? "error" : "warning",
                Message: $"{category}: PreviousRunInterrupted",
                Tags: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["diagnostic.kind"] = category,
                    ["error.category"] = category,
                },
                Extra: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["previous_run_was_dictating"] = previousRunWasDictating,
                },
                Fingerprint: ["handled_error", category, _identity.Environment]),
            cancellationToken);
    }

    private static readonly TimeSpan DefaultUnhandledFlushTimeout = TimeSpan.FromSeconds(2);

    public Task<Guid?> CaptureUnhandledExceptionAsync(
        Exception exception,
        string mechanismType = "unhandled_exception",
        bool handled = false,
        string level = "fatal",
        CancellationToken cancellationToken = default) =>
        _sentry.CaptureExceptionAsync(
            exception,
            mechanismType: mechanismType,
            handled: handled,
            level: level,
            cancellationToken: cancellationToken);

    public Guid? CaptureUnhandledExceptionBlocking(
        Exception exception,
        string mechanismType = "unhandled_exception",
        bool handled = false,
        string level = "fatal",
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var effectiveTimeout = timeout ?? DefaultUnhandledFlushTimeout;
        try
        {
            using var cts = new CancellationTokenSource(effectiveTimeout);
            var token = cts.Token;
            return Task.Run(
                    () => CaptureUnhandledExceptionAsync(
                        exception,
                        mechanismType: mechanismType,
                        handled: handled,
                        level: level,
                        cancellationToken: token),
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
            // Never throw from a fatal crash handler path.
            return null;
        }
    }

    /// <summary>
    /// Bridges typed <see cref="PrivacySafeDiagnosticRecord"/> emissions from <see cref="PrivacySafeObservabilityLogger"/>
    /// into PostHog usage metrics and Sentry breadcrumbs/warnings.
    /// </summary>
    public void ObserveDiagnosticRecord(PrivacySafeDiagnosticRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        AppSettings settingsSnapshot;
        lock (_lock)
        {
            settingsSnapshot = _currentSettings;
        }

        var inputMode = settingsSnapshot.Preferences.Dictation.RecordingMode == DictationRecordingMode.Toggle
            ? "toggle"
            : "push_to_talk";
        var asrBackend = (record.Engine?.ToString() ?? settingsSnapshot.Preferences.Dictation.FinalEngine.ToString())
            .ToLowerInvariant();
        var llmProvider = (record.Provider?.ToString() ?? settingsSnapshot.Preferences.Polish.Provider.ToString())
            .ToLowerInvariant();

        switch (record.Event)
        {
            case AppEventCode.DictationCompleted:
                RecordDictationCompleted(
                    inputMode: inputMode,
                    asrBackend: asrBackend,
                    llmProvider: llmProvider,
                    deliveryOutcome: "completed",
                    elapsedMs: record.ElapsedMilliseconds,
                    recognitionLanguage: record.RecognitionLanguage?.ToString().ToLowerInvariant());
                break;

            case AppEventCode.DictationCancelled:
                RecordDictationCanceled(
                    inputMode: inputMode,
                    asrBackend: asrBackend,
                    elapsedMs: record.ElapsedMilliseconds);
                break;

            case AppEventCode.PolishCompleted:
                RecordLlmPolishCompleted(
                    provider: llmProvider,
                    elapsedMs: record.ElapsedMilliseconds,
                    result: "success");
                break;

            case AppEventCode.PolishDegraded:
            case AppEventCode.PolishOutputRefused:
                RecordLlmPolishFailed(
                    provider: llmProvider,
                    failureCategory: record.Failure,
                    errorCode: record.ErrorCode,
                    elapsedMs: record.ElapsedMilliseconds);
                break;

            case AppEventCode.DictationTranscriptionFailed:
                RecordPipelineFailed(
                    stage: "transcription",
                    failureCategory: record.Failure,
                    errorCode: record.ErrorCode,
                    asrBackend: asrBackend,
                    elapsedMs: record.ElapsedMilliseconds);
                if (record.ErrorCode == AppErrorCode.RuntimeWorkerFailed ||
                    record.Failure == AppFailureCategory.RuntimeWorker)
                {
                    _ = RecordRuntimeWorkerFailedAsync(asrBackend, errorCode: record.ErrorCode);
                }
                break;

            case AppEventCode.TextDeliveryFailed:
                RecordPipelineFailed(
                    stage: "delivery",
                    failureCategory: record.Failure,
                    errorCode: record.ErrorCode,
                    asrBackend: asrBackend,
                    elapsedMs: record.ElapsedMilliseconds);
                break;

            case AppEventCode.DictationSessionFailed:
                RecordPipelineFailed(
                    stage: "session",
                    failureCategory: record.Failure,
                    errorCode: record.ErrorCode,
                    asrBackend: asrBackend,
                    elapsedMs: record.ElapsedMilliseconds);
                break;

            case AppEventCode.LivePreviewStarted:
                _postHog.Capture("live_preview.outcome", new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["engine"] = settingsSnapshot.Preferences.LivePreviewEngine.ToString().ToLowerInvariant(),
                    ["outcome"] = "started",
                });
                break;

            case AppEventCode.LivePreviewFailed:
            case AppEventCode.LivePreviewAborted:
                _postHog.Capture("live_preview.outcome", new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["engine"] = settingsSnapshot.Preferences.LivePreviewEngine.ToString().ToLowerInvariant(),
                    ["outcome"] = record.Event == AppEventCode.LivePreviewAborted ? "aborted" : "failed",
                });
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_processHandlersAttached)
            {
                AppDomain.CurrentDomain.UnhandledException -= OnAppDomainUnhandledException;
                TaskScheduler.UnobservedTaskException -= OnTaskSchedulerUnobservedTaskException;
                _processHandlersAttached = false;
            }
        }

        await _postHog.DisposeAsync().ConfigureAwait(false);
        _sentry.Dispose();
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            _ = CaptureUnhandledExceptionBlocking(
                exception,
                mechanismType: "appdomain_unhandled_exception",
                handled: false,
                level: e.IsTerminating ? "fatal" : "error");
        }
    }

    private void OnTaskSchedulerUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        if (e.Exception is { } exception)
        {
            _ = CaptureUnhandledExceptionAsync(
                exception,
                mechanismType: "unobserved_task_exception",
                handled: true,
                level: "error");
        }
    }

    public static string FormatGpuVendors(HardwareSnapshot? hardware)
    {
        if (hardware is null)
        {
            return "unknown";
        }

        var vendors = hardware.GraphicsAdapters
            .Where(adapter => adapter.IsActive && adapter.Vendor != GraphicsVendor.Unknown)
            .Select(adapter => adapter.Vendor.ToString().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        return vendors.Length == 0 ? "none" : string.Join(",", vendors);
    }

    public static string DeriveAcceleration(HardwareSnapshot? hardware)
    {
        if (hardware is null)
        {
            return "unknown";
        }

        if (hardware.Cuda.IsDriverAvailable && hardware.Cuda.DeviceCount > 0)
        {
            return "cuda";
        }

        if (hardware.IsDirectMlRuntimeAvailable &&
            hardware.GraphicsAdapters.Any(adapter => adapter.IsActive && adapter.IsDirectMlCandidate))
        {
            return "directml";
        }

        return "cpu";
    }

    private static string FormatArchitecture(ProcessorArchitectureKind? architecture) =>
        architecture switch
        {
            ProcessorArchitectureKind.X64 => "x64",
            ProcessorArchitectureKind.Arm64 => "arm64",
            _ => "unknown",
        };

    private static string FormatCpuVendor(ProcessorVendor? vendor) =>
        vendor switch
        {
            ProcessorVendor.Intel => "intel",
            ProcessorVendor.Amd => "amd",
            ProcessorVendor.Qualcomm => "qualcomm",
            _ => "unknown",
        };

    private static string FormatCoreBucket(int? cores) =>
        cores switch
        {
            null or <= 0 => "unknown",
            <= 4 => "1-4",
            <= 8 => "5-8",
            <= 16 => "9-16",
            _ => "17+",
        };

    private static string FormatRamBucket(ulong? bytes)
    {
        if (bytes is null or 0)
        {
            return "unknown";
        }

        var gb = bytes.Value / (1024.0 * 1024.0 * 1024.0);
        return gb switch
        {
            <= 8.5 => "<=8",
            <= 17.0 => "16",
            <= 34.0 => "32",
            _ => "64+",
        };
    }
}
