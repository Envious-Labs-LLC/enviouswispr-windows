using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Runtime;

namespace EnviousWispr.Architecture.Tests;

/// <summary>The log can say why a run is on the path it is on, for every reason a selector has.</summary>
/// <remarks>
/// THE ENUMERATION IS THE TEST, NOT THE EXAMPLES. Both selectors own a private reason vocabulary and
/// the app now writes a mapped one to the log, so the failure this guards is a member added to a
/// selector and never mapped - which would throw on a real user's startup path. Reflecting over the
/// source enums means the guard covers members that do not exist yet, which a list of cases cannot.
/// </remarks>
public sealed class DiagnosticRuntimeSelectionReasonTests
{
    public static TheoryData<RuntimeSelectionReason, RuntimeProviderKind> ParakeetReasons()
    {
        var data = new TheoryData<RuntimeSelectionReason, RuntimeProviderKind>();
        foreach (var reason in Enum.GetValues<RuntimeSelectionReason>())
        {
            foreach (var provider in Enum.GetValues<RuntimeProviderKind>())
            {
                data.Add(reason, provider);
            }
        }

        return data;
    }

    public static TheoryData<WhisperRuntimeSelectionReason, RuntimeProviderKind> WhisperReasons()
    {
        var data = new TheoryData<WhisperRuntimeSelectionReason, RuntimeProviderKind>();
        foreach (var reason in Enum.GetValues<WhisperRuntimeSelectionReason>())
        {
            foreach (var provider in Enum.GetValues<RuntimeProviderKind>())
            {
                data.Add(reason, provider);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ParakeetReasons))]
    public void EveryParakeetSelectionReasonHasADiagnosticReason(
        RuntimeSelectionReason reason,
        RuntimeProviderKind provider)
    {
        var mapped = DiagnosticRuntimeSelectionReasons.From(new RuntimeSelection(
            true,
            provider,
            ParakeetModelPack.Quantized,
            IntraOpThreads: 4,
            InterOpThreads: 1,
            reason));

        Assert.True(Enum.IsDefined(mapped));
    }

    [Theory]
    [MemberData(nameof(WhisperReasons))]
    public void EveryWhisperSelectionReasonHasADiagnosticReason(
        WhisperRuntimeSelectionReason reason,
        RuntimeProviderKind provider)
    {
        var mapped = DiagnosticRuntimeSelectionReasons.From(new WhisperRuntimeSelection(
            true,
            provider,
            WhisperModelPack.Quantized,
            ThreadCount: 4,
            reason));

        Assert.True(Enum.IsDefined(mapped));
    }

    /// <summary>A user who asked for the processor is not reported as a machine that had no card.</summary>
    /// <remarks>
    /// THE ONE ARM WHERE THE REASON ALONE IS NOT ENOUGH. Both selectors report an explicit choice as
    /// `ManualProviderAccepted` whichever provider was asked for, so a mapping that read only the
    /// reason would answer "why is this slow" with the same word for a deliberate choice and for a
    /// broken card. Those are opposite investigations.
    /// </remarks>
    [Theory]
    [InlineData(RuntimeProviderKind.Cpu, DiagnosticRuntimeSelectionReason.ProcessorSelectedByUserChoice)]
    [InlineData(RuntimeProviderKind.Cuda, DiagnosticRuntimeSelectionReason.GpuSelected)]
    [InlineData(RuntimeProviderKind.DirectMl, DiagnosticRuntimeSelectionReason.GpuSelected)]
    public void ManualAcceptanceIsReportedAgainstTheProviderTheUserChose(
        RuntimeProviderKind provider,
        DiagnosticRuntimeSelectionReason expected)
    {
        Assert.Equal(
            expected,
            DiagnosticRuntimeSelectionReasons.From(new WhisperRuntimeSelection(
                true,
                provider,
                WhisperModelPack.Quantized,
                ThreadCount: 4,
                WhisperRuntimeSelectionReason.ManualProviderAccepted)));
        Assert.Equal(
            expected,
            DiagnosticRuntimeSelectionReasons.From(new RuntimeSelection(
                true,
                provider,
                ParakeetModelPack.Quantized,
                IntraOpThreads: 4,
                InterOpThreads: 1,
                RuntimeSelectionReason.ManualProviderAccepted)));
    }

    [Fact]
    public void TheAutomaticProcessorFallbackSaysNoCardWasAvailable()
    {
        Assert.Equal(
            DiagnosticRuntimeSelectionReason.ProcessorSelectedNoGpuAvailable,
            DiagnosticRuntimeSelectionReasons.From(new WhisperRuntimeSelection(
                true,
                RuntimeProviderKind.Cpu,
                WhisperModelPack.Quantized,
                ThreadCount: 4,
                WhisperRuntimeSelectionReason.TunedCpuWithQuantizedModel)));
        Assert.Equal(
            DiagnosticRuntimeSelectionReason.ProcessorSelectedNoGpuAvailable,
            DiagnosticRuntimeSelectionReasons.From(new RuntimeSelection(
                true,
                RuntimeProviderKind.Cpu,
                ParakeetModelPack.Quantized,
                IntraOpThreads: 4,
                InterOpThreads: 1,
                RuntimeSelectionReason.TunedCpuUniversalFallback)));
    }

    /// <summary>An unmapped member is loud, because a silent one is the defect being fixed.</summary>
    [Fact]
    public void AnUnmappedReasonRefusesRatherThanReportingNothing()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DiagnosticRuntimeSelectionReasons.From(new WhisperRuntimeSelection(
                true,
                RuntimeProviderKind.Cpu,
                WhisperModelPack.Quantized,
                ThreadCount: 4,
                (WhisperRuntimeSelectionReason)int.MaxValue)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DiagnosticRuntimeSelectionReasons.From(new RuntimeSelection(
                true,
                RuntimeProviderKind.Cpu,
                ParakeetModelPack.Quantized,
                IntraOpThreads: 4,
                InterOpThreads: 1,
                (RuntimeSelectionReason)int.MaxValue)));
    }

    [Fact]
    public void AnUndefinedRuntimeSelectionCannotReachThePrivacySafeRecord()
    {
        var record = PrivacySafeDiagnosticRecord.From(new AppLogEntry(
            DateTimeOffset.UtcNow,
            AppEventCode.RuntimeSelectionObserved,
            RuntimeSelection: (DiagnosticRuntimeSelectionReason)int.MaxValue));

        Assert.Null(record.RuntimeSelection);
    }

    [Fact]
    public void TheRuntimeSelectionSurvivesTheTripToTheLocalLine()
    {
        var record = PrivacySafeDiagnosticRecord.From(new AppLogEntry(
            DateTimeOffset.UtcNow,
            AppEventCode.RuntimeSelectionObserved,
            RuntimeSelection: DiagnosticRuntimeSelectionReason.ProcessorSelectedAfterGpuFailedToStart));

        Assert.Equal(
            DiagnosticRuntimeSelectionReason.ProcessorSelectedAfterGpuFailedToStart,
            LocalDiagnosticLine.From(record, dictationId: null).RuntimeSelection);
    }
}

/// <summary>A status says for itself whether the Transcription card should show it.</summary>
/// <remarks>
/// THE WINDOW USED TO DECIDE THIS BY SEARCHING THE SENTENCE FOR FOUR PHRASES, which is only testable
/// through a window that no test here can build. Moving the answer onto the status is what makes it
/// checkable at all, and these are the checks that were impossible before.
/// </remarks>
public sealed class TranscriptionEngineStatusTests
{
    [Fact]
    public void AStatusIsNotAboutTheEngineUnlessItSaysSo()
    {
        Assert.False(DictationStatus.Quiet("Windows resumed. EnviousWispr is ready")
            .DescribesTranscriptionEngine);
        Assert.False(DictationStatus.Quiet("Escape Recovery finished. Text is ready to copy")
            .DescribesTranscriptionEngine);
        Assert.False(DictationStatus.Quiet("Ollama is ready").DescribesTranscriptionEngine);
        Assert.False(DictationStatus.Quiet("Cleaned locally; the selected Ollama model is not installed")
            .DescribesTranscriptionEngine);
    }

    /// <summary>Marking a status changes nothing else about it.</summary>
    [Fact]
    public void MarkingAStatusKeepsItsSentencePillAndButton()
    {
        var action = new PillAction("Open settings", PillActionKind.OpenTranscriptionSettings);
        var marked = DictationStatus
            .Advisory("Your graphics card did not start, so dictation is slower", action)
            .AboutTheTranscriptionEngine();

        Assert.True(marked.DescribesTranscriptionEngine);
        Assert.Equal("Your graphics card did not start, so dictation is slower", marked.Text);
        Assert.Equal(DictationOverlayState.Advisory, marked.State);
        Assert.Equal(action, marked.Action);
    }

    /// <summary>
    /// A sentence carrying none of the four old phrases still reaches the card once it is marked.
    /// </summary>
    /// <remarks>
    /// THIS IS THE HALF THE OLD MECHANISM COULD NOT DO. The new degraded sentence contains no
    /// "ready", no "unavailable" and no "not installed", so under the previous rule the Transcription
    /// card would have kept showing whatever it last said while the app told the user on the pill
    /// that their graphics card had failed.
    /// </remarks>
    [Fact]
    public void AnEngineSentenceReachesTheCardWithoutContainingAnyOldTriggerWord()
    {
        var status = DictationStatus
            .Advisory("Your graphics card did not start, so dictation is slower")
            .AboutTheTranscriptionEngine();

        foreach (var phrase in new[]
                 {
                     "ready", "model is not installed", "transcription is unavailable",
                     "worker could not start",
                 })
        {
            Assert.DoesNotContain(phrase, status.Text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.True(status.DescribesTranscriptionEngine);
    }
}
