using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace EnviousWispr.Architecture.Tests;

public sealed partial class DesignSystemTokenTests
{
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly string[] TokenNames =
    [
        "BrandPageBg",
        "BrandCardBg",
        "BrandSidebarBg",
        "BrandWindowBg",
        "BrandTextPrimary",
        "BrandTextBody",
        "BrandTextSecondary",
        "BrandTextTertiary",
        "BrandAccent",
        "BrandAccentSolid",
        "BrandAccentLight",
        "BrandSuccess",
        "BrandWarning",
        "BrandWarningSoft",
        "BrandError",
        "BrandToggleOn",
        "BrandToggleOff",
        "BrandDivider",
        "BrandSpectrumRed",
        "BrandSpectrumOrange",
        "BrandSpectrumGold",
        "BrandSpectrumLime",
        "BrandSpectrumSpring",
        "BrandSpectrumCyan",
        "BrandSpectrumBlue",
        "BrandSpectrumRoyal",
        "BrandSpectrumViolet",
    ];

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
            foreach (var tokenName in TokenNames)
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
    public void LightAndDarkTokensMatchMacDynamicColors()
    {
        var repositoryRoot = FindRepositoryRoot();
        var swiftPath = Path.Combine(
            repositoryRoot,
            "macos-source",
            "Sources",
            "EnviousWisprAppKit",
            "Views",
            "Settings",
            "SettingsDesignTokens.swift");
        Assert.True(
            File.Exists(swiftPath),
            $"The authoritative Swift token file was not found at '{swiftPath}'.");

        var swiftTokens = ParseSwiftTokens(File.ReadAllText(swiftPath));
        var themeDictionaries = LoadThemeDictionaries(repositoryRoot);
        foreach (var mapping in SwiftTokenMap)
        {
            if (!swiftTokens.TryGetValue(mapping.SwiftName, out var expected))
            {
                throw new InvalidOperationException(
                    $"Mapped Swift token '{mapping.SwiftName}' for '{mapping.BrandName}' was not found in '{swiftPath}'.");
            }

            AssertColorMatches(themeDictionaries, "Light", mapping.BrandName, expected.Light);
            AssertColorMatches(themeDictionaries, "Dark", mapping.BrandName, expected.Dark);
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
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(xamlFiles.Length > 0, $"No app XAML files were found under '{appDirectory}'.");

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
        IReadOnlyDictionary<string, XElement> themeDictionaries,
        string themeName,
        string brandName,
        Rgba expected)
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

        AssertChannelsMatch(
            themeName,
            resourceKey,
            element.Value.Trim(),
            expected);

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
            (string?)brush.Attribute("Color") ?? string.Empty,
            expected);
    }

    private static void AssertChannelsMatch(
        string themeName,
        string resourceKey,
        string value,
        Rgba expected)
    {
        var actual = ParseXamlColor(resourceKey, themeName, value);
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
                $"{themeName}/{resourceKey} {channel.Name} differs from the Swift source. " +
                $"Expected {channel.Expected.ToString("0.###", CultureInfo.InvariantCulture)}, " +
                $"found {channel.Actual.ToString("0.###", CultureInfo.InvariantCulture)} ({value}).");
        }
    }

    private static Rgba ParseXamlColor(string resourceKey, string themeName, string value)
    {
        if (!value.StartsWith('#') || value.Length is not (7 or 9))
        {
            throw new InvalidOperationException(
                $"{themeName}/{resourceKey} must be a #RRGGBB or #AARRGGBB literal in DesignTokens.xaml, found '{value}'.");
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

    private readonly record struct Rgba(double Red, double Green, double Blue, double Alpha);
}
