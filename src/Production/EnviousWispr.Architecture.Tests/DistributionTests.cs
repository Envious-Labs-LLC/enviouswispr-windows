using EnviousWispr.Core.Distribution;
using EnviousWispr.Services.Distribution;
using Windows.ApplicationModel;
using Windows.Services.Store;

namespace EnviousWispr.Architecture.Tests;

public sealed class DistributionTests
{
    [Fact]
    public void ReleaseIdentitiesAreIsolatedAcrossEveryChannel()
    {
        var identities = Enum.GetValues<ReleaseChannel>()
            .Select(ReleaseIdentity.For)
            .ToArray();

        Assert.Equal(identities.Length, identities.Select(item => item.ChannelName).Distinct().Count());
        Assert.Equal(identities.Length, identities.Select(item => item.DataDirectoryName).Distinct().Count());
        Assert.Equal(identities.Length, identities.Select(item => item.SingleInstanceKey).Distinct().Count());
    }

    [Theory]
    [InlineData("stable", ReleaseChannel.Stable)]
    [InlineData("win-x64-founder", ReleaseChannel.Founder)]
    [InlineData("BETA", ReleaseChannel.Beta)]
    public void ReleaseIdentityParserAcceptsOnlyBoundedChannels(
        string value,
        ReleaseChannel expected)
    {
        Assert.True(ReleaseIdentity.TryParse(value, out var identity));
        Assert.Equal(expected, identity.Channel);
    }

    [Fact]
    public void ReleaseIdentityParserFailsClosedToStable()
    {
        Assert.False(ReleaseIdentity.TryParse("nightly", out var identity));
        Assert.Equal(ReleaseIdentity.Stable, identity);
    }

    /// <summary>
    /// EVERY STATE THE STORE CAN END AN INSTALL ON, WRITTEN OUT. Only Completed is an install and only
    /// Canceled is the person saying no; the rest left this copy unchanged. The table is literal and must
    /// cover the Store's whole enum, so a state a later SDK adds fails here until someone decides what it
    /// means, rather than falling silently into the default.
    /// </summary>
    [Fact]
    public void EveryStoreInstallStateMapsToTheStatusThePersonSees()
    {
        var expected = new Dictionary<StorePackageUpdateState, UpdateOperationStatus>
        {
            [StorePackageUpdateState.Pending] = UpdateOperationStatus.Failed,
            [StorePackageUpdateState.Downloading] = UpdateOperationStatus.Failed,
            [StorePackageUpdateState.Deploying] = UpdateOperationStatus.Failed,
            [StorePackageUpdateState.Completed] = UpdateOperationStatus.Installing,
            [StorePackageUpdateState.Canceled] = UpdateOperationStatus.Cancelled,
            [StorePackageUpdateState.OtherError] = UpdateOperationStatus.Failed,
            [StorePackageUpdateState.ErrorLowBattery] = UpdateOperationStatus.Failed,
            [StorePackageUpdateState.ErrorWiFiRecommended] = UpdateOperationStatus.Failed,
            [StorePackageUpdateState.ErrorWiFiRequired] = UpdateOperationStatus.Failed,
        };

        Assert.Equal(
            Enum.GetValues<StorePackageUpdateState>().Order(),
            expected.Keys.Order());
        foreach (var (state, status) in expected)
        {
            Assert.Equal(status, StoreUpdateOutcome.FromInstall(state));
        }
    }

    [Fact]
    public void ACheckWithNothingOfferedIsNoUpdateAndNamesTheInstalledVersion()
    {
        var result = StoreUpdateOutcome.FromCheck([], "0.19.0.0");

        Assert.Equal(new UpdateOperationResult(UpdateOperationStatus.NoUpdate, "0.19.0.0"), result);
        Assert.False(result.CanApply);
    }

    /// <summary>
    /// THE NEWEST BY NUMBER, WHATEVER THE ORDER. Each pair differs in one part only, and the smaller one
    /// sorts later as text (9 after 10), so a first-item or string comparison picks the wrong version.
    /// </summary>
    [Theory]
    [InlineData(new ushort[] { 1, 9, 0, 0 }, new ushort[] { 1, 10, 0, 0 }, "1.10.0.0")]
    [InlineData(new ushort[] { 1, 2, 9, 0 }, new ushort[] { 1, 2, 10, 0 }, "1.2.10.0")]
    [InlineData(new ushort[] { 1, 2, 3, 9 }, new ushort[] { 1, 2, 3, 10 }, "1.2.3.10")]
    [InlineData(new ushort[] { 9, 65535, 65535, 65535 }, new ushort[] { 10, 0, 0, 0 }, "10.0.0.0")]
    public void ACheckOffersTheNewestVersionInEitherOrder(ushort[] older, ushort[] newer, string expected)
    {
        var olderVersion = new PackageVersion(older[0], older[1], older[2], older[3]);
        var newerVersion = new PackageVersion(newer[0], newer[1], newer[2], newer[3]);

        foreach (var offered in new[] { new[] { olderVersion, newerVersion }, new[] { newerVersion, olderVersion } })
        {
            var result = StoreUpdateOutcome.FromCheck(offered, "0.19.0.0");

            Assert.Equal(new UpdateOperationResult(UpdateOperationStatus.UpdateAvailable, expected), result);
            Assert.True(result.CanApply);
        }
    }

    /// <summary>Only an update the Store has offered can be installed; every other answer leaves Install off.</summary>
    [Fact]
    public void OnlyAnAvailableUpdateCanBeApplied()
    {
        var applicable = Enum.GetValues<UpdateOperationStatus>()
            .Where(status => new UpdateOperationResult(status, "1.0.0.0").CanApply)
            .ToArray();

        Assert.Equal(new[] { UpdateOperationStatus.UpdateAvailable }, applicable);
    }
}
