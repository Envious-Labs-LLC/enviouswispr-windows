using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Input;
using EnviousWispr.Pipeline;
using EnviousWispr.Services.Input;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EnviousWispr.Architecture.Tests;

/// <summary>A fact that needs the desk's clipboard; skipped where no clipboard can be reached.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ClipboardFactAttribute : FactAttribute
{
    public ClipboardFactAttribute()
    {
        if (!WindowsTextDeliverySafetyTests.ClipboardAvailable())
        {
            Skip = "No clipboard can be reached from this session; the production-paste half did not run.";
        }
    }
}

public sealed class WindowsTextDeliverySafetyTests
{
    internal static bool ClipboardAvailable() => ClipboardGuard.Available();

    [Fact]
    public void TheProductionAdapterNamesOnlyWhatAUiAutomationCallRefusedAsAccessibilityUnavailable()
    {
        // THE BOUNDARY IS THE CALL, NOT THE TYPE (plan-2 step 13). UI Automation hands most of its
        // failures to Marshal.ThrowExceptionForHR, so a refused operation arrives as an
        // InvalidOperationException raised by the runtime itself - the same type, from the same
        // place, as a defect of ours. Inside Automation(...) it is the control refusing and is
        // carried out as AutomationRefusalException; a disposal is never a refusal and comes out
        // unchanged; a cast failure inside is a defect and comes out unchanged; an
        // InvalidOperationException outside any call is ours.
        const int InvalidOperationHResult = unchecked((int)0x80131509);
        const int ElementNotAvailableHResult = unchecked((int)0x80040201);

        var fromHResult = Assert.Throws<AutomationRefusalException>(() =>
            WindowsTextTargetAdapter.Automation(() =>
            {
                Marshal.ThrowExceptionForHR(InvalidOperationHResult);
                return 0;
            }));
        Assert.IsType<InvalidOperationException>(fromHResult.InnerException);
        // RAISED BY THE RUNTIME, NOT BY UI AUTOMATION: the throwing assembly cannot be the test.
        Assert.NotEqual("UIAutomationClient", fromHResult.InnerException.Source);

        var fromComHResult = Assert.Throws<AutomationRefusalException>(() =>
            WindowsTextTargetAdapter.Automation(() =>
            {
                Marshal.ThrowExceptionForHR(ElementNotAvailableHResult);
                return 0;
            }));
        Assert.IsAssignableFrom<COMException>(fromComHResult.InnerException);

        var notEnabled = Assert.Throws<AutomationRefusalException>(() =>
            WindowsTextTargetAdapter.Automation<int>(() => throw new ElementNotEnabledException()));
        Assert.IsType<ElementNotEnabledException>(notEnabled.InnerException);
        Assert.IsType<ElementNotAvailableException>(Assert.Throws<AutomationRefusalException>(() =>
            WindowsTextTargetAdapter.Automation<int>(() => throw new ElementNotAvailableException())).InnerException);
        Assert.IsType<UnauthorizedAccessException>(Assert.Throws<AutomationRefusalException>(() =>
            WindowsTextTargetAdapter.Automation<int>(() => throw new UnauthorizedAccessException())).InnerException);
        Assert.IsType<Win32Exception>(Assert.Throws<AutomationRefusalException>(() =>
            WindowsTextTargetAdapter.Automation<int>(() => throw new Win32Exception(5))).InnerException);

        Assert.Throws<ObjectDisposedException>(() =>
            WindowsTextTargetAdapter.Automation<int>(() => throw new ObjectDisposedException("gate")));
        Assert.Throws<InvalidCastException>(() =>
            WindowsTextTargetAdapter.Automation<int>(() => throw new InvalidCastException()));
        Assert.Throws<OperationCanceledException>(() =>
            WindowsTextTargetAdapter.Automation<int>(() => throw new OperationCanceledException()));

        Assert.True(WindowsTextTargetAdapter.IsExpectedAutomationFailure(fromHResult));
        Assert.False(WindowsTextTargetAdapter.IsExpectedAutomationFailure(new InvalidOperationException("a defect of ours")));
        Assert.False(WindowsTextTargetAdapter.IsExpectedAutomationFailure(new ObjectDisposedException("gate")));
        Assert.False(WindowsTextTargetAdapter.IsExpectedAutomationFailure(fromHResult.InnerException));
        Assert.Equal(1, WindowsTextTargetAdapter.Automation(() => 1));
    }

    [Fact]
    public void EveryUiAutomationCallInTheAdapterIsMadeThroughTheBoundary()
    {
        // NOTHING ELSE TOUCHES UI AUTOMATION: a call made outside Automation(...) would answer a
        // refusal as a fault. Judged by what the compiler resolves, not by what the text looks like:
        // every member access or invocation in the adapter whose member belongs to a UI Automation
        // type - a property read, a pattern lookup, a write, a range operation, however it is split
        // across locals - must sit inside an Automation(...) call, or inside one of the two helpers
        // (ReadCaret, RuntimeId) whose only callers sit inside one. A UI Automation constant (a
        // pattern id, a control type, an enum member) is data, not a call, and is not counted.
        var input = Path.Combine(RepositoryRoot(), "src", "Production", "EnviousWispr.Services", "Input");
        // THE PROJECT'S IMPLICIT USINGS, SUPPLIED, so every local's type resolves and every access
        // has a symbol: a scan that could not type `valuePattern` would skip its calls and pass.
        var implicitUsings = CSharpSyntaxTree.ParseText(
            "global using System; global using System.Collections.Generic; global using System.IO; global using System.Linq; " +
            "global using System.Net.Http; global using System.Threading; global using System.Threading.Tasks;",
            path: "implicit-usings.cs");
        var trees = Directory
            .EnumerateFiles(input, "*.cs", SearchOption.TopDirectoryOnly)
            .Select(file => CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file))
            .Append(implicitUsings)
            .ToArray();
        var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
            .Split(Path.PathSeparator)
            .Where(path => path.Length > 0 && File.Exists(path))
            .Append(typeof(AutomationElement).Assembly.Location)
            .Append(typeof(ElementNotAvailableException).Assembly.Location)
            .Append(typeof(System.Windows.Forms.Clipboard).Assembly.Location)
            .Append(typeof(System.Drawing.Bitmap).Assembly.Location)
            .Append(typeof(TextDeliveryRefusalReason).Assembly.Location)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToArray();
        var compilation = CSharpCompilation.Create(
            "adapter-scan",
            trees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var automationElement = compilation.GetTypeByMetadataName(typeof(AutomationElement).FullName!);
        Assert.True(automationElement is { TypeKind: not TypeKind.Error }, "UI Automation did not resolve, so this gate would have scanned for nothing.");
        var adapterErrors = compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error && diagnostic.Location.SourceTree?.FilePath.EndsWith("WindowsTextTargetAdapter.cs", StringComparison.Ordinal) == true)
            .Select(diagnostic => diagnostic.ToString())
            .ToArray();
        Assert.True(adapterErrors.Length == 0, "the adapter did not compile in the scan, so its calls would not resolve: " + string.Join("; ", adapterErrors.Take(5)));

        var adapterTree = Assert.Single(trees, tree => tree.FilePath.EndsWith("WindowsTextTargetAdapter.cs", StringComparison.Ordinal));
        var model = compilation.GetSemanticModel(adapterTree);
        var root = adapterTree.GetRoot();
        var adapter = Assert.Single(root.DescendantNodes().OfType<ClassDeclarationSyntax>(), type => type.Identifier.Text == "WindowsTextTargetAdapter");
        var helpers = new[] { "ReadCaret", "RuntimeId" };
        Assert.Single(adapter.Members.OfType<MethodDeclarationSyntax>(), method => method.Identifier.Text == "Automation");
        var dynamicToken = adapter.DescendantTokens().FirstOrDefault(token => token.IsKind(SyntaxKind.IdentifierToken) && token.Text == "dynamic");
        Assert.True(dynamicToken == default, $"A dynamically dispatched call in the adapter at line {dynamicToken.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: {dynamicToken.Parent?.Parent}");
        // EVERY EXPRESSION THE COMPILER RESOLVES TO A MEMBER, WHATEVER ITS SPELLING: `x.Member`,
        // the conditional `x?.Member`, a bare identifier under a `using static`, a qualified or
        // unqualified helper call, a helper taken as a delegate. The outermost expression for each
        // reference is judged (an invocation, not also the name inside it).
        var accesses = 0;
        var helperReferences = helpers.ToDictionary(helper => helper, _ => new List<ExpressionSyntax>(), StringComparer.Ordinal);
        foreach (var expression in adapter.DescendantNodes().OfType<ExpressionSyntax>())
        {
            // NOTHING IN THE ADAPTER IS RESOLVED AT RUNTIME. A `dynamic` receiver defers the member
            // lookup to the runtime binder and a reflection call names the member as a string; the
            // compiler resolves neither, so the scan would have no symbol to judge and would skip
            // the call. Both are refused outright, before the null-symbol skip.
            var typeInfo = model.GetTypeInfo(expression);
            Assert.True(
                typeInfo.Type is not IDynamicTypeSymbol && typeInfo.ConvertedType is not IDynamicTypeSymbol,
                $"A dynamically dispatched call in the adapter at line {Line(expression)}: {expression.Parent}");
            var symbol = Resolve(model, expression);
            Assert.True(
                symbol is null || !(symbol.ContainingNamespace?.ToDisplayString() ?? string.Empty).StartsWith("System.Reflection", StringComparison.Ordinal),
                $"A reflective call in the adapter at line {Line(expression)}: {expression.Parent}");
            if (symbol is null || (expression.Parent is ExpressionSyntax parent && SymbolEqualityComparer.Default.Equals(Resolve(model, parent), symbol)))
            {
                continue;
            }

            if (IsUiAutomationCall(symbol))
            {
                accesses++;
                // A METHOD IS CALLED, NEVER CAPTURED: a method group taken inside the boundary is
                // invoked later, outside it, where a refusal would be a fault.
                Assert.True(symbol is not IMethodSymbol || IsInvoked(expression), $"A UI Automation method captured instead of called at line {Line(expression)}: {expression.Parent}");
                Assert.True(ExecutesInsideTheBoundary(expression, model, helpers), $"A UI Automation call outside the boundary at line {Line(expression)}: {expression.Parent}");
            }
            else if (symbol is IMethodSymbol { ContainingType.Name: "WindowsTextTargetAdapter" } method && helperReferences.TryGetValue(method.Name, out var uses))
            {
                Assert.True(IsInvoked(expression), $"{method.Name} captured instead of called at line {Line(expression)}: {expression.Parent}");
                uses.Add(expression);
            }
        }

        Assert.True(accesses >= 20, $"the scan resolved only {accesses} UI Automation calls; the adapter makes more than that, so the resolution is broken");

        // THE HELPERS ARE REFERRED TO FROM INSIDE THE BOUNDARY, ONCE EACH, AND FROM NOWHERE ELSE -
        // by symbol, so a qualified call or a delegate reference counts the same as the plain call.
        foreach (var (helper, helperUses) in helperReferences)
        {
            var reference = Assert.Single(helperUses);
            Assert.True(ExecutesInsideTheBoundary(reference, model, []), $"{helper} is called outside the boundary at line {Line(reference)}");
        }
    }

    private static ISymbol? Resolve(SemanticModel model, ExpressionSyntax expression)
    {
        var info = model.GetSymbolInfo(expression);
        return info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
    }

    private static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    /// <summary>Whether a resolved member is a live UI Automation call: a property or method of a type in the UI Automation namespaces, not a constant.</summary>
    private static bool IsUiAutomationCall(ISymbol symbol)
    {
        var space = symbol.ContainingType?.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        if (!space.StartsWith("System.Windows.Automation", StringComparison.Ordinal))
        {
            return false;
        }

        return symbol switch
        {
            IPropertySymbol => true,
            IMethodSymbol => true,
            _ => false,
        };
    }

    /// <summary>Whether a member expression is the target of an invocation - called, not merely named.</summary>
    private static bool IsInvoked(ExpressionSyntax expression) =>
        expression is InvocationExpressionSyntax ||
        (expression.Parent is InvocationExpressionSyntax invocation && invocation.Expression == expression);

    /// <summary>
    /// Whether the node executes inside the boundary: its nearest enclosing function is either a
    /// synchronous lambda that is the argument of an invocation of the adapter's own Automation
    /// method, or the body of an exempt helper itself. Nothing is inherited across a nested lambda
    /// or a local function - they may run later, outside the boundary's handler - nor across a query
    /// expression, whose clauses run when it is enumerated, and an async lambda resumes outside it. An access in the argument expression itself -
    /// `Automation(valuePattern.Current.Value.ToString)` - runs before the call and is outside.
    /// </summary>
    private static bool ExecutesInsideTheBoundary(SyntaxNode node, SemanticModel model, string[] exemptHelpers)
    {
        // A QUERY EXPRESSION IS A DEFERRED CALLBACK WITHOUT A LAMBDA TO SEE: its clauses run when
        // the query is enumerated, after the boundary has returned, so it stops the search like a
        // nested lambda does.
        var enclosing = node.Ancestors().FirstOrDefault(ancestor =>
            ancestor is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax or MethodDeclarationSyntax or QueryExpressionSyntax);
        return enclosing switch
        {
            MethodDeclarationSyntax method => exemptHelpers.Contains(method.Identifier.Text, StringComparer.Ordinal),
            AnonymousFunctionExpressionSyntax lambda => lambda.AsyncKeyword.IsKind(SyntaxKind.None) && IsTheBoundaryLambda(lambda, model),
            _ => false,
        };
    }

    private static bool IsTheBoundaryLambda(AnonymousFunctionExpressionSyntax lambda, SemanticModel model) =>
        lambda.Parent is ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax invocation } } &&
        invocation.Expression is IdentifierNameSyntax { Identifier.Text: "Automation" } &&
        ResolvesToTheAdapter(model.GetSymbolInfo(invocation));

    private static bool ResolvesToTheAdapter(SymbolInfo info)
    {
        // A lambda argument the scan cannot fully type leaves the call unresolved with candidates;
        // the candidates are still the adapter's Automation<T>, and nothing else is named that.
        var candidates = info.Symbol is { } symbol ? [symbol] : info.CandidateSymbols;
        return candidates.Length == 0 || candidates.All(candidate => candidate is IMethodSymbol { Name: "Automation", ContainingType.Name: "WindowsTextTargetAdapter" });
    }

    [Theory]
    [InlineData(TargetContextStatus.AccessibilityUnavailable, TextDeliveryRefusalReason.AccessibilityUnavailable, TextDeliveryRefusalReason.AccessibilityUnavailable)]
    [InlineData(TargetContextStatus.AccessibilityUnavailable, TextDeliveryRefusalReason.UnsupportedTarget, TextDeliveryRefusalReason.UnsupportedTarget)]
    [InlineData(TargetContextStatus.AccessibilityUnavailable, TextDeliveryRefusalReason.None, TextDeliveryRefusalReason.AccessibilityUnavailable)]
    [InlineData(TargetContextStatus.Elevated, TextDeliveryRefusalReason.ElevatedTarget, TextDeliveryRefusalReason.ElevatedTarget)]
    [InlineData(TargetContextStatus.Protected, TextDeliveryRefusalReason.ProtectedField, TextDeliveryRefusalReason.ProtectedField)]
    [InlineData(TargetContextStatus.TargetUnavailable, TextDeliveryRefusalReason.TargetUnavailable, TextDeliveryRefusalReason.TargetChanged)]
    [InlineData(TargetContextStatus.TargetChanged, TextDeliveryRefusalReason.TargetChanged, TextDeliveryRefusalReason.TargetChanged)]
    [InlineData(TargetContextStatus.Available, TextDeliveryRefusalReason.None, TextDeliveryRefusalReason.TargetChanged)]
    public void TheCommitsSecondReadKeepsItsOwnName(TargetContextStatus status, TextDeliveryRefusalReason carried, TextDeliveryRefusalReason expected)
    {
        // THE COMMIT READS THE TARGET AGAIN JUST BEFORE THE WRITE. Accessibility that did not answer
        // the second time used to be filed as "target changed"; it says what it is now, with the
        // reason the capture gave. An available target whose caret moved is a changed target.
        var current = new TargetContextResult(status, RefusalReason: carried);

        Assert.Equal(expected, WindowsTextTargetAdapter.RevalidationRefusal(current));
    }

    [Theory]
    [InlineData(TargetContextStatus.AccessibilityUnavailable, TextDeliveryRefusalReason.UnsupportedTarget, TextDeliveryRefusalReason.UnsupportedTarget)]
    [InlineData(TargetContextStatus.AccessibilityUnavailable, TextDeliveryRefusalReason.AccessibilityUnavailable, TextDeliveryRefusalReason.AccessibilityUnavailable)]
    [InlineData(TargetContextStatus.AccessibilityUnavailable, TextDeliveryRefusalReason.None, TextDeliveryRefusalReason.AccessibilityUnavailable)]
    [InlineData(TargetContextStatus.Protected, TextDeliveryRefusalReason.ProtectedField, TextDeliveryRefusalReason.ProtectedField)]
    [InlineData(TargetContextStatus.Elevated, TextDeliveryRefusalReason.None, TextDeliveryRefusalReason.ElevatedTarget)]
    [InlineData(TargetContextStatus.TargetChanged, TextDeliveryRefusalReason.TargetChanged, TextDeliveryRefusalReason.TargetChanged)]
    public void ThePastesLastLookKeepsTheCapturesOwnRefusal(TargetContextStatus status, TextDeliveryRefusalReason carried, TextDeliveryRefusalReason expected)
    {
        // THE PREFLIGHT'S CAPTURE NAMES ITS OWN REFUSAL: a selection that became unsupported
        // between the commit's read and the keystroke is UnsupportedTarget, not "accessibility
        // unavailable"; the status is the answer only when the capture gave no reason.
        var expectedContext = Context(new TargetWindowId(42, 7, "1.2.3"), "before", string.Empty, "after");

        Assert.Equal(expected, WindowsTextTargetAdapter.PreflightRefusal(new TargetContextResult(status, RefusalReason: carried), expectedContext));
        Assert.Equal(
            TextDeliveryRefusalReason.None,
            WindowsTextTargetAdapter.PreflightRefusal(new TargetContextResult(TargetContextStatus.Available, expectedContext), expectedContext));
        Assert.Equal(
            TextDeliveryRefusalReason.TargetChanged,
            WindowsTextTargetAdapter.PreflightRefusal(new TargetContextResult(TargetContextStatus.Available, expectedContext with { Left = "moved" }), expectedContext));
    }

    [ClipboardFact]
    public async Task ASelectionThatBecameUnsupportedAtTheLastLookReachesTheDeliveryUnderItsOwnName()
    {
        // THROUGH THE PRODUCTION PASTE AND THE DELIVERY: the preflight's capture answers
        // AccessibilityUnavailable / UnsupportedTarget, and what comes back is UnsupportedTarget with
        // the fallback words on the clipboard - the code, the sentence and the words agree.
        using var guard = ClipboardGuard.Capture();
        var expectedContext = Context(new TargetWindowId(42, 7, "1.2.3"), "before", string.Empty, "after");
        var delivery = new ContextAwareTextDelivery(new ProductionPastingAdapter(() => WindowsTextTargetAdapter.PreflightRefusal(
            new TargetContextResult(TargetContextStatus.AccessibilityUnavailable, RefusalReason: TextDeliveryRefusalReason.UnsupportedTarget),
            expectedContext)));

        var result = await delivery.DeliverAsync(new TextDeliveryRequest(
            new ProcessedText(new DictationSessionId(Guid.NewGuid()), "kept"),
            new TargetWindowId(42, 7, "1.2.3"),
            "en",
            TextDeliveryOptions.Default));

        Assert.Equal(TextDeliveryRefusalReason.UnsupportedTarget, result.RefusalReason);
        Assert.True(result.ClipboardFallback);
        Assert.Equal(TextDeliveryRoute.ClipboardOnly, result.Route);
        Assert.Null(result.Fault);
        Assert.Equal("Automatic paste is unsafe here, so the text was copied only", EnviousWispr.Core.Presentation.DeliveryStatusReport.For(result).Text);
        Assert.Equal("kept ", ClipboardGuard.GetText());
    }

    [Fact]
    public void ThePastesPreflightTranslatesOnlyWhatAccessibilityRefused()
    {
        // THE PREFLIGHT RUNS ON THE CLIPBOARD THREAD, inside the paste, where nothing knows UI
        // Automation. What accessibility refused is translated here, in the adapter; a defect in the
        // preflight comes out to be named - not answered "clipboard unavailable".
        Assert.Equal(TextDeliveryRefusalReason.None, WindowsTextTargetAdapter.GuardedPreflight(() => TextDeliveryRefusalReason.None));
        Assert.Equal(TextDeliveryRefusalReason.TargetChanged, WindowsTextTargetAdapter.GuardedPreflight(() => TextDeliveryRefusalReason.TargetChanged));
        Assert.Equal(
            TextDeliveryRefusalReason.AccessibilityUnavailable,
            WindowsTextTargetAdapter.GuardedPreflight(() => throw new AutomationRefusalException("refused", new InvalidOperationException())));
        Assert.Throws<InvalidCastException>(() => WindowsTextTargetAdapter.GuardedPreflight(() => throw new InvalidCastException()));
        Assert.Throws<ObjectDisposedException>(() => WindowsTextTargetAdapter.GuardedPreflight(() => throw new ObjectDisposedException("gate")));
    }

    [ClipboardFact]
    public async Task APreflightDefectDuringTheProductionPasteComesOutNamedWithTheClipboardRestoredAndNothingPasted()
    {
        // THE PRODUCTION PASTE, ON THE REAL CLIPBOARD, WITH A PREFLIGHT THAT THROWS. The words are on
        // the clipboard by then and nothing has been pasted; the paste puts the clipboard back
        // (the sentinel placed before it is there after it), the defect comes out of the paste as
        // itself - not as "clipboard unavailable" - and the delivery over an adapter that pastes
        // through the production paste names it DeliveryFaulted at the commit, with the words kept.
        var sentinel = $"EnviousWispr clipboard sentinel {Guid.NewGuid():N}";
        using var guard = ClipboardGuard.Capture();
        ClipboardGuard.SetText(sentinel);

        await Assert.ThrowsAsync<InvalidCastException>(() => WindowsClipboardPaste.PasteAsync(
            "words that must not be pasted",
            "words that must not be pasted ",
            restoreClipboard: true,
            static () => throw new InvalidCastException("a defect in the preflight"),
            CancellationToken.None));
        Assert.Equal(sentinel, ClipboardGuard.GetText());

        var delivery = new ContextAwareTextDelivery(new ProductionPastingAdapter(static () => throw new InvalidCastException("a defect in the preflight")));
        var result = await delivery.DeliverAsync(new TextDeliveryRequest(
            new ProcessedText(new DictationSessionId(Guid.NewGuid()), "kept"),
            new TargetWindowId(42, 7, "1.2.3"),
            "en",
            TextDeliveryOptions.Default));

        Assert.Equal(TextDeliveryRefusalReason.DeliveryFaulted, result.RefusalReason);
        Assert.Equal(new DeliveryFault(DeliveryStage.Commit, DeliveryFaultKind.InvalidCast, nameof(InvalidCastException)), result.Fault);
        Assert.False(result.ClipboardFallback);
        Assert.Equal("kept", delivery.RecoveryText?.Text);
        Assert.Equal(sentinel, ClipboardGuard.GetText());
    }

    [ClipboardFact]
    public async Task AccessibilityRefusedInTheProductionPastesPreflightLeavesTheFallbackOnTheClipboardWithItsName()
    {
        // THE SAME PASTE, WITH ACCESSIBILITY REFUSING IN THE PREFLIGHT (through the adapter's guard):
        // nothing is pasted, the fallback words are left on the clipboard for the person to paste, and
        // the refusal keeps its name through the delivery.
        using var guard = ClipboardGuard.Capture();
        ClipboardGuard.SetText("before");

        var pasted = await WindowsClipboardPaste.PasteAsync(
            "words",
            "words ",
            restoreClipboard: true,
            () => WindowsTextTargetAdapter.GuardedPreflight(static () => throw new AutomationRefusalException("refused", new InvalidOperationException())),
            CancellationToken.None);

        Assert.Equal(TextDeliveryRoute.ClipboardOnly, pasted.Route);
        Assert.True(pasted.ClipboardFallback);
        Assert.False(pasted.Delivered);
        Assert.Equal(TextDeliveryRefusalReason.AccessibilityUnavailable, pasted.RefusalReason);
        Assert.Equal("words ", ClipboardGuard.GetText());

        var delivery = new ContextAwareTextDelivery(new ProductionPastingAdapter(static () => throw new AutomationRefusalException("refused", new InvalidOperationException())));
        var result = await delivery.DeliverAsync(new TextDeliveryRequest(
            new ProcessedText(new DictationSessionId(Guid.NewGuid()), "kept"),
            new TargetWindowId(42, 7, "1.2.3"),
            "en",
            TextDeliveryOptions.Default));

        Assert.Equal(TextDeliveryRefusalReason.AccessibilityUnavailable, result.RefusalReason);
        Assert.True(result.ClipboardFallback);
        Assert.Null(result.Fault);
        Assert.Null(delivery.RecoveryText);
    }

    /// <summary>An adapter whose context is a standard field that cannot be written directly and whose commit is the production paste with the given preflight.</summary>
    private sealed class ProductionPastingAdapter(Func<TextDeliveryRefusalReason> preflight) : ITextTargetAdapter
    {
        public Task<TextCommitResult> CopyOnlyAsync(ProcessedText text, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TargetContextResult> CaptureContextAsync(TargetWindowId target, TextDeliveryOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TargetContextResult(TargetContextStatus.Available, Context(target, "before", string.Empty, "after")));

        public Task<TextCommitResult> CommitAsync(TextCommitRequest request, CancellationToken cancellationToken = default) =>
            WindowsClipboardPaste.PasteAsync(
                request.Text.Text,
                request.LegacyText.Text,
                request.Options.RestoreClipboardAfterPaste,
                () => WindowsTextTargetAdapter.GuardedPreflight(preflight),
                cancellationToken);
    }

    /// <summary>
    /// The desk's clipboard, every format copied through the production snapshot before a test
    /// touches it, and put back whole when the test ends - by the using, so on a failed assertion
    /// too. A capture that cannot copy every format refuses, and the test does not run against a
    /// clipboard it could not restore.
    /// </summary>
    private sealed class ClipboardGuard : IDisposable
    {
        private readonly WindowsClipboardPaste.ClipboardSnapshot _snapshot;

        private ClipboardGuard(WindowsClipboardPaste.ClipboardSnapshot snapshot)
        {
            _snapshot = snapshot;
        }

        public static ClipboardGuard Capture()
        {
            var snapshot = OnSta(WindowsClipboardPaste.TrySnapshotClipboard);
            Assert.True(snapshot is not null, "the desk's clipboard could not be copied whole, so it was not touched");
            return new ClipboardGuard(snapshot!);
        }

        public static string? GetText() => OnSta(static () => System.Windows.Forms.Clipboard.ContainsText() ? System.Windows.Forms.Clipboard.GetText() : null);

        public static void SetText(string text) => OnSta(() =>
        {
            System.Windows.Forms.Clipboard.SetText(text);
            return true;
        });

        /// <summary>Whether the clipboard can be reached and copied whole from this session; a test that needs it is skipped otherwise.</summary>
        public static bool Available()
        {
            try
            {
                return OnSta(WindowsClipboardPaste.TrySnapshotClipboard) is not null;
            }
            catch (Exception exception) when (exception is ExternalException or InvalidOperationException or ThreadStateException)
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (!OnSta(() => WindowsClipboardPaste.TryRestoreClipboard(_snapshot)))
            {
                throw new InvalidOperationException("The desk's clipboard could not be put back after the test.");
            }
        }

        private static T OnSta<T>(Func<T> operation)
        {
            T? result = default;
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    result = operation();
                }
                catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
                {
                    failure = exception;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (failure is not null)
            {
                throw failure;
            }

            return result!;
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EnviousWispr.Windows.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }

    [Fact]
    public async Task ADisposedProductionAdapterSurfacesItsDisposalNotAnAccessibilityFailure()
    {
        // THE PRODUCTION ADAPTER, DISPOSED, THEN ASKED. Its automation gate throws
        // ObjectDisposedException before any window is touched; the adapter used to swallow that as
        // "accessibility unavailable" through its InvalidOperationException arm. It escapes now, and
        // the delivery over it answers DeliveryDisposed with the words kept.
        var adapter = new WindowsTextTargetAdapter();
        adapter.Dispose();
        var target = new TargetWindowId(42, 7, "1.2.3");

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            adapter.CaptureContextAsync(target, TextDeliveryOptions.Default));

        var delivery = new ContextAwareTextDelivery(adapter);
        var result = await delivery.DeliverAsync(new TextDeliveryRequest(
            new ProcessedText(new DictationSessionId(Guid.NewGuid()), "kept"),
            target,
            "en",
            TextDeliveryOptions.Default));

        Assert.Equal(TextDeliveryRefusalReason.DeliveryDisposed, result.RefusalReason);
        Assert.Equal(new DeliveryFault(DeliveryStage.ContextCapture, DeliveryFaultKind.ObjectDisposed, nameof(ObjectDisposedException)), result.Fault);
        Assert.Equal("kept", delivery.RecoveryText?.Text);
    }

    [Fact]
    public void ClipboardSnapshotClonesKnownValuesAndRefusesUnknownReferences()
    {
        var bytes = new byte[] { 1, 2, 3 };

        var clonedBytes = Assert.IsType<byte[]>(
            WindowsClipboardPaste.CloneClipboardValue(bytes));

        Assert.Equal(bytes, clonedBytes);
        Assert.NotSame(bytes, clonedBytes);
        Assert.Equal("immutable", WindowsClipboardPaste.CloneClipboardValue("immutable"));
        Assert.Null(WindowsClipboardPaste.CloneClipboardValue(new object()));
    }

    [Fact]
    public void AClipboardStreamIsCopiedWholeOrTheSnapshotIsRefused()
    {
        // THE WHOLE VALUE OR NOTHING (plan-2 step 13, round five). A seekable stream standing past
        // its start is copied from the start and left where it stood; a stream that cannot seek
        // would be consumed by the copy, so it is refused; a stream that fails to read is refused
        // with its position put back. A refused value refuses the whole snapshot.
        using var seekable = new SeekableOnly([1, 2, 3, 4, 5]) { Position = 3 };
        var copy = Assert.IsType<MemoryStream>(WindowsClipboardPaste.CloneClipboardValue(seekable));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, copy.ToArray());
        Assert.Equal(0, copy.Position);
        Assert.Equal(3, seekable.Position);

        using var forwardOnly = new ForwardOnly([1, 2, 3]);
        Assert.Null(WindowsClipboardPaste.CloneClipboardValue(forwardOnly));
        Assert.Equal(0, forwardOnly.Reads);

        using var failing = new SeekableOnly([1, 2, 3]) { Position = 2, FailReads = true };
        Assert.Null(WindowsClipboardPaste.CloneClipboardValue(failing));
        Assert.Equal(2, failing.Position);

        // A COPY THAT READ EVERYTHING AND THEN COULD NOT PUT THE STREAM BACK IS REFUSED TOO: the
        // clipboard's value has changed, and a snapshot taken then would not be the clipboard.
        using var stuck = new SeekableOnly([1, 2, 3]) { Position = 1, FailPositionRestoreAfterRead = true };
        Assert.Null(WindowsClipboardPaste.CloneClipboardValue(stuck));
        Assert.True(stuck.ReadToTheEnd, "the copy should have read the stream before the position failed to go back");
    }

    /// <summary>A stream that is not a MemoryStream: seekable, readable, and able to fail its reads on request.</summary>
    private sealed class SeekableOnly(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);

        public bool FailReads { get; init; }

        /// <summary>When set, the position can be set to the start for the copy but refuses to go back afterwards.</summary>
        public bool FailPositionRestoreAfterRead { get; init; }

        public bool ReadToTheEnd { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set
            {
                if (FailPositionRestoreAfterRead && ReadToTheEnd)
                {
                    throw new IOException("the stream would not go back");
                }

                _inner.Position = value;
            }
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (FailReads)
            {
                throw new IOException("the stream would not read");
            }

            var read = _inner.Read(buffer, offset, count);
            ReadToTheEnd |= _inner.Position == _inner.Length;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>A stream that cannot seek: reading it consumes it, and the snapshot must not.</summary>
    private sealed class ForwardOnly(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);

        public int Reads { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Reads++;
            return _inner.Read(buffer, offset, count);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    [Theory]
    [InlineData(0, 16_384)]
    [InlineData(4_097, 16_384)]
    [InlineData(256, 0)]
    [InlineData(256, 1_048_577)]
    public async Task DeliveryOptionsRejectUnboundedNativeReadsAndWrites(
        int contextWindowCharacters,
        int maximumDirectValueCharacters)
    {
        using var adapter = new WindowsTextTargetAdapter();
        var options = new TextDeliveryOptions(
            RestoreClipboardAfterPaste: true,
            contextWindowCharacters,
            maximumDirectValueCharacters);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            adapter.CaptureContextAsync(new TargetWindowId(1), options));
    }

    [Fact]
    public void SendInputLayoutMatchesTheWin64Abi()
    {
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(40, WindowsClipboardPaste.NativeInputSize);
        Assert.Equal(24, WindowsClipboardPaste.NativeKeyboardInputSize);
        Assert.Equal(4, WindowsClipboardPaste.NativeKeyboardFlagsOffset);
    }

    [Theory]
    [InlineData(TextTargetKind.Terminal, "safe command", TextDeliveryRefusalReason.None)]
    [InlineData(
        TextTargetKind.Terminal,
        "unsafe command\r\n",
        TextDeliveryRefusalReason.UnsafeMultilineTarget)]
    [InlineData(
        TextTargetKind.Game,
        "hello",
        TextDeliveryRefusalReason.UnsupportedTarget)]
    [InlineData(TextTargetKind.Browser, "hello", TextDeliveryRefusalReason.None)]
    public void CompatibilityPolicyRefusesOnlyPinnedUnsafeShapes(
        TextTargetKind kind,
        string text,
        TextDeliveryRefusalReason expected)
    {
        Assert.Equal(expected, WindowsTextTargetAdapter.CompatibilityRefusal(kind, text));
    }

    [Fact]
    public void CaretIdentityIncludesElementAndBoundedSeam()
    {
        var target = new TargetWindowId(42, 7, "1.2.3");
        var expected = Context(target, "left", "", "right");

        Assert.True(WindowsTextTargetAdapter.CaretUnchanged(
            expected,
            Context(target, "left", "", "right")));
        Assert.False(WindowsTextTargetAdapter.CaretUnchanged(
            expected,
            Context(target with { FocusedElementId = "9.9.9" }, "left", "", "right")));
        Assert.False(WindowsTextTargetAdapter.CaretUnchanged(
            expected,
            Context(target, "changed", "", "right")));
    }

    private static CaretContext Context(
        TargetWindowId target,
        string left,
        string selection,
        string right) => new(
        target,
        target.FocusedElementId!,
        TextTargetKind.StandardEdit,
        left,
        selection,
        right,
        LeftReachedDocumentStart: true,
        RightReachedDocumentEnd: true,
        HasTextContext: true,
        SupportsDirectValueWrite: true,
        DirectValueWriteAtEnd: true);
}
