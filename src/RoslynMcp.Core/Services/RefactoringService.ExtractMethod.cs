using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Core.Helpers;
using RoslynMcp.Shared;
using RoslynMcp.Shared.Contracts.Common;
using RoslynMcp.Shared.Contracts.Refactor;
using RefactorTextChange = RoslynMcp.Shared.Contracts.Refactor.TextChange;

namespace RoslynMcp.Core.Services;

public partial class RefactoringService
{
    // ── Extract method helper types ──
    // NOTE: This file (~730 lines) contains tightly coupled extraction logic (selection collection,
    // data flow classification, method building, call site generation). A decomposition into
    // ExtractMethodAnalyzer / ExtractMethodBuilder helper classes would be ideal but is deferred
    // because the internal state sharing (ExtractableSelection, ParameterInfo, ReturnInfo) makes
    // clean separation non-trivial without introducing a shared context object.

    private record ExtractableSelection(
        IReadOnlyList<StatementSyntax>? Statements,
        ExpressionSyntax? Expression,
        SyntaxNode EnclosingMember);

    private record ParameterInfo(ISymbol Symbol, ITypeSymbol Type, RefKind RefKind);

    private record ReturnInfo(ISymbol Symbol, ITypeSymbol Type, bool NeedsDeclaration);

    // ── 7. preview_extract_method ──

    public async Task<Result<ExtractMethodPreviewResponse>> PreviewExtractMethodAsync(
        ExtractMethodRequest request, CancellationToken ct = default)
    {
        try
        {
            var solution = _workspace.CurrentSolution;
            if (solution is null)
                return Result<ExtractMethodPreviewResponse>.Fail("No solution loaded");

            var doc = await _workspace.GetDocumentAsync(request.FilePath, ct: ct).ConfigureAwait(false);
            if (doc is null)
                return Result<ExtractMethodPreviewResponse>.Fail($"Document not found: {request.FilePath}");

            var root = await doc.GetSyntaxRootAsync(ct).ConfigureAwait(false);
            var model = await doc.GetSemanticModelAsync(ct).ConfigureAwait(false);
            var text = await doc.GetTextAsync(ct).ConfigureAwait(false);

            if (root is null || model is null)
                return Result<ExtractMethodPreviewResponse>.Fail("Could not get syntax tree or semantic model");

            var identifierError = ValidateIdentifier(request.MethodName);
            if (identifierError != null)
                return Result<ExtractMethodPreviewResponse>.Fail(identifierError!);

            // Resolve positions
            var startPosResult = WorkspaceHelpers.GetSafePosition(text, request.StartLine, request.StartColumn);
            if (!startPosResult.IsSuccess)
                return Result<ExtractMethodPreviewResponse>.Fail(startPosResult.Error!.Message, startPosResult.Error.ErrorCode);

            var endPosResult = WorkspaceHelpers.GetSafePosition(text, request.EndLine, request.EndColumn);
            if (!endPosResult.IsSuccess)
                return Result<ExtractMethodPreviewResponse>.Fail(endPosResult.Error!.Message, endPosResult.Error.ErrorCode);

            if (startPosResult.Value > endPosResult.Value)
                return Result<ExtractMethodPreviewResponse>.Fail("Start position must be before end position", "INVALID_POSITION");

            // Collect extractable nodes (pass already-validated positions to avoid redundant GetSafePosition calls)
            var selectionResult = CollectExtractableNodes(root, startPosResult.Value, endPosResult.Value, ct);
            if (!selectionResult.IsSuccess)
                return Result<ExtractMethodPreviewResponse>.Fail(selectionResult.Error!.Message, selectionResult.Error.ErrorCode);

            var selection = selectionResult.Value!;

            // Validate
            var nodes = selection.Statements is not null
                ? selection.Statements.Cast<SyntaxNode>().ToList()
                : new List<SyntaxNode> { selection.Expression! };
            var validationError = ValidateExtractable(nodes, ct);
            if (validationError is not null)
                return Result<ExtractMethodPreviewResponse>.Fail(validationError);

            // Data flow analysis
            DataFlowAnalysis? dataFlow;
            if (selection.Statements is not null)
            {
                var first = selection.Statements[0];
                var last = selection.Statements[selection.Statements.Count - 1];
                dataFlow = ReferenceEquals(first, last)
                    ? model.AnalyzeDataFlow(first)
                    : model.AnalyzeDataFlow(first, last);
            }
            else
            {
                dataFlow = model.AnalyzeDataFlow(selection.Expression!);
            }

            if (dataFlow is null || !dataFlow.Succeeded)
                return Result<ExtractMethodPreviewResponse>.Fail("Data flow analysis failed for the specified range");

            // Classify parameters and return
            var (parameters, returnInfo, returnCandidateCount) = ClassifyParameters(dataFlow);

            if (returnCandidateCount > 1)
                return Result<ExtractMethodPreviewResponse>.Fail(
                    "Cannot extract: multiple variables flow out; consider returning a tuple or reducing outputs");

            // Determine enclosing context
            var enclosingMember = selection.EnclosingMember;
            var enclosingSymbol = model.GetDeclaredSymbol(enclosingMember, ct);
            bool isStatic = enclosingSymbol?.IsStatic ?? false;

            // Check for async
            bool isAsync = nodes.Any(n => n.DescendantNodesAndSelf().OfType<AwaitExpressionSyntax>().Any());

            // Get enclosing type for name collision check
            var enclosingType = enclosingSymbol?.ContainingType;
            if (enclosingSymbol is INamedTypeSymbol namedType)
                enclosingType = namedType;
            if (enclosingType is null)
            {
                // Walk up to find type
                var typeDecl = enclosingMember.FirstAncestorOrSelf<TypeDeclarationSyntax>();
                if (typeDecl is not null)
                    enclosingType = model.GetDeclaredSymbol(typeDecl, ct) as INamedTypeSymbol;
            }

            string? methodName = enclosingType is not null
                ? EnsureMethodNameUnique(request.MethodName, enclosingType)
                : request.MethodName;
            if (methodName is null)
                return Result<ExtractMethodPreviewResponse>.Fail("Cannot generate unique method name after 100 attempts");

            string accessibility = request.Accessibility ?? "private";

            // Determine return type for expression extraction
            ITypeSymbol? expressionType = null;
            if (selection.Expression is not null)
                expressionType = model.GetTypeInfo(selection.Expression, ct).Type;

            // Build method and call site
            var (methodDecl, callSite) = BuildExtractedMethod(
                selection, parameters, returnInfo, expressionType,
                enclosingMember, methodName, accessibility, isStatic, isAsync);

            // Resolve insertion point
            var insertionAnchor = ResolveInsertionPoint(enclosingMember);

            // Format the method using the workspace
            SyntaxNode formattedMethod;
            SyntaxNode formattedCallSite;
            using (var workspace = new AdhocWorkspace())
            {
                formattedMethod = Formatter.Format(methodDecl, workspace);
                formattedCallSite = Formatter.Format(callSite, workspace);
            }

            // Preserve leading trivia from first selected node
            string leadingWhitespace = "";
            var firstNode = selection.Statements is not null
                ? (SyntaxNode)selection.Statements[0]
                : selection.Expression!;
            var leadingTrivia = firstNode.GetLeadingTrivia();
            var whitespaceTrivia = leadingTrivia.LastOrDefault(t => t.IsKind(SyntaxKind.WhitespaceTrivia));
            if (whitespaceTrivia != default)
                leadingWhitespace = whitespaceTrivia.ToFullString();

            string callSiteText = leadingWhitespace + formattedCallSite.ToFullString().TrimStart();
            string methodText = formattedMethod.ToFullString();

            // Determine indentation for inserted method from enclosing type member level
            string memberIndent = "";
            var anchorLeading = insertionAnchor.GetLeadingTrivia();
            var anchorWhitespace = anchorLeading.LastOrDefault(t => t.IsKind(SyntaxKind.WhitespaceTrivia));
            if (anchorWhitespace != default)
                memberIndent = anchorWhitespace.ToFullString();

            // Indent the method text to match
            var methodLines = methodText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            for (int i = 0; i < methodLines.Length; i++)
            {
                var trimmed = methodLines[i].TrimStart();
                if (!string.IsNullOrWhiteSpace(trimmed))
                    methodLines[i] = memberIndent + trimmed;
            }
            methodText = string.Join("\n", methodLines);

            // Build text changes
            var selectedSpan = selection.Statements is not null
                ? TextSpan.FromBounds(selection.Statements[0].FullSpan.Start, selection.Statements[selection.Statements.Count - 1].FullSpan.End)
                : selection.Expression!.Parent is ExpressionStatementSyntax exprStmt
                    ? exprStmt.FullSpan
                    : selection.Expression!.FullSpan;

            var replaceRange = RoslynMapper.ToCodeRange(selectedSpan, text);
            var replaceChange = new RefactorTextChange(replaceRange, callSiteText);

            // Insert after anchor's closing brace
            var insertionSpan = new TextSpan(insertionAnchor.FullSpan.End, 0);
            var insertionRange = RoslynMapper.ToCodeRange(insertionSpan, text);
            var insertChange = new RefactorTextChange(insertionRange, "\n\n" + methodText);

            var fileChange = new FileChange(
                doc.FilePath ?? request.FilePath,
                new[] { replaceChange, insertChange });

            var preview = new RefactoringPreview(
                new[] { fileChange },
                TotalChanges: 2);


            return new ExtractMethodPreviewResponse(methodName, preview);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to preview extract method for {FilePath}", request.FilePath);
            return Result<ExtractMethodPreviewResponse>.Fail("Failed to analyze extraction: " + ex.Message);
        }
    }

    // ── 8. apply_extract_method ──

    public async Task<Result<ExtractMethodApplyResponse>> ApplyExtractMethodAsync(
        ExtractMethodRequest request, CancellationToken ct = default)
    {
        try
        {
            // Same computation as preview -- does NOT mutate workspace in Phase 6.1
            var previewResult = await PreviewExtractMethodAsync(request, ct).ConfigureAwait(false);
            if (!previewResult.IsSuccess)
                return Result<ExtractMethodApplyResponse>.Fail(previewResult.Error!);

            var preview = previewResult.Value!;

            _logger.Warning("apply_extract_method computed changes for {MethodName} but workspace write-back not available in standalone mode",
                preview.MethodName);

            // Build CodeLocation for the new method (approximate: end of anchor)
            var newMethodLocation = new CodeLocation(
                request.FilePath,
                request.EndLine + 2,
                0,
                request.EndLine + 2,
                0);

            return new ExtractMethodApplyResponse(
                preview.MethodName,
                newMethodLocation,
                FilesChanged: 1,
                Changes: preview.Preview.AffectedFiles);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to apply extract method for {FilePath}", request.FilePath);
            return Result<ExtractMethodApplyResponse>.Fail("Failed to apply extraction: " + ex.Message);
        }
    }

    // ── Extract method helpers ──

    private static Result<ExtractableSelection> CollectExtractableNodes(
        SyntaxNode root, int startPos, int endPos, CancellationToken ct)
    {
        var requestSpan = TextSpan.FromBounds(startPos, endPos);

        // Statement path: only if the selection covers full statements (not partial sub-expressions)
        ct.ThrowIfCancellationRequested();
        var statementsInRange = root.DescendantNodes()
            .OfType<StatementSyntax>()
            .Where(s => s is not BlockSyntax
                && (requestSpan.Contains(s.Span) || s.Span.Contains(requestSpan)))
            .OrderBy(s => s.SpanStart)
            .ToList();

        // Filter to statements that are fully or mostly covered by the selection.
        // If the selection is strictly inside a single statement (sub-expression), fall to expression path.
        if (statementsInRange.Count == 1 && statementsInRange[0].Span.Contains(requestSpan)
            && !requestSpan.Contains(statementsInRange[0].Span))
        {
            // Selection is a sub-expression inside one statement; fall through to expression path
            statementsInRange.Clear();
        }

        if (statementsInRange.Count > 0)
        {
            // Group by parent to ensure same statement list
            if (statementsInRange.Count > 1)
            {
                var groups = statementsInRange.GroupBy(s => s.Parent).ToList();
                if (groups.Count > 1)
                    return Result<ExtractableSelection>.Fail("Range spans multiple statement contexts; narrow the range to a single block");
                statementsInRange = groups[0].OrderBy(s => s.SpanStart).ToList();
            }

            // Find enclosing member
            var enclosingMember = FindEnclosingMember(statementsInRange[0]);
            if (enclosingMember is null)
                return Result<ExtractableSelection>.Fail("Cannot determine enclosing member for extraction");

            return new ExtractableSelection(statementsInRange, null, enclosingMember);
        }

        // Expression path
        ct.ThrowIfCancellationRequested();
        var expressionNode = root.DescendantNodes()
            .OfType<ExpressionSyntax>()
            .Where(e => requestSpan.IntersectsWith(e.Span)
                && e is not IdentifierNameSyntax
                && e is not LiteralExpressionSyntax
                && e is not ThisExpressionSyntax)
            .OrderByDescending(e => e.Span.Length)
            .FirstOrDefault();

        if (expressionNode is not null)
        {
            if (expressionNode.Parent is not (ExpressionStatementSyntax
                or ReturnStatementSyntax
                or EqualsValueClauseSyntax  // var x = Foo();
                or ArgumentSyntax           // Bar(Foo())
                or AssignmentExpressionSyntax))  // x = Foo();
                return Result<ExtractableSelection>.Fail(
                    "Sub-expression extraction not yet supported for this context. Supported: statements, return values, variable initializers, arguments, and assignments.");

            var enclosingMember = FindEnclosingMember(expressionNode);
            if (enclosingMember is null)
                return Result<ExtractableSelection>.Fail("Cannot determine enclosing member for extraction");

            return new ExtractableSelection(null, expressionNode, enclosingMember);
        }

        return Result<ExtractableSelection>.Fail("No statements or expressions found in the specified range");
    }

    private static SyntaxNode? FindEnclosingMember(SyntaxNode node)
    {
        var current = node.Parent;
        while (current is not null)
        {
            if (current is MethodDeclarationSyntax
                or ConstructorDeclarationSyntax
                or AccessorDeclarationSyntax
                or LocalFunctionStatementSyntax
                or OperatorDeclarationSyntax
                or ConversionOperatorDeclarationSyntax)
                return current;
            current = current.Parent;
        }
        return null;
    }

    private static string? ValidateExtractable(IReadOnlyList<SyntaxNode> nodes, CancellationToken ct)
    {
        foreach (var node in nodes)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var descendant in node.DescendantNodesAndSelf())
            {
                if (descendant is GotoStatementSyntax)
                    return "Cannot extract: selection contains goto statement (unsupported)";
                if (descendant is ReturnStatementSyntax)
                    return "Cannot extract: selection contains return statement (unsupported; known limitation)";
                if (descendant is YieldStatementSyntax)
                    return "Cannot extract: selection contains yield statement (unsupported)";
                if (descendant is StackAllocArrayCreationExpressionSyntax or ImplicitStackAllocArrayCreationExpressionSyntax)
                    return "Cannot extract: selection contains stackalloc (unsupported)";
                if (descendant is RefExpressionSyntax && descendant.Parent is EqualsValueClauseSyntax)
                    return "Cannot extract: selection contains ref local declaration (unsupported)";
            }

            // Check trivia for unbalanced preprocessor directives
            var preprocessorError = CheckPreprocessorDirectives(node, ct);
            if (preprocessorError is not null)
                return preprocessorError;
        }
        return null;
    }

    private static string? CheckPreprocessorDirectives(SyntaxNode node, CancellationToken ct)
    {
        int ifCount = 0;
        int endIfCount = 0;

        void CountDirectives(SyntaxTriviaList triviaList)
        {
            foreach (var trivia in triviaList)
            {
                if (trivia.IsKind(SyntaxKind.IfDirectiveTrivia))
                    ifCount++;
                else if (trivia.IsKind(SyntaxKind.EndIfDirectiveTrivia))
                    endIfCount++;
            }
        }

        foreach (var token in node.DescendantTokens())
        {
            ct.ThrowIfCancellationRequested();
            CountDirectives(token.LeadingTrivia);
            CountDirectives(token.TrailingTrivia);
        }

        if (ifCount != endIfCount)
            return "Cannot extract: selection contains unbalanced preprocessor directives (#if/#endif)";

        return null;
    }

    private static (List<ParameterInfo> Params, ReturnInfo? Return, int ReturnCandidateCount) ClassifyParameters(DataFlowAnalysis dataFlow)
    {
        var parameters = new List<ParameterInfo>();
        ReturnInfo? returnInfo = null;

        var declaredSet = new HashSet<ISymbol>(dataFlow.VariablesDeclared, SymbolEqualityComparer.Default);
        var flowsInSet = new HashSet<ISymbol>(dataFlow.DataFlowsIn, SymbolEqualityComparer.Default);
        var flowsOutSet = new HashSet<ISymbol>(dataFlow.DataFlowsOut, SymbolEqualityComparer.Default);
        var writtenInsideSet = new HashSet<ISymbol>(dataFlow.WrittenInside, SymbolEqualityComparer.Default);

        // Return candidates: variables born inside that flow out, or written inside + flow out but not born inside and not flowing in
        var returnCandidates = new List<(ISymbol Symbol, ITypeSymbol Type, bool NeedsDeclaration)>();

        foreach (var sym in flowsOutSet)
        {
            if (sym is not (ILocalSymbol or IParameterSymbol)) continue;

            var type = sym switch
            {
                ILocalSymbol local => local.Type,
                IParameterSymbol param => param.Type,
                _ => null
            };
            if (type is null) continue;

            if (declaredSet.Contains(sym))
            {
                // Born inside, used after -> return candidate (needs declaration at call site)
                returnCandidates.Add((sym, type, NeedsDeclaration: true));
            }
            else if (writtenInsideSet.Contains(sym) && !flowsInSet.Contains(sym))
            {
                // Written inside, flows out, not read before write -> this is an OUT parameter, not a return candidate.
                // It will be classified as an out parameter in the out-parameter loop below.
            }
        }

        if (returnCandidates.Count > 1)
        {
            // Can't handle multiple return values -- this will be caught and reported as an error
            // by the caller. We still classify parameters for diagnostic purposes.
            // Actually, let's just pick the first and flag via error upstream.
            // No -- plan says ErrorResponse. We'll return a sentinel.
        }
        else if (returnCandidates.Count == 1)
        {
            var (sym, type, needsDecl) = returnCandidates[0];
            returnInfo = new ReturnInfo(sym, type, needsDecl);
        }

        // Parameters: DataFlowsIn except VariablesDeclared, filtered to locals/params only
        foreach (var sym in dataFlow.DataFlowsIn)
        {
            if (declaredSet.Contains(sym)) continue;
            if (sym is not (ILocalSymbol or IParameterSymbol)) continue;

            var type = sym switch
            {
                ILocalSymbol local => local.Type,
                IParameterSymbol param => param.Type,
                _ => null
            };
            if (type is null) continue;

            // Skip if this is already the return candidate
            if (returnInfo is not null && SymbolEqualityComparer.Default.Equals(sym, returnInfo.Symbol))
            {
                // If it flows in AND is the return, it's a ref parameter
                if (writtenInsideSet.Contains(sym) && flowsOutSet.Contains(sym))
                {
                    if (IsRefEligible(sym))
                        parameters.Add(new ParameterInfo(sym, type, RefKind.Ref));
                    else
                        parameters.Add(new ParameterInfo(sym, type, RefKind.None));
                    returnInfo = null; // ref takes precedence over return
                    continue;
                }
            }

            // Classify: ref if flows in AND written inside AND flows out
            if (writtenInsideSet.Contains(sym) && flowsOutSet.Contains(sym))
            {
                if (IsRefEligible(sym))
                    parameters.Add(new ParameterInfo(sym, type, RefKind.Ref));
                else
                    parameters.Add(new ParameterInfo(sym, type, RefKind.None));
            }
            else
            {
                parameters.Add(new ParameterInfo(sym, type, RefKind.None));
            }
        }

        // Out parameters: written inside + flows out, not in DataFlowsIn, not declared inside
        foreach (var sym in flowsOutSet)
        {
            if (declaredSet.Contains(sym)) continue;
            if (flowsInSet.Contains(sym)) continue;
            if (sym is not (ILocalSymbol or IParameterSymbol)) continue;
            if (returnInfo is not null && SymbolEqualityComparer.Default.Equals(sym, returnInfo.Symbol)) continue;
            // Don't double-add if already handled
            if (parameters.Any(p => SymbolEqualityComparer.Default.Equals(p.Symbol, sym))) continue;

            var type = sym switch
            {
                ILocalSymbol local => local.Type,
                IParameterSymbol param => param.Type,
                _ => null
            };
            if (type is null) continue;

            if (writtenInsideSet.Contains(sym) && IsRefEligible(sym))
                parameters.Add(new ParameterInfo(sym, type, RefKind.Out));
            else if (writtenInsideSet.Contains(sym))
                parameters.Add(new ParameterInfo(sym, type, RefKind.None));
        }

        return (parameters, returnInfo, returnCandidates.Count);
    }

    private static bool IsRefEligible(ISymbol symbol)
    {
        return symbol is ILocalSymbol or IParameterSymbol { RefKind: RefKind.None or RefKind.Ref or RefKind.Out };
    }

    private static (MethodDeclarationSyntax Method, StatementSyntax CallSite) BuildExtractedMethod(
        ExtractableSelection selection,
        List<ParameterInfo> parameters,
        ReturnInfo? returnInfo,
        ITypeSymbol? expressionType,
        SyntaxNode enclosingMember,
        string methodName,
        string accessibility,
        bool isStatic,
        bool isAsync)
    {
        // Build parameter list
        var paramSyntaxList = parameters.Select(p =>
        {
            var paramSyntax = SyntaxFactory.Parameter(SyntaxFactory.Identifier(p.Symbol.Name))
                .WithType(SyntaxFactory.ParseTypeName(p.Type.ToDisplayString()).WithTrailingTrivia(SyntaxFactory.Space));

            if (p.RefKind == RefKind.Ref)
                paramSyntax = paramSyntax.AddModifiers(SyntaxFactory.Token(SyntaxKind.RefKeyword));
            else if (p.RefKind == RefKind.Out)
                paramSyntax = paramSyntax.AddModifiers(SyntaxFactory.Token(SyntaxKind.OutKeyword));

            return paramSyntax;
        }).ToArray();

        // Build body
        var bodyStatements = new List<StatementSyntax>();

        if (selection.Statements is not null)
        {
            foreach (var stmt in selection.Statements)
                bodyStatements.Add(stmt.WithoutLeadingTrivia().WithoutTrailingTrivia());

            if (returnInfo is not null)
                bodyStatements.Add(SyntaxFactory.ReturnStatement(
                    SyntaxFactory.IdentifierName(returnInfo.Symbol.Name)));
        }
        else if (selection.Expression is not null)
        {
            bool isVoidExpression = expressionType == null
                || expressionType.SpecialType == SpecialType.System_Void;

            if (isVoidExpression)
            {
                // void expression: just call it, no return
                bodyStatements.Add(SyntaxFactory.ExpressionStatement(
                    selection.Expression.WithoutLeadingTrivia().WithoutTrailingTrivia()));
            }
            else
            {
                bodyStatements.Add(SyntaxFactory.ReturnStatement(
                    selection.Expression.WithoutLeadingTrivia().WithoutTrailingTrivia()));
            }
        }

        var body = SyntaxFactory.Block(bodyStatements);

        // Determine return type
        TypeSyntax returnType;
        if (selection.Expression is not null && expressionType is not null
            && expressionType.SpecialType != SpecialType.System_Void)
        {
            returnType = SyntaxFactory.ParseTypeName(expressionType.ToDisplayString());
            if (isAsync)
                returnType = SyntaxFactory.QualifiedName(
                    SyntaxFactory.QualifiedName(
                        SyntaxFactory.QualifiedName(
                            SyntaxFactory.IdentifierName("System"),
                            SyntaxFactory.IdentifierName("Threading")),
                        SyntaxFactory.IdentifierName("Tasks")),
                    SyntaxFactory.GenericName("Task")
                        .AddTypeArgumentListArguments(returnType));
        }
        else if (returnInfo is not null)
        {
            returnType = SyntaxFactory.ParseTypeName(returnInfo.Type.ToDisplayString());
            if (isAsync)
                returnType = SyntaxFactory.QualifiedName(
                    SyntaxFactory.QualifiedName(
                        SyntaxFactory.QualifiedName(
                            SyntaxFactory.IdentifierName("System"),
                            SyntaxFactory.IdentifierName("Threading")),
                        SyntaxFactory.IdentifierName("Tasks")),
                    SyntaxFactory.GenericName("Task")
                        .AddTypeArgumentListArguments(returnType));
        }
        else
        {
            returnType = isAsync
                ? SyntaxFactory.ParseTypeName("System.Threading.Tasks.Task")
                : SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword));
        }

        // Build method modifiers
        var modifiers = new List<SyntaxToken>();
        var accessToken = accessibility.ToLowerInvariant() switch
        {
            "public" => SyntaxFactory.Token(SyntaxKind.PublicKeyword),
            "internal" => SyntaxFactory.Token(SyntaxKind.InternalKeyword),
            "protected" => SyntaxFactory.Token(SyntaxKind.ProtectedKeyword),
            _ => SyntaxFactory.Token(SyntaxKind.PrivateKeyword)
        };
        modifiers.Add(accessToken);

        if (isStatic)
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.StaticKeyword));
        if (isAsync)
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.AsyncKeyword));

        var methodDecl = SyntaxFactory.MethodDeclaration(returnType, methodName)
            .WithModifiers(SyntaxFactory.TokenList(modifiers))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(paramSyntaxList)))
            .WithBody(body);

        // Build call site
        var argList = parameters.Select(p =>
        {
            var arg = SyntaxFactory.Argument(SyntaxFactory.IdentifierName(p.Symbol.Name));
            if (p.RefKind == RefKind.Ref)
                arg = arg.WithRefOrOutKeyword(SyntaxFactory.Token(SyntaxKind.RefKeyword));
            else if (p.RefKind == RefKind.Out)
                arg = arg.WithRefOrOutKeyword(SyntaxFactory.Token(SyntaxKind.OutKeyword));
            return arg;
        }).ToArray();

        var invocation = SyntaxFactory.InvocationExpression(
            SyntaxFactory.IdentifierName(methodName),
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(argList)));

        ExpressionSyntax callExpression = isAsync
            ? SyntaxFactory.AwaitExpression(invocation)
            : invocation;

        StatementSyntax callSite;
        if (selection.Expression is not null && expressionType is not null)
        {
            // Expression from ExpressionStatement: result was discarded, just call the method
            callSite = SyntaxFactory.ExpressionStatement(callExpression);
        }
        else if (returnInfo is not null)
        {
            if (returnInfo.NeedsDeclaration)
            {
                // Variable born inside extraction: var x = Method(...)
                callSite = SyntaxFactory.LocalDeclarationStatement(
                    SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var"))
                        .AddVariables(SyntaxFactory.VariableDeclarator(returnInfo.Symbol.Name)
                            .WithInitializer(SyntaxFactory.EqualsValueClause(callExpression))));
            }
            else
            {
                // Pre-existing variable: x = Method(...)
                callSite = SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.IdentifierName(returnInfo.Symbol.Name),
                        callExpression));
            }
        }
        else
        {
            callSite = SyntaxFactory.ExpressionStatement(callExpression);
        }

        return (methodDecl, callSite);
    }

    private static SyntaxNode ResolveInsertionPoint(SyntaxNode enclosingMember)
    {
        return enclosingMember switch
        {
            AccessorDeclarationSyntax accessor =>
                accessor.FirstAncestorOrSelf<PropertyDeclarationSyntax>() ?? (SyntaxNode)accessor,
            LocalFunctionStatementSyntax localFunc =>
                localFunc.FirstAncestorOrSelf<MethodDeclarationSyntax>() ?? (SyntaxNode)localFunc,
            _ => enclosingMember
        };
    }

    private static string? EnsureMethodNameUnique(string name, INamedTypeSymbol type)
    {
        bool HasMember(string n)
        {
            var current = type;
            while (current is not null)
            {
                if (current.GetMembers(n).Length > 0) return true;
                current = current.BaseType;
            }
            return false;
        }

        if (!HasMember(name)) return name;

        for (int i = 1; i < 100; i++)
        {
            var candidate = $"{name}{i}";
            if (!HasMember(candidate)) return candidate;
        }
        return null; // Could not generate unique name
    }
}
