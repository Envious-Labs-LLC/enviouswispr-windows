using System.Xml.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// Appearance has no Save button, so every choice on it must commit when it is made.
/// </summary>
/// <remarks>
/// A CARD ONLY THE SAVE BUTTON READS IS A CARD THAT DOES NOTHING HERE. Appearance is the one settings
/// page with no Save button, deliberately: its choices take effect on screen the moment they are
/// picked, so asking somebody to confirm a change they can already see reads as the app not trusting
/// its own preview. The consequence is a rule about every control that lands on that page, and moving
/// the recording pill cards there is exactly the move that breaks it - somebody picks a design, sees
/// it, walks away, and it is gone.
/// </remarks>
public sealed class AppearanceCommitTests
{
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void EveryChoiceCardOnAppearanceCommitsWhenItIsPicked()
    {
        var markup = XDocument.Load(Path.Combine(
            RepositoryRoot(), "src", "Production", "EnviousWispr.App", "MainWindow.xaml"));
        var appearance = markup.Descendants().FirstOrDefault(element =>
            (string?)element.Attribute(XName.Get("Name", XamlNamespace)) == "AppearanceSection");
        Assert.True(appearance is not null, "There is no AppearanceSection to check.");

        var silent = appearance!.Descendants()
            .Where(element => element.Name.LocalName == "RadioButton" &&
                (string?)element.Attribute(XName.Get("Name", XamlNamespace)) is not null &&
                element.Attribute("Checked") is null)
            .Select(element => (string?)element.Attribute(XName.Get("Name", XamlNamespace))!)
            .ToArray();

        Assert.True(
            silent.Length == 0,
            "These Appearance cards have no Checked handler, and Appearance has no Save button, so "
                + "picking one changes the screen and nothing else: " + string.Join(", ", silent));
    }

    [Fact]
    public void ThePillDesignIsAmongTheFieldsAppearanceActuallyWrites()
    {
        // A HANDLER THAT WRITES THE WRONG FIELDS IS THE SAME BUG WEARING A CALLBACK. The persist
        // method names its fields one by one, so a card can be wired to it and still be dropped.
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Production", "EnviousWispr.App", "MainWindow.xaml.cs")));
        var persist = tree.GetRoot().DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(method => method.Identifier.ValueText == "PersistAppearanceChoicesAsync");
        Assert.True(persist is not null, "PersistAppearanceChoicesAsync is gone.");

        var written = persist!.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Select(assignment => assignment.Left.ToString())
            .ToArray();

        Assert.Contains("Theme", written);
        Assert.Contains("OverlayPosition", written);
        Assert.Contains("PillDesignWithoutWords", written);
    }

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
