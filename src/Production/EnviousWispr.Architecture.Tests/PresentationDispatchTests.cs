using EnviousWispr.Presentation;

namespace EnviousWispr.Architecture.Tests;

/// <summary>The production presentation gate at both moments: queued and actually running.</summary>
public sealed class PresentationDispatchTests
{
    [Fact]
    public void AQueuedCallbackRendersWhileOpenAndReturnsItsLease()
    {
        using var gate = new PresentationAdmission();
        Action? pending = null;
        var rendered = 0;
        Assert.True(gate.TryPost(callback => { pending = callback; return true; }, () =>
        {
            Assert.Equal(1, gate.Outstanding);
            rendered++;
        }));
        Assert.Equal(0, gate.Outstanding);
        Assert.Equal(0, rendered);
        Assert.NotNull(pending);
        pending();
        Assert.Equal(1, rendered);
        Assert.Equal(0, gate.Outstanding);
        Assert.True(gate.CloseAsync().IsCompletedSuccessfully);
    }

    [Fact]
    public void ACallbackQueuedBeforeCloseCannotTouchTheClosedWindowEvenAfterDisposal()
    {
        var gate = new PresentationAdmission();
        Action? pending = null;
        var rendered = false;
        Assert.True(gate.TryPost(callback => { pending = callback; return true; }, () => rendered = true));
        // Queued work holds nothing: even a dispatcher that drops it cannot strand shutdown.
        Assert.True(gate.CloseAsync().IsCompletedSuccessfully);
        gate.Dispose();
        Assert.NotNull(pending);
        pending();
        Assert.False(rendered);
        Assert.Equal(0, gate.Outstanding);
    }

    [Fact]
    public void APostAfterCloseDoesNotEvenAskTheDispatcher()
    {
        using var gate = new PresentationAdmission();
        Assert.True(gate.CloseAsync().IsCompletedSuccessfully);
        var enqueued = false;
        Assert.False(gate.TryPost(_ => { enqueued = true; return true; }, () => Assert.Fail("late render")));
        Assert.False(enqueued);
        Assert.Equal(0, gate.Outstanding);
    }

    [Fact]
    public void ACloseBetweenThePostingCheckAndQueueAcceptanceStillRefusesTheDraw()
    {
        using var gate = new PresentationAdmission();
        Action? pending = null;
        Assert.True(gate.TryPost(callback =>
        {
            Assert.True(gate.CloseAsync().IsCompletedSuccessfully);
            pending = callback;
            return true;
        }, () => Assert.Fail("close raced queue admission")));
        Assert.NotNull(pending);
        pending();
        Assert.Equal(0, gate.Outstanding);
    }

    [Fact]
    public void DispatcherRefusalAndExceptionLeaveNothingForTheDrain()
    {
        using var gate = new PresentationAdmission();
        Assert.False(gate.TryPost(_ => false, () => Assert.Fail("refused callback")));
        var fault = new InvalidOperationException("dispatcher fault");
        Assert.Same(fault, Assert.Throws<InvalidOperationException>(() => gate.TryPost(_ => throw fault, () => { })));
        Assert.Equal(0, gate.Outstanding);
        Assert.True(gate.CloseAsync().IsCompletedSuccessfully);
    }

    [Fact]
    public void AThrowingRenderReturnsItsLeaseWithoutHidingTheFault()
    {
        using var gate = new PresentationAdmission();
        var fault = new InvalidOperationException("render fault");
        Assert.Same(fault, Assert.Throws<InvalidOperationException>(() =>
            gate.TryPost(callback => { callback(); return true; }, () => throw fault)));
        Assert.Equal(0, gate.Outstanding);
        Assert.True(gate.CloseAsync().IsCompletedSuccessfully);
    }

    [Fact]
    public void TheDrainWaitsForARunningRenderButNotForQueuedWork()
    {
        using var gate = new PresentationAdmission();
        Task? drain = null;
        Assert.True(gate.TryPost(callback => { callback(); return true; }, () =>
        {
            drain = gate.CloseAsync();
            Assert.True(gate.Closed);
            Assert.Equal(1, gate.Outstanding);
            Assert.False(drain.IsCompleted);
        }));
        Assert.NotNull(drain);
        Assert.True(drain.IsCompletedSuccessfully);
        Assert.Equal(0, gate.Outstanding);
    }
}
