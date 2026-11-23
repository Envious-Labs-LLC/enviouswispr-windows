using System.Runtime.InteropServices;
using System.Windows.Forms;
using EnviousWispr.Services.Lifecycle;

namespace EnviousWispr.Architecture.Tests;

public sealed class WindowsToolWindowStyleTests
{
    private const int GwlExStyle = -20;

    [Fact]
    public void ApplyToolWindowFlagsSetsToolWindowAndClearsAppWindowWhilePreservingOtherBits()
    {
        const nint unrelatedBits = 0x00000008 | 0x00000200; // WS_EX_TOPMOST | WS_EX_CLIENTEDGE
        var initial = unrelatedBits | WindowsToolWindowStyle.WsExAppWindow;

        var updated = WindowsToolWindowStyle.ApplyToolWindowFlags(initial);

        Assert.Equal(unrelatedBits | WindowsToolWindowStyle.WsExToolWindow, updated);
        Assert.Equal(0, (long)(updated & WindowsToolWindowStyle.WsExAppWindow));
    }

    [Fact]
    public void HideFromTaskSwitchersReturnsTrueWithoutTouchingHandleWhenSdkCallSucceeds()
    {
        var window = new NativeWindow();
        window.CreateHandle(new CreateParams
        {
            ExStyle = unchecked((int)WindowsToolWindowStyle.WsExAppWindow),
        });

        try
        {
            var initialStyle = GetWindowLongPtrW(window.Handle, GwlExStyle);
            var invoked = false;

            var hidden = WindowsToolWindowStyle.HideFromTaskSwitchers(
                () => invoked = true,
                window.Handle);

            Assert.True(invoked);
            Assert.True(hidden);
            Assert.Equal(initialStyle, GetWindowLongPtrW(window.Handle, GwlExStyle));
        }
        finally
        {
            window.DestroyHandle();
        }
    }

    [Fact]
    public void HideFromTaskSwitchersFallsBackToToolWindowStyleOnNotImplementedException()
    {
        var window = new NativeWindow();
        window.CreateHandle(new CreateParams
        {
            ExStyle = unchecked((int)WindowsToolWindowStyle.WsExAppWindow),
        });

        try
        {
            var before = GetWindowLongPtrW(window.Handle, GwlExStyle);
            Assert.NotEqual(0, (long)(before & WindowsToolWindowStyle.WsExAppWindow));
            Assert.Equal(0, (long)(before & WindowsToolWindowStyle.WsExToolWindow));

            var hidden = WindowsToolWindowStyle.HideFromTaskSwitchers(
                () => throw new NotImplementedException("SetShownInSwitchers failed."),
                window.Handle);

            var after = GetWindowLongPtrW(window.Handle, GwlExStyle);
            Assert.True(hidden);
            Assert.NotEqual(0, (long)(after & WindowsToolWindowStyle.WsExToolWindow));
            Assert.Equal(0, (long)(after & WindowsToolWindowStyle.WsExAppWindow));
        }
        finally
        {
            window.DestroyHandle();
        }
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2201:Do not raise reserved exception types",
        Justification = "Simulates a WinRT COM E_NOTIMPL error surfaced as COMException.")]
    public void HideFromTaskSwitchersFallsBackToToolWindowStyleOnComNotImplemented()
    {
        var window = new NativeWindow();
        window.CreateHandle(new CreateParams
        {
            ExStyle = unchecked((int)WindowsToolWindowStyle.WsExAppWindow),
        });

        try
        {
            var hidden = WindowsToolWindowStyle.HideFromTaskSwitchers(
                () => throw new COMException("SetShownInSwitchers failed.", unchecked((int)0x80004001)),
                window.Handle);

            var after = GetWindowLongPtrW(window.Handle, GwlExStyle);
            Assert.True(hidden);
            Assert.NotEqual(0, (long)(after & WindowsToolWindowStyle.WsExToolWindow));
            Assert.Equal(0, (long)(after & WindowsToolWindowStyle.WsExAppWindow));
        }
        finally
        {
            window.DestroyHandle();
        }
    }

    [Fact]
    public void HideFromTaskSwitchersRethrowsUnrelatedExceptions()
    {
        Assert.Throws<InvalidOperationException>(() =>
            WindowsToolWindowStyle.HideFromTaskSwitchers(
                () => throw new InvalidOperationException("unexpected"),
                nint.Zero));

        var comException = Assert.Throws<COMException>(() =>
            WindowsToolWindowStyle.HideFromTaskSwitchers(
                () => Marshal.ThrowExceptionForHR(unchecked((int)0x80004005)),
                nint.Zero));
        Assert.Equal(unchecked((int)0x80004005), comException.HResult);
    }

    [Fact]
    public void HideFromTaskSwitchersReturnsFalseOnZeroHandleWhenFallbackRuns()
    {
        var hidden = WindowsToolWindowStyle.HideFromTaskSwitchers(
            () => throw new NotImplementedException("SetShownInSwitchers failed."),
            nint.Zero);

        Assert.False(hidden);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtrW(nint windowHandle, int index);
}
