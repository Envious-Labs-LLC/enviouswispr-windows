using EnviousWispr.Core.Reliability;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// Unloading frees memory and costs the next dictation a cold start, so most of these assert it
/// does NOT unload.
/// </summary>
public sealed class ModelUnloadPolicyTests
{
    private static SystemResourceSnapshot Memory(ulong availableBytes) =>
        new(
            AvailableDiskBytes: long.MaxValue,
            AvailablePhysicalMemoryBytes: availableBytes,
            MemoryLoadPercent: 50,
            IsAvailable: true);

    private static readonly SystemResourceSnapshot Plenty =
        Memory(ModelUnloadPolicy.MemoryPressureBytes * 4);

    private static readonly SystemResourceSnapshot Tight =
        Memory(ModelUnloadPolicy.MemoryPressureBytes / 2);

    /// <summary>
    /// The one outcome that loses a user's words rather than delaying them. Guarded first in the
    /// policy and first here.
    /// </summary>
    [Fact]
    public void AModelIsNeverUnloadedOutFromUnderARunningDictation()
    {
        Assert.Equal(
            ModelUnloadDecision.Keep,
            ModelUnloadPolicy.Decide(isRecording: true, TimeSpan.FromHours(1), Tight));
    }

    [Fact]
    public void AnOrdinaryGapBetweenDictationsKeepsTheModel()
    {
        Assert.Equal(
            ModelUnloadDecision.Keep,
            ModelUnloadPolicy.Decide(isRecording: false, TimeSpan.FromMinutes(2), Plenty));
    }

    /// <summary>
    /// The control for every Keep above. A long idle on a healthy machine must eventually unload,
    /// or a policy that never unloaded would pass the whole rest of this file.
    /// </summary>
    [Fact]
    public void ALongIdleGivesTheMemoryBack()
    {
        Assert.Equal(
            ModelUnloadDecision.Unload,
            ModelUnloadPolicy.Decide(
                isRecording: false,
                ModelUnloadPolicy.IdleBeforeUnload,
                Plenty));
    }

    /// <summary>
    /// Memory pressure is a DUTY rather than an optimisation, so it acts at a much shorter idle:
    /// a machine that is swapping is not one where our warm model is helping anybody.
    /// </summary>
    [Fact]
    public void MemoryPressureUnloadsMuchSooner()
    {
        var idle = ModelUnloadPolicy.IdleBeforeUnloadUnderPressure;

        Assert.Equal(ModelUnloadDecision.Unload, ModelUnloadPolicy.Decide(false, idle, Tight));
        Assert.Equal(ModelUnloadDecision.Keep, ModelUnloadPolicy.Decide(false, idle, Plenty));
    }

    /// <summary>
    /// A momentary spike while the user is mid-thought between two dictations must not cost them a
    /// cold start on the second one.
    /// </summary>
    [Fact]
    public void PressureAloneIsNotEnoughWithoutAnIdle()
    {
        Assert.Equal(
            ModelUnloadDecision.Keep,
            ModelUnloadPolicy.Decide(isRecording: false, TimeSpan.FromSeconds(5), Tight));
    }

    /// <summary>
    /// An unreadable probe is a fact about the PROBE. Letting it stand in for a memory emergency
    /// would make an instrument failure slow down the user's next dictation, which is the
    /// plausible-value trap: a missing reading becoming a confident one.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnUnreadableProbeIsTreatedAsNoPressureRatherThanAsPressure(bool nullSnapshot)
    {
        var snapshot = nullSnapshot
            ? null
            : new SystemResourceSnapshot(
                AvailableDiskBytes: 0,
                AvailablePhysicalMemoryBytes: 0,
                MemoryLoadPercent: 0,
                IsAvailable: false);

        var idle = ModelUnloadPolicy.IdleBeforeUnloadUnderPressure;

        Assert.Equal(ModelUnloadDecision.Keep, ModelUnloadPolicy.Decide(false, idle, snapshot));
        // And the ordinary threshold still applies, so it is not simply never unloading.
        Assert.Equal(
            ModelUnloadDecision.Unload,
            ModelUnloadPolicy.Decide(false, ModelUnloadPolicy.IdleBeforeUnload, snapshot));
    }

    /// <summary>
    /// Both thresholds are reachable from either side, so each is a real duration rather than
    /// whatever the code happens to do.
    /// </summary>
    [Fact]
    public void BothThresholdsAreReachableFromBothSides()
    {
        var tick = TimeSpan.FromMilliseconds(1);

        Assert.Equal(
            ModelUnloadDecision.Keep,
            ModelUnloadPolicy.Decide(false, ModelUnloadPolicy.IdleBeforeUnload - tick, Plenty));
        Assert.Equal(
            ModelUnloadDecision.Unload,
            ModelUnloadPolicy.Decide(false, ModelUnloadPolicy.IdleBeforeUnload, Plenty));

        Assert.Equal(
            ModelUnloadDecision.Keep,
            ModelUnloadPolicy.Decide(false, ModelUnloadPolicy.IdleBeforeUnloadUnderPressure - tick, Tight));
        Assert.Equal(
            ModelUnloadDecision.Unload,
            ModelUnloadPolicy.Decide(false, ModelUnloadPolicy.IdleBeforeUnloadUnderPressure, Tight));
    }

    /// <summary>
    /// The pressure threshold must sit ABOVE the point at which a dictation is refused to start.
    /// This policy exists to act before the user is turned away, not to be a second opinion about
    /// the same emergency - and if the two ever crossed, the model would only be freed once it was
    /// already too late to help.
    /// </summary>
    [Fact]
    public void PressureIsNoticedWellBeforeADictationWouldBeRefused()
    {
        Assert.True(
            ModelUnloadPolicy.MemoryPressureBytes >
                SystemResourceAdmissionPolicy.MinimumDictationMemoryBytes,
            "The unload policy would only free memory after dictation had already been refused.");
    }
}
