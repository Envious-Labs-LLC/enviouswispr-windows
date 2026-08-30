using EnviousWispr.Audio;
using EnviousWispr.Core.Audio;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// Two very different problems wore the same sentence, and only one was the person's to fix.
/// </summary>
/// <remarks>
/// A MICROPHONE NOBODY PLUGGED IN AND A WINDOWS SWITCH THAT IS OFF LOOK IDENTICAL FROM INSIDE THE
/// APP: both leave the capture device list empty. The old card named both causes every time, which
/// is the same as naming neither.
/// </remarks>
public sealed class MicrophoneReadinessTests
{
    [Fact]
    public void AWorkingMicrophoneIsNamedAndOffersNothingToFix()
    {
        var readiness = MicrophoneReadinessReport.For(MicrophoneConsent.Allowed, "Blue Yeti");

        Assert.Equal("Blue Yeti is available.", readiness.Sentence);
        Assert.True(readiness.IsReady);
        Assert.False(readiness.OffersPrivacySettings);
    }

    [Fact]
    public void AWindowsSwitchThatIsOffSaysSoAndOffersThePageThatFixesIt()
    {
        var readiness = MicrophoneReadinessReport.For(MicrophoneConsent.Blocked, defaultDeviceName: null);

        Assert.Contains("blocking", readiness.Sentence, StringComparison.OrdinalIgnoreCase);
        Assert.True(readiness.OffersPrivacySettings);
        Assert.False(readiness.IsReady);
    }

    [Fact]
    public void ABlockedSwitchIsNamedEvenWhenAMicrophoneIsListed()
    {
        // The switch is read first on purpose. A device that is present but unreachable is still
        // unreachable, and calling it available would be the worst sentence on this card.
        var readiness = MicrophoneReadinessReport.For(MicrophoneConsent.Blocked, "Blue Yeti");

        Assert.False(readiness.IsReady);
        Assert.True(readiness.OffersPrivacySettings);
    }

    [Fact]
    public void NoMicrophoneWithTheSwitchOnDoesNotBlamePrivacy()
    {
        // Sending somebody to a privacy page that will tell them everything is fine wastes the one
        // action the card is offering them.
        var readiness = MicrophoneReadinessReport.For(MicrophoneConsent.Allowed, defaultDeviceName: null);

        Assert.DoesNotContain("privacy", readiness.Sentence, StringComparison.OrdinalIgnoreCase);
        Assert.False(readiness.OffersPrivacySettings);
        Assert.False(readiness.IsReady);
    }

    [Fact]
    public void NoMicrophoneAndNoAnswerFromWindowsNamesBothPossibilities()
    {
        var readiness = MicrophoneReadinessReport.For(MicrophoneConsent.Unknown, defaultDeviceName: null);

        Assert.Contains("plugged in", readiness.Sentence, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("allows microphone access", readiness.Sentence, StringComparison.OrdinalIgnoreCase);
        Assert.True(readiness.OffersPrivacySettings);
    }

    [Fact]
    public void WindowsRefusingToListDevicesIsItsOwnSentenceAndOffersNoFix()
    {
        // With the switch on, a refusal to list is a fault rather than a permission, and there is
        // nothing on a privacy page that would help.
        var readiness = MicrophoneReadinessReport.For(
            MicrophoneConsent.Allowed,
            "Blue Yeti",
            enumerationFailed: true);

        Assert.Contains("could not list", readiness.Sentence, StringComparison.OrdinalIgnoreCase);
        Assert.False(readiness.OffersPrivacySettings);
        Assert.False(readiness.IsReady);
    }

    [Theory]
    [InlineData("Allow", MicrophoneConsent.Allowed)]
    [InlineData("allow", MicrophoneConsent.Allowed)]
    [InlineData(" ALLOW ", MicrophoneConsent.Allowed)]
    [InlineData("Deny", MicrophoneConsent.Blocked)]
    [InlineData("deny", MicrophoneConsent.Blocked)]
    [InlineData("", MicrophoneConsent.Unknown)]
    [InlineData("Prompt", MicrophoneConsent.Unknown)]
    [InlineData(null, MicrophoneConsent.Unknown)]
    public void OnlyTheTwoWordsWindowsWritesAreAnAnswer(string? stored, MicrophoneConsent expected)
    {
        // Anything unrecognised has to be Unknown rather than Allowed. Telling somebody their
        // microphone is fine while it is switched off is the failure that costs them a dictation.
        Assert.Equal(expected, WindowsMicrophoneConsent.Interpret(stored));
    }

    [Fact]
    public void ABlockedSwitchOutranksWindowsRefusingToListDevices()
    {
        // A refusal to list devices is one of the things a blocked switch CAUSES, so the generic
        // sentence there hides the cause behind its own symptom.
        var readiness = MicrophoneReadinessReport.For(
            MicrophoneConsent.Blocked,
            defaultDeviceName: null,
            enumerationFailed: true);

        Assert.Contains("blocking", readiness.Sentence, StringComparison.OrdinalIgnoreCase);
        Assert.True(readiness.OffersPrivacySettings);
    }

    [Theory]
    [InlineData(MicrophoneConsent.Blocked, MicrophoneConsent.Allowed, MicrophoneConsent.Allowed, MicrophoneConsent.Blocked)]
    [InlineData(MicrophoneConsent.Allowed, MicrophoneConsent.Blocked, MicrophoneConsent.Allowed, MicrophoneConsent.Blocked)]
    [InlineData(MicrophoneConsent.Allowed, MicrophoneConsent.Allowed, MicrophoneConsent.Blocked, MicrophoneConsent.Blocked)]
    [InlineData(MicrophoneConsent.Blocked, MicrophoneConsent.Unknown, MicrophoneConsent.Unknown, MicrophoneConsent.Blocked)]
    [InlineData(MicrophoneConsent.Allowed, MicrophoneConsent.Allowed, MicrophoneConsent.Allowed, MicrophoneConsent.Allowed)]
    [InlineData(MicrophoneConsent.Unknown, MicrophoneConsent.Allowed, MicrophoneConsent.Allowed, MicrophoneConsent.Unknown)]
    [InlineData(MicrophoneConsent.Allowed, MicrophoneConsent.Allowed, MicrophoneConsent.Unknown, MicrophoneConsent.Unknown)]
    public void AnySwitchThatSaysNoIsTheAnswer(
        MicrophoneConsent machine,
        MicrophoneConsent user,
        MicrophoneConsent desktopApps,
        MicrophoneConsent expected)
    {
        // THREE SWITCHES DECIDE THIS, and the third is the one that governs this app: "let desktop
        // apps access your microphone" is its own control. Reading only the global one told somebody
        // who had turned desktop apps off that everything was fine.
        Assert.Equal(expected, WindowsMicrophoneConsent.Combine(machine, user, desktopApps));
    }

    [Theory]
    [InlineData(2, MicrophoneConsent.Blocked)]
    [InlineData(1, MicrophoneConsent.Allowed)]
    [InlineData(0, MicrophoneConsent.Unknown)]
    [InlineData(null, MicrophoneConsent.Unknown)]
    [InlineData("Deny", MicrophoneConsent.Unknown)]
    [InlineData(7, MicrophoneConsent.Unknown)]
    public void AWorkplacePolicyIsANumberAndItsSilenceIsNotADoubt(
        object? stored,
        MicrophoneConsent expected)
    {
        // A managed machine is configured somewhere else entirely and as a number, not a word. Its
        // ABSENCE is the ordinary case on every PC nobody manages, so silence has to mean "the
        // person's own switches decide" rather than "something could not be read".
        Assert.Equal(expected, WindowsMicrophoneConsent.InterpretPolicy(stored));
    }

    [Fact]
    public void NoReadingsAtAllCannotAccuseWindowsOfAnything()
    {
        // No evidence is not evidence of a refusal, and answering Blocked would put a specific
        // accusation on screen that nothing looked at.
        Assert.Equal(MicrophoneConsent.Unknown, WindowsMicrophoneConsent.Combine());
    }

    [Fact]
    public void ReadingTheSwitchOnThisMachineNeverThrows()
    {
        // It runs on a build agent where the key may be absent and the hive may be locked down. The
        // answer there is Unknown, and the card then says nothing about privacy.
        var consent = WindowsMicrophoneConsent.Read();

        Assert.True(Enum.IsDefined(consent));
    }
}
