using EnviousWispr.App.Composition;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Presentation;

namespace EnviousWispr.Architecture.Tests;

public sealed class TranscriptionObservabilityEffectsTests
{
    [Fact]
    public void RecordTranscriptionUnavailableLogsModelPackUnavailable()
    {
        var logger = new RecordingLogger();
        var effects = CreateEffects(logger);

        effects.RecordTranscriptionUnavailable();

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(AppEventCode.DictationTranscriptionFailed, entry.Event);
        Assert.Equal(AppFailureCategory.AsrUnavailable, entry.Failure);
        Assert.Equal(AppErrorCode.ModelPackUnavailable, entry.ErrorCode);
    }

    [Theory]
    [InlineData(AppErrorCode.TranscriptionFailed, AppFailureCategory.AsrUnavailable)]
    [InlineData(AppErrorCode.TranscriptionInputTooShort, AppFailureCategory.AsrUnavailable)]
    [InlineData(AppErrorCode.TranscriptionPreprocessorFailed, AppFailureCategory.AsrUnavailable)]
    [InlineData(AppErrorCode.TranscriptionEncoderFailed, AppFailureCategory.AsrUnavailable)]
    [InlineData(AppErrorCode.TranscriptionDecoderFailed, AppFailureCategory.AsrUnavailable)]
    [InlineData(AppErrorCode.RuntimeWorkerFailed, AppFailureCategory.RuntimeWorker)]
    public void RecordTranscriptionFailedPreservesErrorCodeAndCategory(
        AppErrorCode code,
        AppFailureCategory expectedCategory)
    {
        var logger = new RecordingLogger();
        var effects = CreateEffects(logger);
        var failure = new AppError(code, AppErrorStage.FinalAsr, CanRetry: true);

        effects.RecordTranscriptionFailed(failure, 150);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(AppEventCode.DictationTranscriptionFailed, entry.Event);
        Assert.Equal(expectedCategory, entry.Failure);
        Assert.Equal(code, entry.ErrorCode);
        Assert.Equal(150, entry.ElapsedMilliseconds);
    }

    [Theory]
    [InlineData("hello world", AppEventCode.DictationTranscriptionCompleted)]
    [InlineData("", AppEventCode.DictationTranscriptionEmpty)]
    [InlineData("   ", AppEventCode.DictationTranscriptionEmpty)]
    public void RecordTranscriptionFinishedDistinguishesSpeechAndBlankTranscript(
        string text,
        AppEventCode expectedEvent)
    {
        var logger = new RecordingLogger();
        var effects = CreateEffects(logger);
        var transcript = new Transcript(new DictationSessionId(Guid.NewGuid()), text, "parakeet", []);

        effects.RecordTranscriptionFinished(transcript, 210);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(expectedEvent, entry.Event);
        Assert.Equal(AppFailureCategory.None, entry.Failure);
        Assert.Null(entry.ErrorCode);
        Assert.Equal(210, entry.ElapsedMilliseconds);
    }

    [Theory]
    [InlineData(AppErrorCode.RuntimeProviderUnavailable, AppFailureCategory.RuntimeProvider)]
    [InlineData(AppErrorCode.TranscriptionFailed, AppFailureCategory.AsrUnavailable)]
    [InlineData(AppErrorCode.TranscriptionEncoderFailed, AppFailureCategory.AsrUnavailable)]
    [InlineData(AppErrorCode.RuntimeWorkerFailed, AppFailureCategory.RuntimeWorker)]
    public void RecordTranscriptionFinishedPreservesDegradedErrorCodeAndCategory(
        AppErrorCode degradedCode,
        AppFailureCategory expectedCategory)
    {
        var logger = new RecordingLogger();
        var effects = CreateEffects(logger);
        var degradedError = new AppError(degradedCode, AppErrorStage.FinalAsr, CanRetry: true);
        var transcript = new Transcript(
            new DictationSessionId(Guid.NewGuid()),
            "committed speech",
            "parakeet",
            [],
            UsedFallback: true,
            DegradedError: degradedError);

        effects.RecordTranscriptionFinished(transcript, 320);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(AppEventCode.DictationTranscriptionDegraded, entry.Event);
        Assert.Equal(expectedCategory, entry.Failure);
        Assert.Equal(degradedCode, entry.ErrorCode);
        Assert.Equal(320, entry.ElapsedMilliseconds);
    }

    [Fact]
    public void RecordTranscriptionFinishedMapsUntypedFallbackToUnknownFailure()
    {
        var logger = new RecordingLogger();
        var effects = CreateEffects(logger);
        var transcript = new Transcript(
            new DictationSessionId(Guid.NewGuid()),
            "committed speech",
            "parakeet",
            [],
            UsedFallback: true,
            DegradedError: null);

        effects.RecordTranscriptionFinished(transcript, 125);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(AppEventCode.DictationTranscriptionDegraded, entry.Event);
        Assert.Equal(AppFailureCategory.Unknown, entry.Failure);
        Assert.Null(entry.ErrorCode);
    }

    [Theory]
    [InlineData(false, false, AppEventCode.DeterministicProcessingCompleted, AppFailureCategory.None)]
    [InlineData(true, false, AppEventCode.DeterministicProcessingDegraded, AppFailureCategory.PostProcessing)]
    [InlineData(false, true, AppEventCode.DeterministicProcessingEmpty, AppFailureCategory.None)]
    [InlineData(true, true, AppEventCode.DeterministicProcessingEmpty, AppFailureCategory.PostProcessing)]
    public void RecordDeterministicProcessingFinishedDistinguishesEmptyOutput(
        bool degraded,
        bool empty,
        AppEventCode expectedEvent,
        AppFailureCategory expectedCategory)
    {
        var logger = new RecordingLogger();
        var effects = new TranscriptFinalizationEffects(logger, null!);

        effects.RecordDeterministicProcessingFinished(degraded, 35, empty);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(expectedEvent, entry.Event);
        Assert.Equal(expectedCategory, entry.Failure);
        Assert.Equal(35, entry.ElapsedMilliseconds);
    }

    private static SessionFinalizationEffects CreateEffects(IAppLogger logger)
    {
        var shell = new SessionShell(
            new FakeSessionView(),
            AttachedSession: () => null,
            Dictation: () => Core.Settings.DictationPreferences.Default,
            Engine: () => null,
            Delivery: () => null,
            Options: () => null!,
            CloudPolishProviderName: () => null,
            RunId: () => null,
            RecordingActive: _ => { },
            ArchiveAudio: _ => { },
            DetachCaptureObservers: () => { },
            ReleaseSession: () => { },
            DisposeDeliveryRoute: () => { });
        var parts = new SessionCompositionParts(
            null!,
            null!,
            null!,
            null!,
            null!,
            logger,
            shell,
            TimeProvider.System);
        return new SessionFinalizationEffects(parts);
    }

    private sealed class FakeSessionView : ISessionView
    {
        public void ShowStatus(DictationStatus status)
        {
        }

        public void ReportDelivery(DeliveryReport delivered, string? detectedLanguage)
        {
        }

        public void ShowNotice(string title, string message, bool isError = false)
        {
        }

        public void ShowMainWindow()
        {
        }
    }
}
