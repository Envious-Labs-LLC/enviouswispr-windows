using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// Asking for the clipboard is a choice, and it looks different from a paste that failed.
/// </summary>
/// <remarks>
/// macOS HAS THIS TOGGLE AND WINDOWS HAD ONLY THE FALLBACK. The clipboard route already shipped and
/// was chosen whenever a paste was refused, so the safety net was there and the choice was not.
/// </remarks>
public sealed class CopyInsteadOfPasteTests
{
    [Fact]
    public void NobodyGetsCopyOnlyWithoutAskingForIt()
    {
        Assert.False(TextDeliveryOptions.Default.CopyInsteadOfPaste);
        Assert.False(UserPreferences.Default.CopyInsteadOfPaste);
    }

    [Fact]
    public void AskingForTheClipboardIsNotRecordedAsSomethingGoingWrong()
    {
        // Every other refusal means a paste could not happen. This one means a paste was not wanted,
        // and a diagnostics file that cannot tell them apart reports a fault every time somebody
        // uses the setting the way it is meant to be used.
        Assert.NotEqual(TextDeliveryRefusalReason.None, TextDeliveryRefusalReason.CopyRequested);
        Assert.NotEqual(TextDeliveryRefusalReason.TargetUnavailable, TextDeliveryRefusalReason.CopyRequested);
        Assert.NotEqual(TextDeliveryRefusalReason.InputBlocked, TextDeliveryRefusalReason.CopyRequested);
    }

    [Fact]
    public void TheChoiceSurvivesBeingWrittenDownAndReadBack()
    {
        var asked = UserPreferences.Default with { CopyInsteadOfPaste = true };

        Assert.True(asked.CopyInsteadOfPaste);
        Assert.False(UserPreferences.Default.CopyInsteadOfPaste);
    }

    [Fact]
    public void TheOptionCarriesTheChoiceWithoutDisturbingTheRest()
    {
        var asked = TextDeliveryOptions.Default with { CopyInsteadOfPaste = true };

        Assert.True(asked.CopyInsteadOfPaste);
        Assert.Equal(TextDeliveryOptions.Default.RestoreClipboardAfterPaste, asked.RestoreClipboardAfterPaste);
        Assert.Equal(TextDeliveryOptions.Default.ContextWindowCharacters, asked.ContextWindowCharacters);
        Assert.Equal(
            TextDeliveryOptions.Default.MaximumDirectValueCharacters,
            asked.MaximumDirectValueCharacters);
    }
}
