using System.Globalization;
using EnviousWispr.Core.Onboarding;
using EnviousWispr.Core.Polish;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Architecture.Tests;

public sealed class WindowsSetupRecommenderTests
{
    [Fact]
    public void EmbeddedSetupRulesLoadStrictlyAndMatchBuiltInFallback()
    {
        var embedded = WindowsSetupRules.LoadEmbeddedStrict();
        var fallback = WindowsSetupRules.CreateBuiltInFallback();

        Assert.Equal(WindowsSetupRules.ExpectedSchemaVersion, embedded.SchemaVersion);
        Assert.Equal(fallback.Version, embedded.Version);
        Assert.Equal(fallback.Revision, embedded.Revision);
        Assert.Equal(fallback.ParallelDownloadsMinRamGb, embedded.ParallelDownloadsMinRamGb);
        Assert.Equal(fallback.SpeechCatalog, embedded.SpeechCatalog);
        Assert.Equal(ParakeetModelIds.Final, embedded.SpeechRules[0].PrimaryModelId);
        Assert.Equal(667_974_615L, embedded.SpeechCatalog[ParakeetModelIds.Final].SizeBytes);
        Assert.Equal("638 MB", embedded.SpeechCatalog[ParakeetModelIds.Final].SizeLabel);
        Assert.Equal(fallback.PolishCatalog, embedded.PolishCatalog);
        Assert.Equal(fallback.SpeechRules, embedded.SpeechRules);
        Assert.Equal(fallback.PolishRules.Count, embedded.PolishRules.Count);
        for (var i = 0; i < fallback.PolishRules.Count; i++)
        {
            Assert.Equal(fallback.PolishRules[i].Id, embedded.PolishRules[i].Id);
            Assert.Equal(fallback.PolishRules[i].Tier, embedded.PolishRules[i].Tier);
            Assert.Equal(fallback.PolishRules[i].PrimaryModelId, embedded.PolishRules[i].PrimaryModelId);
            Assert.Equal(fallback.PolishRules[i].SecondaryModelId, embedded.PolishRules[i].SecondaryModelId);
            Assert.Equal(fallback.PolishRules[i].ParallelDownloads, embedded.PolishRules[i].ParallelDownloads);
            Assert.Equal(fallback.PolishRules[i].LowSpecPolishAdvisory, embedded.PolishRules[i].LowSpecPolishAdvisory);
        }
    }

    [Fact]
    public void SetupLanguageMetadataCoversAll101PickerEntriesWithoutDashes()
    {
        Assert.Equal(101, LanguageCatalog.PickerEntries.Count);

        foreach (var entry in LanguageCatalog.PickerEntries)
        {
            Assert.True(
                SetupLanguageMetadata.HasGreetingFor(entry.Code),
                $"Missing native greeting for picker entry '{entry.Code}' ({entry.EnglishName}).");
            var greeting = SetupLanguageMetadata.GetGreeting(entry.Code);
            Assert.False(string.IsNullOrWhiteSpace(greeting));
            Assert.DoesNotContain('\u2014', greeting);
            Assert.DoesNotContain('\u2013', greeting);
        }

        foreach (var worldGreeting in SetupLanguageMetadata.WorldGreetings)
        {
            Assert.DoesNotContain('\u2014', worldGreeting);
            Assert.DoesNotContain('\u2013', worldGreeting);
        }
    }

    [Theory]
    [InlineData("en-US", "en")]
    [InlineData("en-GB", "en-gb")]
    [InlineData("fr-FR", "fr")]
    [InlineData("ja-JP", "ja")]
    [InlineData("nb-NO", "no")]
    [InlineData("zh-HK", "yue")]
    [InlineData("zh-Hans-CN", "zh")]
    [InlineData("fil-PH", "tl")]
    public void SystemLanguageResolutionMapsCulturesCleanly(string cultureName, string expectedCode)
    {
        var resolved = SetupLanguageMetadata.ResolveSystemLanguageCode(CultureInfo.GetCultureInfo(cultureName));
        Assert.Equal(expectedCode, resolved);
    }

    [Fact]
    public void FormatDownloadLabelRendersGigabytesAt1000MbAndKeepsMegabytesBelow()
    {
        Assert.Equal("638 MB", WindowsSetupRules.FormatDownloadLabel(667_974_615L));
        Assert.Equal("548 MB", WindowsSetupRules.FormatDownloadLabel(574_041_195L));
        Assert.Equal("517 MB", WindowsSetupRules.FormatDownloadLabel(541_898_336L));
        Assert.Equal("1.4 GB", WindowsSetupRules.FormatDownloadLabel(1_454_782_528L));
        Assert.Equal("3.0 GB", WindowsSetupRules.FormatDownloadLabel(3_161_421_632L));
        Assert.Equal("3.1 GB", WindowsSetupRules.FormatDownloadLabel(3_328_599_654L));

        Assert.Equal("3.0 GB", WindowsSetupRules.FormatDownloadMegabytes(3015));
        Assert.Equal("1.4 GB", WindowsSetupRules.FormatDownloadMegabytes(1388));
        Assert.Equal("1.0 GB", WindowsSetupRules.FormatDownloadMegabytes(1000));
        Assert.Equal("638 MB", WindowsSetupRules.FormatDownloadMegabytes(638));
        Assert.Equal("542 MB", WindowsSetupRules.FormatDownloadMegabytes(542));

        Assert.Equal("3.0 GB", WindowsSetupRules.FormatDownloadLabel("3015 MB"));
        Assert.Equal("1.4 GB", WindowsSetupRules.FormatDownloadLabel("1388 MB"));
        Assert.Equal("3.1 GB", WindowsSetupRules.FormatDownloadLabel("3.12 GB"));
        Assert.Equal("1.4 GB", WindowsSetupRules.FormatDownloadLabel("1.45 GB"));
        Assert.Equal("542 MB", WindowsSetupRules.FormatDownloadLabel("542 MB"));
    }

    [Fact]
    public void SpeechRecommendationsCoverAll101PickerLanguagesPlusAutoAndExcludeGreekFromParakeetAdvice()
    {
        var nvidiaSnapshot = CreateSnapshot(ramGb: 32, gpu: GpuAccelerationKind.NvidiaCuda, vramGb: 12, cores: 12, cudaInstalled: true);
        var cpuSnapshot = CreateSnapshot(ramGb: 16, gpu: GpuAccelerationKind.None, vramGb: null, cores: 8, cudaInstalled: false);

        Assert.Contains("el", WindowsSetupRecommender.ParakeetAdviceExclusions);
        Assert.Contains("el", LanguageCatalog.ParakeetSupportedCodes);

        foreach (var entry in LanguageCatalog.PickerEntries)
        {
            var rec = WindowsSetupRecommender.Recommend(nvidiaSnapshot with { SelectedLanguageCode = entry.Code });
            var isAdviceExcluded = WindowsSetupRecommender.ParakeetAdviceExclusions.Contains(entry.LockCode);
            var isParakeetSupported = LanguageCatalog.ParakeetSupportedCodes.Contains(entry.LockCode);
            var expectParakeet = isParakeetSupported && !isAdviceExcluded;

            if (expectParakeet)
            {
                Assert.Equal(ParakeetModelIds.Final, rec.Speech.PrimaryModelId);
                Assert.Equal(FinalAsrEngine.Parakeet, rec.Speech.PrimaryEngine);
                Assert.Equal("638 MB", rec.Speech.PrimarySizeLabel);
                Assert.True(rec.Speech.IsParakeetAvailable);
                Assert.Null(rec.Speech.ParakeetUnavailableReason);
                Assert.Equal("< 0.5 sec", rec.Speech.HeroTimeText);
                Assert.Equal("Runs on your NVIDIA GPU", rec.Speech.HardwareBadgeText);
            }
            else
            {
                Assert.Equal(WhisperModelIds.Final, rec.Speech.PrimaryModelId);
                Assert.Equal(FinalAsrEngine.Whisper, rec.Speech.PrimaryEngine);
                Assert.Equal("548 MB", rec.Speech.PrimarySizeLabel);
                Assert.Equal(isParakeetSupported, rec.Speech.IsParakeetAvailable);
                Assert.Equal(
                    isAdviceExcluded
                        ? $"Whisper is more accurate for {entry.EnglishName}"
                        : $"Doesn't support {entry.EnglishName}",
                    rec.Speech.ParakeetUnavailableReason);
                Assert.Equal("~0.8 sec", rec.Speech.HeroTimeText);
                Assert.Equal("Runs on your NVIDIA GPU", rec.Speech.HardwareBadgeText);
            }

            Assert.Equal("For 30 seconds of speech on this PC.", rec.Speech.HeroSubtext);
            Assert.Contains("NVIDIA GPU", rec.Speech.WhisperSpeedNote, StringComparison.Ordinal);
            Assert.DoesNotContain('\u2014', rec.Speech.WhisperSpeedNote);
            Assert.DoesNotContain('\u2013', rec.Speech.WhisperSpeedNote);
        }

        // CPU speed estimates and badges reflect local CPU execution cleanly.
        var cpuEnglish = WindowsSetupRecommender.Recommend(cpuSnapshot with { SelectedLanguageCode = "en" });
        Assert.Equal("~1.2 sec", cpuEnglish.Speech.HeroTimeText);
        Assert.Equal("~1.2 sec", cpuEnglish.Speech.ParakeetHeroTimeText);
        Assert.Equal("~3 sec", cpuEnglish.Speech.WhisperHeroTimeText);
        Assert.Equal("Runs locally on your CPU", cpuEnglish.Speech.HardwareBadgeText);

        var cpuGreek = WindowsSetupRecommender.Recommend(cpuSnapshot with { SelectedLanguageCode = "el" });
        Assert.Equal(FinalAsrEngine.Whisper, cpuGreek.Speech.PrimaryEngine);
        Assert.Equal("~3 sec", cpuGreek.Speech.HeroTimeText);
        Assert.Equal("Runs locally on your CPU", cpuGreek.Speech.HardwareBadgeText);

        // Auto mode follows OS UI language when Whisper-only or Greek, otherwise recommends Parakeet.
        var autoParakeet = WindowsSetupRecommender.Recommend(cpuSnapshot with { SelectedLanguageCode = "auto", SystemLanguageCode = "de-DE" });
        Assert.Equal(ParakeetModelIds.Final, autoParakeet.Speech.PrimaryModelId);
        Assert.Contains("CPU", autoParakeet.Speech.WhisperSpeedNote, StringComparison.Ordinal);

        var autoUnrecognized = WindowsSetupRecommender.Recommend(cpuSnapshot with { SelectedLanguageCode = "auto", SystemLanguageCode = "zz-ZZ" });
        Assert.Equal(ParakeetModelIds.Final, autoUnrecognized.Speech.PrimaryModelId);
        Assert.True(autoUnrecognized.Speech.IsParakeetAvailable);

        var autoJapanese = WindowsSetupRecommender.Recommend(cpuSnapshot with { SelectedLanguageCode = "auto", SystemLanguageCode = "ja-JP" });
        Assert.Equal(WhisperModelIds.Final, autoJapanese.Speech.PrimaryModelId);
        Assert.False(autoJapanese.Speech.IsParakeetAvailable);
        Assert.Equal("Doesn't support Japanese", autoJapanese.Speech.ParakeetUnavailableReason);

        var autoGreek = WindowsSetupRecommender.Recommend(cpuSnapshot with { SelectedLanguageCode = "auto", SystemLanguageCode = "el-GR" });
        Assert.Equal(WhisperModelIds.Final, autoGreek.Speech.PrimaryModelId);
        Assert.True(autoGreek.Speech.IsParakeetAvailable);
        Assert.Equal("Whisper is more accurate for Greek", autoGreek.Speech.ParakeetUnavailableReason);

        // NVIDIA GPU without CUDA runtime pack installed falls back to CPU execution estimates and badge.
        var nvidiaWithoutCuda = CreateSnapshot(ramGb: 32, gpu: GpuAccelerationKind.NvidiaCuda, vramGb: 12, cores: 12, cudaInstalled: false);
        var noCudaParakeet = WindowsSetupRecommender.Recommend(nvidiaWithoutCuda with { SelectedLanguageCode = "en" });
        Assert.Equal("~1.2 sec", noCudaParakeet.Speech.HeroTimeText);
        Assert.Equal("~1.2 sec", noCudaParakeet.Speech.ParakeetHeroTimeText);
        Assert.Equal("~3 sec", noCudaParakeet.Speech.WhisperHeroTimeText);
        Assert.Equal("Runs locally on your CPU", noCudaParakeet.Speech.HardwareBadgeText);
        Assert.Equal("Runs locally on your CPU", noCudaParakeet.Speech.ParakeetHardwareBadgeText);
        Assert.Equal("Runs locally on your CPU", noCudaParakeet.Speech.WhisperHardwareBadgeText);

        var withCudaParakeet = WindowsSetupRecommender.Recommend(nvidiaSnapshot with { SelectedLanguageCode = "en" });
        Assert.Equal("< 0.5 sec", withCudaParakeet.Speech.HeroTimeText);
        Assert.Equal("~0.8 sec", withCudaParakeet.Speech.WhisperHeroTimeText);
        Assert.Equal("Runs on your NVIDIA GPU", withCudaParakeet.Speech.HardwareBadgeText);
    }

    [Fact]
    public void PolishMatrixRecommendsExpectedTiersAcrossAllSixPcProfiles()
    {
        // Tier 1: Discrete/NVIDIA >= 6 GB VRAM & >= 24 GB RAM -> eg-1 primary, eg-1-mini secondary, parallel=true.
        var tier1 = WindowsSetupRecommender.Recommend(
            CreateSnapshot(ramGb: 24, gpu: GpuAccelerationKind.NvidiaCuda, vramGb: 6, cores: 8));
        Assert.Equal(1, tier1.Polish.Tier);
        Assert.Equal(EgOneModelCatalog.FullId, tier1.Polish.PrimaryModelId);
        Assert.Equal("3.1 GB", tier1.Polish.PrimarySizeLabel);
        Assert.Equal("Recommended for your PC", tier1.Polish.PrimaryTag);
        Assert.Equal(EgOneModelCatalog.MiniId, tier1.Polish.SecondaryModelId);
        Assert.Equal("1.4 GB", tier1.Polish.SecondarySizeLabel);
        Assert.Equal("Smaller download", tier1.Polish.SecondaryTag);
        Assert.True(tier1.Polish.ParallelDownloads);
        Assert.False(tier1.Polish.LowSpecPolishAdvisory);

        // Tier 2a: Discrete 4 GB VRAM & 16 GB RAM -> eg-1-mini primary, eg-1-micro secondary, parallel=true.
        var tier2a = WindowsSetupRecommender.Recommend(
            CreateSnapshot(ramGb: 16, gpu: GpuAccelerationKind.DiscreteVulkan, vramGb: 4, cores: 6));
        Assert.Equal(2, tier2a.Polish.Tier);
        Assert.Equal(EgOneModelCatalog.MiniId, tier2a.Polish.PrimaryModelId);
        Assert.Equal(EgOneModelCatalog.MicroId, tier2a.Polish.SecondaryModelId);
        Assert.Equal("Smaller download", tier2a.Polish.SecondaryTag);
        Assert.True(tier2a.Polish.ParallelDownloads);

        // Tier 2b: 32 GB CPU >= 8 cores -> eg-1-mini primary, eg-1 secondary ("Even better results"), parallel=true.
        var tier2b = WindowsSetupRecommender.Recommend(
            CreateSnapshot(ramGb: 32, gpu: GpuAccelerationKind.IntegratedVulkan, vramGb: null, cores: 8));
        Assert.Equal(2, tier2b.Polish.Tier);
        Assert.Equal(EgOneModelCatalog.MiniId, tier2b.Polish.PrimaryModelId);
        Assert.Equal(EgOneModelCatalog.FullId, tier2b.Polish.SecondaryModelId);
        Assert.Equal("Even better results", tier2b.Polish.SecondaryTag);
        Assert.True(tier2b.Polish.ParallelDownloads);

        // Tier 3: 16 GB RAM & 4 cores CPU -> eg-1-mini primary, eg-1-micro secondary, parallel=true.
        var tier3 = WindowsSetupRecommender.Recommend(
            CreateSnapshot(ramGb: 16, gpu: GpuAccelerationKind.IntegratedVulkan, vramGb: null, cores: 4));
        Assert.Equal(3, tier3.Polish.Tier);
        Assert.Equal(EgOneModelCatalog.MiniId, tier3.Polish.PrimaryModelId);
        Assert.Equal(EgOneModelCatalog.MicroId, tier3.Polish.SecondaryModelId);
        Assert.True(tier3.Polish.ParallelDownloads);

        // Tier 4: 12-15.9 GB RAM -> locked non-English uses eg-1-mini primary; English or auto uses eg-1-micro primary; parallel=false.
        var tier4French = WindowsSetupRecommender.Recommend(
            CreateSnapshot(ramGb: 12, gpu: GpuAccelerationKind.None, vramGb: null, cores: 4, selectedLang: "fr"));
        Assert.Equal(4, tier4French.Polish.Tier);
        Assert.Equal(EgOneModelCatalog.MiniId, tier4French.Polish.PrimaryModelId);
        Assert.Equal(EgOneModelCatalog.MicroId, tier4French.Polish.SecondaryModelId);
        Assert.False(tier4French.Polish.ParallelDownloads);

        var tier4Auto = WindowsSetupRecommender.Recommend(
            CreateSnapshot(ramGb: 12, gpu: GpuAccelerationKind.None, vramGb: null, cores: 4, selectedLang: "auto"));
        Assert.Equal(4, tier4Auto.Polish.Tier);
        Assert.Equal(EgOneModelCatalog.MicroId, tier4Auto.Polish.PrimaryModelId);
        Assert.Equal(EgOneModelCatalog.MiniId, tier4Auto.Polish.SecondaryModelId);
        Assert.False(tier4Auto.Polish.ParallelDownloads);

        // Tier 5: 8-11.9 GB RAM -> eg-1-micro primary; >=10 GB & >=4 cores locked non-English promotes eg-1-mini secondary.
        var tier5English = WindowsSetupRecommender.Recommend(
            CreateSnapshot(ramGb: 8, gpu: GpuAccelerationKind.None, vramGb: null, cores: 4, selectedLang: "en"));
        Assert.Equal(5, tier5English.Polish.Tier);
        Assert.Equal(EgOneModelCatalog.MicroId, tier5English.Polish.PrimaryModelId);
        Assert.Equal(EgOneModelCatalog.MiniId, tier5English.Polish.SecondaryModelId);
        Assert.False(tier5English.Polish.SecondaryPromoted);
        Assert.False(tier5English.Polish.ParallelDownloads);

        var tier5NonEnglish10Gb = WindowsSetupRecommender.Recommend(
            CreateSnapshot(ramGb: 10, gpu: GpuAccelerationKind.None, vramGb: null, cores: 4, selectedLang: "es"));
        Assert.Equal(5, tier5NonEnglish10Gb.Polish.Tier);
        Assert.Equal(EgOneModelCatalog.MicroId, tier5NonEnglish10Gb.Polish.PrimaryModelId);
        Assert.Equal(EgOneModelCatalog.MiniId, tier5NonEnglish10Gb.Polish.SecondaryModelId);
        Assert.True(tier5NonEnglish10Gb.Polish.SecondaryPromoted);
        Assert.False(tier5NonEnglish10Gb.Polish.ParallelDownloads);

        // Tier 6: < 8 GB RAM or <= 2 physical cores -> LowSpecPolishAdvisory=true, eg-1-micro "Lightest local option".
        var tier6LowRam = WindowsSetupRecommender.Recommend(
            CreateSnapshot(ramGb: 6, gpu: GpuAccelerationKind.None, vramGb: null, cores: 4));
        Assert.Equal(6, tier6LowRam.Polish.Tier);
        Assert.True(tier6LowRam.Polish.LowSpecPolishAdvisory);
        Assert.Equal(EgOneModelCatalog.MicroId, tier6LowRam.Polish.PrimaryModelId);
        Assert.Equal("Lightest local option", tier6LowRam.Polish.PrimaryTag);
        Assert.False(tier6LowRam.Polish.ParallelDownloads);

        var tier6DualCore = WindowsSetupRecommender.Recommend(
            CreateSnapshot(ramGb: 16, gpu: GpuAccelerationKind.None, vramGb: null, cores: 2));
        Assert.Equal(6, tier6DualCore.Polish.Tier);
        Assert.True(tier6DualCore.Polish.LowSpecPolishAdvisory);
    }

    private static SetupHardwareSnapshot CreateSnapshot(
        double ramGb,
        GpuAccelerationKind gpu,
        double? vramGb,
        int cores,
        string selectedLang = "en",
        string systemLang = "en-US",
        bool cudaInstalled = false) => new(
            TotalRamGb: ramGb,
            GpuKind: gpu,
            DedicatedVramGb: vramGb,
            PhysicalCoreCount: cores,
            PerformanceCoreCount: cores,
            Architecture: ProcessorArchitectureKind.X64,
            AvailableDiskGb: 200d,
            SelectedLanguageCode: selectedLang,
            SystemLanguageCode: systemLang,
            CudaRuntimeInstalled: cudaInstalled);
}
