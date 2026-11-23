using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace EnviousWispr.Analyzers;

/// <summary>
/// Refuses C# declaration and call shapes that infer a pill appearance from a sentence, omit a
/// dictation scope on an async dictation flow, or hand a status to the window without naming its
/// pill.
/// </summary>
/// <remarks>
/// THE COMPILER'S SEMANTIC MODEL ANSWERS WHAT A DECLARATION IS. Text and syntax-only gates could
/// not resolve an imported named delegate declared outside the scanned tree, a lambda assigned to
/// <c>var</c> with inferred return type, a function pointer, a <c>var</c> or pattern-bound local
/// holding a <c>DictationStatus</c>, a <c>using</c> alias for a task or status type, a
/// <c>ValueTask</c> flow, or a third-party type sharing one of these names in another namespace.
/// Ref: #82.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DeclarationShapeAnalyzer : DiagnosticAnalyzer
{
    public const string SentenceToAppearanceDiagnosticId = "EW0001";
    public const string DictationFlowScopeDiagnosticId = "EW0002";
    public const string StatusPillNamingDiagnosticId = "EW0003";

    private const string DictationStatusMetadataName = "EnviousWispr.Core.Presentation.DictationStatus";
    private const string DictationOverlayStateMetadataName = "EnviousWispr.Core.Presentation.DictationOverlayState";
    private const string DictationSessionIdMetadataName = "EnviousWispr.Core.Dictation.DictationSessionId";
    private const string CapturedAudioMetadataName = "EnviousWispr.Core.Dictation.CapturedAudio";
    private const string SessionCommandMetadataName = "EnviousWispr.Pipeline.SessionCommand";
    private const string DictationScopeMetadataName = "EnviousWispr.Core.Diagnostics.DictationScope";
    private const string AppLoggerMetadataName = "EnviousWispr.Core.Diagnostics.IAppLogger";

    public static readonly DiagnosticDescriptor SentenceToAppearanceRule = new(
        id: SentenceToAppearanceDiagnosticId,
        title: "Do not turn a status sentence into a pill appearance",
        messageFormat: "'{0}' is handed a string and hands back a pill appearance; build DictationStatus where the outcome is known and pass it through instead",
        category: "Architecture",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "No member, callable, delegate, or function pointer outside DictationStatus may map a string to DictationStatus or DictationOverlayState.");

    public static readonly DiagnosticDescriptor DictationFlowScopeRule = new(
        id: DictationFlowScopeDiagnosticId,
        title: "Every dictation flow opens its DictationScope before logging",
        messageFormat: "Dictation flow '{0}' is handed a dictation and must call DictationScope.Begin before logging",
        category: "Architecture",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Every async task-like method taking a DictationSessionId, CapturedAudio, or SessionCommand must open a DictationScope before writing any log entry.");

    public static readonly DiagnosticDescriptor StatusPillNamingRule = new(
        id: StatusPillNamingDiagnosticId,
        title: "Every status handed to the window names its pill",
        messageFormat: "Status argument '{0}' reaches the window without naming its pill via a DictationStatus factory or a verified DictationStatus carrier",
        category: "Architecture",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Every call to SetSessionStatus or ShowStatus must pass a DictationStatus built by a named DictationStatus factory or held by a compiler-verified DictationStatus carrier.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(
            SentenceToAppearanceRule,
            DictationFlowScopeRule,
            StatusPillNamingRule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(startContext =>
        {
            var compilation = startContext.Compilation;
            var statusType = compilation.GetTypeByMetadataName(DictationStatusMetadataName);
            var overlayStateType = compilation.GetTypeByMetadataName(DictationOverlayStateMetadataName);
            var sessionIdType = compilation.GetTypeByMetadataName(DictationSessionIdMetadataName);
            var capturedAudioType = compilation.GetTypeByMetadataName(CapturedAudioMetadataName);
            var sessionCommandType = compilation.GetTypeByMetadataName(SessionCommandMetadataName);
            var dictationScopeType = compilation.GetTypeByMetadataName(DictationScopeMetadataName);
            var appLoggerType = compilation.GetTypeByMetadataName(AppLoggerMetadataName);

            var symbols = new KnownSymbols(
                compilation,
                statusType,
                overlayStateType,
                sessionIdType,
                capturedAudioType,
                sessionCommandType,
                dictationScopeType,
                appLoggerType);

            if (statusType is not null || overlayStateType is not null)
            {
                startContext.RegisterSyntaxNodeAction(
                    nodeContext => AnalyzeSentenceToAppearanceDeclaration(nodeContext, symbols),
                    SyntaxKind.MethodDeclaration,
                    SyntaxKind.LocalFunctionStatement,
                    SyntaxKind.IndexerDeclaration,
                    SyntaxKind.OperatorDeclaration,
                    SyntaxKind.ConversionOperatorDeclaration,
                    SyntaxKind.DelegateDeclaration,
                    SyntaxKind.SimpleLambdaExpression,
                    SyntaxKind.ParenthesizedLambdaExpression,
                    SyntaxKind.AnonymousMethodExpression,
                    SyntaxKind.VariableDeclaration,
                    SyntaxKind.PropertyDeclaration,
                    SyntaxKind.Parameter,
                    SyntaxKind.EventDeclaration,
                    SyntaxKind.EventFieldDeclaration,
                    SyntaxKind.UsingDirective);
            }

            if (sessionIdType is not null || capturedAudioType is not null || sessionCommandType is not null)
            {
                startContext.RegisterSyntaxNodeAction(
                    nodeContext => AnalyzeDictationFlow(nodeContext, symbols),
                    SyntaxKind.MethodDeclaration);
            }

            if (statusType is not null)
            {
                startContext.RegisterSyntaxNodeAction(
                    nodeContext => AnalyzeStatusSinkInvocation(nodeContext, symbols),
                    SyntaxKind.InvocationExpression);
                startContext.RegisterSyntaxNodeAction(
                    nodeContext => AnalyzeStatusReturningMethod(nodeContext, symbols),
                    SyntaxKind.MethodDeclaration);
            }
        });
    }

    private static void AnalyzeSentenceToAppearanceDeclaration(
        SyntaxNodeAnalysisContext context,
        KnownSymbols symbols)
    {
        var node = context.Node;
        var model = context.SemanticModel;

        if (IsInsideDictationStatus(node, model, symbols.StatusType))
        {
            return;
        }

        switch (node)
        {
            case MethodDeclarationSyntax method:
            {
                if (model.GetDeclaredSymbol(method, context.CancellationToken) is IMethodSymbol symbol &&
                    (IsSentenceToAppearanceCallable(symbol.ReturnType, symbol.Parameters, symbols) ||
                     IsSentenceToAppearanceTypeSite(symbol.ReturnType, symbols)))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        SentenceToAppearanceRule,
                        method.Identifier.GetLocation(),
                        DescribeHeader(method)));
                }

                break;
            }

            case LocalFunctionStatementSyntax local:
            {
                if (model.GetDeclaredSymbol(local, context.CancellationToken) is IMethodSymbol symbol &&
                    (IsSentenceToAppearanceCallable(symbol.ReturnType, symbol.Parameters, symbols) ||
                     IsSentenceToAppearanceTypeSite(symbol.ReturnType, symbols)))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        SentenceToAppearanceRule,
                        local.Identifier.GetLocation(),
                        DescribeHeader(local)));
                }

                break;
            }

            case IndexerDeclarationSyntax indexer:
            {
                if (model.GetDeclaredSymbol(indexer, context.CancellationToken) is IPropertySymbol symbol &&
                    (IsSentenceToAppearanceCallable(symbol.Type, symbol.Parameters, symbols) ||
                     IsSentenceToAppearanceTypeSite(symbol.Type, symbols)))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        SentenceToAppearanceRule,
                        indexer.ThisKeyword.GetLocation(),
                        DescribeHeader(indexer)));
                }

                break;
            }

            case OperatorDeclarationSyntax op:
            {
                if (model.GetDeclaredSymbol(op, context.CancellationToken) is IMethodSymbol symbol &&
                    (IsSentenceToAppearanceCallable(symbol.ReturnType, symbol.Parameters, symbols) ||
                     IsSentenceToAppearanceTypeSite(symbol.ReturnType, symbols)))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        SentenceToAppearanceRule,
                        op.OperatorToken.GetLocation(),
                        DescribeHeader(op)));
                }

                break;
            }

            case ConversionOperatorDeclarationSyntax conversion:
            {
                if (model.GetDeclaredSymbol(conversion, context.CancellationToken) is IMethodSymbol symbol &&
                    (IsSentenceToAppearanceCallable(symbol.ReturnType, symbol.Parameters, symbols) ||
                     IsSentenceToAppearanceTypeSite(symbol.ReturnType, symbols)))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        SentenceToAppearanceRule,
                        conversion.Type.GetLocation(),
                        DescribeHeader(conversion)));
                }

                break;
            }

            case DelegateDeclarationSyntax nominal:
            {
                if (model.GetDeclaredSymbol(nominal, context.CancellationToken) is INamedTypeSymbol symbol &&
                    symbol.DelegateInvokeMethod is { } invoke &&
                    (IsSentenceToAppearanceCallable(invoke.ReturnType, invoke.Parameters, symbols) ||
                     IsSentenceToAppearanceTypeSite(invoke.ReturnType, symbols)))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        SentenceToAppearanceRule,
                        nominal.Identifier.GetLocation(),
                        DescribeHeader(nominal)));
                }

                break;
            }

            case AnonymousFunctionExpressionSyntax anonymous:
            {
                if (model.GetSymbolInfo(anonymous, context.CancellationToken).Symbol is IMethodSymbol symbol &&
                    IsSentenceToAppearanceCallable(symbol.ReturnType, symbol.Parameters, symbols))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        SentenceToAppearanceRule,
                        anonymous.GetLocation(),
                        DescribeHeader(anonymous)));
                }
                else if (anonymous is ParenthesizedLambdaExpressionSyntax { ReturnType: { } returnTypeSyntax } lambda)
                {
                    var returnType = model.GetTypeInfo(returnTypeSyntax, context.CancellationToken).Type;
                    var hasStringParam = false;
                    foreach (var parameter in lambda.ParameterList.Parameters)
                    {
                        if (parameter.Type is not null &&
                            IsStringType(model.GetTypeInfo(parameter.Type, context.CancellationToken).Type))
                        {
                            hasStringParam = true;
                            break;
                        }
                    }

                    if (hasStringParam && ContainsAppearanceType(returnType, symbols))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            SentenceToAppearanceRule,
                            lambda.GetLocation(),
                            DescribeHeader(lambda)));
                    }
                }

                break;
            }

            case VariableDeclarationSyntax variable:
                ReportIfTypeSiteIsSentenceToAppearance(context, variable.Type, symbols);
                break;

            case PropertyDeclarationSyntax property:
                ReportIfTypeSiteIsSentenceToAppearance(context, property.Type, symbols);
                break;

            case ParameterSyntax { Type: { } parameterType }:
                ReportIfTypeSiteIsSentenceToAppearance(context, parameterType, symbols);
                break;

            case EventDeclarationSyntax eventDeclaration:
                ReportIfTypeSiteIsSentenceToAppearance(context, eventDeclaration.Type, symbols);
                break;

            case EventFieldDeclarationSyntax eventField:
                ReportIfTypeSiteIsSentenceToAppearance(context, eventField.Declaration.Type, symbols);
                break;

            case UsingDirectiveSyntax { Alias: not null, NamespaceOrType: { } aliasTarget }:
                ReportIfTypeSiteIsSentenceToAppearance(context, aliasTarget, symbols);
                break;
        }
    }

    private static void ReportIfTypeSiteIsSentenceToAppearance(
        SyntaxNodeAnalysisContext context,
        SyntaxNode typeSyntax,
        KnownSymbols symbols)
    {
        var type = context.SemanticModel.GetTypeInfo(typeSyntax, context.CancellationToken).Type
            ?? context.SemanticModel.GetSymbolInfo(typeSyntax, context.CancellationToken).Symbol as ITypeSymbol;
        if (IsSentenceToAppearanceTypeSite(type, symbols))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                SentenceToAppearanceRule,
                typeSyntax.GetLocation(),
                typeSyntax.ToString()));
        }
    }

    /// <summary>
    /// Whether a callable signature takes a <see cref="string"/> and returns a pill appearance
    /// (<c>DictationStatus</c> or <c>DictationOverlayState</c>, bare or wrapped).
    /// </summary>
    public static bool IsSentenceToAppearanceCallable(
        ITypeSymbol? returnType,
        ImmutableArray<IParameterSymbol> parameters,
        KnownSymbols symbols)
    {
        if (!ContainsAppearanceType(returnType, symbols))
        {
            return false;
        }

        foreach (var parameter in parameters)
        {
            if (IsStringType(parameter.Type))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a type written at a declaration site is (or wraps) a delegate or function pointer
    /// that turns a <see cref="string"/> into a pill appearance.
    /// </summary>
    /// <remarks>
    /// A NON-DELEGATE GENERIC SUCH AS <c>Dictionary&lt;string, DictationStatus&gt;</c> IS NOT A
    /// CALLABLE. It only triggers if one of its type arguments is itself a callable delegate or
    /// function pointer of the forbidden shape (such as <c>List&lt;Func&lt;string, DictationStatus&gt;&gt;</c>).
    /// </remarks>
    public static bool IsSentenceToAppearanceTypeSite(ITypeSymbol? type, KnownSymbols symbols)
    {
        if (type is null)
        {
            return false;
        }

        if (type is IFunctionPointerTypeSymbol functionPointer)
        {
            return IsSentenceToAppearanceCallable(
                functionPointer.Signature.ReturnType,
                functionPointer.Signature.Parameters,
                symbols);
        }

        if (type is INamedTypeSymbol { TypeKind: TypeKind.Delegate, DelegateInvokeMethod: { } invoke })
        {
            return IsSentenceToAppearanceCallable(invoke.ReturnType, invoke.Parameters, symbols);
        }

        if (type is IArrayTypeSymbol array)
        {
            return IsSentenceToAppearanceTypeSite(array.ElementType, symbols);
        }

        if (type is IPointerTypeSymbol pointer)
        {
            return IsSentenceToAppearanceTypeSite(pointer.PointedAtType, symbols);
        }

        if (type is INamedTypeSymbol named)
        {
            foreach (var argument in named.TypeArguments)
            {
                if (IsSentenceToAppearanceTypeSite(argument, symbols))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static bool IsStringType(ITypeSymbol? type) =>
        type is { SpecialType: SpecialType.System_String };

    public static bool ContainsAppearanceType(ITypeSymbol? type, KnownSymbols symbols)
    {
        if (type is null)
        {
            return false;
        }

        if ((symbols.StatusType is not null && SymbolEqualityComparer.Default.Equals(type, symbols.StatusType)) ||
            (symbols.OverlayStateType is not null && SymbolEqualityComparer.Default.Equals(type, symbols.OverlayStateType)))
        {
            return true;
        }

        if (type is IArrayTypeSymbol array)
        {
            return ContainsAppearanceType(array.ElementType, symbols);
        }

        if (type is IPointerTypeSymbol pointer)
        {
            return ContainsAppearanceType(pointer.PointedAtType, symbols);
        }

        if (type is INamedTypeSymbol { TypeKind: not TypeKind.Delegate } named)
        {
            foreach (var argument in named.TypeArguments)
            {
                if (ContainsAppearanceType(argument, symbols))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsInsideDictationStatus(
        SyntaxNode node,
        SemanticModel model,
        INamedTypeSymbol? statusType)
    {
        if (statusType is null)
        {
            return false;
        }

        var enclosing = model.GetEnclosingSymbol(node.SpanStart);
        while (enclosing is not null)
        {
            if (enclosing is INamedTypeSymbol named &&
                SymbolEqualityComparer.Default.Equals(named, statusType))
            {
                return true;
            }

            enclosing = enclosing.ContainingSymbol;
        }

        return false;
    }

    private static void AnalyzeDictationFlow(
        SyntaxNodeAnalysisContext context,
        KnownSymbols symbols)
    {
        var method = (MethodDeclarationSyntax)context.Node;
        var body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
        if (body is null)
        {
            return;
        }

        var model = context.SemanticModel;
        if (model.GetDeclaredSymbol(method, context.CancellationToken) is not IMethodSymbol symbol ||
            !IsDictationFlow(method, symbol, symbols))
        {
            return;
        }

        InvocationExpressionSyntax? firstWrite = null;
        InvocationExpressionSyntax? opensScope = null;

        foreach (var call in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (opensScope is null && IsDictationScopeBegin(call, model, symbols))
            {
                opensScope = call;
            }

            if (firstWrite is null && IsLoggerWrite(call, model, symbols))
            {
                firstWrite = call;
            }
        }

        if (opensScope is null ||
            (firstWrite is not null && opensScope.SpanStart > firstWrite.SpanStart))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DictationFlowScopeRule,
                method.Identifier.GetLocation(),
                method.Identifier.ValueText));
        }
    }

    /// <summary>
    /// Whether a method is an async task-like dictation flow handed a non-nullable
    /// <c>DictationSessionId</c>, <c>CapturedAudio</c>, or <c>SessionCommand</c>.
    /// </summary>
    public static bool IsDictationFlow(
        MethodDeclarationSyntax methodSyntax,
        IMethodSymbol methodSymbol,
        KnownSymbols symbols)
    {
        if (!methodSymbol.IsAsync && !methodSyntax.Modifiers.Any(SyntaxKind.AsyncKeyword))
        {
            return false;
        }

        if (!IsTaskLikeReturnType(methodSymbol.ReturnType, symbols.Compilation))
        {
            return false;
        }

        foreach (var parameter in methodSymbol.Parameters)
        {
            if (parameter.NullableAnnotation == NullableAnnotation.Annotated)
            {
                continue;
            }

            if ((symbols.SessionIdType is not null &&
                 SymbolEqualityComparer.Default.Equals(parameter.Type, symbols.SessionIdType)) ||
                (symbols.CapturedAudioType is not null &&
                 SymbolEqualityComparer.Default.Equals(parameter.Type, symbols.CapturedAudioType)) ||
                (symbols.SessionCommandType is not null &&
                 SymbolEqualityComparer.Default.Equals(parameter.Type, symbols.SessionCommandType)))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsTaskLikeReturnType(ITypeSymbol? returnType, Compilation compilation)
    {
        if (returnType is not INamedTypeSymbol named ||
            named.SpecialType == SpecialType.System_Void ||
            named.TypeKind == TypeKind.Error)
        {
            return false;
        }

        var original = named.OriginalDefinition;
        var task = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task");
        var genericTask = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task`1");
        var valueTask = compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask");
        var genericValueTask = compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask`1");

        if ((task is not null && SymbolEqualityComparer.Default.Equals(original, task)) ||
            (genericTask is not null && SymbolEqualityComparer.Default.Equals(original, genericTask)) ||
            (valueTask is not null && SymbolEqualityComparer.Default.Equals(original, valueTask)) ||
            (genericValueTask is not null && SymbolEqualityComparer.Default.Equals(original, genericValueTask)))
        {
            return true;
        }

        var asyncBuilder = compilation.GetTypeByMetadataName(
            "System.Runtime.CompilerServices.AsyncMethodBuilderAttribute");
        if (asyncBuilder is not null)
        {
            foreach (var attribute in original.GetAttributes())
            {
                if (SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, asyncBuilder))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsDictationScopeBegin(
        InvocationExpressionSyntax call,
        SemanticModel model,
        KnownSymbols symbols) =>
        symbols.DictationScopeType is not null &&
        model.GetSymbolInfo(call).Symbol is IMethodSymbol method &&
        method.Name == "Begin" &&
        SymbolEqualityComparer.Default.Equals(method.ContainingType, symbols.DictationScopeType);

    private static bool IsLoggerWrite(
        InvocationExpressionSyntax call,
        SemanticModel model,
        KnownSymbols symbols) =>
        symbols.AppLoggerType is not null &&
        model.GetSymbolInfo(call).Symbol is IMethodSymbol method &&
        method.Name == "Write" &&
        (SymbolEqualityComparer.Default.Equals(method.ContainingType, symbols.AppLoggerType) ||
         method.ContainingType?.AllInterfaces.Any(iface =>
             SymbolEqualityComparer.Default.Equals(iface, symbols.AppLoggerType)) == true);

    private static void AnalyzeStatusSinkInvocation(
        SyntaxNodeAnalysisContext context,
        KnownSymbols symbols)
    {
        if (symbols.StatusType is null)
        {
            return;
        }

        if (IsInsideDictationStatus(context.Node, context.SemanticModel, symbols.StatusType))
        {
            return;
        }

        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.ArgumentList.Arguments.Count == 0)
        {
            return;
        }

        if (IsStatusSinkCall(invocation, context.SemanticModel, symbols.StatusType))
        {
            var argument = invocation.ArgumentList.Arguments[0].Expression;
            if (!StatusArgumentNamesItsPill(argument, context.SemanticModel, symbols))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    StatusPillNamingRule,
                    argument.GetLocation(),
                    argument.ToString()));
            }

            return;
        }

        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is IMethodSymbol target &&
            target.DeclaringSyntaxReferences.Length > 0 &&
            !SymbolEqualityComparer.Default.Equals(target.ContainingType, symbols.StatusType))
        {
            for (var index = 0; index < invocation.ArgumentList.Arguments.Count && index < target.Parameters.Length; index++)
            {
                var parameter = target.Parameters[index];
                if (!SymbolEqualityComparer.Default.Equals(parameter.Type, symbols.StatusType))
                {
                    continue;
                }

                var argument = invocation.ArgumentList.Arguments[index].Expression;
                if (!StatusArgumentNamesItsPill(argument, context.SemanticModel, symbols))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        StatusPillNamingRule,
                        argument.GetLocation(),
                        argument.ToString()));
                }
            }
        }
    }

    private static void AnalyzeStatusReturningMethod(
        SyntaxNodeAnalysisContext context,
        KnownSymbols symbols)
    {
        if (symbols.StatusType is null ||
            IsInsideDictationStatus(context.Node, context.SemanticModel, symbols.StatusType))
        {
            return;
        }

        var methodSyntax = (MethodDeclarationSyntax)context.Node;
        if (methodSyntax.Body is null && methodSyntax.ExpressionBody is null)
        {
            return;
        }

        if (context.SemanticModel.GetDeclaredSymbol(methodSyntax, context.CancellationToken) is not IMethodSymbol method ||
            !IsDictationStatusOrNullable(method.ReturnType, symbols.StatusType) ||
            IsSentenceToAppearanceCallable(method.ReturnType, method.Parameters, symbols))
        {
            return;
        }

        if (!CarrierSymbolNamesItsPill(
                method,
                context.SemanticModel,
                symbols,
                new System.Collections.Generic.HashSet<ISymbol>(SymbolEqualityComparer.Default)))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                StatusPillNamingRule,
                methodSyntax.Identifier.GetLocation(),
                methodSyntax.Identifier.ValueText));
        }
    }

    public static bool IsStatusSinkCall(
        InvocationExpressionSyntax invocation,
        SemanticModel model,
        INamedTypeSymbol statusType)
    {
        var name = InvokedMethodName(invocation.Expression);
        if (name is not ("SetSessionStatus" or "ShowStatus"))
        {
            return false;
        }

        var symbolInfo = model.GetSymbolInfo(invocation);
        var target = symbolInfo.Symbol as IMethodSymbol
            ?? (symbolInfo.CandidateReason == CandidateReason.LateBound
                ? symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault()
                : null);

        return target is not null &&
            target.Parameters.Length >= 1 &&
            SymbolEqualityComparer.Default.Equals(target.Parameters[0].Type, statusType);
    }

    /// <summary>
    /// Whether an expression passed to <c>SetSessionStatus</c> or <c>ShowStatus</c> names its pill
    /// through a <c>DictationStatus</c> factory or a compiler-verified <c>DictationStatus</c> carrier.
    /// </summary>
    public static bool StatusArgumentNamesItsPill(
        ExpressionSyntax expression,
        SemanticModel model,
        KnownSymbols symbols) =>
        StatusArgumentNamesItsPillCore(
            expression,
            model,
            symbols,
            allowNullForNullableCarrier: false,
            visitedLocals: new System.Collections.Generic.HashSet<ISymbol>(SymbolEqualityComparer.Default));

    private static bool StatusArgumentNamesItsPillCore(
        ExpressionSyntax expression,
        SemanticModel model,
        KnownSymbols symbols,
        bool allowNullForNullableCarrier,
        System.Collections.Generic.HashSet<ISymbol> visitedLocals)
    {
        if (symbols.StatusType is null)
        {
            return false;
        }

        var statusType = symbols.StatusType;
        switch (expression)
        {
            case LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression }:
                return allowNullForNullableCarrier;

            case ParenthesizedExpressionSyntax parenthesized:
                return StatusArgumentNamesItsPillCore(
                    parenthesized.Expression,
                    model,
                    symbols,
                    allowNullForNullableCarrier,
                    visitedLocals);

            case PostfixUnaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.SuppressNullableWarningExpression,
                Operand: var operand,
            }:
                return StatusArgumentNamesItsPillCore(
                    operand,
                    model,
                    symbols,
                    allowNullForNullableCarrier,
                    visitedLocals);

            case ConditionalExpressionSyntax conditional:
                return StatusArgumentNamesItsPillCore(
                        conditional.WhenTrue,
                        model,
                        symbols,
                        allowNullForNullableCarrier,
                        visitedLocals) &&
                    StatusArgumentNamesItsPillCore(
                        conditional.WhenFalse,
                        model,
                        symbols,
                        allowNullForNullableCarrier,
                        visitedLocals);

            case SwitchExpressionSyntax switchExpression:
            {
                if (switchExpression.Arms.Count == 0)
                {
                    return false;
                }

                foreach (var arm in switchExpression.Arms)
                {
                    if (!StatusArgumentNamesItsPillCore(
                            arm.Expression,
                            model,
                            symbols,
                            allowNullForNullableCarrier,
                            visitedLocals))
                    {
                        return false;
                    }
                }

                return true;
            }

            case BinaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.CoalesceExpression,
                Left: var left,
                Right: var right,
            }:
                return StatusArgumentNamesItsPillCore(
                        left,
                        model,
                        symbols,
                        allowNullForNullableCarrier,
                        visitedLocals) &&
                    StatusArgumentNamesItsPillCore(
                        right,
                        model,
                        symbols,
                        allowNullForNullableCarrier,
                        visitedLocals);

            case InvocationExpressionSyntax invocation:
            {
                if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method)
                {
                    return false;
                }

                if (SymbolEqualityComparer.Default.Equals(method.ContainingType, statusType))
                {
                    if (method.IsStatic)
                    {
                        return true;
                    }

                    if (invocation.Expression is MemberAccessExpressionSyntax instanceAccess)
                    {
                        return StatusArgumentNamesItsPillCore(
                            instanceAccess.Expression,
                            model,
                            symbols,
                            allowNullForNullableCarrier,
                            visitedLocals);
                    }
                }

                // A compilation-declared carrier method that returns DictationStatus (or DictationStatus?)
                // and is not itself a sentence-to-appearance helper.
                if (IsDictationStatusOrNullable(method.ReturnType, statusType) &&
                    !IsSentenceToAppearanceCallable(method.ReturnType, method.Parameters, symbols))
                {
                    return CarrierSymbolNamesItsPill(method, model, symbols, visitedLocals);
                }

                return false;
            }

            case MemberAccessExpressionSyntax memberAccess:
            {
                if (memberAccess.Name.Identifier.ValueText == "Value" &&
                    IsDictationStatusOrNullable(model.GetTypeInfo(memberAccess.Expression).Type, statusType))
                {
                    return StatusArgumentNamesItsPillCore(
                        memberAccess.Expression,
                        model,
                        symbols,
                        allowNullForNullableCarrier: true,
                        visitedLocals);
                }

                var memberSymbol = model.GetSymbolInfo(memberAccess).Symbol;
                return IsDictationStatusCarrierSymbol(memberSymbol, statusType) &&
                    CarrierSymbolNamesItsPill(memberSymbol!, model, symbols, visitedLocals);
            }

            case IdentifierNameSyntax identifier:
            {
                var symbol = model.GetSymbolInfo(identifier).Symbol;
                if (!IsDictationStatusCarrierSymbol(symbol, statusType))
                {
                    return false;
                }

                return CarrierSymbolNamesItsPill(symbol!, model, symbols, visitedLocals);
            }

            default:
                return false;
        }
    }

    private static bool CarrierSymbolNamesItsPill(
        ISymbol symbol,
        SemanticModel model,
        KnownSymbols symbols,
        System.Collections.Generic.HashSet<ISymbol> inProgress)
    {
        if (symbol.DeclaringSyntaxReferences.Length == 0)
        {
            return IsVerifiedMetadataCarrierSymbol(symbol, symbols.StatusType!, symbols);
        }

        if (!inProgress.Add(symbol))
        {
            return false;
        }

        try
        {
            var statusType = symbols.StatusType!;
            if (symbol is IParameterSymbol parameter)
            {
                if (parameter.RefKind != RefKind.None || parameter.HasExplicitDefaultValue)
                {
                    return false;
                }

                foreach (var syntaxRef in parameter.DeclaringSyntaxReferences)
                {
                    var syntax = syntaxRef.GetSyntax();
                    if (syntax is ParameterSyntax { Default: not null })
                    {
                        return false;
                    }

                    var syntaxModel = syntax.SyntaxTree == model.SyntaxTree
                        ? model
                        : symbols.Compilation.GetSemanticModel(syntax.SyntaxTree);
                    var scopeRoot = EnclosingCallableBodyForParameter(syntax);
                    if (scopeRoot is not null &&
                        HasAnyWriteOrRefUseOfSymbol(scopeRoot, parameter, syntaxModel))
                    {
                        return false;
                    }
                }

                return true;
            }

            if (symbol is ILocalSymbol { IsRef: true })
            {
                return false;
            }

            var allowNull = symbol switch
            {
                ILocalSymbol local => IsNullableDictationStatus(local.Type, statusType),
                IFieldSymbol field => IsNullableDictationStatus(field.Type, statusType),
                IPropertySymbol property => IsNullableDictationStatus(property.Type, statusType),
                IMethodSymbol method => IsNullableDictationStatus(method.ReturnType, statusType),
                _ => false,
            };

            var hasVerifiedOrigin = false;
            foreach (var syntaxRef in symbol.DeclaringSyntaxReferences)
            {
                var syntax = syntaxRef.GetSyntax();
                var syntaxModel = syntax.SyntaxTree == model.SyntaxTree
                    ? model
                    : symbols.Compilation.GetSemanticModel(syntax.SyntaxTree);

                switch (syntax)
                {
                    case VariableDeclaratorSyntax declarator:
                    {
                        // Both local variables and fields must be initialized at declaration so they
                        // never start life holding default(DictationStatus).
                        if (declarator.Initializer is not { Value: { } initializer } ||
                            !StatusArgumentNamesItsPillCore(
                                initializer,
                                syntaxModel,
                                symbols,
                                allowNull,
                                inProgress))
                        {
                            return false;
                        }

                        hasVerifiedOrigin = true;
                        break;
                    }

                    case SingleVariableDesignationSyntax designation:
                    {
                        var patternSource = FindPatternInputExpression(designation);
                        if (patternSource is null ||
                            !StatusArgumentNamesItsPillCore(
                                patternSource,
                                syntaxModel,
                                symbols,
                                allowNullForNullableCarrier: true,
                                inProgress))
                        {
                            return false;
                        }

                        hasVerifiedOrigin = true;
                        break;
                    }

                    case PropertyDeclarationSyntax propertyDeclaration:
                    {
                        var propExpr = propertyDeclaration.Initializer?.Value
                            ?? propertyDeclaration.ExpressionBody?.Expression;
                        var hasGetterImplementation = false;
                        if (propExpr is not null)
                        {
                            if (!StatusArgumentNamesItsPillCore(
                                    propExpr,
                                    syntaxModel,
                                    symbols,
                                    allowNull,
                                    inProgress))
                            {
                                return false;
                            }

                            hasVerifiedOrigin = true;
                            hasGetterImplementation = true;
                        }

                        if (propertyDeclaration.AccessorList is { } accessorList)
                        {
                            foreach (var accessor in accessorList.Accessors)
                            {
                                if (!accessor.Keyword.IsKind(SyntaxKind.GetKeyword))
                                {
                                    continue;
                                }

                                if (accessor.ExpressionBody is { Expression: { } accessorExpr })
                                {
                                    if (!StatusArgumentNamesItsPillCore(
                                            accessorExpr,
                                            syntaxModel,
                                            symbols,
                                            allowNull,
                                            inProgress))
                                    {
                                        return false;
                                    }

                                    hasVerifiedOrigin = true;
                                    hasGetterImplementation = true;
                                }
                                else if (accessor.Body is { } accessorBody)
                                {
                                    if (!VerifyCallableBlockReturns(
                                            accessorBody,
                                            syntaxModel,
                                            symbols,
                                            allowNull,
                                            inProgress))
                                    {
                                        return false;
                                    }

                                    hasVerifiedOrigin = true;
                                    hasGetterImplementation = true;
                                }
                            }
                        }

                        if (!hasGetterImplementation)
                        {
                            return false;
                        }

                        break;
                    }

                    case MethodDeclarationSyntax methodDeclaration:
                    {
                        if (methodDeclaration.ExpressionBody is { Expression: { } methodExpr })
                        {
                            if (!StatusArgumentNamesItsPillCore(
                                    methodExpr,
                                    syntaxModel,
                                    symbols,
                                    allowNull,
                                    inProgress))
                            {
                                return false;
                            }

                            hasVerifiedOrigin = true;
                        }
                        else if (methodDeclaration.Body is { } methodBody)
                        {
                            if (!VerifyCallableBlockReturns(
                                    methodBody,
                                    syntaxModel,
                                    symbols,
                                    allowNull,
                                    inProgress))
                            {
                                return false;
                            }

                            hasVerifiedOrigin = true;
                        }
                        else
                        {
                            return false;
                        }

                        break;
                    }

                    case LocalFunctionStatementSyntax localFunction:
                    {
                        if (localFunction.ExpressionBody is { Expression: { } localExpr })
                        {
                            if (!StatusArgumentNamesItsPillCore(
                                    localExpr,
                                    syntaxModel,
                                    symbols,
                                    allowNull,
                                    inProgress))
                            {
                                return false;
                            }

                            hasVerifiedOrigin = true;
                        }
                        else if (localFunction.Body is { } localBody)
                        {
                            if (!VerifyCallableBlockReturns(
                                    localBody,
                                    syntaxModel,
                                    symbols,
                                    allowNull,
                                    inProgress))
                            {
                                return false;
                            }

                            hasVerifiedOrigin = true;
                        }
                        else
                        {
                            return false;
                        }

                        break;
                    }
                }

                // Check any assignments or out/ref writes to this symbol across all partial declarations
                // of its containing type (or its enclosing member for a local).
                foreach (var scopeRoot in EnclosingScopesForAssignments(symbol, syntax))
                {
                    var scopeModel = scopeRoot.SyntaxTree == model.SyntaxTree
                        ? model
                        : symbols.Compilation.GetSemanticModel(scopeRoot.SyntaxTree);
                    foreach (var assignment in scopeRoot.DescendantNodes().OfType<AssignmentExpressionSyntax>())
                    {
                        if (ReferencesSymbol(assignment.Left, symbol, scopeModel))
                        {
                            if (!SymbolEqualityComparer.Default.Equals(
                                    scopeModel.GetSymbolInfo(assignment.Left).Symbol,
                                    symbol) ||
                                !StatusArgumentNamesItsPillCore(
                                    assignment.Right,
                                    scopeModel,
                                    symbols,
                                    allowNull,
                                    inProgress))
                            {
                                return false;
                            }

                            hasVerifiedOrigin = true;
                        }
                    }

                    foreach (var argument in scopeRoot.DescendantNodes().OfType<ArgumentSyntax>())
                    {
                        if (!argument.RefKindKeyword.IsKind(SyntaxKind.None) &&
                            ReferencesSymbol(argument.Expression, symbol, scopeModel))
                        {
                            return false;
                        }
                    }
                }
            }

            return hasVerifiedOrigin;
        }
        finally
        {
            inProgress.Remove(symbol);
        }
    }

    private static bool VerifyCallableBlockReturns(
        BlockSyntax body,
        SemanticModel model,
        KnownSymbols symbols,
        bool allowNull,
        System.Collections.Generic.HashSet<ISymbol> inProgress)
    {
        var hasReturn = false;
        foreach (var returnStmt in DirectReturnStatements(body))
        {
            hasReturn = true;
            if (returnStmt.Expression is null ||
                !StatusArgumentNamesItsPillCore(
                    returnStmt.Expression,
                    model,
                    symbols,
                    allowNull,
                    inProgress))
            {
                return false;
            }
        }

        return hasReturn;
    }

    private static SyntaxNode? EnclosingCallableBodyForParameter(SyntaxNode parameterSyntax)
    {
        for (var current = parameterSyntax.Parent; current is not null; current = current.Parent)
        {
            if (current is BaseMethodDeclarationSyntax or
                LocalFunctionStatementSyntax or
                AnonymousFunctionExpressionSyntax or
                IndexerDeclarationSyntax)
            {
                return current;
            }
        }

        return null;
    }

    private static bool HasAnyWriteOrRefUseOfSymbol(
        SyntaxNode scopeRoot,
        ISymbol symbol,
        SemanticModel model)
    {
        foreach (var assignment in scopeRoot.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (ReferencesSymbol(assignment.Left, symbol, model))
            {
                return true;
            }
        }

        foreach (var argument in scopeRoot.DescendantNodes().OfType<ArgumentSyntax>())
        {
            if (!argument.RefKindKeyword.IsKind(SyntaxKind.None) &&
                ReferencesSymbol(argument.Expression, symbol, model))
            {
                return true;
            }
        }

        return false;
    }

    private static System.Collections.Generic.IEnumerable<ReturnStatementSyntax> DirectReturnStatements(
        BlockSyntax body) =>
        body.DescendantNodes(node =>
                node is not LocalFunctionStatementSyntax and not AnonymousFunctionExpressionSyntax)
            .OfType<ReturnStatementSyntax>();

    private static System.Collections.Generic.IEnumerable<SyntaxNode> EnclosingScopesForAssignments(
        ISymbol symbol,
        SyntaxNode declaringSyntax)
    {
        if (symbol is (IFieldSymbol or IPropertySymbol) &&
            symbol.ContainingType is { DeclaringSyntaxReferences.Length: > 0 } containingType)
        {
            foreach (var typeRef in containingType.DeclaringSyntaxReferences)
            {
                yield return typeRef.GetSyntax();
            }

            yield break;
        }

        yield return (SyntaxNode?)declaringSyntax.FirstAncestorOrSelf<MemberDeclarationSyntax>()
            ?? declaringSyntax.SyntaxTree.GetRoot();
    }

    private static ExpressionSyntax? FindPatternInputExpression(SyntaxNode designation)
    {
        for (var current = designation.Parent; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case IsPatternExpressionSyntax isPattern:
                    return isPattern.Expression;
                case SwitchExpressionSyntax switchExpression:
                    return switchExpression.GoverningExpression;
                case SwitchStatementSyntax switchStatement:
                    return switchStatement.Expression;
            }
        }

        return null;
    }

    private static bool ReferencesSymbol(
        SyntaxNode node,
        ISymbol target,
        SemanticModel model)
    {
        foreach (var identifier in node.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
            if (SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(identifier).Symbol, target))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsNullableDictationStatus(ITypeSymbol? type, INamedTypeSymbol statusType) =>
        type is INamedTypeSymbol
        {
            OriginalDefinition.SpecialType: SpecialType.System_Nullable_T,
            TypeArguments.Length: 1,
        } nullable &&
        SymbolEqualityComparer.Default.Equals(nullable.TypeArguments[0], statusType);

    private static bool IsVerifiedMetadataCarrierSymbol(
        ISymbol symbol,
        INamedTypeSymbol statusType,
        KnownSymbols symbols) =>
        symbol is IMethodSymbol { MethodKind: MethodKind.Ordinary } method &&
        SymbolEqualityComparer.Default.Equals(method.ContainingAssembly, statusType.ContainingAssembly) &&
        !SymbolEqualityComparer.Default.Equals(method.ContainingType, statusType) &&
        IsDictationStatusOrNullable(method.OriginalDefinition.ReturnType, statusType) &&
        !IsSentenceToAppearanceCallable(method.ReturnType, method.Parameters, symbols);

    public static bool IsDictationStatusCarrierSymbol(ISymbol? symbol, INamedTypeSymbol statusType) =>
        symbol switch
        {
            ILocalSymbol local => IsDictationStatusOrNullable(local.Type, statusType),
            IParameterSymbol parameter => IsDictationStatusOrNullable(parameter.Type, statusType),
            IFieldSymbol field => IsDictationStatusOrNullable(field.Type, statusType),
            IPropertySymbol property => IsDictationStatusOrNullable(property.Type, statusType),
            IMethodSymbol method => IsDictationStatusOrNullable(method.ReturnType, statusType),
            _ => false,
        };

    public static bool IsDictationStatusOrNullable(ITypeSymbol? type, INamedTypeSymbol statusType)
    {
        if (type is null)
        {
            return false;
        }

        if (SymbolEqualityComparer.Default.Equals(type, statusType))
        {
            return true;
        }

        if (type is INamedTypeSymbol
            {
                OriginalDefinition.SpecialType: SpecialType.System_Nullable_T,
                TypeArguments.Length: 1,
            } nullable)
        {
            return SymbolEqualityComparer.Default.Equals(nullable.TypeArguments[0], statusType);
        }

        return false;
    }

    private static string? InvokedMethodName(ExpressionSyntax expression) =>
        expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
            MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
            _ => null,
        };

    private static readonly char[] HeaderStopCharacters = new[] { '{', '=', ';' };

    private static string DescribeHeader(SyntaxNode node)
    {
        var text = node.ToString();
        var bodyStart = text.IndexOfAny(HeaderStopCharacters);
        var header = bodyStart > 0 ? text.Substring(0, bodyStart) : text;
        return string.Join(
            " ",
            header.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries));
    }

    public sealed class KnownSymbols
    {
        public KnownSymbols(
            Compilation compilation,
            INamedTypeSymbol? statusType,
            INamedTypeSymbol? overlayStateType,
            INamedTypeSymbol? sessionIdType,
            INamedTypeSymbol? capturedAudioType,
            INamedTypeSymbol? sessionCommandType,
            INamedTypeSymbol? dictationScopeType,
            INamedTypeSymbol? appLoggerType = null)
        {
            Compilation = compilation;
            StatusType = statusType;
            OverlayStateType = overlayStateType;
            SessionIdType = sessionIdType;
            CapturedAudioType = capturedAudioType;
            SessionCommandType = sessionCommandType;
            DictationScopeType = dictationScopeType;
            AppLoggerType = appLoggerType;
        }

        public Compilation Compilation { get; }

        public INamedTypeSymbol? StatusType { get; }

        public INamedTypeSymbol? OverlayStateType { get; }

        public INamedTypeSymbol? SessionIdType { get; }

        public INamedTypeSymbol? CapturedAudioType { get; }

        public INamedTypeSymbol? SessionCommandType { get; }

        public INamedTypeSymbol? DictationScopeType { get; }

        public INamedTypeSymbol? AppLoggerType { get; }

        public static KnownSymbols From(Compilation compilation) =>
            new(
                compilation,
                compilation.GetTypeByMetadataName(DictationStatusMetadataName),
                compilation.GetTypeByMetadataName(DictationOverlayStateMetadataName),
                compilation.GetTypeByMetadataName(DictationSessionIdMetadataName),
                compilation.GetTypeByMetadataName(CapturedAudioMetadataName),
                compilation.GetTypeByMetadataName(SessionCommandMetadataName),
                compilation.GetTypeByMetadataName(DictationScopeMetadataName),
                compilation.GetTypeByMetadataName(AppLoggerMetadataName));
    }
}
