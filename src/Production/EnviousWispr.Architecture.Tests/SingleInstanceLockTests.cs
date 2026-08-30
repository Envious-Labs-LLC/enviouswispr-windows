using EnviousWispr.Services.Lifecycle;

namespace EnviousWispr.Architecture.Tests;

public sealed class SingleInstanceLockTests
{
    [Fact]
    public void TryAcquireRejectsASecondOwner()
    {
        var key = $"EnviousWispr.Tests.{Guid.NewGuid():N}";

        Assert.True(SingleInstanceLock.TryAcquire(key, out var first));
        using (first)
        {
            Assert.False(SingleInstanceLock.TryAcquire(key, out var second));
            Assert.Null(second);
        }
    }
}
