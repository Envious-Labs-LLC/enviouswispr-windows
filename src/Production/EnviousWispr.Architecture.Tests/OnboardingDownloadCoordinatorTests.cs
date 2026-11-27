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

    private sealed class FixedDiskProbe(long availableBytes) : IDiskSpaceProbe
    {
        public long GetAvailableBytes(string path) => availableBytes;
    }
}
