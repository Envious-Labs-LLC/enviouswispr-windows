using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Services.Runtime;

namespace EnviousWispr.Architecture.Tests;

public sealed class RuntimeResourceArbiterTests
{
    [Fact]
    public async Task FinalAsrWaitsUntilPreviewReleasesAccelerator()
    {
        using var arbiter = new RuntimeResourceArbiter();
        var preview = await arbiter.AcquireAsync(
            RuntimeResourceKind.Accelerator,
            RuntimeWorkloadKind.LivePreview,
            TimeSpan.Zero);

        var blockedFinal = await arbiter.AcquireAsync(
            RuntimeResourceKind.Accelerator,
            RuntimeWorkloadKind.FinalAsr,
            TimeSpan.FromMilliseconds(20));
        await preview.Lease!.DisposeAsync();
        var final = await arbiter.AcquireAsync(
            RuntimeResourceKind.Accelerator,
            RuntimeWorkloadKind.FinalAsr,
            TimeSpan.Zero);

        Assert.True(preview.Succeeded);
        Assert.False(blockedFinal.Succeeded);
        Assert.Equal(AppErrorCode.RuntimeResourceBusy, blockedFinal.Error?.Code);
        Assert.True(final.Succeeded);
        Assert.Equal(
            RuntimeWorkloadKind.FinalAsr,
            arbiter.ActiveOwners[RuntimeResourceKind.Accelerator]);
        await final.Lease!.DisposeAsync();
        Assert.Empty(arbiter.ActiveOwners);
    }

    [Fact]
    public async Task CpuAndAcceleratorHaveIndependentLeases()
    {
        using var arbiter = new RuntimeResourceArbiter();

        var cpu = await arbiter.AcquireAsync(
            RuntimeResourceKind.Cpu,
            RuntimeWorkloadKind.LocalPolish,
            TimeSpan.Zero);
        var accelerator = await arbiter.AcquireAsync(
            RuntimeResourceKind.Accelerator,
            RuntimeWorkloadKind.FinalAsr,
            TimeSpan.Zero);

        Assert.True(cpu.Succeeded);
        Assert.True(accelerator.Succeeded);
        Assert.Equal(2, arbiter.ActiveOwners.Count);
        await cpu.Lease!.DisposeAsync();
        await accelerator.Lease!.DisposeAsync();
    }
}
