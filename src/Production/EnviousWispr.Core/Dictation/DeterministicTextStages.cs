namespace EnviousWispr.Core.Dictation;

/// <summary>The named steps the deterministic text pipeline runs, in order.</summary>
/// <remarks>
/// IN CORE RATHER THAN BESIDE THE PIPELINE, because a diagnostic record has to name the stage it is
/// reporting on and <c>EnviousWispr.Core</c> is the layer below the pipeline. Mirroring the enum into
/// diagnostics instead would leave two lists to keep in step, and the one that drifts is always the
/// copy nobody edits.
/// </remarks>
public enum DeterministicTextStage
{
    CustomWords,
    FillerAndFalseStarts,
    SpokenEmoji,
    InverseTextNormalization,
    EmojiRestoration,
}

/// <summary>What one deterministic stage did with the text it was handed.</summary>
/// <remarks>
/// <see cref="Skipped"/> IS A RESULT, NOT AN ABSENCE. A stage that was switched off, or had nothing to
/// work with, is the answer to most questions asked of this pipeline: an empty custom-word list makes
/// correction vanish, and "it ran and changed nothing" and "it never ran" are different facts about a
/// product that claims to correct words.
/// </remarks>
public enum DeterministicStageStatus
{
    Completed,
    Skipped,
    TimedOut,
    Failed,
}
