using EnviousWispr.Audio;
using EnviousWispr.Core.Settings;
using NAudio.Wave;
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
    public void RecordingChimeWaveformGeneratesDeterministicNormalizedEnvelopes()
    {
        var assetDirectory = Path.Combine(
            AppContext.BaseDirectory,
            RecordingSoundAssetCatalog.RelativeDirectory);

        foreach (var pairing in Enum.GetValues<RecordingSoundPairing>())
        {
            var heights = RecordingChimeWaveform.GetBarHeights(pairing, assetDirectory);
            Assert.Equal(RecordingChimeWaveform.BarCount, heights.Count);
            Assert.All(heights, h => Assert.InRange(h, 0.0, 1.0));
            Assert.True(heights.Any(h => h > 0.5), $"Pairing {pairing} should have distinct peaks");

            var heightsAgain = RecordingChimeWaveform.GetBarHeights(pairing, assetDirectory);
            Assert.Equal(heights, heightsAgain);
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

    [Fact]
    public void FinitePcmWaveProviderReadsExactPayloadAndReturnsZeroAtEof()
    {
        var format = new WaveFormat(44_100, 16, 1);
        var payload = Enumerable.Range(0, 256).Select(i => (byte)(i & 0xFF)).ToArray();
        var provider = new FinitePcmWaveProvider(format, payload);

        var collected = new List<byte>();
        var buffer = new byte[64];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            collected.AddRange(buffer.Take(read));
        }

        Assert.Equal(payload, collected);
        Assert.Equal(0, provider.Read(buffer, 0, buffer.Length));
    }

    [Theory]
    [InlineData(48_000, 2, WaveFormatEncoding.IeeeFloat, 32)]
    [InlineData(48_000, 2, WaveFormatEncoding.Pcm, 16)]
    [InlineData(48_000, 6, WaveFormatEncoding.IeeeFloat, 32)]
    [InlineData(44_100, 2, WaveFormatEncoding.IeeeFloat, 32)]
    public void AdaptedProviderResamplesAndExpandsChannelsWithCleanEof(
        int sampleRate,
        int channels,
        WaveFormatEncoding encoding,
        int bitsPerSample)
    {
        var assetDirectory = Path.Combine(
            AppContext.BaseDirectory,
            RecordingSoundAssetCatalog.RelativeDirectory);
        var asset = RecordingSoundAssetCatalog.Load(
            assetDirectory,
            RecordingSoundPairing.WhisperTick,
            RecordingSoundMoment.Start);
        var targetFormat = encoding == WaveFormatEncoding.IeeeFloat
            ? WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels)
            : new WaveFormat(sampleRate, bitsPerSample, channels);

        var adapted = RecordingSoundCuePlayer.BuildAdaptedProvider(asset, targetFormat);

        Assert.Equal(sampleRate, adapted.WaveFormat.SampleRate);
        Assert.Equal(channels, adapted.WaveFormat.Channels);
        Assert.Equal(encoding, adapted.WaveFormat.Encoding);
        Assert.Equal(bitsPerSample, adapted.WaveFormat.BitsPerSample);

        var buffer = new byte[4096];
        long totalBytes = 0;
        var iterations = 0;
        int read;
        while ((read = adapted.Read(buffer.AsSpan())) > 0)
        {
            totalBytes += read;
            Assert.True(++iterations < 500, "Adapted audio stream did not terminate at EOF.");
        }

        var expectedFrames = (double)asset.Pcm.Length / asset.Format.BlockAlign
            * sampleRate / asset.Format.SampleRate;
        var actualFrames = (double)totalBytes / adapted.WaveFormat.BlockAlign;
        Assert.InRange(actualFrames, expectedFrames * 0.9, expectedFrames * 1.1 + 128);
        Assert.Equal(0, adapted.Read(buffer.AsSpan()));
    }

    [Fact]
    public async Task PreloadCachesAssetsInMemorySoPlaybackSurvivesDeletedSourceDirectory()
    {
        var sourceDir = Path.Combine(
            AppContext.BaseDirectory,
            RecordingSoundAssetCatalog.RelativeDirectory);
        var tempDir = Path.Combine(
            Path.GetTempPath(),
            $"enviouswispr-cues-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var startFile = RecordingSoundAssetCatalog.FileNameFor(
                RecordingSoundPairing.WhisperTick,
                RecordingSoundMoment.Start);
            var stopFile = RecordingSoundAssetCatalog.FileNameFor(
                RecordingSoundPairing.WhisperTick,
                RecordingSoundMoment.Stop);
            File.Copy(Path.Combine(sourceDir, startFile), Path.Combine(tempDir, startFile));
            File.Copy(Path.Combine(sourceDir, stopFile), Path.Combine(tempDir, stopFile));

            var playedMoments = new List<int>();
            var startCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var failures = new List<Exception>();
            using var player = new RecordingSoundCuePlayer(
                tempDir,
                (_, _, ex) => failures.Add(ex),
                (asset, _) =>
                {
                    playedMoments.Add(asset.Pcm.Length);
                    startCompleted.TrySetResult(true);
                    return Task.CompletedTask;
                });

            player.Preload(RecordingSoundPairing.WhisperTick);
            Assert.True(player.HasCachedAsset(RecordingSoundPairing.WhisperTick, RecordingSoundMoment.Start));
            Assert.True(player.HasCachedAsset(RecordingSoundPairing.WhisperTick, RecordingSoundMoment.Stop));

            Directory.Delete(tempDir, recursive: true);

            Assert.True(player.Play(RecordingSoundPairing.WhisperTick, RecordingSoundMoment.Start));
            await startCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(failures);
            Assert.Single(playedMoments);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PlayDispatchesOffCallingThreadAndFailureCallbackRollsBackGenerationSafely()
    {
        var sourceDir = Path.Combine(
            AppContext.BaseDirectory,
            RecordingSoundAssetCatalog.RelativeDirectory);
        var releasePlayback = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failureReported = new TaskCompletionSource<(RecordingSoundPairing Pairing, RecordingSoundMoment Moment, Exception Ex)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        RecordingSoundCueCoordinator? coordinator = null;
        using var player = new RecordingSoundCuePlayer(
            sourceDir,
            (pairing, moment, ex) => failureReported.TrySetResult((pairing, moment, ex)),
            async (_, _) =>
            {
                await releasePlayback.Task.ConfigureAwait(false);
                throw new InvalidOperationException("No output device");
            });

        var attempted = new List<(RecordingSoundPairing Pairing, RecordingSoundMoment Moment)>();
        var staleGeneration = 0UL;
        coordinator = new RecordingSoundCueCoordinator((pairing, moment) =>
        {
            attempted.Add((pairing, moment));
            if (moment == RecordingSoundMoment.Start)
            {
                var generation = coordinator!.CurrentGeneration;
                staleGeneration = generation;
                return player.Play(pairing, moment, () => coordinator.RollbackStart(pairing, generation));
            }

            return player.Play(pairing, moment);
        });

        coordinator.Handle(isRecording: true, enabled: true, RecordingSoundPairing.WhisperTick);
        Assert.False(failureReported.Task.IsCompleted, "Play returned without waiting for async playback completion.");

        releasePlayback.SetResult(true);
        var reported = await failureReported.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RecordingSoundPairing.WhisperTick, reported.Pairing);
        Assert.Equal(RecordingSoundMoment.Start, reported.Moment);
        Assert.IsType<InvalidOperationException>(reported.Ex);

        // Once rolled back by the failure callback, Stop no longer fires an orphan cue.
        coordinator.Handle(isRecording: false, enabled: true, RecordingSoundPairing.WhisperTick);
        Assert.Single(attempted);
        Assert.False(coordinator.RollbackStart(RecordingSoundPairing.WhisperTick, staleGeneration));
    }

    [Fact]
    public async Task CancelPendingStartCancelsInFlightStartAndSerializesWithStopCue()
    {
        var sourceDir = Path.Combine(
            AppContext.BaseDirectory,
            RecordingSoundAssetCatalog.RelativeDirectory);
        var startAsset = RecordingSoundAssetCatalog.Load(
            sourceDir,
            RecordingSoundPairing.WhisperTick,
            RecordingSoundMoment.Start);
        var startEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopExecuted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sequence = new List<string>();
        var failures = new List<Exception>();

        using var player = new RecordingSoundCuePlayer(
            sourceDir,
            (_, _, ex) => failures.Add(ex),
            async (asset, ct) =>
            {
                if (asset.Pcm.AsSpan().SequenceEqual(startAsset.Pcm))
                {
                    sequence.Add("Start:Entered");
                    startEntered.TrySetResult(true);
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
                    return;
                }

                sequence.Add("Stop:Executed");
                stopExecuted.TrySetResult(true);
            });

        Assert.True(player.Play(RecordingSoundPairing.WhisperTick, RecordingSoundMoment.Start));
        await startEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Queue Stop right before cancelling pending Start to verify FIFO serialization + cancellation.
        Assert.True(player.Play(RecordingSoundPairing.WhisperTick, RecordingSoundMoment.Stop));
        player.CancelPendingStart();

        await stopExecuted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["Start:Entered", "Stop:Executed"], sequence);
        Assert.Empty(failures);
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
