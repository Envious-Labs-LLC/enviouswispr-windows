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

            // A HANDLE DOES NOT LEAVE THE BOUNDARY. What a member's namespace says is not what a call
            // does: `((object)element).GetHashCode()` resolves to Object.GetHashCode and dispatches to
            // UI Automation; `Expression.Constant(element)` hands the element to code that will call
            // it by name. So outside the boundary a live UI Automation object may only be the result
            // of an Automation call, tested against null, or passed to one of the adapter's own
            // methods - whose bodies this same scan reads. A cast, an alias, a member access, an
            // argument to anything else: refused.
            // NO HANDLE RIDES IN A TUPLE: a tuple converts component by component, so one element
            // can be erased while the tuple as a whole still carries a handle and passes every
            // aggregate check. A tuple type that carries a handle, as a value's type or as what it
            // converts to, is refused; the adapter's own records carry what needs carrying.
            Assert.True(
                !CarriesHandleInTuple(typeInfo.Type) && !CarriesHandleInTuple(typeInfo.ConvertedType),
                $"A UI Automation handle in a tuple at line {Line(expression)} ({expression.Kind()} {expression}): {expression.Parent}");

            // A HANDLE IS NEVER ERASED, INSIDE THE BOUNDARY OR OUT: a handle converted to object, to
            // an interface or to a type parameter - by a cast, an `as`, or the context it stands in -
            // leaves as something the scan cannot see and reaches UI Automation through virtual
            // dispatch later. A type name is not a value: `var`, a parameter's type and a cast's
            // target only name one.
            if (symbol is not ITypeSymbol)
            {
                Assert.True(
                    !IsHandleType(typeInfo.Type) || IsHandleType(typeInfo.ConvertedType),
                    $"A UI Automation handle erased at line {Line(expression)} ({expression.Kind()} {expression}): {expression.Parent}");
                var erasedByCast = expression switch
                {
                    CastExpressionSyntax cast => IsHandleType(model.GetTypeInfo(cast.Expression).Type) && !IsHandleType(model.GetTypeInfo(cast).Type),
                    BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AsExpression } conversion => IsHandleType(model.GetTypeInfo(conversion.Left).Type) && !IsHandleType(model.GetTypeInfo(conversion).Type),
                    _ => false,
                };
                Assert.False(erasedByCast, $"A UI Automation handle erased at line {Line(expression)} ({expression.Kind()} {expression}): {expression.Parent}");
            }

            if (IsHandleType(typeInfo.Type) && symbol is not ITypeSymbol && !ExecutesInsideTheBoundary(expression, model, helpers))
            {
                Assert.True(
                    HandleUseIsAllowedOutside(expression, model),
                    $"A UI Automation handle used outside the boundary at line {Line(expression)} ({expression.Kind()} {expression}): {expression.Parent}");
            }

            if (symbol is null || (expression.Parent is ExpressionSyntax parent && SymbolEqualityComparer.Default.Equals(Resolve(model, parent), symbol)))
            {
                continue;
            }

            // A CALL ON A HANDLE IS A UI AUTOMATION CALL WHATEVER DECLARED THE MEMBER: Array.CopyTo on
            // an AutomationElement[], Object.GetHashCode on an element, Array.Clone, GetEnumerator -
            // the receiver is the handle, and the member reaches it. Held to every rule below.
            if (IsUiAutomationCall(symbol) || IsCallOnHandle(expression, model))
            {
                accesses++;
                // A METHOD IS CALLED, NEVER CAPTURED: a method group taken inside the boundary is
                // invoked later, outside it, where a refusal would be a fault.
                Assert.True(symbol is not IMethodSymbol || IsInvoked(expression), $"A UI Automation method captured instead of called at line {Line(expression)}: {expression.Parent}");
                Assert.True(ExecutesInsideTheBoundary(expression, model, helpers), $"A UI Automation call outside the boundary at line {Line(expression)}: {expression.Parent}");

                // AN UNTYPED RESULT IS NOT TAKEN: a UI Automation member that answers `object`
                // (a generic property read) hands back what may be an element under a type the
                // scan cannot see; the adapter reads typed members only. An `out object` - the
                // pattern lookup - is admitted only as `out var x` narrowed at once by `is T t`,
                // and never used otherwise.
                var answers = symbol switch
                {
                    IPropertySymbol property => property.Type,
                    IMethodSymbol called => called.ReturnType,
                    _ => null,
                };
                Assert.True(
                    answers is null || !IsUntyped(answers),
                    $"An untyped UI Automation result at line {Line(expression)}: {expression.Parent}");

                // NO UNTYPED BUFFER IS HANDED IN EITHER: a call that fills an Array, an object or a
                // non-generic collection the caller allocated (CopyTo) moves handles into it inside
                // the library, with no conversion for the erasure check to see.
                if (symbol is IMethodSymbol filling && expression is InvocationExpressionSyntax fillingCall)
                {
                    // An `out` argument is the answer, not a buffer: it is held to the narrowing rule below.
                    foreach (var argument in fillingCall.ArgumentList.Arguments.Where(argument => !argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)))
                    {
                        var handed = model.GetTypeInfo(argument.Expression).ConvertedType;
                        Assert.True(
                            !IsUntyped(handed) && handed?.SpecialType != SpecialType.System_Array && handed is not IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Object },
                            $"An untyped buffer handed to UI Automation at line {Line(expression)} ({filling.Name}): {expression.Parent}");
                    }
                }

                // THE ADAPTER DOES NOT ENUMERATE UI AUTOMATION: it works on the focused element and
                // its patterns. A collection of elements is refused outright, wherever it appears -
                // every way of emptying one (a loop, a spread, a copy, an enumerator) is a call the
                // boundary cannot follow.
                Assert.True(
                    !IsHandleCollection(answers),
                    $"A UI Automation collection taken at line {Line(expression)}: {expression.Parent}");
                if (symbol is IMethodSymbol withOut && expression is InvocationExpressionSyntax outCall)
                {
                    foreach (var parameter in withOut.Parameters.Where(parameter => parameter.RefKind == RefKind.Out && parameter.Type.SpecialType == SpecialType.System_Object))
                    {
                        var argument = outCall.ArgumentList.Arguments.ElementAtOrDefault(parameter.Ordinal);
                        var designation = (argument?.Expression as DeclarationExpressionSyntax)?.Designation as SingleVariableDesignationSyntax;
                        Assert.True(designation is not null, $"An untyped UI Automation result taken other than as `out var` at line {Line(expression)}: {expression.Parent}");
                        var local = model.GetDeclaredSymbol(designation!);
                        var narrowings = adapter.DescendantNodes().OfType<IdentifierNameSyntax>()
                            .Where(name => name.Identifier.Text == designation!.Identifier.Text && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(name).Symbol, local))
                            .ToArray();
                        Assert.True(narrowings.Length > 0, $"An untyped UI Automation result never narrowed at line {Line(expression)}: {expression.Parent}");
                        foreach (var reference in narrowings)
                        {
                            Assert.True(
                                reference.Parent is IsPatternExpressionSyntax { Pattern: DeclarationPatternSyntax } && ExecutesInsideTheBoundary(reference, model, helpers),
                                $"An untyped UI Automation result used before it is narrowed at line {Line(reference)}: {reference.Parent}");
                        }
                    }
                }
            }
            else if (symbol is IMethodSymbol { ContainingType.Name: "WindowsTextTargetAdapter" } method && helperReferences.TryGetValue(method.Name, out var uses))
            {
                Assert.True(IsInvoked(expression), $"{method.Name} captured instead of called at line {Line(expression)}: {expression.Parent}");
                uses.Add(expression);
            }
        }

        Assert.True(accesses >= 20, $"the scan resolved only {accesses} UI Automation calls; the adapter makes more than that, so the resolution is broken");

        // A PATTERN IS A CONVERSION WITHOUT A CAST: `element is object snapshot` binds the handle to
        // an object-typed local with no expression for the erasure check to see. A pattern whose
        // input carries a handle may narrow it only to a type that still carries one; a null test
        // narrows to nothing and is fine.
        foreach (var pattern in adapter.DescendantNodes().OfType<PatternSyntax>())
        {
            var info = model.GetTypeInfo(pattern);
            if (!IsHandleType(info.Type) || pattern is ConstantPatternSyntax or UnaryPatternSyntax or DiscardPatternSyntax)
            {
                continue;
            }

            var narrowed = pattern switch
            {
                DeclarationPatternSyntax declared => model.GetTypeInfo(declared.Type).Type,
                RecursivePatternSyntax { Type: { } named } => model.GetTypeInfo(named).Type,
                _ => info.ConvertedType ?? info.Type,
            };
            Assert.True(IsHandleType(narrowed), $"A UI Automation handle erased at line {Line(pattern)} (pattern {pattern}): {pattern.Parent}");
        }

        // A DECONSTRUCTION IS A CONVERSION PER COMPONENT: `(object key, var facts) = captured` erases
        // one element of a handle-bearing tuple while the destination as a whole still carries one.
        // A handle-bearing value is not deconstructed at all; the string-only tuple the adapter
        // takes apart carries none.
        foreach (var assignment in adapter.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            var deconstructs = assignment.Left is TupleExpressionSyntax || assignment.Left is DeclarationExpressionSyntax { Designation: ParenthesizedVariableDesignationSyntax };
            Assert.True(
                !deconstructs || !IsHandleType(model.GetTypeInfo(assignment.Right).Type),
                $"A UI Automation handle deconstructed at line {Line(assignment)}: {assignment}");
        }

        foreach (var loop in adapter.DescendantNodes().OfType<ForEachVariableStatementSyntax>())
        {
            Assert.True(
                !IsHandleType(model.GetForEachStatementInfo(loop).ElementType),
                $"A UI Automation handle deconstructed at line {Line(loop)}: foreach ({loop.Variable} in {loop.Expression})");
        }

        // A SPREAD IS A LOOP WITHOUT A KEYWORD: `[.. collection]` enumerates a handle-bearing
        // collection into whatever element type the target names, `object[]` included. It is refused
        // wherever it stands; the adapter materialises nothing from UI Automation.
        foreach (var spread in adapter.DescendantNodes().OfType<SpreadElementSyntax>())
        {
            Assert.True(!IsHandleType(model.GetTypeInfo(spread.Expression).Type), $"A UI Automation collection spread at line {Line(spread)}: {spread}");
        }

        // A LOOP IS A CALL TOO: `foreach` over a handle-bearing collection asks it for an enumerator
        // and each element - calls the expression scan does not see - so it happens inside the
        // boundary or not at all, and an element it yields as `object` is an untyped result.
        foreach (var loop in adapter.DescendantNodes().OfType<ForEachStatementSyntax>())
        {
            if (!IsHandleType(model.GetTypeInfo(loop.Expression).Type))
            {
                continue;
            }

            Assert.True(ExecutesInsideTheBoundary(loop.Expression, model, helpers), $"A UI Automation collection enumerated outside the boundary at line {Line(loop)}: {loop.Expression}");
            Assert.True(!IsUntyped(model.GetForEachStatementInfo(loop).ElementType), $"An untyped UI Automation result at line {Line(loop)}: foreach over {loop.Expression}");
            // THE LOOP VARIABLE KEEPS THE ELEMENT'S TYPE: `foreach (object child in handles)` erases
            // each element as it is bound, with no expression for the erasure check to see.
            var variable = model.GetDeclaredSymbol(loop) as ILocalSymbol;
            Assert.True(
                variable is not null && IsHandleType(variable.Type),
                $"A UI Automation handle erased at line {Line(loop)}: foreach ({loop.Type} {loop.Identifier.Text} in {loop.Expression})");
        }

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

    /// <summary>
    /// Whether a type is, or carries, a live UI Automation object: an element, a pattern, a text range
    /// - or a tuple, an array, a generic instantiation or one of the adapter's own records with one
    /// inside. A compound value carries the handle out as surely as the handle itself: a tuple's
    /// GetHashCode hashes its element through AutomationElement.GetHashCode. Identifiers, constants
    /// and exceptions of the same namespaces are not handles.
    /// </summary>
    private static bool IsHandleType(ITypeSymbol? type) => CarriesHandle(type, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default));

    private static bool CarriesHandle(ITypeSymbol? type, HashSet<ITypeSymbol> visited)
    {
        if (type is null || !visited.Add(type))
        {
            return false;
        }

        switch (type)
        {
            case IArrayTypeSymbol array:
                return CarriesHandle(array.ElementType, visited);
            case INamedTypeSymbol { IsTupleType: true } tuple:
                return tuple.TupleElements.Any(element => CarriesHandle(element.Type, visited));
            case INamedTypeSymbol named:
                if (IsBareHandleType(named))
                {
                    return true;
                }

                if (named.TypeArguments.Any(argument => CarriesHandle(argument, visited)))
                {
                    return true;
                }

                // THE ADAPTER'S OWN RECORDS AND CLASSES ARE OPENED, BASES INCLUDED: a source-declared
                // type with a handle-typed field or property carries it, and so does one that
                // inherits such a member - a record's generated hash takes in its base's state. A
                // library type's fields are its own.
                if (named.Locations.Any(location => location.IsInSource) &&
                    named.GetMembers().Any(member => member switch
                    {
                        IFieldSymbol { IsStatic: false } field => CarriesHandle(field.Type, visited),
                        IPropertySymbol { IsStatic: false } property => CarriesHandle(property.Type, visited),
                        _ => false,
                    }))
                {
                    return true;
                }

                return CarriesHandle(named.BaseType, visited);
            default:
                return false;
        }
    }

    private static bool IsBareHandleType(INamedTypeSymbol named)
    {
        if (named is not { TypeKind: TypeKind.Class or TypeKind.Struct } ||
            !(named.ContainingNamespace?.ToDisplayString() ?? string.Empty).StartsWith("System.Windows.Automation", StringComparison.Ordinal))
        {
            return false;
        }

        for (var ancestor = named; ancestor is not null; ancestor = ancestor.BaseType)
        {
            if (ancestor.Name is "Exception" or "AutomationIdentifier")
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The only shapes a live UI Automation object may take outside the boundary: the boundary's own
    /// result, a null test, an argument to the adapter's own method - and in every one of them the
    /// object keeps its own type. A conversion to object or to a type parameter erases what the scan
    /// needs to see: `object cached = Automation(() => element)` and `ElementHash(element)` over an
    /// `object` parameter both reach AutomationElement.GetHashCode, which reads the runtime id.
    /// </summary>
    private static bool HandleUseIsAllowedOutside(ExpressionSyntax expression, SemanticModel model)
    {
        if (!IsHandleType(model.GetTypeInfo(expression).ConvertedType))
        {
            return false;
        }

        switch (expression)
        {
            case InvocationExpressionSyntax invocation:
                return invocation.Expression is IdentifierNameSyntax { Identifier.Text: "Automation" } && ResolvesToTheAdapter(model.GetSymbolInfo(invocation));
            case IdentifierNameSyntax identifier:
                return identifier.Parent switch
                {
                    IsPatternExpressionSyntax { Pattern: ConstantPatternSyntax { Expression: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression } } } => true,
                    IsPatternExpressionSyntax { Pattern: UnaryPatternSyntax { Pattern: ConstantPatternSyntax { Expression: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression } } } } => true,
                    BinaryExpressionSyntax { RawKind: (int)SyntaxKind.EqualsExpression or (int)SyntaxKind.NotEqualsExpression } comparison
                        when comparison.Left is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression } || comparison.Right is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression } => true,
                    ArgumentSyntax { Parent.Parent: InvocationExpressionSyntax call } argument =>
                        model.GetSymbolInfo(call).Symbol is IMethodSymbol { ContainingType.Name: "WindowsTextTargetAdapter", IsGenericMethod: false } method &&
                        IsHandleType(ParameterFor(method, argument)?.Type),
                    _ => false,
                };
            default:
                return false;
        }
    }

    /// <summary>Whether a type is a tuple that carries a handle, directly or in a nested tuple, array or type argument.</summary>
    private static bool CarriesHandleInTuple(ITypeSymbol? type) => type switch
    {
        INamedTypeSymbol { IsTupleType: true } tuple => IsHandleType(tuple),
        INamedTypeSymbol named => named.TypeArguments.Any(CarriesHandleInTuple),
        IArrayTypeSymbol array => CarriesHandleInTuple(array.ElementType),
        _ => false,
    };

    /// <summary>Whether a member access or binding is made on a receiver that is, or carries, a handle.</summary>
    private static bool IsCallOnHandle(ExpressionSyntax expression, SemanticModel model)
    {
        var receiver = expression switch
        {
            InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } => access.Expression,
            MemberAccessExpressionSyntax access => access.Expression,
            InvocationExpressionSyntax { Expression: MemberBindingExpressionSyntax binding } => binding.Ancestors().OfType<ConditionalAccessExpressionSyntax>().FirstOrDefault()?.Expression,
            MemberBindingExpressionSyntax binding => binding.Ancestors().OfType<ConditionalAccessExpressionSyntax>().FirstOrDefault()?.Expression,
            _ => null,
        };
        return receiver is not null && model.GetSymbolInfo(receiver).Symbol is not ITypeSymbol && IsHandleType(model.GetTypeInfo(receiver).Type);
    }

    /// <summary>A UI Automation type that is itself a collection of handles - AutomationElementCollection and its kind.</summary>
    private static bool IsHandleCollection(ITypeSymbol? type) =>
        type is INamedTypeSymbol named && IsBareHandleType(named) &&
        named.AllInterfaces.Any(contract => contract.ToDisplayString() is "System.Collections.IEnumerable" or "System.Collections.ICollection");

    /// <summary>A type that says nothing about what it holds: object, or a non-generic collection interface whose elements come out as object.</summary>
    private static bool IsUntyped(ITypeSymbol? type) =>
        type is not null &&
        (type.SpecialType == SpecialType.System_Object ||
         (type is INamedTypeSymbol { TypeKind: TypeKind.Interface, IsGenericType: false } && type.ContainingNamespace?.ToDisplayString() == "System.Collections"));

    /// <summary>The parameter an argument binds to, by name or by position.</summary>
    private static IParameterSymbol? ParameterFor(IMethodSymbol method, ArgumentSyntax argument)
    {
        if (argument.NameColon is { } name)
        {
            return method.Parameters.FirstOrDefault(parameter => parameter.Name == name.Name.Identifier.Text);
        }

        var index = ((ArgumentListSyntax)argument.Parent!).Arguments.IndexOf(argument);
        return index >= 0 && index < method.Parameters.Length ? method.Parameters[index] : null;
    }

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
            TextDeliveryOptions.Default,
            SnippetExpanded: false));

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
            TextDeliveryOptions.Default,
            SnippetExpanded: false));

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
            TextDeliveryOptions.Default,
            SnippetExpanded: false));

        Assert.Equal(TextDeliveryRefusalReason.AccessibilityUnavailable, result.RefusalReason);
        Assert.True(result.ClipboardFallback);
        Assert.Null(result.Fault);
        Assert.Null(delivery.RecoveryText);
    }

    [ClipboardFact]
    public async Task ARefusedPasteWhoseFallbackCannotBeWrittenGivesTheClipboardBack()
    {
        // #242, THE PRODUCTION PASTE ON THE REAL CLIPBOARD. The insertion goes on the clipboard, the
        // preflight refuses, and the fallback write fails (a null payload is refused by the clipboard
        // itself, before it is touched). Nothing was pasted and no fallback landed, so the person's
        // clipboard is put back: the sentinel placed before the paste is there after it.
        const string sentinel = "EnviousWispr sentinel copied before the paste";
        using var guard = ClipboardGuard.Capture();
        ClipboardGuard.SetText(sentinel);
        string? duringPreflight = null;

        var pasted = await WindowsClipboardPaste.PasteAsync(
            "dictated words",
            null!,
            restoreClipboard: true,
            () =>
            {
                duringPreflight = ClipboardGuard.GetText();
                return TextDeliveryRefusalReason.TargetChanged;
            },
            CancellationToken.None);

        Assert.Equal("dictated words", duringPreflight);
        Assert.Equal(sentinel, ClipboardGuard.GetText());
        Assert.Equal(TextDeliveryRoute.None, pasted.Route);
        Assert.Equal(TextDeliveryRefusalReason.ClipboardUnavailable, pasted.RefusalReason);
        Assert.False(pasted.Delivered);
        Assert.False(pasted.ClipboardFallback);
        Assert.True(pasted.ClipboardRestored);
        Assert.False(pasted.ClipboardUncertain);
    }

    [ClipboardFact]
    public async Task ARefusedPasteWhoseClipboardCannotBeRestoredSaysTheClipboardIsUncertain()
    {
        // #242: ANOTHER HOLDER HAS THE CLIPBOARD OPEN from the moment the preflight refuses until the paste
        // has answered - the real way a fallback write fails. The restore fails the same way, and the
        // result says the clipboard is uncertain rather than a plain failure: once the holder lets go it
        // still holds the dictated words, not the sentinel the person had.
        const string sentinel = "EnviousWispr sentinel copied before the paste";
        using var guard = ClipboardGuard.Capture();
        ClipboardGuard.SetText(sentinel);
        using var holder = new ClipboardHolder();

        var pasted = await WindowsClipboardPaste.PasteAsync(
            "dictated words",
            "dictated words ",
            restoreClipboard: true,
            () =>
            {
                holder.Open();
                return TextDeliveryRefusalReason.TargetChanged;
            },
            CancellationToken.None);
        holder.Dispose();

        Assert.True(holder.Opened, "the clipboard was held open, so both writes met a held clipboard");
        Assert.Equal(TextDeliveryRoute.None, pasted.Route);
        Assert.Equal(TextDeliveryRefusalReason.ClipboardUnavailable, pasted.RefusalReason);
        Assert.False(pasted.ClipboardFallback);
        Assert.False(pasted.ClipboardRestored);
        Assert.True(pasted.ClipboardUncertain);
        Assert.Equal("dictated words", ClipboardGuard.GetText());
    }

    [ClipboardFact]
    public async Task ARefusedPasteNeverOverwritesAClipboardThePersonChangedMeanwhile()
    {
        // #242, THE SEQUENCE-NUMBER GUARD: the person copies something while the paste is deciding. The
        // fallback write then fails, and the give-back must not undo the person's copy to undo ours - the
        // clipboard keeps what they copied, and that is neither a restore nor an uncertain clipboard.
        const string sentinel = "EnviousWispr sentinel copied before the paste";
        const string meanwhile = "EnviousWispr text the person copied meanwhile";
        using var guard = ClipboardGuard.Capture();
        ClipboardGuard.SetText(sentinel);

        var pasted = await WindowsClipboardPaste.PasteAsync(
            "dictated words",
            null!,
            restoreClipboard: true,
            () =>
            {
                ClipboardGuard.SetText(meanwhile);
                return TextDeliveryRefusalReason.TargetChanged;
            },
            CancellationToken.None);

        Assert.Equal(meanwhile, ClipboardGuard.GetText());
        Assert.Equal(TextDeliveryRefusalReason.ClipboardUnavailable, pasted.RefusalReason);
        Assert.False(pasted.ClipboardRestored);
        Assert.False(pasted.ClipboardUncertain);
    }

    [ClipboardFact]
    public async Task AnInsertionWriteThatEmptiedTheClipboardAndThenFailedGivesTheClipboardBack()
    {
        // #242: THE FIRST WRITE FAILS AFTER EMPTYING THE CLIPBOARD, as OleSetClipboard can (it empties before
        // it offers a format) - here the real clipboard is really emptied on the paste's own thread, then the
        // write throws. The empty is our own change, so the person's clipboard is put back.
        const string sentinel = "EnviousWispr sentinel copied before the paste";
        using var guard = ClipboardGuard.Capture();
        ClipboardGuard.SetText(sentinel);

        var pasted = await WindowsClipboardPaste.PasteAsync(
            "dictated words",
            "dictated words ",
            restoreClipboard: true,
            static () => TextDeliveryRefusalReason.None,
            EmptyThenFailFor("dictated words"),
            CancellationToken.None);

        Assert.Equal(sentinel, ClipboardGuard.GetText());
        Assert.Equal(TextDeliveryRoute.None, pasted.Route);
        Assert.Equal(TextDeliveryRefusalReason.ClipboardUnavailable, pasted.RefusalReason);
        Assert.False(pasted.Delivered);
        Assert.True(pasted.ClipboardRestored);
        Assert.False(pasted.ClipboardUncertain);
    }

    [ClipboardFact]
    public async Task ARefusedPasteWhoseFallbackWriteEmptiedTheClipboardAndThenFailedGivesTheClipboardBack()
    {
        // #242: THE INSERTION LANDS, THE PREFLIGHT REFUSES, AND THE FALLBACK WRITE EMPTIES THE CLIPBOARD AND THEN
        // FAILS. The sequence number moved past the insertion's by our own hand; that is still ours, so the
        // person's clipboard is put back rather than left empty.
        const string sentinel = "EnviousWispr sentinel copied before the paste";
        using var guard = ClipboardGuard.Capture();
        ClipboardGuard.SetText(sentinel);

        var pasted = await WindowsClipboardPaste.PasteAsync(
            "dictated words",
            "dictated words ",
            restoreClipboard: true,
            static () => TextDeliveryRefusalReason.TargetChanged,
            EmptyThenFailFor("dictated words "),
            CancellationToken.None);

        Assert.Equal(sentinel, ClipboardGuard.GetText());
        Assert.Equal(TextDeliveryRefusalReason.ClipboardUnavailable, pasted.RefusalReason);
        Assert.False(pasted.ClipboardFallback);
        Assert.True(pasted.ClipboardRestored);
        Assert.False(pasted.ClipboardUncertain);
    }

    [ClipboardFact]
    public async Task AnInsertionWriteThatEmptiedTheClipboardAndCannotBeRestoredSaysTheClipboardIsUncertain()
    {
        // #242: THE SAME EMPTY-THEN-FAIL, with another holder taking the clipboard before the paste can put it
        // back. The restore fails, and the result says the clipboard is uncertain - not a plain failure.
        const string sentinel = "EnviousWispr sentinel copied before the paste";
        using var guard = ClipboardGuard.Capture();
        ClipboardGuard.SetText(sentinel);
        using var holder = new ClipboardHolder();

        var pasted = await WindowsClipboardPaste.PasteAsync(
            "dictated words",
            "dictated words ",
            restoreClipboard: true,
            static () => TextDeliveryRefusalReason.None,
            text =>
            {
                System.Windows.Forms.Clipboard.Clear();
                holder.Open();
                Marshal.ThrowExceptionForHR(ClipboardCantClose); // what OleSetClipboard answers when it fails after emptying
            },
            CancellationToken.None);
        holder.Dispose();

        Assert.True(holder.Opened, "the clipboard was held open, so the restore met a held clipboard");
        Assert.Equal(TextDeliveryRefusalReason.ClipboardUnavailable, pasted.RefusalReason);
        Assert.False(pasted.ClipboardRestored);
        Assert.True(pasted.ClipboardUncertain);
        Assert.Null(ClipboardGuard.GetText());
    }

    [ClipboardFact]
    public async Task AFailedWriteDuringWhichSomebodyElseCopiedLeavesTheirCopyAlone()
    {
        // #242, THE OWNER HALF OF THE GUARD: the insertion write fails having changed nothing itself, while
        // somebody else (another thread, standing in for another app) copies. The sequence number moved, but
        // not by our hand, so nothing is restored over their copy and nothing is uncertain.
        const string sentinel = "EnviousWispr sentinel copied before the paste";
        const string meanwhile = "EnviousWispr text the person copied meanwhile";
        using var guard = ClipboardGuard.Capture();
        ClipboardGuard.SetText(sentinel);

        var pasted = await WindowsClipboardPaste.PasteAsync(
            "dictated words",
            "dictated words ",
            restoreClipboard: true,
            static () => TextDeliveryRefusalReason.None,
            text =>
            {
                ClipboardGuard.SetText(meanwhile);
                Marshal.ThrowExceptionForHR(ClipboardCantOpen);
            },
            CancellationToken.None);

        Assert.Equal(meanwhile, ClipboardGuard.GetText());
        Assert.Equal(TextDeliveryRefusalReason.ClipboardUnavailable, pasted.RefusalReason);
        Assert.False(pasted.ClipboardRestored);
        Assert.False(pasted.ClipboardUncertain);
    }

    /// <summary>CLIPBRD_E_CANT_OPEN: OleSetClipboard could not open the clipboard, before changing anything.</summary>
    private const int ClipboardCantOpen = unchecked((int)0x800401D0);

    /// <summary>CLIPBRD_E_CANT_CLOSE: OleSetClipboard failed after it had opened and emptied the clipboard.</summary>
    private const int ClipboardCantClose = unchecked((int)0x800401D4);

    /// <summary>A clipboard writer that, for the named payload only, really empties the clipboard on the calling thread and then fails; every other payload is written for real.</summary>
    private static Action<string> EmptyThenFailFor(string failingPayload) => text =>
    {
        if (text == failingPayload)
        {
            System.Windows.Forms.Clipboard.Clear();
            Marshal.ThrowExceptionForHR(ClipboardCantClose); // what OleSetClipboard answers when it fails after emptying
        }

        System.Windows.Forms.Clipboard.SetDataObject(text, copy: true, retryTimes: 10, retryDelay: 50);
    };

    /// <summary>Holds the clipboard open from a thread of its own, as another app would, until disposed.</summary>
    private sealed class ClipboardHolder : IDisposable
    {
        private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
        private readonly ManualResetEventSlim _opened = new();
        private readonly ManualResetEventSlim _release = new();
        private Thread? _thread;

        public bool Opened { get; private set; }

        /// <summary>Opens the clipboard on the holder's thread and returns once it is open (or could not be).</summary>
        public void Open()
        {
            _thread = new Thread(() =>
            {
                Opened = OpenClipboard(IntPtr.Zero);
                _opened.Set();
                if (Opened)
                {
                    // deadline-fallback: released by Dispose; the deadline only bounds a test that never disposes.
                    _release.Wait(Deadline);
                    CloseClipboard();
                }
            })
            {
                IsBackground = true,
            };
            _thread.Start();
            if (!_opened.Wait(Deadline))
            {
                throw new TimeoutException("The clipboard holder did not report within its deadline.");
            }
        }

        public void Dispose()
        {
            _release.Set();
            _thread?.Join(Deadline);
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr newOwner);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseClipboard();
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
                request.FallbackText.Text,
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
            TextDeliveryOptions.Default,
            SnippetExpanded: false));

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
