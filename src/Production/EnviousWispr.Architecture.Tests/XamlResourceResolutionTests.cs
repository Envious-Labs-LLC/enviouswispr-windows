using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace EnviousWispr.Architecture.Tests;

public sealed partial class XamlResourceResolutionTests
{
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    // This is intentionally a small, explicit slice of the WinUI hierarchy. The check handles
    // exact matches plus the transitive parents listed here. It cannot prove compatibility for
    // custom controls or for any WinUI subtype relationship absent from this table.
    private static readonly Dictionary<string, string> KnownDirectBaseTypes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["RadioButton"] = "ToggleButton",
            ["CheckBox"] = "ToggleButton",
            ["ToggleButton"] = "ButtonBase",
            ["Button"] = "ButtonBase",
            ["RepeatButton"] = "ButtonBase",
            ["HyperlinkButton"] = "ButtonBase",
            ["ButtonBase"] = "ContentControl",
            ["ContentControl"] = "Control",
            ["Control"] = "FrameworkElement",
            ["Border"] = "FrameworkElement",
            ["TextBlock"] = "FrameworkElement",
        };

    // Reach: resource resolution covers only app-owned Brand* and Pill* keys. Style compatibility
    // covers only explicit Style="{StaticResource ...}" applications of those owned keyed styles.
    // It cannot validate custom-control inheritance, WinUI inheritance absent from the small table
    // above, platform-owned styles, implicit styles, or compatibility through BasedOn chains.
    [Fact]
    public void OwnedResourcesResolveAndOwnedStylesAreTypeCompatible()
    {
        var repositoryRoot = FindRepositoryRoot();
        var appDirectory = Path.Combine(
            repositoryRoot,
            "src",
            "Production",
            "EnviousWispr.App");
        var sources = LoadAppXamlSources(repositoryRoot, appDirectory);

        // A non-empty assertion would still pass if a bad exclusion left only a token file.
        // These two real views keep the scan's intended reach explicit.
        foreach (var required in new[] { "MainWindow.xaml", "DictationOverlayWindow.xaml" })
        {
            Assert.True(
                sources.Any(source => Path.GetFileName(source.Path).Equals(required, StringComparison.Ordinal)),
                $"'{required}' was not in the scanned set under '{appDirectory}'. The scan's scope is wrong, "
                    + "so a pass would prove nothing.");
        }

        var result = Analyze(sources);

        var failures = result.UnresolvedResources
            .Select(issue => $"unresolved resource: {issue}")
            .Concat(result.TypeMismatches.Select(issue => $"type mismatch: {issue}"))
            .ToArray();
        Assert.True(
            failures.Length == 0,
            $"Owned XAML resource validation failed:{Environment.NewLine}"
                + string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void ResolutionDetectorReportsBogusOwnedKeyButNotGoodOwnedKeys()
    {
        const string themeFixture = """
            <ResourceDictionary
                xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <SolidColorBrush x:Key="BrandThemeFixtureBrush" Color="#FFFFFF" />
            </ResourceDictionary>
            """;
        const string viewFixture = """
            <Window
                xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <Window.Resources>
                    <SolidColorBrush x:Key="PillLocalFixtureBrush" Color="#FFFFFF" />
                </Window.Resources>
                <StackPanel>
                    <Border Background="{ThemeResource BrandThemeFixtureBrush}" />
                    <Border Background="{StaticResource PillLocalFixtureBrush}" />
                    <Border Background="{ThemeResource BrandDoesNotExist}" />
                </StackPanel>
            </Window>
            """;

        // This permanent negative control uses the same parser and widened resolver as the
        // repository gate. It proves both theme and file-local keys resolve while the bogus key does not.
        var result = Analyze(
        [
            new XamlSource("Theme/NegativeControl.xaml", themeFixture, IsTheme: true),
            new XamlSource("Views/NegativeControl.xaml", viewFixture, IsTheme: false),
        ]);

        var unresolved = Assert.Single(result.UnresolvedResources);
        Assert.Contains("Views/NegativeControl.xaml:10", unresolved);
        Assert.Contains("BrandDoesNotExist", unresolved);
        Assert.DoesNotContain("BrandThemeFixtureBrush", unresolved);
        Assert.DoesNotContain("PillLocalFixtureBrush", unresolved);
    }

    [Fact]
    public void ElementLevelLocalResourceResolvesOnlyWithinItsOwnFile()
    {
        const string declaringView = """
            <UserControl
                xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <Grid>
                    <Grid.Resources>
                        <SolidColorBrush x:Key="BrandElementLocalBrush" Color="#FFFFFF" />
                    </Grid.Resources>
                    <Border Background="{StaticResource BrandElementLocalBrush}" />
                </Grid>
            </UserControl>
            """;
        const string otherView = """
            <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <Border Background="{StaticResource BrandElementLocalBrush}" />
            </Window>
            """;

        var result = Analyze(
        [
            new XamlSource("Views/DeclaringView.xaml", declaringView, IsTheme: false),
            new XamlSource("Views/OtherView.xaml", otherView, IsTheme: false),
        ]);

        var unresolved = Assert.Single(result.UnresolvedResources);
        Assert.Contains("Views/OtherView.xaml:2", unresolved);
        Assert.DoesNotContain("Views/DeclaringView.xaml", unresolved);
    }

    [Fact]
    public void TypeCompatibilityDetectorReportsIncompatibleOwnedStyleApplication()
    {
        const string fixture = """
            <ResourceDictionary
                xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <Style x:Key="BrandFixtureStyle" TargetType="Border" />
                <RadioButton Style="{StaticResource BrandFixtureStyle}" />
            </ResourceDictionary>
            """;

        var result = Analyze([new XamlSource("Theme/TypeNegativeControl.xaml", fixture, IsTheme: true)]);

        Assert.Empty(result.UnresolvedResources);
        var mismatch = Assert.Single(result.TypeMismatches);
        Assert.Contains("Theme/TypeNegativeControl.xaml:5", mismatch);
        Assert.Contains("RadioButton uses style 'BrandFixtureStyle' targeting Border", mismatch);
    }

    private static AnalysisResult Analyze(IReadOnlyCollection<XamlSource> sources)
    {
        var declaredThemeKeys = new HashSet<string>(StringComparer.Ordinal);
        var declaredLocalKeys = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var ownedStyles = new Dictionary<string, StyleDeclaration>(StringComparer.Ordinal);

        foreach (var source in sources)
        {
            var sourceKeys = new HashSet<string>(StringComparer.Ordinal);
            var document = XDocument.Parse(source.Content, LoadOptions.SetLineInfo);
            foreach (var element in document.Root?.DescendantsAndSelf() ?? Enumerable.Empty<XElement>())
            {
                var key = (string?)element.Attribute(XName.Get("Key", XamlNamespace));
                if (IsOwnedKey(key))
                {
                    sourceKeys.Add(key!);
                    if (source.IsTheme)
                    {
                        declaredThemeKeys.Add(key!);
                    }
                }

                if (element.Name.LocalName != "Style" || !IsOwnedKey(key))
                {
                    continue;
                }

                var targetType = NormalizeTypeName((string?)element.Attribute("TargetType"));
                if (string.IsNullOrEmpty(targetType))
                {
                    continue;
                }

                var declaration = new StyleDeclaration(targetType, source.Path, GetLineNumber(element));
                if (ownedStyles.TryGetValue(key!, out var existing)
                    && !existing.TargetType.Equals(targetType, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Owned style '{key}' has conflicting TargetTypes '{existing.TargetType}' and "
                            + $"'{targetType}' at {source.Path}:{declaration.Line}.");
                }

                ownedStyles[key!] = declaration;
            }

            declaredLocalKeys[source.Path] = sourceKeys;
        }

        var unresolvedResources = new List<string>();
        var typeMismatches = new List<string>();
        foreach (var source in sources)
        {
            foreach (var reference in ExtractResourceReferences(source))
            {
                // WinUI owns many platform keys. Naming those in an allow-list would rot, so this
                // assertion honestly covers only the app-owned Brand* and Pill* namespaces.
                if (IsOwnedKey(reference.Key)
                    && !declaredThemeKeys.Contains(reference.Key)
                    && !declaredLocalKeys[source.Path].Contains(reference.Key))
                {
                    unresolvedResources.Add(
                        $"{source.Path}:{reference.Line}: {reference.Kind} '{reference.Key}'");
                }
            }

            var document = XDocument.Parse(source.Content, LoadOptions.SetLineInfo);
            foreach (var element in document.Root?.DescendantsAndSelf() ?? Enumerable.Empty<XElement>())
            {
                var styleAttribute = element.Attribute("Style");
                if (styleAttribute is null)
                {
                    continue;
                }

                var match = ExactStaticResourceRegex().Match(styleAttribute.Value);
                if (!match.Success)
                {
                    continue;
                }

                var styleKey = match.Groups["key"].Value;
                // Platform styles such as AccentButtonStyle are outside our resource namespace and
                // cannot be resolved from the app's Theme folder without a brittle WinUI allow-list.
                if (!IsOwnedKey(styleKey))
                {
                    continue;
                }

                if (!ownedStyles.TryGetValue(styleKey, out var style))
                {
                    typeMismatches.Add(
                        $"{source.Path}:{GetLineNumber(styleAttribute)}: {element.Name.LocalName} uses owned "
                            + $"resource '{styleKey}' as a Style, but no Style declaration with a TargetType was found.");
                    continue;
                }

                var elementType = NormalizeTypeName(element.Name.LocalName);
                if (!IsTypeCompatible(elementType, style.TargetType))
                {
                    typeMismatches.Add(
                        $"{source.Path}:{GetLineNumber(styleAttribute)}: {elementType} uses style "
                            + $"'{styleKey}' targeting {style.TargetType} "
                            + $"(declared at {style.Path}:{style.Line})");
                }
            }
        }

        return new AnalysisResult(unresolvedResources, typeMismatches);
    }

    private static List<ResourceReference> ExtractResourceReferences(XamlSource source)
    {
        var contentWithoutComments = XmlCommentRegex().Replace(
            source.Content,
            match => NewlinePreservingWhitespace(match.Value));
        var references = new List<ResourceReference>();

        foreach (Match match in ResourceReferenceRegex().Matches(contentWithoutComments))
        {
            references.Add(new ResourceReference(
                match.Groups["kind"].Value,
                match.Groups["key"].Value,
                GetLineNumber(contentWithoutComments, match.Index)));
        }

        return references;
    }

    private static XamlSource[] LoadAppXamlSources(string repositoryRoot, string appDirectory)
    {
        var themeDirectory = Path.Combine(appDirectory, "Theme") + Path.DirectorySeparatorChar;
        return Directory.GetFiles(appDirectory, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(appDirectory, path))
            .Order(StringComparer.Ordinal)
            .Select(path => new XamlSource(
                Path.GetRelativePath(repositoryRoot, path),
                File.ReadAllText(path),
                Path.GetFullPath(path).StartsWith(themeDirectory, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    private static bool IsBuildOutput(string appDirectory, string path)
    {
        var relative = Path.GetRelativePath(appDirectory, Path.GetFullPath(path));
        return relative
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment =>
                segment.Equals("obj", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("bin", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsOwnedKey(string? key) =>
        key is not null
        && (key.StartsWith("Brand", StringComparison.Ordinal)
            || key.StartsWith("Pill", StringComparison.Ordinal));

    private static bool IsTypeCompatible(string elementType, string targetType)
    {
        for (var candidate = elementType; ;)
        {
            if (candidate.Equals(targetType, StringComparison.Ordinal))
            {
                return true;
            }

            if (!KnownDirectBaseTypes.TryGetValue(candidate, out var parent))
            {
                return false;
            }

            candidate = parent;
        }
    }

    private static string NormalizeTypeName(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return string.Empty;
        }

        var trimmed = typeName.Trim();
        var colon = trimmed.LastIndexOf(':');
        return colon >= 0 ? trimmed[(colon + 1)..] : trimmed;
    }

    private static int GetLineNumber(XObject node) =>
        node is IXmlLineInfo lineInfo && lineInfo.HasLineInfo() ? lineInfo.LineNumber : 1;

    private static int GetLineNumber(string content, int position)
    {
        var line = 1;
        for (var index = 0; index < position; index++)
        {
            if (content[index] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    private static string NewlinePreservingWhitespace(string value) =>
        new string(value.Select(character => character is '\r' or '\n' ? character : ' ').ToArray());

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
            $"Could not locate the repository root by walking up from test assembly directory '{AppContext.BaseDirectory}'. "
                + "Expected to find EnviousWispr.Windows.slnx.");
    }

    [GeneratedRegex(
        @"\{\s*(?<kind>ThemeResource|StaticResource)\s+(?<key>[^\s,}]+)\s*\}",
        RegexOptions.CultureInvariant)]
    private static partial Regex ResourceReferenceRegex();

    [GeneratedRegex(
        @"^\{\s*StaticResource\s+(?<key>[^\s,}]+)\s*\}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ExactStaticResourceRegex();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex XmlCommentRegex();

    private sealed record XamlSource(string Path, string Content, bool IsTheme);

    private sealed record StyleDeclaration(string TargetType, string Path, int Line);

    private sealed record ResourceReference(string Kind, string Key, int Line);

    private sealed record AnalysisResult(
        IReadOnlyList<string> UnresolvedResources,
        IReadOnlyList<string> TypeMismatches);
}
