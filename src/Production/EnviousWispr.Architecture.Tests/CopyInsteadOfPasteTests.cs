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
        // Every value in this enum means a paste could not happen, so a requested copy - which is an
        // ordinary success - must not have a name among them. Where the text went is carried by the
        // route instead, which already has a value for the clipboard.
        Assert.DoesNotContain(
            Enum.GetNames<TextDeliveryRefusalReason>(),
            name => name.Contains("Copy", StringComparison.Ordinal));
        Assert.Contains(TextDeliveryRoute.ClipboardOnly, Enum.GetValues<TextDeliveryRoute>());
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
