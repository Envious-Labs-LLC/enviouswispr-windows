using EnviousWispr.Core.Settings;
using EnviousWispr.PostProcessing;
using EnviousWispr.PostProcessing.Learning;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// Exercises the actual shipped <see cref="BuiltinWords.All"/> catalog through
/// <see cref="CustomWordCorrector"/> (mirroring macOS <c>BuiltinDictionaryCorrectionTests</c>).
/// </summary>
public sealed class BuiltinDictionaryCorrectionTests
{
    private static readonly CustomWordEntry[] Shipped =
        BuiltinWords.All.Select(item => item.Entry).ToArray();

    private static string Correct(string input, CustomWordEntry[]? words = null) =>
        CustomWordCorrector.Correct(
            input,
            words ?? Shipped,
            packWords: null,
            excludeLearnedAliases: true,
            reserveExcludedLearnedAliases: true).Text;

    [Fact]
    public void CatalogHasExpectedEighteenShippedEntriesWithBuiltinOrigin()
    {
        Assert.Equal(18, BuiltinWords.All.Count);
        Assert.All(BuiltinWords.All, item =>
        {
            Assert.Equal(CustomWordOrigin.Builtin, item.Entry.Origin);
            Assert.StartsWith("builtin:", item.Entry.Id, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Eg1IsShippedUnderStableId()
    {
        var entry = BuiltinWords.FindById("eg1");
        Assert.NotNull(entry);
        Assert.Equal("EG-1", entry.Entry.Replacement);
    }

    [Theory]
    [InlineData("I ran it through EG 1")]
    [InlineData("I ran it through E G 1")]
    [InlineData("I ran it through EG1")]
    [InlineData("I ran it through EG-one")]
    public void Eg1SpokenFormsCorrect(string input)
    {
        Assert.Equal("I ran it through EG-1", Correct(input));
    }

    [Theory]
    [InlineData("envious whisper")]
    [InlineData("envious wisper")]
    [InlineData("envious whispr")]
    [InlineData("envious visper")]
    [InlineData("envious cisper")]
    [InlineData("mbs cisper")]
    [InlineData("in vious wispr")]
    [InlineData("envy us wispr")]
    [InlineData("NVS Visper")]
    [InlineData("NBS Vesper")]
    [InlineData("envious wispr")]
    [InlineData("Enviousvisper")]
    [InlineData("NVSBesper")]
    [InlineData("NVSBSPur")]
    [InlineData("NVIS VICPRSO")]
    [InlineData("EnvyS Visper")]
    [InlineData("senvy wpr")]
    [InlineData("Dambius Bispe")]
    [InlineData("MBS Visper")]
    [InlineData("MVS Visper")]
    [InlineData("MBS Vesper")]
    [InlineData("MBS Whisper")]
    [InlineData("NVIS VISPR")]
    [InlineData("NVIS Whisper")]
    [InlineData("Envy S Whisper")]
    [InlineData("envy as whisper")]
    [InlineData("envy as whisker")]
    [InlineData("Envice whisper")]
    [InlineData("NvSvisker")]
    [InlineData("VS Visper")]
    [InlineData("Invisper")]
    [InlineData("NVSISPR")]
    [InlineData("Envy's Dispware")]
    [InlineData("Envy's Whisper")]
    public void EnviousWisprMishearingsCorrect(string heard)
    {
        Assert.Equal("I use EnviousWispr daily", Correct($"I use {heard} daily"));
    }

    [Theory]
    [InlineData("envious laps")]
    [InlineData("MVS Labs")]
    [InlineData("NVIS Labs")]
    [InlineData("NVIS LAPS")]
    [InlineData("NVS laps")]
    [InlineData("NVS Labs")]
    [InlineData("MBS Labs")]
    [InlineData("NBS Labs")]
    [InlineData("Envy's labs")]
    public void EnviousLabsMishearingsCorrect(string heard)
    {
        Assert.Equal("I work at Envious Labs today", Correct($"I work at {heard} today"));
    }

    [Theory]
    [InlineData("Envious Meet", "EnviousMeet")]
    [InlineData("envious meat", "EnviousMeet")]
    [InlineData("envious need", "EnviousMeet")]
    [InlineData("Envious Me", "EnviousMeet")]
    [InlineData("EnviousMe", "EnviousMeet")]
    [InlineData("clot code", "Claude Code")]
    [InlineData("clawed code", "Claude Code")]
    [InlineData("clawd code", "Claude Code")]
    [InlineData("claude coat", "Claude Code")]
    [InlineData("claude code", "Claude Code")]
    [InlineData("mac OS", "macOS")]
    [InlineData("Mack OS", "macOS")]
    [InlineData("I OS", "iOS")]
    [InlineData("eye OS", "iOS")]
    [InlineData("git hub", "GitHub")]
    [InlineData("get hub", "GitHub")]
    [InlineData("chat GPT", "ChatGPT")]
    [InlineData("chat G P T", "ChatGPT")]
    [InlineData("open AI", "OpenAI")]
    [InlineData("open A I", "OpenAI")]
    [InlineData("A P I", "API")]
    [InlineData("C L I", "CLI")]
    [InlineData("vs code", "VS Code")]
    [InlineData("vscode", "VS Code")]
    [InlineData("V S code", "VS Code")]
    [InlineData("airpods", "AirPods")]
    [InlineData("air pods", "AirPods")]
    [InlineData("wifi", "Wi-Fi")]
    [InlineData("wi fi", "Wi-Fi")]
    [InlineData("Wi Fi", "Wi-Fi")]
    [InlineData("gmail", "Gmail")]
    [InlineData("g mail", "Gmail")]
    [InlineData("gemini", "Gemini")]
    public void RemainingBuiltinsAndMeasuredAdditionsCorrect(string heard, string expected)
    {
        Assert.Equal($"Check {expected} now", Correct($"Check {heard} now"));
    }

    [Theory]
    [InlineData("for example one of them left")]
    [InlineData("she cracked an egg on the pan")]
    [InlineData("the meeting is at one")]
    [InlineData("do not beg one of them for it")]
    [InlineData("he broke a leg on the stairs")]
    [InlineData("the item is e g on the list")]
    [InlineData("his ego got in the way")]
    [InlineData("she hurt her leg badly")]
    [InlineData("the egg was already cracked")]
    [InlineData("Parakeet vs Whisper is close")]
    [InlineData("we compared it with whisper today")]
    [InlineData("Please whisper to me")]
    [InlineData("that super whisper app")]
    [InlineData("I visited AWS Labs")]
    [InlineData("Bell Labs was famous")]
    [InlineData("the MIT labs are open")]
    [InlineData("my labs are done")]
    [InlineData("the news labs team")]
    [InlineData("I was envious of their lab")]
    [InlineData("I am envious of his laps")]
    [InlineData("the NBA visit")]
    [InlineData("MBS degree holders")]
    [InlineData("the NVIDIA labs")]
    [InlineData("Miss Vesper came by")]
    [InlineData("he is envious as ever")]
    [InlineData("she was envious of me")]
    [InlineData("I was envious whispering")]
    [InlineData("she was envious whispering it")]
    [InlineData("the envious whisperer")]
    [InlineData("an envious whispers campaign")]
    [InlineData("The question is still open. AI agents can help")]
    [InlineData("It is still open, AI is not done")]
    [InlineData("She's a PI on the grant")]
    [InlineData("deploy cloud code")]
    [InlineData("my airport gate")]
    [InlineData("my wife called")]
    [InlineData("send an email")]
    [InlineData("genuine gem")]
    public void OrdinarySentencesAreLeftAlone(string sentence)
    {
        Assert.Equal(sentence, Correct(sentence));
    }

    [Fact]
    public void TypoOnAliasStillCorrectsToEnviousWispr()
    {
        Assert.Equal("I use EnviousWispr daily", Correct("I use envious whisperr daily"));
    }

    [Fact]
    public void ExcludedInflectedCandidateStillCompetesForMargin()
    {
        var user = new CustomWordEntry(["envious whisprs"], "WhisperDesk");
        const string sentence = "an envious whispers campaign";
        Assert.Equal(sentence, Correct(sentence, [.. Shipped, user]));
    }

    [Theory]
    [InlineData("The cat clawed the sofa")]
    [InlineData("I asked clawed to write it")]
    [InlineData("a clod of earth")]
    public void ClaudeEverydayWordAliasesAreNotHardSwapped(string sentence)
    {
        Assert.Equal(sentence, Correct(sentence));
    }

    [Fact]
    public void ClaudeShipsCheckOnlyProvenanceWithoutAutoLearnedBadge()
    {
        var claude = Assert.Single(Shipped, entry => entry.Replacement == "Claude");
        Assert.Equal(CustomWordOrigin.Builtin, claude.Origin);
        Assert.Equal(["clod", "clawed"], claude.LearnedAliases);
        Assert.True(claude.HasCheckerAliases);
        Assert.False(claude.IsAutoLearned);
        Assert.Equal("I asked Claude", Correct("I asked claude"));

        var extracted = LearnedWordCandidates.ExtractLearnedWords(Shipped);
        var claudeCandidate = Assert.Single(extracted);
        Assert.Equal("Claude", claudeCandidate.Canonical);
        Assert.Equal(["clod", "clawed"], claudeCandidate.ObservedMisspellings);
        Assert.Single(Shipped, entry => entry.HasCheckerAliases);
    }
}
