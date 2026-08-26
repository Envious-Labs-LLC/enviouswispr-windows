using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Architecture.Tests;

public sealed class HotkeyGestureParserTests
{
    [Theory]
    [InlineData("F8", HotkeyModifiers.None, "F8")]
    [InlineData("ctrl+shift+f12", HotkeyModifiers.Control | HotkeyModifiers.Shift, "F12")]
    [InlineData("F9 + Windows + Alt", HotkeyModifiers.Windows | HotkeyModifiers.Alt, "F9")]
    [InlineData("control + space", HotkeyModifiers.Control, "Space")]
    [InlineData("esc", HotkeyModifiers.None, "Escape")]
    public void ValidGesturesAreCanonicalized(
        string value,
        HotkeyModifiers expectedModifiers,
        string expectedKey)
    {
        var result = HotkeyGestureParser.Parse(value);

        Assert.True(result.Succeeded);
        Assert.Equal(expectedModifiers, result.Gesture?.Modifiers);
        Assert.Equal(expectedKey, result.Gesture?.Key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+Ctrl+F8")]
    [InlineData("F8+F9")]
    [InlineData("F25")]
    [InlineData("Ctrl++F8")]
    public void InvalidGesturesReturnContentFreeTypedFailure(string? value)
    {
        var result = HotkeyGestureParser.Parse(value);

        Assert.False(result.Succeeded);
        Assert.Null(result.Gesture);
        Assert.Equal(AppErrorCode.HotkeyInvalid, result.Error?.Code);
        Assert.Equal(AppErrorStage.HotkeyConfiguration, result.Error?.Stage);
    }

    [Fact]
    public void SettingsValidationRejectsUninstallableGesture()
    {
        var settings = AppSettings.Default with
        {
            Preferences = UserPreferences.Default with
            {
                Dictation = DictationPreferences.Default with
                {
                    CancelGesture = "F8",
                },
            },
        };

        var error = AppSettingsValidator.Validate(settings, AppErrorStage.SettingsSave);

        Assert.Equal(AppErrorCode.InvalidData, error?.Code);
    }
}
