using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace EnviousWispr.Architecture.Tests;

/// <summary>A brand override of a WinUI resource must be the TYPE WinUI's own template uses it as.</summary>
/// <remarks>
/// A BRUSH WHERE THE TEMPLATE ANIMATES A COLOUR IS IGNORED WITHOUT A WORD. WinUI's ToggleSwitch animates the off
/// track's hover, pressed and disabled looks with LinearColorKeyFrame, so those six resources must be Colors. The
/// window overrode them as SolidColorBrushes; the build, the XAML compiler and every other gate were content, and a
/// disabled off switch drew no track at all - a bare grey dot - until somebody looked at the Diagnostics page
/// (2026-09-25). Nothing that reads the app's own XAML can see this: the rule lives in WinUI's template. So this reads
/// THAT template - the generic.xaml of the exact WinUI package this build restored, found through the build's own
/// package record rather than a path written down - and refuses any brush override of a key it animates as a colour.
/// </remarks>
public sealed class WinUiResourceTypeTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void NoBrandOverrideIsABrushWhereWinUiAnimatesAColour()
    {
        var root = FindRepositoryRoot();
        var app = Path.Combine(root, "src", "Production", "EnviousWispr.App");
        var colourKeys = ColourAnimatedKeys(WinUiGenericXaml(app));

        // THE INSTRUMENT CAN SEE: the keys this defect was found on are in the set, so an empty or wrong template
        // cannot pass this gate by finding nothing to compare against.
        foreach (var known in new[] { "ToggleSwitchFillOffPointerOver", "ToggleSwitchStrokeOffPointerOver", "ToggleSwitchFillOffPressed",
                     "ToggleSwitchStrokeOffPressed", "ToggleSwitchFillOffDisabled", "ToggleSwitchStrokeOffDisabled" })
        {
            Assert.Contains(known, colourKeys);
        }

        var offenders = Directory.EnumerateFiles(app, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                           !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(path => XDocument.Load(path).Descendants(Presentation + "SolidColorBrush")
                .Select(brush => (string?)brush.Attribute(Xaml + "Key"))
                .OfType<string>()
                .Where(colourKeys.Contains)
                .Select(key => $"{Path.GetFileName(path)}: {key}"))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "These override a resource WinUI's template animates as a COLOUR with a brush, which WinUI ignores silently; "
                + "define them as Colors (StaticResource of a Brand*Color token in the theme dictionaries): "
                + string.Join(", ", offenders));
    }

    /// <summary>A Color whose element text is a markup extension is not a reference, it is an unparseable colour.</summary>
    /// <remarks>
    /// `<Color x:Key="X">{ThemeResource SystemColorWindowColor}</Color>` reads like a reference and is not one: XAML hands
    /// element text to the colour converter, which knows no colour by that name. Every High Contrast colour token in
    /// DesignTokens.xaml (27) and PillTokens.xaml (14) was written this way, so High Contrast resolved none of them
    /// (#217). An alias is `<StaticResource x:Key="X" ResourceKey="SystemColorWindowColor" />`, as WinUI's own theme does.
    /// </remarks>
    [Fact]
    public void NoColourIsAResourceReferenceWrittenAsText()
    {
        var app = Path.Combine(FindRepositoryRoot(), "src", "Production", "EnviousWispr.App");
        var offenders = Directory.EnumerateFiles(app, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                           !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(path => XDocument.Load(path).Descendants(Presentation + "Color")
                .Where(colour => colour.Value.TrimStart().StartsWith('{'))
                .Select(colour => $"{Path.GetFileName(path)}: {(string?)colour.Attribute(Xaml + "Key")} = {colour.Value.Trim()}"))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "These colours are a resource reference written as element text, which XAML parses as a colour name and "
                + "cannot resolve; alias them with <StaticResource x:Key=... ResourceKey=... /> instead: "
                + string.Join(", ", offenders));
    }

    /// <summary>Every resource WinUI's template names as the value of a colour animation or colour key frame.</summary>
    private static HashSet<string> ColourAnimatedKeys(string genericXaml)
    {
        var text = File.ReadAllText(genericXaml);
        return Regex.Matches(
                text,
                @"<(?:LinearColorKeyFrame|DiscreteColorKeyFrame|EasingColorKeyFrame|SplineColorKeyFrame|ColorAnimation)\b[^>]*\{ThemeResource\s+(\w+)\}")
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The generic.xaml of the WinUI package the app restored: its version and folder come from obj/project.assets.json,
    /// which the restore writes on every machine, CI included.
    /// </summary>
    private static string WinUiGenericXaml(string app)
    {
        var assets = Path.Combine(app, "obj", "project.assets.json");
        Assert.True(File.Exists(assets), $"The app has not been restored: {assets} is missing.");
        using var document = JsonDocument.Parse(File.ReadAllText(assets));
        var library = document.RootElement.GetProperty("libraries").EnumerateObject()
            .First(entry => entry.Name.StartsWith("Microsoft.WindowsAppSDK.WinUI/", StringComparison.Ordinal));
        var relative = library.Value.GetProperty("path").GetString()!;
        foreach (var folder in document.RootElement.GetProperty("packageFolders").EnumerateObject())
        {
            var candidate = Path.Combine(folder.Name, relative, "lib", "native", "Microsoft.UI", "Themes", "generic.xaml");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        Assert.Fail($"WinUI's generic.xaml for {library.Name} was not found in any restored package folder.");
        return string.Empty;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CLAUDE.md")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
