using EnviousWispr.Audio;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Architecture.Tests;

public sealed class RecordingAppearanceCatalogTests
{
    [Fact]
    public void PillResolverKeepsCapabilityAndDesignCompatible()
    {
        Assert.Equal(
            RecordingPillDesign.LevelRail,
            RecordingPillCatalog.Resolve(
                livePreviewEnabled: false,
                RecordingPillDesign.LevelRail,
                RecordingPillDesign.ReadingWell));
        Assert.Equal(
            RecordingPillDesign.ReadingWell,
            RecordingPillCatalog.Resolve(
                livePreviewEnabled: true,
                RecordingPillDesign.Classic,
                RecordingPillDesign.ReadingWell));
        Assert.Equal(
            RecordingPillDesign.Classic,
            RecordingPillCatalog.Resolve(
                livePreviewEnabled: false,
                RecordingPillDesign.ReadingWell,
                RecordingPillDesign.ReadingWell));
    }

    [Fact]
    public void SoundCatalogMatchesTheFounderApprovedMacOrder()
    {
        var expected = new[]
        {
            RecordingSoundPairing.DustMote,
            RecordingSoundPairing.VelvetHush,
            RecordingSoundPairing.MutedConfirm,
            RecordingSoundPairing.WhisperTick,
            RecordingSoundPairing.RoundPebble,
            RecordingSoundPairing.PaperTap,
            RecordingSoundPairing.SoftHush,
            RecordingSoundPairing.LowNod,
            RecordingSoundPairing.CloudPop,
            RecordingSoundPairing.VelvetTap,
            RecordingSoundPairing.SatinShift,
            RecordingSoundPairing.AirGlint,
        };

        Assert.Equal(expected, RecordingSoundCatalog.Choices.Select(choice => choice.Pairing));
        Assert.All(RecordingSoundCatalog.Choices, choice =>
        {
            Assert.False(string.IsNullOrWhiteSpace(choice.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(choice.Description));
        });
    }

    [Theory]
    [InlineData(RecordingSoundPairing.DustMote)]
    [InlineData(RecordingSoundPairing.WhisperTick)]
    [InlineData(RecordingSoundPairing.AirGlint)]
    public void ProceduralSoundPairsProduceBoundedPcm(
        RecordingSoundPairing pairing)
    {
        var start = RecordingSoundSynthesizer.Create(pairing, RecordingSoundMoment.Start, 44_100);
        var stop = RecordingSoundSynthesizer.Create(pairing, RecordingSoundMoment.Stop, 44_100);

        Assert.NotEmpty(start);
        Assert.NotEmpty(stop);
        Assert.Equal(0, start.Length % sizeof(short));
        Assert.Equal(0, stop.Length % sizeof(short));
        Assert.InRange(start.Length, 2_000, 25_000);
        Assert.InRange(stop.Length, 2_000, 25_000);
    }

    [Fact]
    public void SoundCoordinatorPlaysExactlyOneMatchingPairPerRecording()
    {
        var played = new List<(RecordingSoundPairing Pairing, RecordingSoundMoment Moment)>();
        var coordinator = new RecordingSoundCueCoordinator((pairing, moment) =>
        {
            played.Add((pairing, moment));
            return true;
        });

        coordinator.Handle(true, enabled: true, RecordingSoundPairing.WhisperTick);
        coordinator.Handle(true, enabled: true, RecordingSoundPairing.AirGlint);
        coordinator.Handle(false, enabled: false, RecordingSoundPairing.AirGlint);
        coordinator.Handle(false, enabled: false, RecordingSoundPairing.AirGlint);

        Assert.Equal(
            [
                (RecordingSoundPairing.WhisperTick, RecordingSoundMoment.Start),
                (RecordingSoundPairing.WhisperTick, RecordingSoundMoment.Stop),
            ],
            played);
    }

    [Fact]
    public void FailedStartNeverArmsAnUnmatchedStop()
    {
        var played = new List<RecordingSoundMoment>();
        var coordinator = new RecordingSoundCueCoordinator((_, moment) =>
        {
            played.Add(moment);
            return false;
        });

        coordinator.Handle(true, enabled: true, RecordingSoundPairing.WhisperTick);
        coordinator.Handle(false, enabled: true, RecordingSoundPairing.WhisperTick);

        Assert.Equal([RecordingSoundMoment.Start], played);
    }
}
