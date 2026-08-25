using EnviousWispr.Input;

namespace EnviousWispr.Tests;

public sealed class TextInserterTests
{
    [Fact]
    public void NativeInputLayoutMatchesWin64Abi()
    {
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(40, TextInserter.NativeInputSize);
        Assert.Equal(24, TextInserter.NativeKeyboardInputSize);
        Assert.Equal(4, TextInserter.NativeKeyboardFlagsOffset);
    }
}
