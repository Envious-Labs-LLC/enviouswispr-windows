using System.Collections.Concurrent;
using EnviousWispr.Core.Polish;
using EnviousWispr.Core.Runtime;
using EnviousWispr.ModelDelivery;

namespace EnviousWispr.Architecture.Tests;

public sealed class OnboardingDownloadCoordinatorTests
{
    [Fact]
    public void DiskPreflightEnforcesSpeechPlusPolishPlus500MbHeadroom()
    {
        var plan = new OnboardingDownloadPlan(
            SpeechModelId: ParakeetModelIds.Final,
            SpeechTotalBytes: 640L * 1024 * 1024,
            PolishModelId: EgOneModelCatalog.MiniId,
            PolishTotalBytes: 1450L * 1024 * 1024,
            ParallelDownloads: true);

        long expectedRequired = (640L + 1450L) * 1024 * 1024 + OnboardingDownloadCoordinator.DiskHeadroomBytes;

        var insufficient = OnboardingDownloadCoordinator.PreflightDiskSpace(plan, expectedRequired - 1);
        Assert.False(insufficient.Succeeded);
        Assert.Equal(ModelDeliveryFailure.InsufficientDisk, insufficient.Failure);
        Assert.Equal(expectedRequired, insufficient.RequiredBytes);
        Assert.NotNull(insufficient.Message);
        Assert.DoesNotContain('\u2014', insufficient.Message);
        Assert.DoesNotContain('\u2013', insufficient.Message);

        var sufficient = OnboardingDownloadCoordinator.PreflightDiskSpace(plan, expectedRequired);
        Assert.True(sufficient.Succeeded);
        Assert.Equal(ModelDeliveryFailure.None, sufficient.Failure);

        // Already-installed models require 0 bytes so preflight passes even on tight disk.
        var installedPlan = plan with { SpeechAlreadyInstalled = true, PolishAlreadyInstalled = true };
        Assert.True(OnboardingDownloadCoordinator.PreflightDiskSpace(installedPlan, 1024).Succeeded);
    }

    [Fact]
    public void BackgroundPillSummaryFormatsCombinedPercentageCleanly()
    {
        var plan = new OnboardingDownloadPlan(
            SpeechModelId: ParakeetModelIds.Final,
            SpeechTotalBytes: 1000,
            PolishModelId: EgOneModelCatalog.MicroId,
            PolishTotalBytes: 1000,
            ParallelDownloads: true);

        var active = new OnboardingDownloadSnapshot(
            Plan: plan,
            Speech: new OnboardingModelDownloadState(plan.SpeechModelId, OnboardingDownloadStage.Downloading, 420, 1000, 42),
            Polish: new OnboardingModelDownloadState(EgOneModelCatalog.MicroId, OnboardingDownloadStage.Downloading, 420, 1000, 42),
            CombinedPercent: 42.0,
            IsRunning: true,
            IsSpeechReady: false,
            IsPolishReady: false,
            BackgroundPillSummary: null,
            FailureMessage: null);

        Assert.Equal("Downloading models \u00B7 42%", OnboardingDownloadCoordinator.FormatBackgroundPillSummary(active));

        var idle = active with { IsRunning = false };
        Assert.Null(OnboardingDownloadCoordinator.FormatBackgroundPillSummary(idle));
    }

    [Fact]
    public async Task ParallelModeRunsSpeechAndEgOneTogetherAndConfiguresSpeechImmediately()
    {
        var plan = new OnboardingDownloadPlan(
            SpeechModelId: ParakeetModelIds.Final,
            SpeechTotalBytes: 100,
            PolishModelId: EgOneModelCatalog.MiniId,
            PolishTotalBytes: 200,
            ParallelDownloads: true);

        var speechStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var polishStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePolish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var configuredOrder = new ConcurrentQueue<string>();

        using var coordinator = new OnboardingDownloadCoordinator(
            plan,
            provisionAsync: async (modelId, observer, ct) =>
            {
                if (modelId == ParakeetModelIds.Final)
                {
                    speechStarted.TrySetResult();
                    observer.Observe(new ModelDeliveryEvent(DateTimeOffset.UtcNow, ModelDeliveryEventCode.DownloadStarted, CompletedBytes: 50, TotalBytes: 100));
                    observer.Observe(new ModelDeliveryEvent(DateTimeOffset.UtcNow, ModelDeliveryEventCode.ModelActivated, CompletedBytes: 100, TotalBytes: 100));
                    return new ModelDeliveryResult(true);
                }

                polishStarted.TrySetResult();
                await releasePolish.Task.WaitAsync(ct);
                observer.Observe(new ModelDeliveryEvent(DateTimeOffset.UtcNow, ModelDeliveryEventCode.ModelActivated, CompletedBytes: 200, TotalBytes: 200));
                return new ModelDeliveryResult(true);
            },
            onSpeechInstalledAsync: _ =>
            {
                configuredOrder.Enqueue("speech");
                return Task.CompletedTask;
            },
            onPolishInstalledAsync: _ =>
            {
                configuredOrder.Enqueue("polish");
                return Task.CompletedTask;
            },
            diskSpaceProbe: new FixedDiskProbe(10L * 1024 * 1024 * 1024));

        var runTask = coordinator.RunAsync();

        // Both Speech and EG-1 must start concurrently in parallel mode.
        await Task.WhenAll(speechStarted.Task, polishStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));

        // Speech must configure immediately without waiting for EG-1!
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!coordinator.Snapshot.IsSpeechReady && DateTime.UtcNow < deadline)
        {
            await Task.Delay(15);
        }

        Assert.True(coordinator.Snapshot.IsSpeechReady);
        Assert.False(coordinator.Snapshot.IsPolishReady);
        Assert.Contains("speech", configuredOrder);

        releasePolish.SetResult();
        var finalSnapshot = await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(finalSnapshot.IsSpeechReady);
        Assert.True(finalSnapshot.IsPolishReady);
        Assert.Equal(["speech", "polish"], [.. configuredOrder]);
    }

    [Fact]
    public async Task SequentialModeInstallsSpeechFirstActivatesImmediatelyThenDownloadsPolishAndSupportsRetryAndCancel()
    {
        var plan = new OnboardingDownloadPlan(
            SpeechModelId: ParakeetModelIds.Final,
            SpeechTotalBytes: 100,
            PolishModelId: EgOneModelCatalog.MicroId,
            PolishTotalBytes: 200,
            ParallelDownloads: false);

        var trace = new ConcurrentQueue<string>();
        var failPolishOnce = true;

        using var coordinator = new OnboardingDownloadCoordinator(
            plan,
            provisionAsync: (modelId, observer, _) =>
            {
                trace.Enqueue($"provision:{modelId}");
                if (modelId == EgOneModelCatalog.MicroId && failPolishOnce)
                {
                    failPolishOnce = false;
                    return Task.FromResult(new ModelDeliveryResult(false, ModelDeliveryFailure.NetworkUnavailable));
                }

                observer.Observe(new ModelDeliveryEvent(DateTimeOffset.UtcNow, ModelDeliveryEventCode.ModelActivated, CompletedBytes: 100, TotalBytes: 100));
                return Task.FromResult(new ModelDeliveryResult(true));
            },
            onSpeechInstalledAsync: _ =>
            {
                trace.Enqueue("configure:speech");
                return Task.CompletedTask;
            },
            onPolishInstalledAsync: _ =>
            {
                trace.Enqueue("configure:polish");
                return Task.CompletedTask;
            },
            diskSpaceProbe: new FixedDiskProbe(10L * 1024 * 1024 * 1024));

        var firstRun = await coordinator.RunAsync();
        Assert.True(firstRun.IsSpeechReady);
        Assert.False(firstRun.IsPolishReady);
        Assert.Equal(OnboardingDownloadStage.Failed, firstRun.Polish!.Stage);
        Assert.True(firstRun.Polish.CanRetry);

        // Sequential guarantee: speech installed + activated BEFORE polish began.
        Assert.Equal(
            [$"provision:{ParakeetModelIds.Final}", "configure:speech", $"provision:{EgOneModelCatalog.MicroId}"],
            [.. trace]);

        // Retrying retries ONLY EG-1 without re-provisioning already-installed Speech!
        var retried = await coordinator.RetryFailedAsync();
        Assert.True(retried.IsSpeechReady);
        Assert.True(retried.IsPolishReady);
        Assert.Equal(
            [
                $"provision:{ParakeetModelIds.Final}",
                "configure:speech",
                $"provision:{EgOneModelCatalog.MicroId}",
                $"provision:{EgOneModelCatalog.MicroId}",
                "configure:polish",
            ],
            [.. trace]);
    }

    [Fact]
    public async Task ActivationFailureTransitionsSlotToRetryableFailedStateAndRecoversOnRetry()
    {
        var plan = new OnboardingDownloadPlan(
            SpeechModelId: ParakeetModelIds.Final,
            SpeechTotalBytes: 100,
            PolishModelId: null,
            PolishTotalBytes: 0,
            ParallelDownloads: false);

        var throwOnce = true;
        using var coordinator = new OnboardingDownloadCoordinator(
            plan,
            provisionAsync: (_, _, _) => Task.FromResult(new ModelDeliveryResult(true)),
            onSpeechInstalledAsync: _ =>
            {
                if (throwOnce)
                {
                    throwOnce = false;
                    throw new InvalidOperationException("Simulated worker start failure");
                }

                return Task.CompletedTask;
            },
            diskSpaceProbe: new FixedDiskProbe(10L * 1024 * 1024 * 1024));

        var failed = await coordinator.RunAsync();
        Assert.False(failed.IsSpeechReady);
        Assert.False(failed.IsRunning);
        Assert.Equal(OnboardingDownloadStage.Failed, failed.Speech.Stage);
        Assert.True(failed.Speech.CanRetry);
        Assert.False(failed.Speech.CanCancel);
        Assert.NotNull(failed.FailureMessage);

        var recovered = await coordinator.RetryFailedAsync();
        Assert.True(recovered.IsSpeechReady);
        Assert.Equal(OnboardingDownloadStage.Installed, recovered.Speech.Stage);
    }

    [Fact]
    public async Task DiskPreflightAccountsForShardedManifestAssemblyAndAlreadyInstalledPublishesSnapshot()
    {
        var verifier = new ModelManifestVerifier(new Dictionary<string, string>());
        var whisperManifest = BundledModelManifests.Load(WhisperModelIds.Final, verifier);
        Assert.True(whisperManifest.Succeeded);
        var downloadBytes = whisperManifest.Manifest!.Payload.Files.Sum(f => f.SizeBytes);
        var diskBytes = OnboardingDownloadCoordinator.ComputeManifestRequiredDiskBytes(whisperManifest.Manifest!);
        Assert.True(diskBytes > downloadBytes);

        var shardedPlan = new OnboardingDownloadPlan(
            SpeechModelId: WhisperModelIds.Final,
            SpeechTotalBytes: downloadBytes,
            PolishModelId: null,
            PolishTotalBytes: 0,
            ParallelDownloads: false,
            SpeechDiskBytes: diskBytes);

        long tooTightForShardedAssembly = downloadBytes + OnboardingDownloadCoordinator.DiskHeadroomBytes;
        var blocked = OnboardingDownloadCoordinator.PreflightDiskSpace(shardedPlan, tooTightForShardedAssembly);
        Assert.False(blocked.Succeeded);
        Assert.Equal(ModelDeliveryFailure.InsufficientDisk, blocked.Failure);

        long enoughForShardedAssembly = diskBytes + OnboardingDownloadCoordinator.DiskHeadroomBytes;
        Assert.True(OnboardingDownloadCoordinator.PreflightDiskSpace(shardedPlan, enoughForShardedAssembly).Succeeded);

        // When both models are already installed, RunAsync publishes the ready snapshot to observers.
        OnboardingDownloadSnapshot? observed = null;
        var installedPlan = new OnboardingDownloadPlan(
            SpeechModelId: ParakeetModelIds.Final,
            SpeechTotalBytes: 100,
            PolishModelId: EgOneModelCatalog.MicroId,
            PolishTotalBytes: 100,
            ParallelDownloads: true,
            SpeechAlreadyInstalled: true,
            PolishAlreadyInstalled: true);
        using var coordinator = new OnboardingDownloadCoordinator(
            installedPlan,
            provisionAsync: (_, _, _) => throw new InvalidOperationException("Should not download"),
            onSnapshotChanged: snap => observed = snap);
        var result = await coordinator.RunAsync();
        Assert.NotNull(observed);
        Assert.Equal(result, observed);
        Assert.True(observed!.IsSpeechReady);
        Assert.True(observed.IsPolishReady);
    }

    [Fact]
    public async Task DiskPreflightAllowsActivationWhenModelFilesAreAlreadyOnDisk()
    {
        var activationOnlyPlan = new OnboardingDownloadPlan(
            SpeechModelId: WhisperModelIds.Final,
            SpeechTotalBytes: 600_000_000L,
            PolishModelId: EgOneModelCatalog.MicroId,
            PolishTotalBytes: 542_000_000L,
            ParallelDownloads: false,
            SpeechAlreadyInstalled: false,
            PolishAlreadyInstalled: false,
            SpeechDiskBytes: 0L,
            PolishDiskBytes: 0L);

        const long tightAvailableBytes = 64L * 1024 * 1024;
        var activationOnlyPreflight = OnboardingDownloadCoordinator.PreflightDiskSpace(
            activationOnlyPlan,
            tightAvailableBytes);
        Assert.True(activationOnlyPreflight.Succeeded);
        Assert.Equal(0L, activationOnlyPreflight.RequiredBytes);

        var partialPlan = activationOnlyPlan with { PolishDiskBytes = 200_000_000L };
        Assert.False(OnboardingDownloadCoordinator.PreflightDiskSpace(
            partialPlan,
            200_000_000L + OnboardingDownloadCoordinator.DiskHeadroomBytes - 1).Succeeded);
        Assert.True(OnboardingDownloadCoordinator.PreflightDiskSpace(
            partialPlan,
            200_000_000L + OnboardingDownloadCoordinator.DiskHeadroomBytes).Succeeded);

        var speechActivated = false;
        var polishActivated = false;
        using var coordinator = new OnboardingDownloadCoordinator(
            activationOnlyPlan,
            provisionAsync: (_, _, _) => Task.FromResult(new ModelDeliveryResult(true)),
            onSpeechInstalledAsync: _ =>
            {
                speechActivated = true;
                return Task.CompletedTask;
            },
            onPolishInstalledAsync: _ =>
            {
                polishActivated = true;
                return Task.CompletedTask;
            },
            diskSpaceProbe: new FixedDiskProbe(tightAvailableBytes));

        var snapshot = await coordinator.RunAsync();
        Assert.True(speechActivated);
        Assert.True(polishActivated);
        Assert.True(snapshot.IsSpeechReady);
        Assert.True(snapshot.IsPolishReady);
    }

    private sealed class FixedDiskProbe(long availableBytes) : IDiskSpaceProbe
    {
        public long GetAvailableBytes(string path) => availableBytes;
    }
}
