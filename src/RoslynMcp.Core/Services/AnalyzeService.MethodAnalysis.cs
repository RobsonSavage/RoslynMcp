using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMcp.Core.Helpers;
using RoslynMcp.Shared;
using RoslynMcp.Shared.Contracts.Analyze;
using RoslynMcp.Shared.Contracts.Common;
using Contracts = RoslynMcp.Shared.Contracts.Common;

namespace RoslynMcp.Core.Services;

public partial class AnalyzeService
{
    // ── 2. understand_method ──

    public async Task<Result<UnderstandMethodResponse>> UnderstandMethodAsync(
        UnderstandMethodRequest request, CancellationToken ct = default)
    {
        var (doc, symbol, error) = await _helpers.ResolveAsync(request.FilePath, request.Line, request.Column, ct).ConfigureAwait(false);
        if (error != null) return Result<UnderstandMethodResponse>.Fail(error);

        var solution = _workspace.CurrentSolution;
        if (solution is null)
            return Result<UnderstandMethodResponse>.Fail("No solution loaded");

        if (symbol is not IMethodSymbol methodSymbol)
            return Result<UnderstandMethodResponse>.Fail("Symbol at position is not a method");

        var signature = methodSymbol.ToDisplayString();
        var returnType = methodSymbol.ReturnType.ToDisplayString();
        var parameters = methodSymbol.Parameters.Select(RoslynMapper.ToParameterInfo).ToList();

        // Location
        var loc = methodSymbol.Locations.FirstOrDefault(l => l.IsInSource);
        var codeLocation = loc != null ? RoslynMapper.ToCodeLocation(loc) : null;

        // Body source and metrics
        string? bodySource = null;
        CodeMetrics metrics;
        var declRef = methodSymbol.DeclaringSyntaxReferences.FirstOrDefault();

        // Resolve declaration node, document, and model once for reuse
        SyntaxNode? declNode = null;
        Document? declDoc = null;
        SemanticModel? declModel = null;
        if (declRef != null)
        {
            declNode = await declRef.GetSyntaxAsync(ct).ConfigureAwait(false);
            declDoc = WorkspaceHelpers.GetDocumentByPath(solution, declRef.SyntaxTree.FilePath) ?? doc!;
            declModel = await declDoc.GetSemanticModelAsync(ct).ConfigureAwait(false);
        }

        if (declNode != null)
        {
            bodySource = declNode.ToFullString();
            metrics = ComputeMetrics(declNode, methodSymbol.Parameters.Length);
        }
        else
        {
            metrics = new CodeMetrics(1, 0, 100, methodSymbol.Parameters.Length, 0, 0);
        }

        // Callers
        var callerSymbols = new List<Contracts.SymbolInfo>();
        {
            var callerInfos = await SymbolFinder.FindCallersAsync(methodSymbol, solution, ct).ConfigureAwait(false);
            int callerCount = 0;
            foreach (var caller in callerInfos)
            {
                ct.ThrowIfCancellationRequested();
                if (callerCount >= request.CallerDepth * ValidationLimits.CallersPerDepthLevel) break;
                callerSymbols.Add(RoslynMapper.ToSymbolInfo(caller.CallingSymbol));
                callerCount++;
            }
        }

        // Callees
        var calleeSymbols = new List<Contracts.SymbolInfo>();
        if (declNode != null && declModel != null)
        {
            var walker = new CalleeCollector(declModel, ct);
            walker.Visit(declNode);
            foreach (var (callee, _) in walker.Callees)
            {
                calleeSymbols.Add(RoslynMapper.ToSymbolInfo(callee));
            }
        }


        return new UnderstandMethodResponse(
            RoslynMapper.ToSymbolInfo(methodSymbol),
            signature,
            returnType,
            parameters,
            metrics,
            callerSymbols,
            calleeSymbols,
            codeLocation,
            bodySource);
    }

    // ── 6. get_method_body ──

    public async Task<Result<GetMethodBodyResponse>> GetMethodBodyAsync(
        GetMethodBodyRequest request, CancellationToken ct = default)
    {
        var (doc, symbol, error) = await _helpers.ResolveAsync(request.FilePath, request.Line, request.Column, ct).ConfigureAwait(false);
        if (error != null) return Result<GetMethodBodyResponse>.Fail(error);

        if (symbol is not IMethodSymbol methodSymbol)
            return Result<GetMethodBodyResponse>.Fail("Symbol at position is not a method");

        var declRef = methodSymbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (declRef is null)
            return Result<GetMethodBodyResponse>.Fail("Method has no source declaration");

        var declNode = await declRef.GetSyntaxAsync(ct).ConfigureAwait(false);
        var bodySource = declNode.ToFullString();
        var lineCount = CountLines(bodySource);

        var span = declNode.GetLocation().GetLineSpan();
        var codeLocation = RoslynMapper.ToCodeLocation(span);


        return new GetMethodBodyResponse(
            RoslynMapper.ToSymbolInfo(methodSymbol),
            bodySource,
            codeLocation,
            lineCount);
    }

    // ── 7. get_code_metrics ──

    public async Task<Result<GetCodeMetricsResponse>> GetCodeMetricsAsync(
        GetCodeMetricsRequest request, CancellationToken ct = default)
    {
        // Try to resolve as a method first via file/line/col, then as a type
        if (request.FilePath != null && request.Line.HasValue && request.Column.HasValue)
        {
            var (doc, symbol, error) = await _helpers.ResolveAsync(request.FilePath, request.Line.Value, request.Column.Value, ct).ConfigureAwait(false);
            if (error != null) return Result<GetCodeMetricsResponse>.Fail(error);

            var declRef = symbol!.DeclaringSyntaxReferences.FirstOrDefault();
            if (declRef is null)
                return Result<GetCodeMetricsResponse>.Fail("Symbol has no source declaration");

            var declNode = await declRef.GetSyntaxAsync(ct).ConfigureAwait(false);
            int paramCount = symbol is IMethodSymbol ms ? ms.Parameters.Length : 0;
            var metrics = ComputeMetrics(declNode, paramCount);


            return new GetCodeMetricsResponse(RoslynMapper.ToSymbolInfo(symbol), metrics);
        }

        // Resolve by type name
        var (typeSymbol, typeError) = await _helpers.ResolveTypeAsync(
            request.TypeName, request.FilePath, request.Line, request.Column, ct).ConfigureAwait(false);
        if (typeError != null) return Result<GetCodeMetricsResponse>.Fail(typeError);

        var typeDeclRef = typeSymbol!.DeclaringSyntaxReferences.FirstOrDefault();
        if (typeDeclRef is null)
            return Result<GetCodeMetricsResponse>.Fail("Type has no source declaration");

        var typeDeclNode = await typeDeclRef.GetSyntaxAsync(ct).ConfigureAwait(false);
        var typeMetrics = ComputeMetrics(typeDeclNode, 0);


        return new GetCodeMetricsResponse(RoslynMapper.ToSymbolInfo(typeSymbol), typeMetrics);
    }

    /// <summary>
    /// Computes simplified code metrics for a syntax node.
    /// Uses a simplified Maintainability Index that omits Halstead Volume.
    /// Values are not comparable to Visual Studio Code Analysis MI values.
    /// </summary>
    private static CodeMetrics ComputeMetrics(SyntaxNode node, int parameterCount)
    {
        var sourceText = node.ToFullString();
        var linesOfCode = CountLines(sourceText);

        var complexityWalker = new CyclomaticComplexityWalker();
        complexityWalker.Visit(node);
        int cyclomaticComplexity = complexityWalker.Complexity;

        var nestingWalker = new NestingDepthWalker();
        nestingWalker.Visit(node);
        int nestingDepth = nestingWalker.MaxDepth;

        int returnPoints = node.DescendantNodes().OfType<ReturnStatementSyntax>().Count();

        // Simplified Maintainability Index (without Halstead Volume)
        double lnCC = cyclomaticComplexity > 0 ? Math.Log(cyclomaticComplexity) : 0;
        double lnLOC = linesOfCode > 0 ? Math.Log(linesOfCode) : 0;
        double mi = Math.Max(0, (171.0 - 5.2 * lnCC - 16.2 * lnLOC) * 100.0 / 171.0);
        int maintainabilityIndex = (int)Math.Round(mi);

        return new CodeMetrics(
            cyclomaticComplexity,
            linesOfCode,
            maintainabilityIndex,
            parameterCount,
            nestingDepth,
            returnPoints);
    }

    private static int CountLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int count = 1;
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\n') count++;
        return count;
    }

}
