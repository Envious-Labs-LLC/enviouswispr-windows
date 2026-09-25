using System.Runtime.InteropServices;

namespace EnviousWispr.Services.Input;

/// <summary>Whether the person's fingers are still on a modifier, observed rather than guessed.</summary>
public static class ModifierKeys
{
    private const int VirtualKeyShift = 0x10;
    private const int VirtualKeyControl = 0x11;
    private const int VirtualKeyAlt = 0x12;
    private const int VirtualKeyLeftWindows = 0x5B;
    private const int VirtualKeyRightWindows = 0x5C;

    public static bool AnyHeld() =>
        IsDown(VirtualKeyShift) || IsDown(VirtualKeyControl) || IsDown(VirtualKeyAlt) ||
        IsDown(VirtualKeyLeftWindows) || IsDown(VirtualKeyRightWindows);

    /// <summary>Returns once no modifier is down, or once <paramref name="deadline"/> has passed; true when they came up.</summary>
    /// <remarks>
    /// ON THE CLOCK, NOT A COUNT OF SLEEPS: a loaded machine makes every sleep longer than asked, and a wait counted in
    /// sleeps would run long exactly when the person is waiting on it. Ref: #206 (macOS `waitUntil`).
    /// </remarks>
    public static async Task<bool> WaitForReleaseAsync(TimeSpan deadline, CancellationToken cancellationToken)
    {
        var end = Environment.TickCount64 + (long)deadline.TotalMilliseconds;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!AnyHeld())
            {
                return true;
            }

            if (Environment.TickCount64 >= end)
            {
                return false;
            }

            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
