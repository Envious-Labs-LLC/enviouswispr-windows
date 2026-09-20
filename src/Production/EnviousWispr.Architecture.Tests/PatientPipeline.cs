using EnviousWispr.Core.Dictation;
using EnviousWispr.Pipeline;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// The production text stages with their deadlines lifted, for tests that are about what the stages
/// decide and not about whether they decide it in fifty milliseconds.
/// </summary>
/// <remarks>
/// A COLD HOSTED RUNNER MISSES A 50 MS DEADLINE ON ITS FIRST PASS, and the executor then does what it
/// is built to do: falls back to the input. A finalizer test that expected "hello world" saw "um hello
/// world" and went red for a reason that had nothing to do with the finalizer - three of four runs on
/// one afternoon (#165). The tests OF the deadline keep their tight timeouts; they live in the
/// pipeline's own tests. Everything downstream of the pipeline uses this.
/// </remarks>
internal static class PatientPipeline
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    public static DeterministicTextPipeline Create() =>
        new(DeterministicTextPipeline.DefaultSteps().Select(step => new PatientStep(step)).ToArray());

    private sealed class PatientStep(IDeterministicTextStep inner) : IDeterministicTextStep
    {
        public DeterministicTextStage Stage => inner.Stage;

        public TimeSpan Timeout => Patience;

        public bool IsEnabled(DeterministicTextContext context) => inner.IsEnabled(context);

        public DeterministicTextContext Process(DeterministicTextContext context, CancellationToken cancellationToken) =>
            inner.Process(context, cancellationToken);
    }
}
