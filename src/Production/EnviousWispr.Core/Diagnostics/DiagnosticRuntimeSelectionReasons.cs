using EnviousWispr.Core.Runtime;

namespace EnviousWispr.Core.Diagnostics;

/// <summary>Turns an engine selector's own reason into the one the log records.</summary>
/// <remarks>
/// TWO SELECTORS, TWO PRIVATE VOCABULARIES, ONE QUESTION A READER ASKS. Parakeet answers in
/// <see cref="RuntimeSelectionReason"/> and Whisper in <see cref="WhisperRuntimeSelectionReason"/>,
/// and both spell the same three outcomes differently because each names the MODEL PACK it settled
/// on as well as the provider. A reader of the log is asking something narrower - which path is this
/// run on, and what put it there - so the mapping deliberately collapses the pack half away.
///
/// IT THROWS ON AN UNMAPPED MEMBER RATHER THAN RETURNING NULL. Declining to classify is an action: a
/// null here would be written as a line with no reason on it, which is indistinguishable from the
/// silence this whole change exists to remove. The throw cannot reach a user because
/// `EveryRuntimeSelectionReasonHasADiagnosticReason` enumerates both source enums by reflection, so
/// a member added without a mapping is a red test rather than a startup crash.
///
/// MANUAL ACCEPTANCE IS THE ONE ARM THAT NEEDS THE PROVIDER. Both selectors report a user's explicit
/// choice as one member whichever provider was asked for, so the reason alone cannot tell "the user
/// chose the processor" from "the user chose the card" - and those are opposite answers to why a run
/// is slow.
/// </remarks>
public static class DiagnosticRuntimeSelectionReasons
{
    public static DiagnosticRuntimeSelectionReason From(RuntimeSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return selection.Reason switch
        {
            RuntimeSelectionReason.NvidiaCudaWithQdqFreeModel =>
                DiagnosticRuntimeSelectionReason.GpuSelected,

            // THE ONLY AUTOMATIC ROUTE TO THE PROCESSOR. The selector reaches it when no usable
            // graphics path was found, so the pack it names is not the question a reader has.
            RuntimeSelectionReason.TunedCpuUniversalFallback =>
                DiagnosticRuntimeSelectionReason.ProcessorSelectedNoGpuAvailable,

            RuntimeSelectionReason.ManualProviderAccepted => ForManualChoice(selection.Provider),

            // A DECODER INCOMPATIBILITY IS A PROVIDER THAT IS NOT AVAILABLE HERE, from the point of
            // view of somebody reading why their run went the way it did.
            RuntimeSelectionReason.DirectMlIncompatibleWithParakeetDecoder or
                RuntimeSelectionReason.RequestedProviderUnavailable =>
                DiagnosticRuntimeSelectionReason.SelectionFailedProviderUnavailable,

            RuntimeSelectionReason.RequiredModelPackMissing =>
                DiagnosticRuntimeSelectionReason.SelectionFailedModelPackMissing,

            RuntimeSelectionReason.UnsupportedProcessorArchitecture =>
                DiagnosticRuntimeSelectionReason.SelectionFailedUnsupportedProcessorArchitecture,

            _ => throw new ArgumentOutOfRangeException(nameof(selection)),
        };
    }

    public static DiagnosticRuntimeSelectionReason From(WhisperRuntimeSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return selection.Reason switch
        {
            WhisperRuntimeSelectionReason.NvidiaCudaWithFullPrecisionModel or
                WhisperRuntimeSelectionReason.NvidiaCudaWithQuantizedModel =>
                DiagnosticRuntimeSelectionReason.GpuSelected,

            // BOTH TUNED-CPU MEMBERS MEAN THE SAME THING TO A READER. The selector tries the card
            // first under Automatic and only names a processor pack once that returned nothing, and
            // the pack it settled on is a different question from why the card is not in use.
            WhisperRuntimeSelectionReason.TunedCpuWithQuantizedModel or
                WhisperRuntimeSelectionReason.TunedCpuWithFullPrecisionModel =>
                DiagnosticRuntimeSelectionReason.ProcessorSelectedNoGpuAvailable,

            WhisperRuntimeSelectionReason.ManualProviderAccepted => ForManualChoice(selection.Provider),

            WhisperRuntimeSelectionReason.RequestedProviderUnavailable =>
                DiagnosticRuntimeSelectionReason.SelectionFailedProviderUnavailable,

            WhisperRuntimeSelectionReason.RequiredModelPackMissing =>
                DiagnosticRuntimeSelectionReason.SelectionFailedModelPackMissing,

            WhisperRuntimeSelectionReason.UnsupportedProcessorArchitecture =>
                DiagnosticRuntimeSelectionReason.SelectionFailedUnsupportedProcessorArchitecture,

            _ => throw new ArgumentOutOfRangeException(nameof(selection)),
        };
    }

    private static DiagnosticRuntimeSelectionReason ForManualChoice(RuntimeProviderKind? provider) =>
        provider switch
        {
            RuntimeProviderKind.Cpu => DiagnosticRuntimeSelectionReason.ProcessorSelectedByUserChoice,

            // BOTH ACCELERATED PROVIDERS READ AS "ON THE CARD". The reader's question is whether the
            // slow path is in use, and neither of these is it.
            RuntimeProviderKind.Cuda or RuntimeProviderKind.DirectMl =>
                DiagnosticRuntimeSelectionReason.GpuSelected,

            // A SUCCEEDING SELECTION ALWAYS CARRIES A PROVIDER, so null here is a contract break
            // rather than a state to describe, and inventing a category for it would hide that.
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };
}
