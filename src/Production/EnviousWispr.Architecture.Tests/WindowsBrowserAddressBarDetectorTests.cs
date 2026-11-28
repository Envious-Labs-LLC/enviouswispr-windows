using EnviousWispr.Services.Input;
using Xunit;

namespace EnviousWispr.Architecture.Tests;

public sealed class WindowsBrowserAddressBarDetectorTests
{
    [Theory]
    [InlineData("chrome", true)]
    [InlineData("chrome.exe", true)]
    [InlineData("msedge", true)]
    [InlineData("MSEdge.exe", true)]
    [InlineData("brave", true)]
    [InlineData("vivaldi", true)]
    [InlineData("opera", true)]
    [InlineData("chromium", true)]
    [InlineData("msedgewebview2", false)]
    [InlineData("Discord", false)]
    [InlineData("Code", false)]
    [InlineData("firefox", false)]
    [InlineData("notepad", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void RecognizedChromiumBrowserProcessAllowlistMatchesExpectedProcessesOnly(
        string? processName,
        bool expected)
    {
        Assert.Equal(expected, WindowsBrowserAddressBarDetector.IsRecognizedChromiumBrowserProcess(processName));
    }

    [Fact]
    public void ChromiumOmniboxOutsideRootWebAreaIsAcceptedAcrossVendors()
    {
        var ancestors = new WindowsBrowserAddressBarDetector.AncestorFacts[]
        {
            new("Pane", "LocationBarView", "location-bar"),
            new("Pane", "ToolbarView", "toolbar"),
            new("Window", "Chrome_WidgetWin_1", null),
        };

        Assert.True(WindowsBrowserAddressBarDetector.MatchesChromiumOmniboxSignature(
            "chrome",
            isEditControl: true,
            "OmniboxViewViews",
            ancestors));
        Assert.True(WindowsBrowserAddressBarDetector.MatchesChromiumOmniboxSignature(
            "msedge.exe",
            isEditControl: true,
            "OmniboxViewViews",
            ancestors));
        Assert.True(WindowsBrowserAddressBarDetector.MatchesChromiumOmniboxSignature(
            "brave",
            isEditControl: true,
            "BraveOmniboxViewViews",
            ancestors));
    }

    [Fact]
    public void WebPageShippingAddressInputInsideRootWebAreaIsRejected()
    {
        // Gate 0 failure on main: <input id="shipping-address"> matched "address" substring hacks.
        var webFormAncestors = new WindowsBrowserAddressBarDetector.AncestorFacts[]
        {
            new("Group", string.Empty, "checkout-form"),
            new("Document", "RootWebArea", "RootWebArea"),
            new("Pane", "Chrome_WidgetWin_1", null),
        };

        Assert.False(WindowsBrowserAddressBarDetector.MatchesChromiumOmniboxSignature(
            "chrome",
            isEditControl: true,
            className: string.Empty,
            webFormAncestors));
        Assert.False(WindowsBrowserAddressBarDetector.MatchesChromiumOmniboxSignature(
            "msedge",
            isEditControl: true,
            className: "OmniboxViewViews",
            webFormAncestors));
    }

    [Fact]
    public void AncestorWalkExceedsDepthTenSoDeepDomOrElectronWebAreasAreStillRejected()
    {
        Assert.True(WindowsBrowserAddressBarDetector.MaxAncestorWalkDepth >= 45);

        var deepWebAncestors = new List<WindowsBrowserAddressBarDetector.AncestorFacts>();
        for (var i = 0; i < 36; i++)
        {
            deepWebAncestors.Add(new("Group", "div", $"node-{i}"));
        }

        deepWebAncestors.Add(new("Document", "RootWebArea", "RootWebArea"));

        Assert.False(WindowsBrowserAddressBarDetector.MatchesChromiumOmniboxSignature(
            "chrome",
            isEditControl: true,
            className: "OmniboxViewViews",
            deepWebAncestors));
    }

    [Fact]
    public void NonEditControlAndAriaDocumentAncestorAreRejected()
    {
        Assert.False(WindowsBrowserAddressBarDetector.MatchesChromiumOmniboxSignature(
            "chrome",
            isEditControl: false,
            className: "OmniboxViewViews",
            hasRootWebAreaAncestor: false));

        var ariaDocumentAncestors = new WindowsBrowserAddressBarDetector.AncestorFacts[]
        {
            new(null, null, null, AriaRole: "document"),
        };

        Assert.False(WindowsBrowserAddressBarDetector.MatchesChromiumOmniboxSignature(
            "msedge",
            isEditControl: true,
            className: "OmniboxViewViews",
            ariaDocumentAncestors));
    }
}
