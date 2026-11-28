using EnviousWispr.Core.Dictation;
using EnviousWispr.PostProcessing;

namespace EnviousWispr.Architecture.Tests;

public sealed class SeamCasingOracleTests
{
    [Fact]
    public void SupportsAllTwelveApprovedCasingLanguages()
    {
        var expected = new[] { "en", "de", "fr", "it", "es", "pt", "nl", "da", "sv", "fi", "ru", "tr" };
        Assert.Equal(expected.OrderBy(x => x), SeamCasingPolicies.SupportedLanguages.OrderBy(x => x));

        var runtime = SeamCasingOracleRuntime.Shared;
        foreach (var lang in expected)
        {
            Assert.True(runtime.SupportsLanguage(lang));
            Assert.NotNull(runtime.PolicyFor(lang));
        }

        Assert.False(runtime.SupportsLanguage("pl"));
        Assert.Null(runtime.PolicyFor("pl"));
        Assert.True(runtime.PolicyFor("de")!.NounVeto);
        Assert.Contains("sie", runtime.PolicyFor("de")!.PoliteForms);
        Assert.False(runtime.PolicyFor("sv")!.NounVeto);
        Assert.Contains("ni", runtime.PolicyFor("sv")!.PoliteForms);
    }

    [Theory]
    [InlineData("en", "Can you ", "Ask them to send it?", true, CaseRepairDecision.LowercasedFirst)]
    [InlineData("en", "I need to speak with ", "Mark today.", false, CaseRepairDecision.SkippedOracleKeep)]
    [InlineData("en", "Can you ", "Mark the page for review.", true, CaseRepairDecision.LowercasedFirst)]
    [InlineData("en", "Tomorrow is ", "Monday morning.", false, CaseRepairDecision.SkippedAlwaysCapitalized)]
    [InlineData("en", "And ", "I think so.", false, CaseRepairDecision.SkippedPronounI)]
    [InlineData("en", "Use ", "GitHub today.", false, CaseRepairDecision.SkippedMixedCaseOrAcronym)]
    [InlineData("en", "Version ", "V2 works.", false, CaseRepairDecision.SkippedContainsDigit)]
    [InlineData("de", "Siehst du ", "Sie dort?", false, CaseRepairDecision.SkippedPoliteForm)]
    [InlineData("de", "Das ist das ", "Haus am See.", false, CaseRepairDecision.SkippedNotOrdinaryWord)]
    [InlineData("de", "Das ist das ", "Essen heute.", false, CaseRepairDecision.SkippedOracleKeep)]
    [InlineData("de", "Ich muss jetzt ", "Arbeiten gehen.", true, CaseRepairDecision.LowercasedFirst)]
    [InlineData("sv", "Har ", "Ni sett detta?", false, CaseRepairDecision.SkippedPoliteForm)]
    [InlineData("sv", "Vi ska ", "Gå hem nu.", true, CaseRepairDecision.LowercasedFirst)]
    [InlineData("nl", "Het ", "IJs op het meer.", true, CaseRepairDecision.LowercasedFirst)]
    [InlineData("nl", "Van de ", "USA.", false, CaseRepairDecision.SkippedMixedCaseOrAcronym)]
    [InlineData("tr", "Odadaki ", "Işık parlak.", true, CaseRepairDecision.LowercasedFirst)]
    [InlineData("tr", "Bu ", "İnsan iyi.", true, CaseRepairDecision.LowercasedFirst)]
    [InlineData("fr", "Nous devons ", "Aller vite.", true, CaseRepairDecision.LowercasedFirst)]
    [InlineData("it", "Dobbiamo ", "Andare via.", true, CaseRepairDecision.LowercasedFirst)]
    [InlineData("es", "Tenemos que ", "Ir hoy.", true, CaseRepairDecision.LowercasedFirst)]
    [InlineData("pt", "Precisamos ", "Fazer isso.", true, CaseRepairDecision.LowercasedFirst)]
    [InlineData("da", "Han bor ", "I København.", true, CaseRepairDecision.LowercasedFirst)]
    [InlineData("fi", "Haluan ", "Mennä kotiin.", true, CaseRepairDecision.LowercasedFirst)]
    [InlineData("ru", "Мне нужно ", "Сделать это.", true, CaseRepairDecision.LowercasedFirst)]
    public void TwelveLanguageRuntimeMatchesGuardsAndContextVeto(
        string language,
        string left,
        string payload,
        bool expectedLower,
        CaseRepairDecision expectedDecision)
    {
        var bare = SeamCasingPolicies.ExtractBareFirstWord(payload);
        var guard = SeamCasingPolicies.CheckDeterministicGuard(payload, bare, language, protectedWords: null);
        if (guard is not null)
        {
            Assert.False(expectedLower);
            Assert.Equal(expectedDecision, guard.Value);
            return;
        }

        Assert.True(SeamCasingOracleRuntime.Shared.WarmLanguage(language));
        var verdict = SeamCasingOracleRuntime.Shared.Evaluate(bare, left, payload, language);
        Assert.Equal(expectedLower, verdict.Kind == SeamCasingVerdictKind.MayLower);
        Assert.Equal(expectedDecision, verdict.Decision);
    }

    [Fact]
    public void TurkishAndDutchLoweringRulesRespectLocaleAndDigraph()
    {
        Assert.Equal("ışık", SeamCasingPolicies.LoweringLeadingUnit("Işık", "tr"));
        Assert.Equal("insan", SeamCasingPolicies.LoweringLeadingUnit("İnsan", "tr-TR"));
        Assert.Equal("ijs", SeamCasingPolicies.LoweringLeadingUnit("IJs", "nl"));
        Assert.Equal("ijzeren", SeamCasingPolicies.LoweringLeadingUnit("IJzeren", "nl-NL"));
        Assert.False(SeamCasingPolicies.IsDutchIJDigraph("IJs", "en"));
        Assert.True(SeamCasingPolicies.IsDutchIJDigraph("IJs", "nl"));
        Assert.False(SeamCasingPolicies.IsDutchIJDigraph("IJV", "nl"));
    }

    [Fact]
    public void ProtectedWordsHonorExactCaseInsensitiveAndWordBoundaryMatches()
    {
        var protectedSet = new HashSet<string>(["apple", "New York", "Store"], StringComparer.OrdinalIgnoreCase);

        Assert.Equal(
            CaseRepairDecision.SkippedProtectedWord,
            SeamCasingPolicies.CheckDeterministicGuard("Apple pie", "Apple", "en", protectedSet));
        Assert.Equal(
            CaseRepairDecision.SkippedProtectedWord,
            SeamCasingPolicies.CheckDeterministicGuard("New York today", "New", "en", protectedSet));
        Assert.Null(
            SeamCasingPolicies.CheckDeterministicGuard("Storefront sign", "Storefront", "en", protectedSet));
    }

    [Theory]
    [InlineData(CaseRepairDecision.SkippedOracleWarming)]
    [InlineData(CaseRepairDecision.SkippedOracleUnavailable)]
    [InlineData(CaseRepairDecision.SkippedOracleTimedOut)]
    public void UnavailableOracleFailsClosedWithoutLowering(CaseRepairDecision reason)
    {
        var oracle = SeamCasingOracle.Unavailable(reason);
        var verdict = oracle.Evaluate("Ask", "Can you ", "Ask now.", "en");

        Assert.Equal(SeamCasingVerdictKind.Unavailable, verdict.Kind);
        Assert.Equal(reason, verdict.Decision);
    }

    [Fact]
    public void NonBlockingRuntimeReturnsWarmingAndQueuesBackgroundLoadWithoutStalling()
    {
        using var loadedSignal = new ManualResetEventSlim(false);
        var gate = new ManualResetEventSlim(false);
        var runtime = new SeamCasingOracleRuntime(
            _ =>
            {
                gate.Wait(TimeSpan.FromSeconds(5));
                return null;
            },
            static () => ArmBContextVeto.Shared);

        var verdict = runtime.NonBlockingOracle.Evaluate("Ask", "Can you ", "Ask now.", "en");
        gate.Set();

        Assert.Equal(SeamCasingVerdictKind.Unavailable, verdict.Kind);
        Assert.Equal(CaseRepairDecision.SkippedOracleWarming, verdict.Decision);
    }

    [Fact]
    public async Task WaitForLanguageReadyAsyncHonorsBudgetWhileDictionaryLoadIsInFlight()
    {
        using var loadStarted = new ManualResetEventSlim(false);
        using var allowFinish = new ManualResetEventSlim(false);
        var runtime = new SeamCasingOracleRuntime(
            _ =>
            {
                loadStarted.Set();
                allowFinish.Wait(TimeSpan.FromSeconds(5));
                return null;
            },
            static () => ArmBContextVeto.Shared);

        runtime.BeginBackgroundWarmup("pt");
        Assert.True(loadStarted.Wait(TimeSpan.FromSeconds(2)));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ready = await runtime.WaitForLanguageReadyAsync("pt", TimeSpan.FromMilliseconds(40));
        sw.Stop();
        allowFinish.Set();

        Assert.False(ready);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"Expected timeout to return promptly, took {sw.ElapsedMilliseconds} ms.");
    }
}
