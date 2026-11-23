using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// Dynamic controls created in MainWindow partial classes must have explicit UIA names,
/// keyboard operability, and screen-reader announcements.
/// </summary>
/// <remarks>
/// ACCESSIBILITY DEFECTS IN DYNAMICALLY CREATED CONTROLS ARE INVISIBLE TO XAML SCANS.
/// XAML linting only inspects declarative markup; dynamically instantiated cards and
/// buttons (such as chime selection cards and play buttons) exist only in C# code.
/// Without explicit architecture tests, missing AutomationProperties.Name or omitted
/// keyboard handlers pass standard CI unnoticed until an interactive screen-reader or
/// UIA audit fails.
/// </remarks>
public sealed class DynamicControlAccessibilityTests
{
    [Fact]
    public void EveryDynamicChimePlayButtonHasExplicitAutomationName()
    {
        var root = ParseMainWindowCode();
        var method = FindMethod(root, "InitializeChimeCards");
        Assert.NotNull(method);

        var code = method.ToString();

        // The play button must receive an explicit AutomationProperties.SetName call.
        Assert.Contains("AutomationProperties.SetName(playButton", code, StringComparison.Ordinal);
        Assert.Contains("Preview ", code, StringComparison.Ordinal);
        Assert.Contains(" sound", code, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryDynamicChimeCardIsKeyboardOperableAndAccessible()
    {
        var root = ParseMainWindowCode();
        var method = FindMethod(root, "InitializeChimeCards");
        Assert.NotNull(method);

        var code = method.ToString();

        // The selectable card must be a peer-backed Button control for full UIA and keyboard operability.
        Assert.Contains("var selectButton = new Button", code, StringComparison.Ordinal);

        // The selection button must have an explicit UIA Name and HelpText.
        Assert.Contains("AutomationProperties.SetName(selectButton", code, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetHelpText(selectButton", code, StringComparison.Ordinal);

        // The selection button must have click / activation handling.
        Assert.Contains("selectButton.Click +=", code, StringComparison.Ordinal);

        // The selection button must track focus states for visual feedback on the outer card.
        Assert.Contains("selectButton.GotFocus +=", code, StringComparison.Ordinal);
        Assert.Contains("selectButton.LostFocus +=", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ChimeCardSelectionStateAnnouncedToAutomation()
    {
        var root = ParseMainWindowCode();

        // Visual updater must set ItemStatus on the peer-backed selection button to Selected / Not selected.
        var updateMethod = FindMethod(root, "UpdateChimeCardsVisuals");
        Assert.NotNull(updateMethod);
        var updateCode = updateMethod.ToString();
        Assert.Contains("AutomationProperties.SetItemStatus(", updateCode, StringComparison.Ordinal);
        Assert.Contains("\"Selected\"", updateCode, StringComparison.Ordinal);
        Assert.Contains("\"Not selected\"", updateCode, StringComparison.Ordinal);

        // Selection handler must raise UIA notification for screen readers through the peer-backed selection button.
        var selectMethod = FindMethod(root, "SelectChimePairing");
        Assert.NotNull(selectMethod);
        var selectCode = selectMethod.ToString();
        Assert.Contains("RaiseNotificationEvent(", selectCode, StringComparison.Ordinal);
        Assert.Contains("selected", selectCode, StringComparison.Ordinal);
        Assert.Contains("_chimeSelectButtons", selectCode, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryDynamicButtonInMainWindowFilesHasExplicitAutomationName()
    {
        var mainWindowFiles = Directory.GetFiles(
            Path.Combine(RepositoryRoot(), "src", "Production", "EnviousWispr.App"),
            "MainWindow*.cs");

        Assert.NotEmpty(mainWindowFiles);

        foreach (var file in mainWindowFiles)
        {
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file);
            var root = tree.GetRoot();

            // Find all new Button instantiations
            var buttonCreations = root.DescendantNodes()
                .OfType<ObjectCreationExpressionSyntax>()
                .Where(creation => creation.Type.ToString() == "Button")
                .ToList();

            foreach (var creation in buttonCreations)
            {
                // The creation must be in a method or block that sets AutomationProperties.SetName
                var enclosingBlock = creation.Ancestors().OfType<BlockSyntax>().FirstOrDefault();
                Assert.NotNull(enclosingBlock);

                var blockCode = enclosingBlock.ToString();
                Assert.True(
                    blockCode.Contains("AutomationProperties.SetName", StringComparison.Ordinal),
                    $"Dynamic button creation in '{Path.GetFileName(file)}' at line " +
                    $"{creation.GetLocation().GetLineSpan().StartLinePosition.Line + 1} " +
                    "is missing an explicit AutomationProperties.SetName call.");
            }
        }
    }

    private static CompilationUnitSyntax ParseMainWindowCode()
    {
        var path = Path.Combine(RepositoryRoot(), "src", "Production", "EnviousWispr.App", "MainWindow.xaml.cs");
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path);
        return tree.GetCompilationUnitRoot();
    }

    private static MethodDeclarationSyntax? FindMethod(CompilationUnitSyntax root, string name) =>
        root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.ValueText == name);

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
