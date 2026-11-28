using System.Globalization;
using System.IO;
using System.Xml.Linq;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Onboarding;
using EnviousWispr.Core.Polish;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.LLM;
using EnviousWispr.Presentation;
using EnviousWispr.Services.Diagnostics;
using EnviousWispr.Services.Distribution;

namespace EnviousWispr.Architecture.Tests;

[Collection("UatHardwareProfileEnv")]
public sealed class LiveMemoryBudgetTests
{
    private const string XNamespaceUri = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData(64UL << 30, 20UL << 30, 44UL << 30)]
    [InlineData(1000UL, 0UL, 1000UL)]
    [InlineData(1000UL, 1000UL, 0UL)]
    public void SystemMemoryReaderComputesValidUsedBytes(ulong total, ulong available, ulong expectedUsed)
    {
        Assert.Equal(expectedUsed, SystemMemoryReader.ComputeUsedBytes(total, available));
    }

    [Theory]
    [InlineData(0UL, 0UL)]
    [InlineData(100UL, 101UL)]
    [InlineData(1UL << 30, (1UL << 30) + 1UL)]
    public void SystemMemoryReaderReturnsNullOnInconsistentCounters(ulong total, ulong available)
    {
        Assert.Null(SystemMemoryReader.ComputeUsedBytes(total, available));
    }

    [Fact]
    public void SystemMemoryReaderHonorsAndClampsUatOverrides()
    {
        var osReading = new SystemMemoryReading(64UL * EgOneMemoryFacts.BytesPerGiB, 32UL * EgOneMemoryFacts.BytesPerGiB);

        var bothOverridden = SystemMemoryReader.ApplyOverrides(osReading, "ram=16;gpu=none", "10.5");
        Assert.NotNull(bothOverridden);
        Assert.Equal(16UL * EgOneMemoryFacts.BytesPerGiB, bothOverridden.Value.TotalPhysicalBytes);
        Assert.Equal((ulong)Math.Round(10.5 * EgOneMemoryFacts.BytesPerGiB), bothOverridden.Value.UsedBytes);

        var clampedUsed = SystemMemoryReader.ApplyOverrides(osReading, "ram=8", "99");
        Assert.Equal(8UL * EgOneMemoryFacts.BytesPerGiB, clampedUsed!.Value.TotalPhysicalBytes);
        Assert.Equal(8UL * EgOneMemoryFacts.BytesPerGiB, clampedUsed.Value.UsedBytes);

        var ramOnlyClampsRealUsed = SystemMemoryReader.ApplyOverrides(osReading, "ram=8", null);
        Assert.Equal(8UL * EgOneMemoryFacts.BytesPerGiB, ramOnlyClampsRealUsed!.Value.TotalPhysicalBytes);
        Assert.Equal(8UL * EgOneMemoryFacts.BytesPerGiB, ramOnlyClampsRealUsed.Value.UsedBytes);

        var invalidUsedIgnored = SystemMemoryReader.ApplyOverrides(osReading, null, "not-a-number");
        Assert.Equal(osReading, invalidUsedIgnored);

        var zeroUsedAllowed = SystemMemoryReader.ApplyOverrides(osReading, null, "0");
        Assert.Equal(0UL, zeroUsedAllowed!.Value.UsedBytes);

        Assert.Null(SystemMemoryReader.ApplyOverrides(null, "ram=16", null));
    }

    [Fact]
    public void SystemMemoryReaderLiveEnvOverrideAndRealReadStayWithinBounds()
    {
        var previousProfile = Environment.GetEnvironmentVariable(HardwareProfileSummary.UatHardwareProfileEnvVar);
        var previousUsed = Environment.GetEnvironmentVariable(SystemMemoryReader.UatMemoryUsedGbEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(HardwareProfileSummary.UatHardwareProfileEnvVar, null);
            Environment.SetEnvironmentVariable(SystemMemoryReader.UatMemoryUsedGbEnvVar, null);

            var live = SystemMemoryReader.ReadCurrent();
            Assert.NotNull(live);
            Assert.True(live.Value.TotalPhysicalBytes > 0UL);
            Assert.True(live.Value.UsedBytes <= live.Value.TotalPhysicalBytes);

            Environment.SetEnvironmentVariable(HardwareProfileSummary.UatHardwareProfileEnvVar, "ram=12;gpu=none;cores=4");
            Environment.SetEnvironmentVariable(SystemMemoryReader.UatMemoryUsedGbEnvVar, "15");
            var overridden = SystemMemoryReader.ReadCurrent();
            Assert.NotNull(overridden);
            Assert.Equal(12UL * EgOneMemoryFacts.BytesPerGiB, overridden.Value.TotalPhysicalBytes);
            Assert.Equal(12UL * EgOneMemoryFacts.BytesPerGiB, overridden.Value.UsedBytes);
        }
        finally
        {
            Environment.SetEnvironmentVariable(HardwareProfileSummary.UatHardwareProfileEnvVar, previousProfile);
            Environment.SetEnvironmentVariable(SystemMemoryReader.UatMemoryUsedGbEnvVar, previousUsed);
        }
    }

    [Fact]
    public void SystemMemorySamplerPublishesOnlyShownChangesAndSkipsWhenHidden()
    {
        var total = 64UL * EgOneMemoryFacts.BytesPerGiB;
        SystemMemoryReading? current = new(total, 10UL * EgOneMemoryFacts.BytesPerGiB);
        var visible = true;
        var reads = 0;

        var sampler = new SystemMemorySampler(
            () =>
            {
                reads++;
                return current;
            },
            () => visible);

        Assert.True(sampler.Tick());
        Assert.Equal(1, reads);
        Assert.Equal(10UL * EgOneMemoryFacts.BytesPerGiB, sampler.LatestReading!.Value.UsedBytes);

        current = new(total, (ulong)(10.04 * EgOneMemoryFacts.BytesPerGiB));
        Assert.False(sampler.Tick());
        Assert.Equal(10UL * EgOneMemoryFacts.BytesPerGiB, sampler.LatestReading!.Value.UsedBytes);

        current = new(total, (ulong)(10.06 * EgOneMemoryFacts.BytesPerGiB));
        Assert.True(sampler.Tick());
        Assert.Equal((ulong)(10.06 * EgOneMemoryFacts.BytesPerGiB), sampler.LatestReading!.Value.UsedBytes);

        visible = false;
        var readsBeforeHidden = reads;
        current = new(total, 20UL * EgOneMemoryFacts.BytesPerGiB);
        Assert.False(sampler.Tick());
        Assert.Equal(readsBeforeHidden, reads);

        visible = true;
        Assert.True(sampler.Tick());
        Assert.Equal(20UL * EgOneMemoryFacts.BytesPerGiB, sampler.LatestReading!.Value.UsedBytes);

        current = null;
        Assert.True(sampler.Tick());
        Assert.Null(sampler.LatestReading);
        Assert.False(sampler.Tick());

        current = new(total, 12UL * EgOneMemoryFacts.BytesPerGiB);
        Assert.True(sampler.Tick());
        Assert.Equal(12UL * EgOneMemoryFacts.BytesPerGiB, sampler.LatestReading!.Value.UsedBytes);

        current = new(32UL * EgOneMemoryFacts.BytesPerGiB, 12UL * EgOneMemoryFacts.BytesPerGiB);
        Assert.True(sampler.Tick());
        Assert.Equal(32UL * EgOneMemoryFacts.BytesPerGiB, sampler.LatestReading!.Value.TotalPhysicalBytes);
    }

    [Fact]
    public void EgOneMemoryFactsMatchExpectedWindowsTierWorkingSets()
    {
        Assert.Equal(3_650_722_202L, EgOneMemoryFacts.PeakMemoryBytes(EgOneModelCatalog.FullId));
        Assert.Equal(1_717_986_918L, EgOneMemoryFacts.PeakMemoryBytes(EgOneModelCatalog.MiniId));
        Assert.Equal(1_073_741_824L, EgOneMemoryFacts.PeakMemoryBytes(EgOneModelCatalog.MicroId));
    }

    [Fact]
    public void EgOneMemoryBarFallbackKeepsHalfMemoryRuleAndNoKey()
    {
        var quarter = EgOneMemoryBarPresentation.Describe(1L, "EG-1 Mini", 4UL, null, EgOneServerActivity.Inactive, isLightest: false);
        Assert.Equal(0d, quarter.UsedFraction);
        Assert.Equal(0d, quarter.PeakStart);
        Assert.Equal(0.25d, quarter.PeakFraction, 6);
        Assert.Null(quarter.ProjectedPercent);
        Assert.Null(quarter.Key);
        Assert.Equal(EgOneMemoryWarning.None, quarter.Warning);
        Assert.Null(quarter.WarningText);

        var tight = EgOneMemoryBarPresentation.Describe(3L, "EG-1", 4UL, null, EgOneServerActivity.Inactive, isLightest: false);
        Assert.Equal(0.75d, tight.PeakFraction, 6);
        Assert.Equal(EgOneMemoryWarning.Tight, tight.Warning);
        Assert.Equal(EgOneMemoryBarPresentation.TightOnThisPcMessage, tight.WarningText);

        var capped = EgOneMemoryBarPresentation.Describe(10L, "EG-1", 4UL, null, EgOneServerActivity.Inactive, isLightest: false);
        Assert.Equal(1d, capped.PeakFraction, 6);
        Assert.Equal(EgOneMemoryWarning.Tight, capped.Warning);
    }

    [Fact]
    public void EgOneMemoryBarInactiveStacksAndAppliesExactNinetyPercentBoundary()
    {
        var stacked = EgOneMemoryBarPresentation.Describe(2L, "EG-1 Mini", 16UL, 8UL, EgOneServerActivity.Inactive, isLightest: false);
        Assert.Equal(0.5d, stacked.UsedFraction, 6);
        Assert.Equal(0.5d, stacked.PeakStart, 6);
        Assert.Equal(0.125d, stacked.PeakFraction, 6);

        var clampedTail = EgOneMemoryBarPresentation.Describe(4L, "EG-1", 16UL, 15UL, EgOneServerActivity.Inactive, isLightest: false);
        Assert.Equal(1d, clampedTail.PeakStart + clampedTail.PeakFraction, 6);

        var under = EgOneMemoryBarPresentation.Describe(10L, "EG-1", 100UL, 79UL, EgOneServerActivity.Inactive, isLightest: false);
        Assert.Equal(89, under.ProjectedPercent);
        Assert.Equal(EgOneMemoryWarning.None, under.Warning);

        var exactNinety = EgOneMemoryBarPresentation.Describe(10L, "EG-1", 100UL, 80UL, EgOneServerActivity.Inactive, isLightest: false);
        Assert.Equal(90, exactNinety.ProjectedPercent);
        Assert.Equal(EgOneMemoryWarning.None, exactNinety.Warning);

        var overNinety = EgOneMemoryBarPresentation.Describe(10L, "EG-1", 100UL, 81UL, EgOneServerActivity.Inactive, isLightest: false);
        Assert.Equal(91, overNinety.ProjectedPercent);
        Assert.Equal(EgOneMemoryWarning.WontFitNow, overNinety.Warning);
        Assert.Equal(EgOneMemoryBarPresentation.WontFitNowMessage, overNinety.WarningText);

        // Live data replaces the fallback >50% rule when room remains.
        var sixtyWithRoom = EgOneMemoryBarPresentation.Describe(60L, "EG-1", 100UL, 0UL, EgOneServerActivity.Inactive, isLightest: false);
        Assert.Equal(EgOneMemoryWarning.None, sixtyWithRoom.Warning);

        // Large values must never overflow in 90% arithmetic.
        var huge = EgOneMemoryBarPresentation.Describe((long)(ulong.MaxValue / 4UL), "EG-1", ulong.MaxValue, ulong.MaxValue - 10UL, EgOneServerActivity.Inactive, isLightest: false);
        Assert.Equal(EgOneMemoryWarning.WontFitNow, huge.Warning);
    }

    [Theory]
    [InlineData(EgOneServerActivity.Starting)]
    [InlineData(EgOneServerActivity.Ready)]
    public void EgOneMemoryBarStartingAndReadyOverlayInsideGrayWithoutWarning(EgOneServerActivity activity)
    {
        var bar = EgOneMemoryBarPresentation.Describe(4L, "EG-1 Mini", 16UL, 12UL, activity, isLightest: false);
        Assert.Equal(0.75d, bar.UsedFraction, 6);
        Assert.Equal(0.5d, bar.PeakStart, 6);
        Assert.Equal(0.25d, bar.PeakFraction, 6);
        Assert.Equal(bar.UsedFraction, bar.PeakStart + bar.PeakFraction, 6);
        Assert.Equal(75, bar.ProjectedPercent);
        Assert.Equal(EgOneMemoryWarning.None, bar.Warning);

        var peakAboveUsed = EgOneMemoryBarPresentation.Describe(8L, "EG-1 Full", 16UL, 4UL, activity, isLightest: false);
        Assert.Equal(0d, peakAboveUsed.PeakStart, 6);
        Assert.Equal(0.25d, peakAboveUsed.PeakFraction, 6);

        var extremeFallback = EgOneMemoryBarPresentation.Describe(6L, "EG-1", 8UL, null, activity, isLightest: false);
        Assert.Equal(EgOneMemoryWarning.None, extremeFallback.Warning);

        var ninetyFive = EgOneMemoryBarPresentation.Describe(10L, "EG-1", 100UL, 95UL, activity, isLightest: false);
        Assert.Equal(EgOneMemoryWarning.None, ninetyFive.Warning);
    }

    [Fact]
    public void EgOneMemoryBarCopyKeyLightestAccessibilityAndHeadingMatchSpec()
    {
        var gib = EgOneMemoryFacts.BytesPerGiB;
        var inactive = EgOneMemoryBarPresentation.Describe(EgOneModelCatalog.MiniId, "EG-1 Mini", 64UL * gib, 32UL * gib, EgOneServerActivity.Inactive, isLightest: false);
        Assert.Equal("1.6 of 64 GB", inactive.ValueText);
        Assert.Null(inactive.LightestMark);
        Assert.NotNull(inactive.Key);
        Assert.Equal("In use now", inactive.Key.InUseLabel);
        Assert.Equal("EG-1 Mini adds 1.6 GB", inactive.Key.SizeLabel);
        Assert.Equal("52% with this size", inactive.Key.PercentText);
        Assert.Equal("Needs about 1.6 GB. PC is using 32.0 GB of 64 GB now. 52% with this size.", inactive.AccessibilityValue);

        var ready = EgOneMemoryBarPresentation.Describe(EgOneModelCatalog.MiniId, "EG-1 Mini", 64UL * gib, 32UL * gib, EgOneServerActivity.Ready, isLightest: false);
        Assert.Equal(new EgOneMemoryKey("Other apps", "EG-1 Mini, running", "50% in use"), ready.Key);

        var starting = EgOneMemoryBarPresentation.Describe(EgOneModelCatalog.MicroId, "EG-1 Micro", 64UL * gib, 32UL * gib, EgOneServerActivity.Starting, isLightest: true);
        Assert.Equal("lightest", starting.LightestMark);
        Assert.Equal(new EgOneMemoryKey("Other apps", "EG-1 Micro, starting", "50% in use"), starting.Key);

        var warnedLightest = EgOneMemoryBarPresentation.Describe(EgOneModelCatalog.MicroId, "EG-1 Micro", 8UL * gib, 8UL * gib, EgOneServerActivity.Inactive, isLightest: true);
        Assert.Equal(EgOneMemoryWarning.WontFitNow, warnedLightest.Warning);
        Assert.Null(warnedLightest.LightestMark);

        var germanNeed = EgOneMemoryBarPresentation.Describe(EgOneModelCatalog.FullId, "EG-1", 64UL * gib, 32UL * gib, EgOneServerActivity.Inactive, isLightest: false, culture: new CultureInfo("de-DE"));
        Assert.StartsWith("3,4 ", germanNeed.ValueText, StringComparison.Ordinal);

        var liveUsed = (ulong)Math.Round(36.1 * gib);
        var heading = EgOneMemoryBarPresentation.FormatPcMemoryHeading(64d, GpuAccelerationKind.NvidiaCuda, 24d, 24, new SystemMemoryReading(64UL * gib, liveUsed));
        Assert.Equal("This PC: 64 GB RAM \u00B7 36.1 GB in use \u00B7 56% \u00B7 NVIDIA GPU (24 GB VRAM)", heading);
        Assert.DoesNotContain('\u2014', heading);
        Assert.DoesNotContain('\u2013', heading);

        var fallbackHeading = EgOneMemoryBarPresentation.FormatPcMemoryHeading(32d, GpuAccelerationKind.None, null, 8, null);
        Assert.Equal("This PC: 32 GB RAM \u00B7 8-core CPU", fallbackHeading);
    }

    [Fact]
    public void EngineAndEgOnePresentationsCarryDynamicRecommendationsAndMemoryState()
    {
        var whisperRecommended = EnginePolishPresentation.DescribeEngine(
            FinalAsrEngine.Whisper,
            FinalAsrEngine.Whisper,
            "Recommended for Japanese");
        var parakeetNonRecommended = EnginePolishPresentation.DescribeEngine(
            FinalAsrEngine.Parakeet,
            FinalAsrEngine.Whisper,
            "Recommended for Japanese");

        Assert.True(whisperRecommended.IsRecommended);
        Assert.Equal("Recommended for Japanese", whisperRecommended.BadgeText);
        Assert.False(parakeetNonRecommended.IsRecommended);
        Assert.Equal("25 languages", parakeetNonRecommended.BadgeText);

        var card = EnginePolishPresentation.DescribeEngineCard(
            FinalAsrEngine.Whisper,
            FinalAsrEngine.Whisper,
            new SpeechEngineModelState(true, true, false, SpeechModelDelivery.Installed),
            otherDownloadInFlight: false,
            FinalAsrEngine.Whisper,
            "Recommended for Japanese");
        Assert.True(card.IsRecommended);
        Assert.Equal("Recommended for Japanese", card.BadgeText);

        var gib = EgOneMemoryFacts.BytesPerGiB;
        var egOneCard = EnginePolishPresentation.DescribeEgOneCard(
            EgOneModelCatalog.MiniId,
            EgOneModelCatalog.MiniId,
            EgOneModelCatalog.MiniId,
            new EgOneModelState(EgOneModelCatalog.MiniId, true, true, true, SpeechModelDelivery.Installed),
            otherDownloadInFlight: false,
            serverExecutableExists: true,
            totalPhysicalMemoryBytes: 32UL * gib,
            systemUsedMemoryBytes: 16UL * gib,
            serverActivity: EgOneServerActivity.Ready);
        Assert.Contains("~1.6 GB memory", egOneCard.SpecsSummary, StringComparison.Ordinal);
        Assert.NotNull(egOneCard.Memory);
        Assert.Equal(EgOneServerActivity.Ready, egOneCard.Memory.Activity);
        Assert.Equal("Other apps", egOneCard.Memory.Key!.InUseLabel);
    }

    [Fact]
    public async Task EgOneServerManagerAndProviderExposeSafeActivityAcrossLifecycleAndDisposal()
    {
        var missingRuntime = new EgOneServerManager(new EgOneServerOptions(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing-server.exe"),
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing-model.gguf")));
        Assert.Equal(EgOneServerActivity.Inactive, missingRuntime.ServerActivity);
        Assert.Null(await missingRuntime.EnsureReadyAsync(CancellationToken.None));
        Assert.Equal(EgOneServerActivity.Inactive, missingRuntime.ServerActivity);
        await missingRuntime.DisposeAsync();
        Assert.Equal(EgOneServerActivity.Inactive, missingRuntime.ServerActivity);

        var runtime = new StubActivityRuntime { Activity = EgOneServerActivity.Starting };
        var provider = new EgOnePolishProvider(runtime, new EgOnePolishOptions(
            new EgOneServerOptions("C:\\fake\\server.exe", "C:\\fake\\model.gguf"),
            EgOneModelCatalog.MiniId));
        Assert.Equal(EgOneServerActivity.Starting, provider.ServerActivity);

        runtime.Activity = EgOneServerActivity.Ready;
        Assert.Equal(EgOneServerActivity.Ready, provider.ServerActivity);

        await provider.DisposeAsync();
        Assert.Equal(EgOneServerActivity.Inactive, provider.ServerActivity);
    }

    [Fact]
    public void SettingsMarkupAndCodeBehindWireLiveMemoryBudgetAndUnifiedRecommendations()
    {
        var root = FindRepositoryRoot();
        var document = AppXamlSources.LoadMainWindowAndPagesDocument(root);
        var activePolishRecommendedBadge = FindNamedElement(document, "ActivePolishProviderRecommendedBadge");
        Assert.Equal("{StaticResource BrandSolidAccentChipStyle}", (string?)activePolishRecommendedBadge.Attribute("Style"));

        foreach (var tier in new[] { "Full", "Mini", "Micro" })
        {
            Assert.NotNull(FindNamedElement(document, $"EgOne{tier}MemoryPanel"));
            Assert.NotNull(FindNamedElement(document, $"EgOne{tier}MemoryValueText"));
            Assert.NotNull(FindNamedElement(document, $"EgOne{tier}MemoryTrack"));
            Assert.NotNull(FindNamedElement(document, $"EgOne{tier}MemoryUsedBar"));
            Assert.NotNull(FindNamedElement(document, $"EgOne{tier}MemoryPeakBar"));
            Assert.NotNull(FindNamedElement(document, $"EgOne{tier}MemoryKeyGrid"));
            Assert.NotNull(FindNamedElement(document, $"EgOne{tier}MemoryWarningText"));
        }

        var aiPolishCode = File.ReadAllText(Path.Combine(root, "src", "Production", "EnviousWispr.App", "MainWindow.AiPolish.cs"));
        var speechCode = File.ReadAllText(Path.Combine(root, "src", "Production", "EnviousWispr.App", "MainWindow.SpeechEngine.cs"));
        var navigationCode = File.ReadAllText(Path.Combine(root, "src", "Production", "EnviousWispr.App", "MainWindow.Navigation.cs"));
        var pagesCode = File.ReadAllText(Path.Combine(root, "src", "Production", "EnviousWispr.App", "MainWindow.Pages.cs"));
        var windowCode = File.ReadAllText(Path.Combine(root, "src", "Production", "EnviousWispr.App", "MainWindow.xaml.cs"));
        var appPolishCode = File.ReadAllText(Path.Combine(root, "src", "Production", "EnviousWispr.App", "App.Polish.cs"));

        Assert.Contains("WindowsSetupRecommender.Recommend(BuildSetupHardwareSnapshot())", aiPolishCode, StringComparison.Ordinal);
        Assert.Contains("WindowsSetupRecommender.Recommend(BuildSetupHardwareSnapshot())", speechCode, StringComparison.Ordinal);
        Assert.Contains("StopEgOneMemoryTimer()", pagesCode, StringComparison.Ordinal);
        Assert.Contains("StopEgOneMemoryTimer()", windowCode, StringComparison.Ordinal);
        Assert.Contains("OverlappedPresenterState.Minimized", aiPolishCode, StringComparison.Ordinal);
        Assert.Contains("_wasEgOneMemorySamplingVisible", aiPolishCode, StringComparison.Ordinal);
        Assert.Contains("if (!IsEgOneMemorySamplingVisible())", aiPolishCode, StringComparison.Ordinal);
        Assert.Contains("AppWindow.Changed += OnMainAppWindowChanged", windowCode, StringComparison.Ordinal);
        Assert.Contains("UpdateEgOneMemoryTimer(", navigationCode, StringComparison.Ordinal);
        Assert.Contains("QueryEgOneServerActivities", appPolishCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemMemorySamplerDetectsNinetyPercentWarningCrossingWithinSameTenthGiB()
    {
        var gib = EgOneMemoryFacts.BytesPerGiB;
        var total = 16UL * gib;
        SystemMemoryReading current = new(total, (ulong)Math.Round(12.77 * gib));
        var sampler = new SystemMemorySampler(() => current, () => true);

        Assert.True(sampler.Tick());
        var before = EgOneMemoryBarPresentation.Describe(
            EgOneModelCatalog.MiniId,
            "EG-1 Mini",
            total,
            sampler.LatestReading!.Value.UsedBytes,
            EgOneServerActivity.Inactive,
            isLightest: false);
        Assert.Equal(EgOneMemoryWarning.None, before.Warning);

        // Both 12.77 GiB and 12.82 GiB round to 12.8 GB (128 tenths), yet 12.82 + 1.6 > 14.4 (90%).
        current = new(total, (ulong)Math.Round(12.82 * gib));
        Assert.True(sampler.Tick());
        var crossed = EgOneMemoryBarPresentation.Describe(
            EgOneModelCatalog.MiniId,
            "EG-1 Mini",
            total,
            sampler.LatestReading!.Value.UsedBytes,
            EgOneServerActivity.Inactive,
            isLightest: false);
        Assert.Equal(EgOneMemoryWarning.WontFitNow, crossed.Warning);

        current = new(total, (ulong)Math.Round(12.77 * gib));
        Assert.True(sampler.Tick());
        var cleared = EgOneMemoryBarPresentation.Describe(
            EgOneModelCatalog.MiniId,
            "EG-1 Mini",
            total,
            sampler.LatestReading!.Value.UsedBytes,
            EgOneServerActivity.Inactive,
            isLightest: false);
        Assert.Equal(EgOneMemoryWarning.None, cleared.Warning);
    }

    [Fact]
    public async Task EgOneServerManagerSleepIdleTransitionsReadyInactiveWakeAndDisposeSafely()
    {
        var timeProvider = new ManualTimeProvider();
        using var handler = new PreWakeOkHandler();
        var options = new EgOneServerOptions(
            "C:\\fake\\server.exe",
            "C:\\fake\\model.gguf",
            SleepIdleSeconds: 10);
        var manager = new EgOneServerManager(options, handler, timeProvider);
        var events = new List<EgOneServerActivity>();
        manager.ServerActivityChanged += (_, _) => events.Add(manager.ServerActivity);

        var endpoint = new EgOneEndpoint(18_090, "token", 8_192);
        manager.MarkEndpointReadyForTesting(endpoint);
        Assert.Equal(EgOneServerActivity.Ready, manager.ServerActivity);
        Assert.Equal([EgOneServerActivity.Ready], events);

        timeProvider.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(EgOneServerActivity.Ready, manager.ServerActivity);

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(EgOneServerActivity.Inactive, manager.ServerActivity);
        Assert.Equal([EgOneServerActivity.Ready, EgOneServerActivity.Inactive], events);

        manager.BeginModelRequest();
        Assert.Equal(EgOneServerActivity.Ready, manager.ServerActivity);
        Assert.Equal(EgOneServerActivity.Ready, events[^1]);

        // Stays Ready past SleepIdleSeconds while a request holds the active count > 0.
        timeProvider.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal(EgOneServerActivity.Ready, manager.ServerActivity);

        manager.EndModelRequest();
        Assert.Equal(EgOneServerActivity.Ready, manager.ServerActivity);

        timeProvider.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(EgOneServerActivity.Inactive, manager.ServerActivity);
        Assert.Equal(EgOneServerActivity.Inactive, events[^1]);

        Assert.True(await manager.TryPreWakeAsync(CancellationToken.None));
        Assert.Equal(EgOneServerActivity.Ready, manager.ServerActivity);
        Assert.Equal(EgOneServerActivity.Ready, events[^1]);

        timeProvider.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(EgOneServerActivity.Inactive, manager.ServerActivity);

        manager.MarkEndpointReadyForTesting(endpoint);
        Assert.Equal(EgOneServerActivity.Ready, manager.ServerActivity);

        await manager.DisposeAsync();
        Assert.Equal(EgOneServerActivity.Inactive, manager.ServerActivity);
        var countAtDispose = events.Count;

        timeProvider.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(countAtDispose, events.Count);
        Assert.Equal(EgOneServerActivity.Inactive, manager.ServerActivity);
    }

    private static XElement FindNamedElement(XDocument document, string name)
    {
        var element = document.Descendants()
            .FirstOrDefault(node => (string?)node.Attribute(XName.Get("Name", XNamespaceUri)) == name);
        Assert.True(element is not null, $"Expected XAML element '{name}'.");
        return element!;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "EnviousWispr.Windows.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private sealed class StubActivityRuntime : IEgOneRuntime
    {
        public int? OwnedProcessId => null;

        public int? SleepIdleSeconds => null;

        public EgOneServerActivity Activity { get; set; } = EgOneServerActivity.Inactive;

        public EgOneServerActivity ServerActivity => Activity;

        public event EventHandler? ServerActivityChanged
        {
            add { }
            remove { }
        }

        public Task<EgOneEndpoint?> EnsureReadyAsync(CancellationToken cancellationToken) =>
            Task.FromResult<EgOneEndpoint?>(null);

        public EgOneEndpoint? TryGetReadyEndpoint() => null;

        public Task<bool> TryPreWakeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public void TerminateImmediately()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PreWakeOkHandler : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly Lock _lock = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _utcNow = new(2026, 11, 27, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            lock (_lock)
            {
                return _utcNow;
            }
        }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            lock (_lock)
            {
                return _utcNow.Ticks;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, dueTime, period);
            lock (_lock)
            {
                _timers.Add(timer);
            }

            return timer;
        }

        public void Advance(TimeSpan delta)
        {
            List<(TimerCallback Callback, object? State)> due = [];
            lock (_lock)
            {
                _utcNow += delta;
                foreach (var timer in _timers)
                {
                    timer.CollectIfDue(_utcNow, due);
                }
            }

            foreach (var (callback, state) in due)
            {
                callback(state);
            }
        }

        private DateTimeOffset? ResolveNextDue(TimeSpan dueTime) =>
            dueTime == Timeout.InfiniteTimeSpan
                ? null
                : GetUtcNow() + (dueTime < TimeSpan.Zero ? TimeSpan.Zero : dueTime);

        private void RemoveTimer(ManualTimer timer)
        {
            lock (_lock)
            {
                _timers.Remove(timer);
            }
        }

        private sealed class ManualTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) : ITimer
        {
            private DateTimeOffset? _nextDue = owner.ResolveNextDue(dueTime);
            private TimeSpan _period = period;
            private bool _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._lock)
                {
                    if (_disposed)
                    {
                        return false;
                    }

                    _period = period;
                    _nextDue = dueTime == Timeout.InfiniteTimeSpan
                        ? null
                        : owner._utcNow + (dueTime < TimeSpan.Zero ? TimeSpan.Zero : dueTime);
                    return true;
                }
            }

            public void CollectIfDue(DateTimeOffset now, List<(TimerCallback Callback, object? State)> due)
            {
                if (_disposed || _nextDue is not { } target || target > now)
                {
                    return;
                }

                due.Add((callback, state));
                _nextDue = _period == Timeout.InfiniteTimeSpan || _period <= TimeSpan.Zero
                    ? null
                    : target + _period;
            }

            public void Dispose()
            {
                lock (owner._lock)
                {
                    _disposed = true;
                    _nextDue = null;
                }

                owner.RemoveTimer(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
