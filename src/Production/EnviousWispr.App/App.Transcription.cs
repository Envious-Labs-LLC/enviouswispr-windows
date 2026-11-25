using EnviousWispr.App.Composition;
using EnviousWispr.ASR;
using EnviousWispr.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.ModelDelivery;
using EnviousWispr.Pipeline;
using EnviousWispr.Presentation;
using EnviousWispr.Services.Runtime;

namespace EnviousWispr.App;

/// <summary>Final speech-recognition engines, Live Preview configuration, and file transcription.</summary>
public partial class App
{
    // RESOLVED AT EACH CONFIGURATION, NOT ONCE AT CONSTRUCTION: the graphics runtime pack can arrive while
    // the app runs, and the next worker it starts must be given its folder.
    private string? _cudaRuntimeDirectory;
    private RuntimeWorkerTranscriptionEngine? _transcriptionEngine;
    private RuntimeWorkerLivePreviewEngine? _previewEngine;
    /// <summary>Why <see cref="_previewEngine"/> could not be built, or null when it was.</summary>
    /// <remarks>
    /// HELD RATHER THAN LOGGED WHERE IT IS DISCOVERED. Configuration runs on every launch,
    /// including the overwhelming majority where Live Preview is switched off, so writing the
    /// failure there would file a complaint on behalf of a user who never asked for the
    /// feature - once per launch, on every machine without the optional pack. The reason is
    /// kept here and reported at the moment somebody actually turns Live Preview on.
    /// </remarks>
    private AppErrorCode? _previewUnavailableReason;

    private async Task ConfigureTranscriptionAsync(FinalAsrEngine configuredEngine)
    {
        // WHAT THE GRAPHICS RUNTIME OFFER READS, reset first so a configuration that stops early offers nothing.
        _configuredTranscription = null;
        // THE FIRST-RUN GATE READS THIS, NOT THE SENTENCES BELOW: every return from here says which it was.
        _window?.SetTranscriptionReadiness(SpeechEngineReadiness.Preparing);
        if (string.Equals(
                Environment.GetEnvironmentVariable("ENVIOUSWISPR_UAT_DISABLE_LOCAL_RUNTIME"),
                "1",
                StringComparison.Ordinal))
        {
            _window?.SetSessionStatus(DictationStatus
                .Quiet("Local transcription disabled for performance UAT")
                .AboutTheTranscriptionEngine());
            _window?.SetTranscriptionReadiness(SpeechEngineReadiness.Unavailable);
            return;
        }

        var environmentEngine = Environment.GetEnvironmentVariable("ENVIOUSWISPR_ASR_ENGINE");
        if (FinalAsrEngineChoices.TryParse(environmentEngine, out var parsedEngine))
        {
            configuredEngine = parsedEngine;
        }

        var engine = configuredEngine;
        // THE LANGUAGE TO LOAD WITH ONLY: every take reads it again (CurrentWhisperLanguage), #241.
        var whisperLanguage = CurrentWhisperLanguage();

        var modelDirectory = await ResolveModelDirectoryAsync(engine == FinalAsrEngine.Whisper
            ? WhisperTranscriptionEngine.ModelId
            : ParakeetTranscriptionEngine.ModelId).ConfigureAwait(true);
        var parakeetPreviewDirectory = engine == FinalAsrEngine.Whisper
            ? await ResolveModelDirectoryAsync(ParakeetTranscriptionEngine.ModelId).ConfigureAwait(true)
            : modelDirectory;
        var whisperFinalDirectory = engine == FinalAsrEngine.Whisper
            ? modelDirectory
            : await ResolveModelDirectoryAsync(WhisperTranscriptionEngine.ModelId).ConfigureAwait(true);
        var previewModelDirectory = await ResolveModelDirectoryAsync(
            WhisperTranscriptionEngine.PreviewModelId,
            "ENVIOUSWISPR_PREVIEW_MODEL_DIRECTORY").ConfigureAwait(true);
        var hasParakeet = parakeetPreviewDirectory is not null;
        var hasWhisper = whisperFinalDirectory is not null || previewModelDirectory is not null;
        if (modelDirectory is null)
        {
            RecordAppLaunchedOnce(hardware: null, hasParakeet: hasParakeet, hasWhisper: hasWhisper);
            _window?.SetSessionStatus(
                DictationStatus.Advisory(
                        "Local transcription model is not installed", StatusActions.OpenTranscription)
                    .AboutTheTranscriptionEngine());
            _window?.SetTranscriptionReadiness(SpeechEngineReadiness.NotInstalled);
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.DictationTranscriptionFailed,
                AppFailureCategory.AsrUnavailable,
                ErrorCode: AppErrorCode.ModelPackUnavailable));
            return;
        }

        _cudaRuntimeDirectory = await ResolveCudaRuntimeDirectoryAsync().ConfigureAwait(true);
        var hardware = await new WindowsHardwareDiscovery(_cudaRuntimeDirectory)
            .ProbeAsync()
            .ConfigureAwait(true);
        _lastHardwareSnapshot = hardware;
        _configuredTranscription = new ConfiguredTranscription(engine, modelDirectory, hardware);
        var workerExecutable = Path.Combine(AppContext.BaseDirectory, "EnviousWispr.RuntimeWorker.exe");
        RecordAppLaunchedOnce(
            hardware,
            hasParakeet: hasParakeet,
            hasWhisper: hasWhisper);
        // THE SELECTION IS MADE BEFORE IT IS OBSERVED. This line used to be written above the two
        // Create calls, so it could only ever report the hardware and never what the hardware was
        // used FOR - which is why a machine transcribing on the processor beside an idle graphics
        // card had nothing anywhere saying whether the card was absent, refused, or chosen and
        // broken. Ref: #102.
        _transcriptionEngine = engine == FinalAsrEngine.Whisper
            ? CreateWhisperEngine(
                workerExecutable, modelDirectory, hardware, whisperLanguage, out var selectionReason)
            : CreateParakeetEngine(
                workerExecutable, modelDirectory, hardware, whisperLanguage, out selectionReason);
        _logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            AppEventCode.RuntimeSelectionObserved,
            Engine: engine == FinalAsrEngine.Whisper
                ? DiagnosticEngineChoice.Whisper
                : DiagnosticEngineChoice.Parakeet,
            HardwareClass: DiagnosticHardwareClassFor(hardware),
            RuntimeSelection: selectionReason));
        if (_transcriptionEngine is null)
        {
            _window?.SetSessionStatus(
                DictationStatus.Advisory(
                        "Local transcription is unavailable on this machine", StatusActions.OpenTranscription)
                    .AboutTheTranscriptionEngine());
            _window?.SetTranscriptionReadiness(SpeechEngineReadiness.Unavailable);
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.DictationTranscriptionFailed,
                AppFailureCategory.AsrUnavailable,
                ErrorCode: selectionReason switch
                {
                    DiagnosticRuntimeSelectionReason.SelectionFailedModelPackMissing => AppErrorCode.ModelPackUnavailable,
                    DiagnosticRuntimeSelectionReason.SelectionFailedProviderUnavailable => AppErrorCode.RuntimeProviderUnavailable,
                    DiagnosticRuntimeSelectionReason.SelectionFailedUnsupportedProcessorArchitecture => AppErrorCode.RuntimeProviderIncompatible,
                    _ => AppErrorCode.TranscriptionFailed,
                }));
            return;
        }

        var started = await _transcriptionEngine.StartAsync().ConfigureAwait(true);
        if (!started.Succeeded)
        {
            await _transcriptionEngine.DisposeAsync().ConfigureAwait(true);
            _transcriptionEngine = engine == FinalAsrEngine.Whisper
                ? CreateCpuWhisperEngine(workerExecutable, modelDirectory, hardware, whisperLanguage)
                : CreateCpuParakeetEngine(workerExecutable, modelDirectory, hardware, whisperLanguage);
            started = _transcriptionEngine is null
                ? new RuntimeWorkerResult(false, RuntimeWorkerState.Faulted)
                : await _transcriptionEngine.StartAsync().ConfigureAwait(true);
            if (!started.Succeeded)
            {
                if (_transcriptionEngine is not null)
                {
                    await _transcriptionEngine.DisposeAsync().ConfigureAwait(true);
                }

                _transcriptionEngine = null;
                _window?.SetSessionStatus(
                    DictationStatus.Advisory(
                            "Local transcription could not start", StatusActions.OpenTranscription)
                        .AboutTheTranscriptionEngine());
                _window?.SetTranscriptionReadiness(SpeechEngineReadiness.Unavailable);
                _logger.Write(new AppLogEntry(
                    DateTimeOffset.UtcNow,
                    AppEventCode.DictationTranscriptionFailed,
                    AppFailureCategory.RuntimeWorker,
                    ErrorCode: AppErrorCode.RuntimeWorkerFailed));
                return;
            }

            _configuredTranscription = _configuredTranscription with { ForcePreviewCpu = true };
            ConfigureLivePreview(workerExecutable, hardware, whisperLanguage, parakeetPreviewDirectory, previewModelDirectory, forceCpu: true);
            // THE MEMBER NO SELECTOR CAN PRODUCE. Getting here means the selection SUCCEEDED and the
            // runtime then refused to start, which the selector cannot see and therefore cannot
            // report. Measured on the development machine: the graphics libraries were missing, the
            // card was chosen, the start failed, the processor took over, and dictation ran about a
            // hundred times slower for days with nothing saying so. Ref: #102.
            var degradedReason =
                selectionReason == DiagnosticRuntimeSelectionReason.GpuSelected
                    ? DiagnosticRuntimeSelectionReason.ProcessorSelectedAfterGpuFailedToStart
                    : selectionReason;
            // ADVISORY, NOT QUIET, AND ONLY WHERE THE CARD IS ACTUALLY THE STORY. This is the user's
            // setup needing attention rather than the app breaking, so it takes the advisory pill and
            // carries the consequence rather than the mechanism - a person who reads "ready on the
            // processor" has been told a fact about our plumbing and nothing they can act on. Where
            // the run was never going to be on a card, there is no consequence to report and the
            // quiet line stands.
            _window?.SetSessionStatus(
                (degradedReason == DiagnosticRuntimeSelectionReason.ProcessorSelectedAfterGpuFailedToStart
                    ? DictationStatus.Advisory(
                        "Your graphics card did not start, so dictation is slower", StatusActions.OpenTranscription)
                    : DictationStatus.Quiet("Local transcription ready on the processor"))
                    .AboutTheTranscriptionEngine());
            _window?.SetTranscriptionReadiness(SpeechEngineReadiness.Ready);
            SignalPerformanceUatEvent("ENVIOUSWISPR_UAT_RUNTIME_READY_EVENT");
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.DictationTranscriptionDegraded,
                AppFailureCategory.RuntimeProvider,
                ErrorCode: AppErrorCode.RuntimeProviderUnavailable,
                RuntimeSelection: degradedReason));
            return;
        }

        ConfigureLivePreview(workerExecutable, hardware, whisperLanguage, parakeetPreviewDirectory, previewModelDirectory);
        _window?.SetSessionStatus(
            DictationStatus.Quiet("Local transcription ready").AboutTheTranscriptionEngine());
        _window?.SetTranscriptionReadiness(SpeechEngineReadiness.Ready);
        SignalPerformanceUatEvent("ENVIOUSWISPR_UAT_RUNTIME_READY_EVENT");
    }

    private void ConfigureLivePreview(
        string workerExecutable,
        HardwareSnapshot hardware,
        string language,
        string? parakeetModelDirectory,
        string? modelDirectory,
        bool forceCpu = false)
    {
        var parakeetInt8Complete = parakeetModelDirectory is not null &&
            new LocalParakeetModelProbe().Probe(parakeetModelDirectory).Int8Complete;
        var whisperPreviewComplete = modelDirectory is not null &&
            new LocalWhisperModelProbe().Probe(modelDirectory).PreviewSmallComplete;
        var provider = WhisperPreviewRuntime.Select(hardware, forceCpu);
        var plan = LivePreviewRuntimeResolver.Resolve(
            _settings.Preferences.LivePreviewEngine,
            workerExecutable,
            hardware,
            parakeetModelDirectory,
            parakeetInt8Complete,
            modelDirectory,
            whisperPreviewComplete,
            provider,
            language,
            CurrentWhisperLanguage,
            _cudaRuntimeDirectory);
        if (!plan.Succeeded || plan.EffectiveEngine is null)
        {
            _previewUnavailableReason = plan.UnavailableReason ?? AppErrorCode.ModelPackUnavailable;
            return;
        }

        var threads = LivePreviewRuntimeResolver.ChoosePreviewThreads(hardware);
        _previewUnavailableReason = null;
        var options = plan.EffectiveEngine == LivePreviewEngineChoice.Whisper
            ? new RuntimeWorkerTranscriptionOptions(
                workerExecutable,
                modelDirectory!,
                provider,
                ParakeetModelPack.Quantized,
                threads,
                CpuFallbackThreads: threads,
                StartupTimeout: TimeSpan.FromSeconds(15),
                TranscriptionTimeout: TimeSpan.FromSeconds(15),
                Engine: FinalAsrEngine.Whisper,
                WhisperPack: WhisperModelPack.PreviewSmall,
                Language: language,
                CurrentLanguage: CurrentWhisperLanguage,
                CudaRuntimeDirectory: _cudaRuntimeDirectory)
            : LivePreviewRuntimeResolver.CreateParakeetPreviewOptions(
                workerExecutable,
                parakeetModelDirectory!,
                hardware,
                language,
                CurrentWhisperLanguage);
        _previewEngine = new RuntimeWorkerLivePreviewEngine(
            options,
            _resourceArbiter);
    }

    private RuntimeWorkerTranscriptionEngine? CreateParakeetEngine(
        string workerExecutable,
        string modelDirectory,
        HardwareSnapshot hardware,
        string language,
        out DiagnosticRuntimeSelectionReason reason)
    {
        var models = new LocalParakeetModelProbe().Probe(modelDirectory);
        var selection = ParakeetRuntimeSelector.Select(hardware, models);
        var cpuSelection = ParakeetRuntimeSelector.Select(
            hardware,
            models,
            RuntimeProviderPreference.Cpu);
        // REPORTED ON BOTH EXITS, WHICH IS THE POINT OF THE OUT PARAMETER. The failing path is the
        // one nobody could read: returning null told the caller only that there was no engine.
        reason = DiagnosticRuntimeSelectionReasons.From(selection);
        if (!selection.Succeeded ||
            selection.Provider is null ||
            selection.ModelPack is null ||
            !cpuSelection.Succeeded)
        {
            return null;
        }

        return new RuntimeWorkerTranscriptionEngine(
            new RuntimeWorkerTranscriptionOptions(
                workerExecutable,
                modelDirectory,
                selection.Provider.Value,
                selection.ModelPack.Value,
                selection.IntraOpThreads,
                selection.InterOpThreads,
                CpuFallbackThreads: cpuSelection.IntraOpThreads,
                Language: language,
                CurrentLanguage: CurrentWhisperLanguage,
                CudaRuntimeDirectory: _cudaRuntimeDirectory));
    }

    private RuntimeWorkerTranscriptionEngine? CreateCpuParakeetEngine(
        string workerExecutable,
        string modelDirectory,
        HardwareSnapshot hardware,
        string language)
    {
        var models = new LocalParakeetModelProbe().Probe(modelDirectory);
        var selection = ParakeetRuntimeSelector.Select(
            hardware,
            models,
            RuntimeProviderPreference.Cpu);
        if (!selection.Succeeded ||
            selection.Provider is not RuntimeProviderKind.Cpu ||
            selection.ModelPack is null)
        {
            return null;
        }

        return new RuntimeWorkerTranscriptionEngine(
            new RuntimeWorkerTranscriptionOptions(
                workerExecutable,
                modelDirectory,
                RuntimeProviderKind.Cpu,
                selection.ModelPack.Value,
                selection.IntraOpThreads,
                selection.InterOpThreads,
                CpuFallbackThreads: selection.IntraOpThreads,
                Language: language,
                CurrentLanguage: CurrentWhisperLanguage));
    }

    private RuntimeWorkerTranscriptionEngine? CreateWhisperEngine(
        string workerExecutable,
        string modelDirectory,
        HardwareSnapshot hardware,
        string language,
        out DiagnosticRuntimeSelectionReason reason)
    {
        var models = new LocalWhisperModelProbe().Probe(modelDirectory);
        var selection = WhisperRuntimeSelector.Select(hardware, models);
        var cpuSelection = WhisperRuntimeSelector.Select(
            hardware,
            models,
            RuntimeProviderPreference.Cpu);
        reason = DiagnosticRuntimeSelectionReasons.From(selection);
        if (!selection.Succeeded ||
            selection.Provider is null ||
            selection.ModelPack is null ||
            !cpuSelection.Succeeded)
        {
            return null;
        }

        return new RuntimeWorkerTranscriptionEngine(new RuntimeWorkerTranscriptionOptions(
            workerExecutable,
            modelDirectory,
            selection.Provider.Value,
            ParakeetModelPack.Quantized,
            selection.ThreadCount,
            CpuFallbackThreads: cpuSelection.ThreadCount,
            Engine: FinalAsrEngine.Whisper,
            WhisperPack: selection.ModelPack.Value,
            Language: language,
            CurrentLanguage: CurrentWhisperLanguage,
            CudaRuntimeDirectory: _cudaRuntimeDirectory));
    }

    private RuntimeWorkerTranscriptionEngine? CreateCpuWhisperEngine(
        string workerExecutable,
        string modelDirectory,
        HardwareSnapshot hardware,
        string language)
    {
        var models = new LocalWhisperModelProbe().Probe(modelDirectory);
        var selection = WhisperRuntimeSelector.Select(
            hardware,
            models,
            RuntimeProviderPreference.Cpu);
        if (!selection.Succeeded ||
            selection.Provider is not RuntimeProviderKind.Cpu ||
            selection.ModelPack is null)
        {
            return null;
        }

        return new RuntimeWorkerTranscriptionEngine(new RuntimeWorkerTranscriptionOptions(
            workerExecutable,
            modelDirectory,
            RuntimeProviderKind.Cpu,
            ParakeetModelPack.Quantized,
            selection.ThreadCount,
            CpuFallbackThreads: selection.ThreadCount,
            Engine: FinalAsrEngine.Whisper,
            WhisperPack: selection.ModelPack.Value,
            Language: language,
            CurrentLanguage: CurrentWhisperLanguage));
    }

    /// <summary>Transcribes a chosen audio file for the Transcribe a File page. Ref: #211, macOS #2648.</summary>
    /// <remarks>
    /// ADMITTED LIKE THE WINDOW'S OTHER OPERATIONS, because it borrows the final engine the exit disposes. THE SESSION
    /// IS HELD FOR THE WHOLE FILE, taken before the file is opened and given back after the job returns, inside the
    /// presentation lease so the exit cancels the job and then waits for the hold: a record press meanwhile is refused
    /// at once and the pill says why (macOS, founder decision 2026-09-04). A start refused because the session is in
    /// use says so; with no engine ready the job refuses at once rather than waiting for one that is not coming. Content-free log: the outcome and how long
    /// it took, never a path, a file name or a word.
    /// </remarks>
    private async Task<FileTranscriptionResult> TranscribeFileAsync(
        string path,
        IProgress<FileTranscriptionProgress> progress,
        CancellationToken cancellationToken)
    {
        static FileTranscriptionResult Refused() =>
            new(FileTranscriptionOutcome.Failed, string.Empty, Pieces: 0, TimeSpan.Zero);

        if (Leaving || _transcriptionEngine is null ||
            _presentation is not { } presentation ||
            !presentation.TryEnter(out var lease))
        {
            return Refused();
        }

        using (lease)
        {
            var attempt = _sessionCoordinator?.TryHold(SessionHolder.FileTranscription)
                ?? new SessionHoldAttempt(NoScope.Instance);
            if (attempt.Hold is null)
            {
                _logger.Write(new AppLogEntry(DateTimeOffset.UtcNow, AppEventCode.FileTranscriptionRefused));
                return new FileTranscriptionResult(
                    FileTranscriptionOutcome.Refused, string.Empty, Pieces: 0, TimeSpan.Zero, Refusal: attempt);
            }

            try
            {
                try
                {
            // THE PAGE HEARS THE JOB HAS STARTED before the file is even opened, so it can clear the last result.
            progress.Report(new FileTranscriptionProgress(0, TimeSpan.Zero, null));
            using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.Closing);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            _logger.Write(new AppLogEntry(DateTimeOffset.UtcNow, AppEventCode.FileTranscriptionStarted));
            FileTranscriptionResult result;
            try
            {
                var (source, failure) = await Task.Run(
                    () =>
                    {
                        var opened = AudioFileSource.TryOpen(path, out var why);
                        return (opened, why);
                    },
                    run.Token).ConfigureAwait(false);
                if (source is null)
                {
                    _logger.Write(new AppLogEntry(
                        DateTimeOffset.UtcNow,
                        AppEventCode.FileTranscriptionFailed,
                        failure == AudioFileOpenFailure.NotAudio ? AppFailureCategory.InvalidData : AppFailureCategory.StorageUnavailable,
                        timer.ElapsedMilliseconds,
                        ErrorCode: failure == AudioFileOpenFailure.NotAudio
                            ? AppErrorCode.AudioFormatUnsupported
                            : AppErrorCode.StorageUnavailable));
                    return Refused();
                }

                using (source)
                {
                    var environment = new FileTranscriptionEnvironment(
                        Transcribe: (audio, token) => (_transcriptionEngine
                                ?? throw new TranscriptionEngineException(new AppError(
                                    AppErrorCode.RuntimeProviderUnavailable,
                                    AppErrorStage.FinalAsr,
                                    CanRetry: true)))
                            .TranscribeAsync(audio, token),
                        Finish: async (transcript, token) => (await _deterministicTextPipeline.ProcessAsync(
                                new DeterministicTextRequest(
                                    transcript,
                                    _settings.UserData.CustomWords,
                                    _deterministicTextOptions with { CasualModeEnabled = false },
                                    EnabledVocabularyPackIds: _settings.UserData.EnabledVocabularyPackIds),
                                token).ConfigureAwait(false)).Output.Text);
                    result = await Task.Run(
                        () => FileTranscriptionJob.RunAsync(source, environment, progress, run.Token),
                        run.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (run.IsCancellationRequested)
            {
                result = new FileTranscriptionResult(FileTranscriptionOutcome.Cancelled, string.Empty, Pieces: 0, TimeSpan.Zero);
            }
            catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
            {
                result = Refused();
            }

            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                result.Outcome switch
                {
                    FileTranscriptionOutcome.Completed => AppEventCode.FileTranscriptionCompleted,
                    FileTranscriptionOutcome.Empty => AppEventCode.FileTranscriptionEmpty,
                    FileTranscriptionOutcome.Cancelled => AppEventCode.FileTranscriptionCancelled,
                    _ => AppEventCode.FileTranscriptionFailed,
                },
                result.Outcome == FileTranscriptionOutcome.Failed ? AppFailureCategory.AsrUnavailable : AppFailureCategory.None,
                timer.ElapsedMilliseconds,
                ErrorCode: result.Error?.Code));
            return result;
                }
                finally
                {
                    attempt.Hold.Dispose();
                }
            }
            finally
            {
                TrySchedulePendingModelRefresh();
            }
        }
    }
}
