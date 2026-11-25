using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// Guards the WinUI 3 invariants for Dictionary Mass edit mode so toggling it never crashes
/// in Microsoft.UI.Xaml.dll (0xc000027b / E_UNEXPECTED).
/// </summary>
public sealed class DictionaryMassEditTests
{
    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void MassEditWordsToggleUsesCompatibleButtonTemplateStyles()
    {
        var root = FindRepositoryRoot();
        var controlsDoc = XDocument.Load(Path.Combine(
            root, "src", "Production", "EnviousWispr.App", "Theme", "Controls.xaml"));

        var quietStyle = FindStyle(controlsDoc, "BrandQuietButtonStyle");
        Assert.Equal("Button", (string?)quietStyle.Attribute("TargetType"));
        Assert.Equal("{StaticResource DefaultButtonStyle}", (string?)quietStyle.Attribute("BasedOn"));
        Assert.DoesNotContain(
            quietStyle.Elements(),
            element => element.Name.LocalName == "Setter" && (string?)element.Attribute("Property") == "Template");

        var quietActiveStyle = FindStyle(controlsDoc, "BrandQuietActiveButtonStyle");
        Assert.Equal("Button", (string?)quietActiveStyle.Attribute("TargetType"));
        Assert.Equal("{StaticResource BrandQuietButtonStyle}", (string?)quietActiveStyle.Attribute("BasedOn"));
        Assert.DoesNotContain(
            quietActiveStyle.Elements(),
            element => element.Name.LocalName == "Setter" && (string?)element.Attribute("Property") == "Template");

        var mainWindowDoc = XDocument.Load(Path.Combine(
            root, "src", "Production", "EnviousWispr.App", "MainWindow.xaml"));
        var toggleButton = mainWindowDoc.Descendants().Single(element =>
            string.Equals((string?)element.Attribute(XamlNamespace + "Name"), "MassEditWordsToggle", StringComparison.Ordinal));
        Assert.Equal("{StaticResource BrandQuietButtonStyle}", (string?)toggleButton.Attribute("Style"));

        var method = FindMainWindowMethod(root, "MassEditWordsToggle_Click");
        var styleAssignments = method.Body!.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(assignment => assignment.Left is MemberAccessExpressionSyntax member
                && member.Expression is IdentifierNameSyntax target
                && target.Identifier.ValueText == "MassEditWordsToggle"
                && member.Name.Identifier.ValueText == "Style")
            .ToArray();
        var styleAssignment = Assert.Single(styleAssignments);

        var resourceAccess = styleAssignment.Right.DescendantNodesAndSelf()
            .OfType<ElementAccessExpressionSyntax>()
            .Single(access => access.ArgumentList.Arguments.Count == 1);
        var branch = Assert.IsType<ConditionalExpressionSyntax>(resourceAccess.ArgumentList.Arguments[0].Expression);

        var condition = Assert.IsType<IdentifierNameSyntax>(branch.Condition);
        Assert.Equal("_isDictionaryMassEdit", condition.Identifier.ValueText);

        var whenTrue = Assert.IsType<LiteralExpressionSyntax>(branch.WhenTrue);
        var whenFalse = Assert.IsType<LiteralExpressionSyntax>(branch.WhenFalse);
        Assert.Equal("BrandQuietActiveButtonStyle", whenTrue.Token.ValueText);
        Assert.Equal("BrandQuietButtonStyle", whenFalse.Token.ValueText);

        var literals = method.DescendantNodes()
            .OfType<LiteralExpressionSyntax>()
            .Where(literal => literal.IsKind(SyntaxKind.StringLiteralExpression))
            .Select(literal => literal.Token.ValueText)
            .ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("BrandTabStripActiveButtonStyle", literals);
    }

    [Fact]
    public void MassEditWordsToggleClickClearsSelectedItemsOnlyWhileSelectionEnabledAndBeforeSwitchingToNone()
    {
        var root = FindRepositoryRoot();
        var method = FindMainWindowMethod(root, "MassEditWordsToggle_Click");
        var body = method.Body!;

        var selectionModeAssignments = body.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(assignment => IsDictionaryListMember(assignment.Left, "SelectionMode"))
            .ToArray();
        var selectionModeAssignment = Assert.Single(selectionModeAssignments);

        var clearInvocations = body.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => invocation.Expression is MemberAccessExpressionSyntax member
                && member.Name.Identifier.ValueText == "Clear"
                && IsDictionaryListMember(member.Expression, "SelectedItems"))
            .ToArray();
        Assert.NotEmpty(clearInvocations);

        var selectedItemsAccesses = body.DescendantNodes()
            .OfType<MemberAccessExpressionSyntax>()
            .Where(member => IsDictionaryListMember(member, "SelectedItems"))
            .ToArray();
        Assert.NotEmpty(selectedItemsAccesses);

        foreach (var access in selectedItemsAccesses)
        {
            Assert.True(
                access.Span.End < selectionModeAssignment.SpanStart,
                "DictionaryList.SelectedItems must be cleared BEFORE assigning DictionaryList.SelectionMode.");
            Assert.True(
                IsGuardedByNonNoneSelectionMode(access),
                "DictionaryList.SelectedItems must only be accessed while DictionaryList.SelectionMode != ListViewSelectionMode.None.");
        }
    }

    [Fact]
    public void DictionarySelectionReadersGuardAgainstSelectionModeNone()
    {
        var root = FindRepositoryRoot();

        var updateMethod = FindMainWindowMethod(root, "UpdateSelectionDependentButtons");
        var updateSelectedAccesses = updateMethod.Body!.DescendantNodes()
            .OfType<MemberAccessExpressionSyntax>()
            .Where(member => IsDictionaryListMember(member, "SelectedItems"))
            .ToArray();
        Assert.NotEmpty(updateSelectedAccesses);
        Assert.All(updateSelectedAccesses, access => Assert.True(
            IsGuardedByConditionalNonNoneSelectionMode(access),
            "UpdateSelectionDependentButtons must guard DictionaryList.SelectedItems with DictionaryList.SelectionMode."));

        var removeMethod = FindMainWindowMethod(root, "RemoveWordButton_Click");
        var removeSelectedAccesses = removeMethod.Body!.DescendantNodes()
            .OfType<MemberAccessExpressionSyntax>()
            .Where(member => IsDictionaryListMember(member, "SelectedItems"))
            .ToArray();
        Assert.NotEmpty(removeSelectedAccesses);
        Assert.All(removeSelectedAccesses, access => Assert.True(
            IsGuardedByConditionalNonNoneSelectionMode(access),
            "RemoveWordButton_Click must guard DictionaryList.SelectedItems with DictionaryList.SelectionMode."));

        var selectAllMethod = FindMainWindowMethod(root, "SelectAllWordsButton_Click");
        var selectAllBody = selectAllMethod.Body!;
        var earlyReturnGuards = selectAllBody.Statements
            .OfType<IfStatementSyntax>()
            .Where(ifStmt =>
                IsMultipleModeMismatchCondition(ifStmt.Condition)
                && ifStmt.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>().Any())
            .ToArray();
        var guard = Assert.Single(earlyReturnGuards);

        var selectAllAccesses = selectAllBody.DescendantNodes()
            .OfType<MemberAccessExpressionSyntax>()
            .Where(member => IsDictionaryListMember(member, "SelectedItems")
                || IsDictionaryListMember(member, "SelectAll"))
            .ToArray();
        Assert.NotEmpty(selectAllAccesses);
        Assert.All(selectAllAccesses, access => Assert.True(
            access.SpanStart > guard.Span.End,
            "SelectAllWordsButton_Click must ensure DictionaryList.SelectionMode == Multiple before accessing SelectedItems or SelectAll."));
    }

    private static XElement FindStyle(XDocument document, string key) =>
        document.Descendants().Single(element =>
            element.Name.LocalName == "Style"
            && string.Equals((string?)element.Attribute(XamlNamespace + "Key"), key, StringComparison.Ordinal));

    private static MethodDeclarationSyntax FindMainWindowMethod(string root, string methodName)
    {
        var appDir = Path.Combine(root, "src", "Production", "EnviousWispr.App");
        return Directory.EnumerateFiles(appDir, "MainWindow*.cs")
            .Order()
            .SelectMany(codePath => CSharpSyntaxTree.ParseText(File.ReadAllText(codePath), path: codePath)
                .GetRoot()
                .DescendantNodes()
                .OfType<MethodDeclarationSyntax>())
            .Single(m => m.Identifier.ValueText == methodName);
    }

    private static bool IsDictionaryListMember(ExpressionSyntax expression, string memberName) =>
        expression is MemberAccessExpressionSyntax member
        && member.Expression is IdentifierNameSyntax target
        && target.Identifier.ValueText == "DictionaryList"
        && member.Name.Identifier.ValueText == memberName;

    private static bool IsGuardedByNonNoneSelectionMode(SyntaxNode node)
    {
        foreach (var ifStmt in node.Ancestors().OfType<IfStatementSyntax>())
        {
            if (IsNonNoneSelectionModeCondition(ifStmt.Condition)
                && ifStmt.Statement.Span.Contains(node.Span))
            {
                return true;
            }
        }

        return IsGuardedByConditionalNonNoneSelectionMode(node);
    }

    private static bool IsGuardedByConditionalNonNoneSelectionMode(SyntaxNode node)
    {
        foreach (var conditional in node.Ancestors().OfType<ConditionalExpressionSyntax>())
        {
            if (IsNoneSelectionModeCondition(conditional.Condition) && conditional.WhenFalse.Span.Contains(node.Span))
            {
                return true;
            }

            if (IsNonNoneSelectionModeCondition(conditional.Condition) && conditional.WhenTrue.Span.Contains(node.Span))
            {
                return true;
            }
        }

        foreach (var binary in node.Ancestors().OfType<BinaryExpressionSyntax>())
        {
            if (binary.IsKind(SyntaxKind.LogicalAndExpression)
                && binary.Right.Span.Contains(node.Span)
                && IsNonNoneSelectionModeCondition(binary.Left))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsNoneSelectionModeCondition(ExpressionSyntax condition) =>
        IsSelectionModeComparison(condition, SyntaxKind.EqualsExpression, "None");

    private static bool IsNonNoneSelectionModeCondition(ExpressionSyntax condition) =>
        IsSelectionModeComparison(condition, SyntaxKind.NotEqualsExpression, "None")
        || IsSelectionModeComparison(condition, SyntaxKind.EqualsExpression, "Multiple");

    private static bool IsMultipleModeMismatchCondition(ExpressionSyntax condition) =>
        IsSelectionModeComparison(condition, SyntaxKind.NotEqualsExpression, "Multiple");

    private static bool IsSelectionModeComparison(ExpressionSyntax condition, SyntaxKind kind, string modeMember)
    {
        if (condition is ParenthesizedExpressionSyntax paren)
        {
            return IsSelectionModeComparison(paren.Expression, kind, modeMember);
        }

        if (condition is not BinaryExpressionSyntax binary || !binary.IsKind(kind))
        {
            return false;
        }

        return (IsDictionaryListMember(binary.Left, "SelectionMode") && IsListViewSelectionModeMember(binary.Right, modeMember))
            || (IsDictionaryListMember(binary.Right, "SelectionMode") && IsListViewSelectionModeMember(binary.Left, modeMember));
    }

    private static bool IsListViewSelectionModeMember(ExpressionSyntax expression, string modeMember) =>
        expression is MemberAccessExpressionSyntax member
        && member.Expression is IdentifierNameSyntax enumType
        && enumType.Identifier.ValueText == "ListViewSelectionMode"
        && member.Name.Identifier.ValueText == modeMember;

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "EnviousWispr.Windows.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}
