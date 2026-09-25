using System.Runtime.InteropServices;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Settings;
using EnviousWispr.Services.Input;

namespace EnviousWispr.Architecture.Tests;

/// <summary>A Ctrl+Alt binding stands aside when the person's layout types a character with it. Ref: #206.</summary>
public sealed class AltGrTypingChordTests
{
    private const uint F8 = 0x77;
    private const uint Escape = 0x1B;
    private const HotkeyModifiers CtrlAlt = HotkeyModifiers.Control | HotkeyModifiers.Alt;

    [Fact]
    public void QuickAddStandsAsideWhenTheLayoutTypesTheChord()
    {
        var tracker = Tracker(typesCharacter: (_, _) => true);

        var down = tracker.Process('W', isKeyDown: true, CtrlAlt);
        var up = tracker.Process('W', isKeyDown: false, CtrlAlt);

        Assert.Null(down.Signal);
        Assert.False(down.Consume);
        Assert.Null(up.Signal);
        Assert.False(up.Consume);
    }

    [Fact]
    public void QuickAddStillFiresWhereTheLayoutTypesNothing()
    {
        var tracker = Tracker(typesCharacter: (_, _) => false);

        var down = tracker.Process('W', isKeyDown: true, CtrlAlt);
        var up = tracker.Process('W', isKeyDown: false, CtrlAlt);

        Assert.Equal(PushToTalkSignal.QuickAdd, down.Signal);
        Assert.True(down.Consume);
        Assert.True(up.Consume);
    }

    /// <summary>A press that went to the application stays there through its repeats, whatever the layout says later.</summary>
    [Fact]
    public void ATypedPressStaysTypedThroughItsRepeatsAndRelease()
    {
        var types = true;
        var tracker = Tracker(typesCharacter: (_, _) => types);

        var down = tracker.Process('W', isKeyDown: true, CtrlAlt);
        types = false;
        var repeat = tracker.Process('W', isKeyDown: true, CtrlAlt);
        var up = tracker.Process('W', isKeyDown: false, CtrlAlt);

        Assert.All([down, repeat, up], decision =>
        {
            Assert.Null(decision.Signal);
            Assert.False(decision.Consume);
        });
    }

    /// <summary>And a press the shortcut owns stays the shortcut's.</summary>
    [Fact]
    public void AnOwnedPressStaysOwnedThroughItsRepeatsAndRelease()
    {
        var types = false;
        var tracker = Tracker(typesCharacter: (_, _) => types);

        var down = tracker.Process('W', isKeyDown: true, CtrlAlt);
        types = true;
        var repeat = tracker.Process('W', isKeyDown: true, CtrlAlt);
        var up = tracker.Process('W', isKeyDown: false, CtrlAlt);

        Assert.Equal(PushToTalkSignal.QuickAdd, down.Signal);
        Assert.True(down.Consume);
        Assert.Null(repeat.Signal);
        Assert.True(repeat.Consume);
        Assert.True(up.Consume);
    }

    /// <summary>A layout question that throws is answered as typing, inside the hook, not thrown out of it.</summary>
    [Fact]
    public void ALayoutQuestionThatThrowsCountsAsTyping()
    {
        var tracker = Tracker(typesCharacter: (_, _) => throw new InvalidOperationException("layout unreadable"));

        var down = tracker.Process('W', isKeyDown: true, CtrlAlt);

        Assert.Null(down.Signal);
        Assert.False(down.Consume);
    }

    /// <summary>A recording bound to a Ctrl+Alt chord stands aside the same way.</summary>
    [Fact]
    public void ARecordBindingOnCtrlAltStandsAsideToo()
    {
        var tracker = new HotkeyEdgeTracker(
            new HotkeyBinding('Q', CtrlAlt),
            new HotkeyBinding(Escape, HotkeyModifiers.None),
            new HotkeyBinding('W', CtrlAlt),
            DictationRecordingMode.PushToTalk,
            (_, _) => true);

        var down = tracker.Process('Q', isKeyDown: true, CtrlAlt);

        Assert.Null(down.Signal);
        Assert.False(down.Consume);
    }

    /// <summary>The layout is asked about Ctrl+Alt presses only, and only when a binding would match.</summary>
    [Fact]
    public void TheLayoutIsNotAskedAboutAnythingElse()
    {
        var asked = new List<(uint Key, HotkeyModifiers Modifiers)>();
        var tracker = Tracker(typesCharacter: (key, modifiers) =>
        {
            asked.Add((key, modifiers));
            return false;
        });

        tracker.Process(F8, isKeyDown: true, HotkeyModifiers.None);
        tracker.Process(F8, isKeyDown: false, HotkeyModifiers.None);
        tracker.Process('A', isKeyDown: true, CtrlAlt);
        tracker.Process('A', isKeyDown: false, CtrlAlt);
        tracker.Process('W', isKeyDown: true, HotkeyModifiers.Control | HotkeyModifiers.Shift);
        tracker.Process('W', isKeyDown: true, CtrlAlt | HotkeyModifiers.Windows);

        Assert.Empty(asked);
    }

    /// <summary>The real layouts. Built from literals: what each layout types is a fact about Windows, not about us.</summary>
    [Theory]
    [InlineData("0000040E", 'W', true)]   // Hungarian: AltGr+W is "|"
    [InlineData("0000040E", 'V', true)]   // Hungarian: AltGr+V is "@"
    [InlineData("00000405", 'W', true)]   // Czech: AltGr+W is "|"
    [InlineData("00000415", 'C', true)]   // Polish (Programmers): AltGr+C is "c-acute"
    [InlineData("00000409", 'W', false)]  // US: nothing on Ctrl+Alt+W
    [InlineData("00000409", 'V', false)]  // US: nothing on Ctrl+Alt+V
    public void RealLayoutsAnswerAsTheyType(string layoutName, char key, bool types)
    {
        using var layout = TemporaryLayout.Load(layoutName);

        Assert.Equal(types, KeyboardLayoutTyping.Types(key, CtrlAlt, layout.Handle, capsLock: false));
    }

    private static HotkeyEdgeTracker Tracker(Func<uint, HotkeyModifiers, bool> typesCharacter) => new(
        new HotkeyBinding(F8, HotkeyModifiers.None),
        new HotkeyBinding(Escape, HotkeyModifiers.None),
        new HotkeyBinding('W', CtrlAlt),
        DictationRecordingMode.PushToTalk,
        typesCharacter);

    /// <summary>A layout loaded for one test and unloaded afterwards only if this test was what loaded it.</summary>
    /// <remarks>
    /// SAVE AND RESTORE, ASSERTED. The layout list belongs to the person's session; a test that leaves a
    /// Hungarian keyboard in someone's language bar has changed their machine. The list is read before
    /// and after, and a difference fails the test rather than being reported as a pass.
    /// </remarks>
    private sealed class TemporaryLayout : IDisposable
    {
        private const uint DoNotTellShell = 0x00000080;
        private readonly IReadOnlySet<nint> _before;
        private readonly bool _loadedHere;

        private TemporaryLayout(nint handle, IReadOnlySet<nint> before)
        {
            Handle = handle;
            _before = before;
            _loadedHere = !before.Contains(handle);
        }

        public nint Handle { get; }

        public static TemporaryLayout Load(string name)
        {
            var before = LayoutList();
            var handle = LoadKeyboardLayout(name, DoNotTellShell);
            Assert.NotEqual(0, handle);
            return new TemporaryLayout(handle, before);
        }

        public void Dispose()
        {
            if (_loadedHere)
            {
                Assert.True(UnloadKeyboardLayout(Handle), "The test could not unload the keyboard layout it loaded.");
            }

            Assert.True(_before.SetEquals(LayoutList()), "The test left the keyboard layout list changed.");
        }

        private static HashSet<nint> LayoutList()
        {
            var count = GetKeyboardLayoutList(0, null);
            var layouts = new nint[count];
            var filled = GetKeyboardLayoutList(count, layouts);
            Assert.Equal(count, filled);
            return [.. layouts];
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern nint LoadKeyboardLayout(string name, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnloadKeyboardLayout(nint layout);

        [DllImport("user32.dll")]
        private static extern int GetKeyboardLayoutList(int count, [Out] nint[]? layouts);
    }
}
