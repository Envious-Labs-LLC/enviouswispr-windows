using EnviousWispr.Audio;
using EnviousWispr.Core.Audio;

namespace EnviousWispr.Architecture.Tests;

public sealed class AudioDeviceChangeTrackerTests
{
    [Fact]
    public void KnownCaptureRemovalRemainsClassifiedAfterEndpointDisappears()
    {
        var tracker = new AudioDeviceChangeTracker();
        tracker.ReplaceKnownCaptureDevices(new[] { new AudioDeviceId("capture-one") });

        var change = tracker.Removed("capture-one");

        Assert.Equal(AudioDeviceChangeKind.Removed, change.Kind);
        Assert.True(change.AffectsCapture);
    }

    [Fact]
    public void RenderRemovalDoesNotAffectCapture()
    {
        var tracker = new AudioDeviceChangeTracker();

        var change = tracker.Removed("render-one");

        Assert.False(change.AffectsCapture);
    }

    [Fact]
    public void InactiveTransitionRemovesKnownCaptureIdentity()
    {
        var tracker = new AudioDeviceChangeTracker();
        tracker.Added("capture-one", isCapture: true);

        var inactive = tracker.StateChanged("capture-one", isCapture: false, isActive: false);
        var removedAfterInactive = tracker.Removed("capture-one");

        Assert.True(inactive.AffectsCapture);
        Assert.False(removedAfterInactive.AffectsCapture);
    }

    [Fact]
    public void DefaultCaptureTransitionIsClassifiedWithoutDeviceIdentity()
    {
        var change = AudioDeviceChangeTracker.DefaultChanged(
            deviceId: null,
            affectsCapture: true);

        Assert.Equal(string.Empty, change.Id.Value);
        Assert.Equal(AudioDeviceChangeKind.DefaultChanged, change.Kind);
        Assert.True(change.AffectsCapture);
    }
}
