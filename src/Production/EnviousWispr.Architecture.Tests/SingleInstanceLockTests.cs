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
            Assert.True(SingleInstanceLock.IsHeld(key));
            Assert.False(SingleInstanceLock.TryAcquire(key, out var second));
            Assert.Null(second);
        }

        Assert.False(SingleInstanceLock.IsHeld(key));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveKeyReturnsBaseKeyUnchangedWhenCustomDataDirectoryIsMissing(string? customDirectory)
    {
        Assert.Equal("EnviousWispr.Dev", SingleInstanceLock.ResolveKey("EnviousWispr.Dev", customDirectory));
    }

    [Fact]
    public void ResolveKeyScopesByNormalizedDataDirectory()
    {
        var a = SingleInstanceLock.ResolveKey("EnviousWispr.Dev", @"C:\Temp\EW-Run-A\");
        var aEquivalent = SingleInstanceLock.ResolveKey("EnviousWispr.Dev", @"c:/temp/ew-run-a");
        var b = SingleInstanceLock.ResolveKey("EnviousWispr.Dev", @"C:\Temp\EW-Run-B");

        Assert.StartsWith("EnviousWispr.Dev-", a, StringComparison.Ordinal);
        Assert.Equal("EnviousWispr.Dev-".Length + 16, a.Length);
        Assert.Equal(a, aEquivalent);
        Assert.NotEqual(a, b);
    }
}
