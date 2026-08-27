using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace EnviousWispr.Architecture.Tests;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class MacSnapshotFactAttribute : FactAttribute
{
    public MacSnapshotFactAttribute()
    {
        var swiftPath = DesignSystemTokenTests.GetMacSnapshotPath();
        if (!File.Exists(swiftPath))
        {
            Skip = $"macOS snapshot not found at '{swiftPath}'; the macOS-parity half did not run.";
        }
    }
}

public sealed partial class DesignSystemTokenTests
{
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly Dictionary<string, ThemePair> ExpectedTokenColors = new(StringComparer.Ordinal)
    {
        ["BrandPageBg"] = new("#F8F5FF", "#131019"),
        ["BrandCardBg"] = new("#FFFFFF", "#201B2B"),
        ["BrandSidebarBg"] = new("#E8E2F5", "#1A1623"),
        ["BrandWindowBg"] = new("#DDD5EE", "#0D0B12"),
        ["BrandTextPrimary"] = new("#0F0A1A", "#ECE9F4"),
        ["BrandTextBody"] = new("#332D47", "#D5D1E2"),
        ["BrandTextSecondary"] = new("#4A3D60", "#AAA2BF"),
        ["BrandTextTertiary"] = new("#6B5E86", "#7A7290"),
        ["BrandAccent"] = new("#7C3AED", "#A78BFA"),
        ["BrandAccentSolid"] = new("#7C3AED", "#8B46F0"),
        ["BrandAccentLight"] = new("#177C3AED", "#29A78BFA"),
        ["BrandSuccess"] = new("#00A366", "#5CC99A"),
        ["BrandWarning"] = new("#CC7000", "#E6B766"),
        ["BrandWarningSoft"] = new("#1ACC7000", "#24E6B766"),
        ["BrandError"] = new("#C0392B", "#EF7C89"),
        ["BrandToggleOn"] = new("#00A366", "#5CC99A"),
        ["BrandToggleOff"] = new("#9B8EB8", "#4A4360"),
        ["BrandDivider"] = new("#148A2BE2", "#24B8AAD6"),
        ["BrandSpectrumRed"] = new("#FF2A40", "#FF2A40"),
        ["BrandSpectrumOrange"] = new("#FF8C00", "#FF8C00"),
        ["BrandSpectrumGold"] = new("#FFD700", "#FFD700"),
        ["BrandSpectrumLime"] = new("#ADFF2F", "#ADFF2F"),
        ["BrandSpectrumSpring"] = new("#00FA9A", "#00FA9A"),
        ["BrandSpectrumCyan"] = new("#00FFFF", "#00FFFF"),
        ["BrandSpectrumBlue"] = new("#1E90FF", "#1E90FF"),
        ["BrandSpectrumRoyal"] = new("#4169E1", "#4169E1"),
        ["BrandSpectrumViolet"] = new("#8A2BE2", "#8A2BE2"),
    };

    private static readonly (string SwiftName, string BrandName)[] SwiftTokenMap =
    [
        ("stPageBg", "BrandPageBg"),
        ("stSectionBg", "BrandCardBg"),
        ("stSidebarBg", "BrandSidebarBg"),
        ("stWindowBg", "BrandWindowBg"),
        ("stTextPrimary", "BrandTextPrimary"),
        ("stTextBody", "BrandTextBody"),
        ("stTextSecondary", "BrandTextSecondary"),
        ("stTextTertiary", "BrandTextTertiary"),
        ("stAccent", "BrandAccent"),
        ("stAccentSolid", "BrandAccentSolid"),
        ("stAccentLight", "BrandAccentLight"),
        ("stSuccess", "BrandSuccess"),
        ("stWarning", "BrandWarning"),
        ("stWarningSoft", "BrandWarningSoft"),
        ("stError", "BrandError"),
        ("stToggleOn", "BrandToggleOn"),
        ("stToggleOff", "BrandToggleOff"),
        ("stDivider", "BrandDivider"),
    ];

    [Fact]
    public void EveryThemeDeclaresEveryColorAndBrushToken()
    {
        var repositoryRoot = FindRepositoryRoot();
        var themeDictionaries = LoadThemeDictionaries(repositoryRoot);
        var missing = new List<string>();

        foreach (var themeName in new[] { "Light", "Dark", "HighContrast" })
        {
            if (!themeDictionaries.TryGetValue(themeName, out var theme))
            {
                missing.Add($"theme dictionary '{themeName}'");
                continue;
            }

            var keys = theme.Elements()
                .Select(element => (string?)element.Attribute(XName.Get("Key", XamlNamespace)))
                .Where(key => key is not null)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var tokenName in ExpectedTokenColors.Keys)
            {
                foreach (var suffix in new[] { "Color", "Brush" })
                {
                    var resourceKey = tokenName + suffix;
                    if (!keys.Contains(resourceKey))
                    {
                        missing.Add($"{themeName}/{resourceKey}");
                    }
                }
            }
        }

        Assert.True(
            missing.Count == 0,
            $"Design token resources are missing: {string.Join(", ", missing)}");
    }

    [Fact]
    public void LightAndDarkTokensMatchExpectedTable()
    {
        var repositoryRoot = FindRepositoryRoot();
        var themeDictionaries = LoadThemeDictionaries(repositoryRoot);
        foreach (var token in ExpectedTokenColors)
        {
            AssertColorMatches(themeDictionaries, "Light", token.Key, token.Value.Light);
            AssertColorMatches(themeDictionaries, "Dark", token.Key, token.Value.Dark);
        }
    }

    [MacSnapshotFact]
    public void MacDynamicColorsMatchExpectedTable()
    {
        var swiftPath = GetMacSnapshotPath();
        Assert.True(
            File.Exists(swiftPath),
            $"The macOS snapshot disappeared after test discovery at '{swiftPath}'.");

        var swiftTokens = ParseSwiftTokens(File.ReadAllText(swiftPath));
        foreach (var mapping in SwiftTokenMap)
        {
            if (!swiftTokens.TryGetValue(mapping.SwiftName, out var actual))
            {
                throw new InvalidOperationException(
                    $"Mapped Swift token '{mapping.SwiftName}' for '{mapping.BrandName}' was not found in '{swiftPath}'.");
            }

            if (!ExpectedTokenColors.TryGetValue(mapping.BrandName, out var expected))
            {
                throw new InvalidOperationException(
                    $"Mapped Brand token '{mapping.BrandName}' for '{mapping.SwiftName}' is missing from the expected table.");
            }

            AssertChannelsMatch(
                "macOS Light snapshot",
                mapping.BrandName,
                ParseHexColor(mapping.BrandName, "expected Light table", expected.Light),
                actual.Light,
                mapping.SwiftName);
            AssertChannelsMatch(
                "macOS Dark snapshot",
                mapping.BrandName,
                ParseHexColor(mapping.BrandName, "expected Dark table", expected.Dark),
                actual.Dark,
                mapping.SwiftName);
        }
    }

    [Fact]
    public void AppViewsContainNoLiteralColors()
    {
        var repositoryRoot = FindRepositoryRoot();
        var appDirectory = Path.Combine(repositoryRoot, "src", "Production", "EnviousWispr.App");
        var themeDirectory = Path.Combine(appDirectory, "Theme") + Path.DirectorySeparatorChar;
        var xamlFiles = Directory.GetFiles(appDirectory, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !Path.GetFullPath(path).StartsWith(themeDirectory, StringComparison.OrdinalIgnoreCase))
            .Where(path => !IsBuildOutput(appDirectory, path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        // A shrinking scope is how this check quietly stops checking anything, so
        // require the two views by name rather than merely requiring a non-empty
        // set: an exclusion that swallowed everything would still satisfy a count.
        foreach (var required in new[] { "MainWindow.xaml", "DictationOverlayWindow.xaml" })
        {
            Assert.True(
                xamlFiles.Any(path => Path.GetFileName(path).Equals(required, StringComparison.Ordinal)),
                $"'{required}' was not in the scanned set under '{appDirectory}'. The scan's scope is wrong, "
                    + "so a pass would prove nothing.");
        }

        var offenders = new List<string>();
        foreach (var path in xamlFiles)
        {
            var lines = File.ReadAllLines(path);
            for (var index = 0; index < lines.Length; index++)
            {
                foreach (Match match in HexColorRegex().Matches(lines[index]))
                {
                    offenders.Add(DescribeOffender(repositoryRoot, path, index + 1, match.Value));
                }

                foreach (Match match in NamedColorAttributeRegex().Matches(lines[index]))
                {
                    offenders.Add(DescribeOffender(repositoryRoot, path, index + 1, match.Value));
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"Literal colors must use a ThemeResource token:{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}");
    }

    /// <summary>
    /// True when the path sits under a build-output directory.
    /// </summary>
    /// <remarks>
    /// The compiler copies every Page-compiled XAML, <c>Theme/</c> included, into
    /// <c>obj/</c> and <c>bin/</c>. Those copies are the token dictionary itself, so they are
    /// full of the literal colours this check exists to forbid everywhere else. They are also
    /// invisible on a machine that has never built, which is why this was not caught until the
    /// suite ran on Windows.
    /// </remarks>
    private static bool IsBuildOutput(string appDirectory, string path)
    {
        var relative = Path.GetRelativePath(appDirectory, Path.GetFullPath(path));
        return relative
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment =>
                segment.Equals("obj", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("bin", StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<string, XElement> LoadThemeDictionaries(string repositoryRoot)
    {
        var tokenPath = Path.Combine(
            repositoryRoot,
            "src",
            "Production",
            "EnviousWispr.App",
            "Theme",
            "DesignTokens.xaml");
        Assert.True(File.Exists(tokenPath), $"DesignTokens.xaml was not found at '{tokenPath}'.");

        var document = XDocument.Load(tokenPath);
        var container = document.Descendants()
            .SingleOrDefault(element => element.Name.LocalName == "ResourceDictionary.ThemeDictionaries");
        Assert.NotNull(container);
        return container.Elements()
            .Where(element => element.Name.LocalName == "ResourceDictionary")
            .ToDictionary(
                element => (string?)element.Attribute(XName.Get("Key", XamlNamespace)) ?? string.Empty,
                StringComparer.Ordinal);
    }

    private static Dictionary<string, DynamicColor> ParseSwiftTokens(string source)
    {
        var result = new Dictionary<string, DynamicColor>(StringComparer.Ordinal);
        foreach (Match match in SwiftDynamicColorRegex().Matches(source))
        {
            var name = match.Groups["name"].Value;
            if (!result.TryAdd(
                    name,
                    new DynamicColor(
                        ParseSwiftComponents(name, "light", match.Groups["light"].Value),
                        ParseSwiftComponents(name, "dark", match.Groups["dark"].Value))))
            {
                throw new InvalidOperationException($"Swift token '{name}' is declared more than once.");
            }
        }

        Assert.True(result.Count > 0, "No stDynamic color declarations were parsed from the Swift token file.");
        return result;
    }

    private static Rgba ParseSwiftComponents(string tokenName, string themeName, string value)
    {
        var components = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (components.Length != 4 ||
            !components.All(component => double.TryParse(
                component,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out _)))
        {
            throw new InvalidOperationException(
                $"Swift token '{tokenName}' has an invalid {themeName} RGBA tuple: '{value}'.");
        }

        var parsed = components
            .Select(component => double.Parse(component, CultureInfo.InvariantCulture))
            .ToArray();
        return new Rgba(parsed[0], parsed[1], parsed[2], parsed[3]);
    }

    private static void AssertColorMatches(
        Dictionary<string, XElement> themeDictionaries,
        string themeName,
        string brandName,
        string expectedHex)
    {
        if (!themeDictionaries.TryGetValue(themeName, out var theme))
        {
            throw new InvalidOperationException($"Theme dictionary '{themeName}' is missing.");
        }

        var resourceKey = brandName + "Color";
        var element = theme.Elements().SingleOrDefault(candidate =>
            candidate.Name.LocalName == "Color" &&
            string.Equals(
                (string?)candidate.Attribute(XName.Get("Key", XamlNamespace)),
                resourceKey,
                StringComparison.Ordinal));
        if (element is null)
        {
            throw new InvalidOperationException($"Color resource '{resourceKey}' is missing from theme '{themeName}'.");
        }

        var expected = ParseHexColor(resourceKey, $"expected {themeName} table", expectedHex);
        AssertChannelsMatch(
            themeName,
            resourceKey,
            expected,
            ParseHexColor(resourceKey, themeName, element.Value.Trim()),
            element.Value.Trim());

        var brushKey = brandName + "Brush";
        var brush = theme.Elements().SingleOrDefault(candidate =>
            candidate.Name.LocalName == "SolidColorBrush" &&
            string.Equals(
                (string?)candidate.Attribute(XName.Get("Key", XamlNamespace)),
                brushKey,
                StringComparison.Ordinal));
        if (brush is null)
        {
            throw new InvalidOperationException($"Brush resource '{brushKey}' is missing from theme '{themeName}'.");
        }

        AssertChannelsMatch(
            themeName,
            brushKey,
            expected,
            ParseHexColor(brushKey, themeName, (string?)brush.Attribute("Color") ?? string.Empty),
            (string?)brush.Attribute("Color") ?? string.Empty);
    }

    private static void AssertChannelsMatch(
        string scope,
        string resourceKey,
        Rgba expected,
        Rgba actual,
        string actualDescription)
    {
        var tolerance = 1d / 255d;
        var channels = new[]
        {
            (Name: "red", Expected: expected.Red, Actual: actual.Red),
            (Name: "green", Expected: expected.Green, Actual: actual.Green),
            (Name: "blue", Expected: expected.Blue, Actual: actual.Blue),
            (Name: "alpha", Expected: expected.Alpha, Actual: actual.Alpha),
        };
        foreach (var channel in channels)
        {
            Assert.True(
                Math.Abs(channel.Expected - channel.Actual) <= tolerance + double.Epsilon,
                $"{scope}/{resourceKey} {channel.Name} differs from the expected table. " +
                $"Expected {channel.Expected.ToString("0.###", CultureInfo.InvariantCulture)}, " +
                $"found {channel.Actual.ToString("0.###", CultureInfo.InvariantCulture)} ({actualDescription}).");
        }
    }

    private static Rgba ParseHexColor(string resourceKey, string sourceName, string value)
    {
        if (!value.StartsWith('#') || value.Length is not (7 or 9))
        {
            throw new InvalidOperationException(
                $"{sourceName}/{resourceKey} must be a #RRGGBB or #AARRGGBB literal, found '{value}'.");
        }

        var offset = value.Length == 9 ? 3 : 1;
        var alpha = value.Length == 9 ? ParseByte(value, 1) : byte.MaxValue;
        return new Rgba(
            ParseByte(value, offset) / 255d,
            ParseByte(value, offset + 2) / 255d,
            ParseByte(value, offset + 4) / 255d,
            alpha / 255d);
    }

    private static byte ParseByte(string value, int offset)
    {
        if (!byte.TryParse(
                value.AsSpan(offset, 2),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var component))
        {
            throw new InvalidOperationException($"Invalid XAML color literal '{value}'.");
        }

        return component;
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "EnviousWispr.Windows.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate the repository root by walking up from test assembly directory '{AppContext.BaseDirectory}'. " +
            "Expected to find EnviousWispr.Windows.slnx.");
    }

    internal static string GetMacSnapshotPath() =>
        Path.Combine(
            FindRepositoryRoot(),
            "macos-source",
            "Sources",
            "EnviousWisprAppKit",
            "Views",
            "Settings",
            "SettingsDesignTokens.swift");

    private static string DescribeOffender(string repositoryRoot, string path, int line, string value) =>
        $"{Path.GetRelativePath(repositoryRoot, path)}:{line}: {value}";

    [GeneratedRegex(
        @"(?<![0-9A-Fa-f])#(?:[0-9A-Fa-f]{8}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{4}|[0-9A-Fa-f]{3})(?![0-9A-Fa-f])",
        RegexOptions.CultureInvariant)]
    private static partial Regex HexColorRegex();

    [GeneratedRegex(
        @"\b(?:Brush|Background|Foreground|Fill|Stroke|BorderBrush)\s*=\s*[""'](?<color>[A-Za-z]+)[""']",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex NamedColorAttributeRegex();

    [GeneratedRegex(
        @"static\s+let\s+(?<name>st\w+)\s*=\s*stDynamic\(\s*lightRGB:\s*\((?<light>[^)]*)\),\s*darkRGB:\s*\((?<dark>[^)]*)\)\s*\)",
        RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex SwiftDynamicColorRegex();

    private readonly record struct DynamicColor(Rgba Light, Rgba Dark);

    private readonly record struct ThemePair(string Light, string Dark);

    private readonly record struct Rgba(double Red, double Green, double Blue, double Alpha);
}
