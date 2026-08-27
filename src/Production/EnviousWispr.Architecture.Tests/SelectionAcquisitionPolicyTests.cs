using EnviousWispr.Core.Input;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// Borrowing the user's clipboard at the wrong moment is worse than the feature is good, so the
/// refusals are the product.
/// </summary>
public sealed class SelectionAcquisitionPolicyTests
{
    private static SelectionAcquisition Decide(
        bool hasValidTarget = true,
        string? published = null,
        bool dictating = false,
        bool delivering = false) =>
        SelectionAcquisitionPolicy.Decide(hasValidTarget, published, dictating, delivering);

    [Fact]
    public void AnAppThatPublishesItsSelectionIsSimplyRead()
    {
        Assert.Equal(SelectionAcquisition.UsePublished, Decide(published: "kubernetes"));
    }

    /// <summary>
    /// The control for the whole file. An app publishing nothing must actually get the synthetic
    /// copy, or a policy that refused everything would pass every refusal test below and quietly
    /// leave Quick Add exactly as broken as it was.
    /// </summary>
    [Fact]
    public void AnAppThatPublishesNothingGetsTheSyntheticCopy()
    {
        Assert.Equal(SelectionAcquisition.SyntheticCopy, Decide(published: null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyPublishedSelectionCountsAsPublishingNothing(string published)
    {
        Assert.Equal(SelectionAcquisition.SyntheticCopy, Decide(published: published));
    }

    [Fact]
    public void NoTargetIsRefusedWhateverElseIsTrue()
    {
        Assert.Equal(
            SelectionAcquisition.Refuse,
            Decide(hasValidTarget: false, published: "kubernetes"));
    }

    /// <summary>
    /// The keys a synthetic copy sends are watched by our own hook, so sending them mid-dictation
    /// puts the app in the position of typing at itself.
    /// </summary>
    [Fact]
    public void ASyntheticCopyIsRefusedWhileADictationIsRunning()
    {
        Assert.Equal(SelectionAcquisition.Refuse, Decide(dictating: true));
    }

    /// <summary>
    /// The clipboard already holds text on its way into a document. Taking it here would land the
    /// user's selection in their document instead of their dictation, and they would have no way to
    /// connect that to a word they were adding.
    /// </summary>
    [Fact]
    public void ASyntheticCopyIsRefusedWhileTextIsBeingDelivered()
    {
        Assert.Equal(SelectionAcquisition.Refuse, Decide(delivering: true));
    }

    /// <summary>
    /// A published selection borrows nothing, so it is safe in every state and is deliberately NOT
    /// subject to the refusals. Refusing it mid-dictation would disable Quick Add for no reason.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void APublishedSelectionIsStillReadWhileTheAppIsBusy(bool dictating, bool delivering)
    {
        Assert.Equal(
            SelectionAcquisition.UsePublished,
            Decide(published: "kubernetes", dictating: dictating, delivering: delivering));
    }

    /// <summary>
    /// Both busy conditions independently refuse. A suite that only ever set one at a time would
    /// pass a policy that ANDed them, and that policy takes the clipboard whenever exactly one of
    /// the two dangerous states is true.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void EitherBusyStateAloneRefusesTheCopy(bool dictating, bool delivering)
    {
        Assert.Equal(SelectionAcquisition.Refuse, Decide(dictating: dictating, delivering: delivering));
    }
}
