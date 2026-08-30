using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// The product must not accuse itself of failing on a number it cannot justify.
/// </summary>
/// <remarks>
/// MEASURED ON A REAL MACHINE, WHICH IS WHY THIS IS A TEST RATHER THAN A NOTE. Home carried a
/// warning reading "EnviousWispr did not close properly last time" and, once past one, "That has now
/// happened N times in a row". On the Windows test machine that number reached nineteen, and almost
/// all of it was a build script calling Stop-Process to release a DLL lock.
///
/// THE COUNT CANNOT MEAN WHAT THE SENTENCE CLAIMS. Nothing distinguishes a fault from a closed
/// laptop, a Restart chosen from the Start menu, a log off, or Task Manager: all four leave exactly
/// the trace a crash leaves, which is the absence of a clean-exit flag. So the tally is not evidence
/// of anything, and it was the headline of a first-screen warning.
///
/// AND IT FIRED WHERE NOTHING HAD BEEN LOST. The banner was raised only when there was NO unfinished
/// dictation to restore. The case where something WAS lost is the recovery card, which is a
/// different path and stays exactly as it is.
///
/// THE FACT ITSELF IS NOT DISCARDED. App.xaml.cs still writes ApplicationRunRecovered to the
/// privacy-safe log on every interrupted run, and the store still keeps the count, so support keeps
/// the signal. What went is a sentence aimed at somebody who could do nothing with it.
///
/// PARSED RATHER THAN MATCHED. A plain text search over these files counts the explanation above
/// and every comment describing the defect, so it would fail on a tree that had fixed it. Only
/// string LITERALS are read here, which is where user-facing copy actually lives.
/// </remarks>
public sealed class RunStateNoticeTests
{
    private static readonly string[] Forbidden =
    [
        "did not close properly",
        "times in a row",
    ];

    [Theory]
    [InlineData("src/Production/EnviousWispr.App/MainWindow.xaml.cs")]
    [InlineData("src/Production/EnviousWispr.App/App.xaml.cs")]
    public void NoUserFacingCopyAccusesTheProductOfRepeatedFailure(string relativePath)
    {
        var path = Path.Combine(FindRepositoryRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot();

        var literals = root.DescendantTokens()
            .Where(token => token.IsKind(SyntaxKind.StringLiteralToken)
                || token.IsKind(SyntaxKind.SingleLineRawStringLiteralToken)
                || token.IsKind(SyntaxKind.MultiLineRawStringLiteralToken)
                || token.IsKind(SyntaxKind.InterpolatedStringTextToken))
            .Select(token => token.ValueText)
            .ToArray();

        var offenders = literals
            .Where(text => Forbidden.Any(phrase =>
                text.Contains(phrase, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"{relativePath} contains user-facing copy accusing the product of repeated failure: "
                + string.Join(" | ", offenders));
    }

    /// <summary>Nothing hands the interrupted-run count to the window.</summary>
    /// <remarks>
    /// THE PHRASE GATE ABOVE GUARDS THE WORDS AND THIS GUARDS THE SHAPE. Rewording the sentence
    /// while still passing the tally to a banner would put the same defect back under a new name, so
    /// this reads the argument expressions of every call made on the window instead.
    /// </remarks>
    [Fact]
    public void TheInterruptedRunCountIsNeverPassedToTheWindow()
    {
        var path = Path.Combine(
            FindRepositoryRoot(),
            "src", "Production", "EnviousWispr.App", "App.xaml.cs");
        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot();

        var offenders = root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => invocation.Expression.ToString().Contains("_window", StringComparison.Ordinal))
            .Where(invocation => invocation.ArgumentList.Arguments.Any(argument =>
                argument.ToString().Contains("ConsecutiveInterruptedRuns", StringComparison.Ordinal)))
            .Select(invocation => invocation.ToString())
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "App.xaml.cs hands the interrupted-run count to the window: "
                + string.Join(" | ", offenders));
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
            "Could not locate the repository root from " + AppContext.BaseDirectory);
    }
}
