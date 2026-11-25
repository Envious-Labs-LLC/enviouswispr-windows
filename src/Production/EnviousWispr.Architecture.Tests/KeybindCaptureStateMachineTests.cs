using EnviousWispr.Core.Input;
using EnviousWispr.Services.Input;

namespace EnviousWispr.Architecture.Tests;

public sealed class KeybindCaptureStateMachineTests
{
    private const uint VkBack = 0x08;
    private const uint VkEscape = 0x1B;
    private const uint VkLShift = 0xA0;
    private const uint VkRShift = 0xA1;
    private const uint VkLControl = 0xA2;
    private const uint VkRControl = 0xA3;
    private const uint VkLAlt = 0xA4;
    private const uint VkRAlt = 0xA5;
    private const uint VkLWin = 0x5B;
    private const uint VkRWin = 0x5C;
    private const uint VkF8 = 0x77;

    [Theory]
    [InlineData(VkLControl, "LeftCtrl")]
    [InlineData(VkRControl, "RightCtrl")]
    [InlineData(VkLAlt, "LeftAlt")]
    [InlineData(VkRAlt, "RightAlt")]
    [InlineData(VkLShift, "LeftShift")]
    [InlineData(VkRShift, "RightShift")]
    [InlineData(VkLWin, "LeftWin")]
    [InlineData(VkRWin, "RightWin")]
    public void SidedSingleModifierCommitsOnReleaseWhenModifierOnlyIsAllowed(uint virtualKey, string expected)
    {
        var machine = new KeybindCaptureStateMachine(allowModifierOnly: true, allowEscapeAsBinding: false);

        var down = machine.Process(virtualKey, isKeyDown: true);
        Assert.Equal(KeybindCaptureStatus.Listening, down.Status);

        var up = machine.Process(virtualKey, isKeyDown: false);
        Assert.Equal(KeybindCaptureStatus.Committed, up.Status);
        Assert.Equal(expected, up.GestureText);
    }

    [Fact]
    public void ModifierPairCtrlWinCommitsWhenBothKeysRelease()
    {
        var machine = new KeybindCaptureStateMachine(allowModifierOnly: true, allowEscapeAsBinding: false);

        Assert.Equal(KeybindCaptureStatus.Listening, machine.Process(VkLControl, isKeyDown: true).Status);
        Assert.Equal(KeybindCaptureStatus.Listening, machine.Process(VkLWin, isKeyDown: true).Status);
        Assert.Equal(KeybindCaptureStatus.Listening, machine.Process(VkLWin, isKeyDown: false).Status);

        var done = machine.Process(VkLControl, isKeyDown: false);
        Assert.Equal(KeybindCaptureStatus.Committed, done.Status);
        Assert.Equal("Ctrl+Win", done.GestureText);
    }

    [Fact]
    public void InvalidModifierPairCtrlAltShowsHintAndStaysListening()
    {
        var machine = new KeybindCaptureStateMachine(allowModifierOnly: true, allowEscapeAsBinding: false);

        machine.Process(VkLControl, isKeyDown: true);
        machine.Process(VkLAlt, isKeyDown: true);
        machine.Process(VkLAlt, isKeyDown: false);

        var invalid = machine.Process(VkLControl, isKeyDown: false);
        Assert.Equal(KeybindCaptureStatus.InvalidAttempt, invalid.Status);
        Assert.False(string.IsNullOrWhiteSpace(invalid.HintMessage));

        // State resets cleanly so the next valid input commits.
        var next = machine.Process(VkF8, isKeyDown: true);
        Assert.Equal(KeybindCaptureStatus.Committed, next.Status);
        Assert.Equal("F8", next.GestureText);
    }

    [Fact]
    public void OrdinaryKeyWithModifiersCommitsImmediatelyOnKeyDown()
    {
        var machine = new KeybindCaptureStateMachine(allowModifierOnly: true, allowEscapeAsBinding: false);

        machine.Process(VkRControl, isKeyDown: true);
        var committed = machine.Process('V', isKeyDown: true);

        Assert.Equal(KeybindCaptureStatus.Committed, committed.Status);
        Assert.Equal("Ctrl+V", committed.GestureText);
    }

    [Fact]
    public void CtrlAltWCommitsImmediatelyOnKeyDown()
    {
        var machine = new KeybindCaptureStateMachine(allowModifierOnly: false, allowEscapeAsBinding: false);

        machine.Process(VkLControl, isKeyDown: true);
        machine.Process(VkLAlt, isKeyDown: true);
        var committed = machine.Process('W', isKeyDown: true);

        Assert.Equal(KeybindCaptureStatus.Committed, committed.Status);
        Assert.Equal("Ctrl+Alt+W", committed.GestureText);
    }

    [Fact]
    public void F8CommitsImmediatelyOnKeyDown()
    {
        var machine = new KeybindCaptureStateMachine(allowModifierOnly: true, allowEscapeAsBinding: false);

        var committed = machine.Process(VkF8, isKeyDown: true);

        Assert.Equal(KeybindCaptureStatus.Committed, committed.Status);
        Assert.Equal("F8", committed.GestureText);
    }

    [Fact]
    public void EscapeCancelsCaptureWhenEscapeIsNotBindable()
    {
        var machine = new KeybindCaptureStateMachine(allowModifierOnly: true, allowEscapeAsBinding: false);

        var result = machine.Process(VkEscape, isKeyDown: true);

        Assert.Equal(KeybindCaptureStatus.Cancelled, result.Status);
        Assert.Null(result.GestureText);
    }

    [Fact]
    public void EscapeBindsItselfWhenEscapeIsAllowedAsBinding()
    {
        var machine = new KeybindCaptureStateMachine(allowModifierOnly: false, allowEscapeAsBinding: true);

        var result = machine.Process(VkEscape, isKeyDown: true);

        Assert.Equal(KeybindCaptureStatus.Committed, result.Status);
        Assert.Equal("Escape", result.GestureText);
    }

    [Theory]
    [InlineData(VkLAlt)]
    [InlineData(VkRControl)]
    public void BareModifierIsRefusedWithHintWhenModifierOnlyIsDisallowed(uint modifierKey)
    {
        var machine = new KeybindCaptureStateMachine(allowModifierOnly: false, allowEscapeAsBinding: false);

        machine.Process(modifierKey, isKeyDown: true);
        var released = machine.Process(modifierKey, isKeyDown: false);

        Assert.Equal(KeybindCaptureStatus.InvalidAttempt, released.Status);
        Assert.False(string.IsNullOrWhiteSpace(released.HintMessage));

        var followUp = machine.Process(VkF8, isKeyDown: true);
        Assert.Equal(KeybindCaptureStatus.Committed, followUp.Status);
        Assert.Equal("F8", followUp.GestureText);
    }

    [Fact]
    public void BackspaceClearsOptionalShortcutWhenAllowed()
    {
        var machine = new KeybindCaptureStateMachine(
            allowModifierOnly: false,
            allowEscapeAsBinding: false,
            allowClear: true);

        var result = machine.Process(VkBack, isKeyDown: true);

        Assert.Equal(KeybindCaptureStatus.Committed, result.Status);
        Assert.Equal(string.Empty, result.GestureText);
    }

    [Theory]
    [InlineData(VkLControl, 0x00u, false, true)]
    [InlineData(VkLControl, 0x00u, true, true)]
    [InlineData(VkRControl, 0x10u, true, true)]
    [InlineData(VkLAlt, 0x10u, true, true)]
    [InlineData(VkRControl, 0x10u, false, false)]
    [InlineData(0xE8u, 0x00u, true, false)]
    [InlineData(0xE8u, 0x10u, true, false)]
    public void HookPolicyAcceptsForegroundInjectedKeysAndRejectsBackgroundOrMaskKeys(
        uint virtualKey,
        uint flags,
        bool isAppForeground,
        bool expected)
    {
        var accepted = WindowsKeybindCaptureSession.ShouldProcessHookKey(virtualKey, flags, isAppForeground);

        Assert.Equal(expected, accepted);
    }
}
