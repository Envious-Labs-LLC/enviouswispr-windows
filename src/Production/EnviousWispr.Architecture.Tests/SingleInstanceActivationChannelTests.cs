using EnviousWispr.Services.Lifecycle;

namespace EnviousWispr.Architecture.Tests;

public sealed class SingleInstanceActivationChannelTests
{
    [Fact]
    public async Task SecondInstanceRequestsActivationWithoutSendingUserData()
    {
        var key = $"EnviousLabs.EnviousWispr.Tests.{Guid.NewGuid():N}";
        await using var channel = new SingleInstanceActivationChannel(key);
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.ActivationRequested += (_, _) => activated.TrySetResult();
        channel.Start();

        var sent = await SingleInstanceActivationChannel.RequestActivationAsync(
            key,
            TimeSpan.FromSeconds(2));

        Assert.True(sent);
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task MissingPrimaryInstanceFailsWithinBoundedTimeout()
    {
        var sent = await SingleInstanceActivationChannel.RequestActivationAsync(
            $"EnviousLabs.EnviousWispr.Tests.{Guid.NewGuid():N}",
            TimeSpan.FromMilliseconds(100));

        Assert.False(sent);
    }
}
