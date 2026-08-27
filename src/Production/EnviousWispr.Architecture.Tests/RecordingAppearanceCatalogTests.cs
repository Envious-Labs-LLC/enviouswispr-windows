using EnviousWispr.Audio;
using EnviousWispr.Core.Settings;
using System.Security.Cryptography;

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

    [Fact]
    public void EverySoundChoiceShipsTheFounderApprovedMacAssetsByteForByte()
    {
        var assetDirectory = Path.Combine(
            AppContext.BaseDirectory,
            RecordingSoundAssetCatalog.RelativeDirectory);
        foreach (var pairing in Enum.GetValues<RecordingSoundPairing>())
        {
            foreach (var moment in Enum.GetValues<RecordingSoundMoment>())
            {
                var fileName = RecordingSoundAssetCatalog.FileNameFor(pairing, moment);
                var path = Path.Combine(assetDirectory, fileName);
                var bytes = File.ReadAllBytes(path);
                var actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                var asset = RecordingSoundAssetCatalog.Load(assetDirectory, pairing, moment);

                Assert.Equal(ApprovedSoundAssetHashes[fileName], actualHash);
                Assert.Equal(44_100, asset.Format.SampleRate);
                Assert.Equal(16, asset.Format.BitsPerSample);
                Assert.Equal(1, asset.Format.Channels);
                Assert.InRange(asset.Pcm.Length, 2_000, 25_000);
            }
        }
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

    private static Dictionary<string, string> ApprovedSoundAssetHashes { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["airGlint_start.wav"] = "bc62b9f65f95c08629dec806c87ca40bd50e32176eddf82d3b8fc1e6b5dab50b",
            ["airGlint_stop.wav"] = "6ea513f468a9323bbf98a14095859e7caab9338a419b3a3ba5bc07ce4c731805",
            ["cloudPop_start.wav"] = "853e7be4997c76cbad7bba876cd1d477433ec46d11b2b53e6cb1702e85598a81",
            ["cloudPop_stop.wav"] = "465a77024617cae416a842f553ed420e0e9f69c6b4188b43f3fc45cbb79d6ad8",
            ["dustMote_start.wav"] = "9b4ddc7e31bc440e1424e1a819ee400814ac37f650152ebbeb45fd5e15107e6e",
            ["dustMote_stop.wav"] = "a1354580579245f64d347b4edbeb4a7e065a809cb3e9dd0e3d3cf525dd832a41",
            ["lowNod_start.wav"] = "236c156510f39cfb445070d18102f5e821e65fd0f22cd61e9b48ce4dea40923e",
            ["lowNod_stop.wav"] = "c1d46459d7d8a9eb27473e1d9e4b74e01f1ecd11ba21734be3b6d0afd186f018",
            ["mutedConfirm_start.wav"] = "8e09d9fc66c5e54dffd572a241ad1e468cd6ef57db86719c29c7450e3f49d988",
            ["mutedConfirm_stop.wav"] = "45ccd1a413ece48af02e4ae3efa1fcaf35c5de3041320ab9959e97345a748f56",
            ["paperTap_start.wav"] = "cc580bb4ca766b9b301fea9befc56797460d8f295db1280706c853a5dfcce8c6",
            ["paperTap_stop.wav"] = "43e655f43940508b54be7b591d5145c3185f338219c75012ec2941921f553b42",
            ["roundPebble_start.wav"] = "136238011a1199daf0229427e8c3648482b0abbf92d115b7671c570677f34fdc",
            ["roundPebble_stop.wav"] = "304c58cc2b0b43a09020c664b62d5411e4c1a9e4c8006d81d904612a3dc4a081",
            ["satinShift_start.wav"] = "fc647c43319ead57e9b2ebd3e737427f74f9dc89ae3f533cc2517d05f612d6ab",
            ["satinShift_stop.wav"] = "43a7f659a294762624b57d14275860337e57d7397fbcdc8cb03dfb0cb8ce37b8",
            ["softHush_start.wav"] = "6db7e6dd8e2ee8888b2b938e47992543abb3b340055100ddd6580a71080a770d",
            ["softHush_stop.wav"] = "38c1ebebf90b5caab8a45ac5a37a683a617099a6ad3abe7d0bcf6cbd50cc458b",
            ["velvetHush_start.wav"] = "b7bc04444016b56dc11e6b33abd3923f170281b06cda5fef86caeedc8e6f0f61",
            ["velvetHush_stop.wav"] = "687ec43d4e6ac5a19aa98c65a6ff104ab47c880e9bec72d3fa9bb3b3d4d5bfc7",
            ["velvetTap_start.wav"] = "defe03fe19bb6e9324d5616754d708c235c0e1fe152b82bef198da326c76039c",
            ["velvetTap_stop.wav"] = "90996a6e58acd1b45627af1791d4d41a2fc92b6636d382248c4d8271bf347e02",
            ["whisperTick_start.wav"] = "fbca7902812da697f2b63a75d291fa34413b28970a51a69c5544fb78d8808d7e",
            ["whisperTick_stop.wav"] = "d37f271fb0123fa067672e4fdc2828980e2a16af9e9203512b4a89bbdf0c131d",
        };
}
