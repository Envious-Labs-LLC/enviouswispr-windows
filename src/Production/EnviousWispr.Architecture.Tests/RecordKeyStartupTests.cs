using EnviousWispr.Core.Input;
using EnviousWispr.Services.Input;

namespace EnviousWispr.Architecture.Tests;

/// <summary>The hook's startup accepts a modifier-only record binding. Ref: #66.</summary>
/// <remarks>
/// EVERY CTRL+WIN BINDING WAS REFUSED AT LAUNCH, as invalid: the key map knows no empty key. The parser accepted it and
/// the tracker implemented it, each green in its own suite, and the app logged HotkeyFailed - the binding chosen as the
/// default could not start. Read through the real parser, so the two agree by test rather than by coincidence.
/// </remarks>
public sealed class RecordKeyStartupTests
{
    [Theory]
    [InlineData("Ctrl+Win", 0u)]
    [InlineData("Ctrl+Shift", 0u)]
    [InlineData("F8", 0x77u)]
    [InlineData("LeftCtrl", 0xA2u)]
    [InlineData("Ctrl+Alt+W", 0x57u)]
    public void EveryBindingTheParserAcceptsHasAKeyTheHookAccepts(string configured, uint expected)
    {
        var parsed = HotkeyGestureParser.Parse(configured);
        Assert.True(parsed.Succeeded, $"the parser refused {configured}");

        Assert.True(WindowsPushToTalkHook.TryMapRecordKey(parsed.Gesture!.Value, out var virtualKey));
        Assert.Equal(expected, virtualKey);
    }
}
