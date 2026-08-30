using System.Xml.Linq;
using EnviousWispr.Core.Input;
using EnviousWispr.Services.Input;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// The four gestures shipped, and on a fresh install nothing could reach them.
/// </summary>
/// <remarks>
/// HOLD, DOUBLE TAP, TAP AND TRIPLE TAP ARE ALL BUILT AND ALL TESTED, and every one needs a modifier
/// binding. The shipped default is F8, so the policy that implements them is never constructed and
/// three macOS features sit in the source unreachable from the product.
/// </remarks>
public sealed class HandsFreeGestureOfferTests
{
    [Fact]
    public void TheBindingTheButtonOffersIsOneTheAppCanActuallyAccept()
    {
        var parsed = HotkeyGestureParser.Parse(HandsFreeRecordBinding.Suggested);

        Assert.True(parsed.Succeeded);
        Assert.Equal(HotkeyModifiers.None, parsed.Gesture?.Modifiers);
        Assert.Equal("RightCtrl", parsed.Gesture?.Key);
    }

    [Fact]
    public void TheOfferedBindingSurvivesBeingWrittenDownAndReadBack()
    {
        // The button writes a string into a text box and the Save button parses it back. A binding
        // that does not round-trip would be offered, accepted and then silently be something else.
        var parsed = HotkeyGestureParser.Parse(HandsFreeRecordBinding.Suggested);

        Assert.Equal(HandsFreeRecordBinding.Suggested, parsed.Gesture?.ToString());
    }

    [Fact]
    public void TheOfferedKeyIsOneTheEngineTreatsAsAModifierBinding()
    {
        // This is the whole point of the offer. A key the edge tracker does not consider a modifier
        // builds no gesture policy, and the button would then change the keybind and deliver none of
        // the four gestures it promised.
        Assert.True(HotkeyEdgeTracker.IsModifierKey(0xA3));
    }

    [Fact]
    public void TheKeybindsPageOffersItAndSaysWhatItDoes()
    {
        var markup = XDocument.Load(Path.Combine(
            RepositoryRoot(), "src", "Production", "EnviousWispr.App", "MainWindow.xaml"));
        var names = markup.Descendants()
            .Select(element => (string?)element.Attribute(XName.Get("Name", XamlNamespace)))
            .Where(name => name is not null)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("HandsFreeGestureButton", names);
        Assert.Contains("HandsFreeHelperText", names);

        // The helper has to name the gestures. A button offering a keybind with no explanation is a
        // button that changes somebody's record key for reasons they cannot see.
        var helper = markup.Descendants()
            .First(element => (string?)element.Attribute(XName.Get("Name", XamlNamespace))
                == "HandsFreeHelperText");
        var text = (string?)helper.Attribute("Text") ?? string.Empty;
        Assert.Contains("hands-free", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("three times", text, StringComparison.OrdinalIgnoreCase);
    }

    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
            !File.Exists(Path.Combine(directory.FullName, "EnviousWispr.Windows.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
