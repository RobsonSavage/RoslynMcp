using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Core.Helpers;
using RoslynMcp.Shared;
using RoslynMcp.Shared.Contracts.Analyze;
using RoslynMcp.Shared.Contracts.Common;

namespace RoslynMcp.Core.Services;

public partial class AnalyzeService
{
    // ── 8. analyze_data_flow ──

    public async Task<Result<AnalyzeDataFlowResponse>> AnalyzeDataFlowAsync(
        AnalyzeDataFlowRequest request, CancellationToken ct = default)
    {
        if (!_workspace.HasSolution)
        {
            _logger.Warning("analyze_data_flow: No solution loaded");
            return Result<AnalyzeDataFlowResponse>.Fail("No solution loaded");
        }

        var doc = await _workspace.GetDocumentAsync(request.FilePath, ct: ct).ConfigureAwait(false);
        if (doc is null)
        {
            _logger.Warning("analyze_data_flow: Document not found: {FilePath}", request.FilePath);
            return Result<AnalyzeDataFlowResponse>.Fail($"Document not found: {request.FilePath}");
        }

        var root = await doc.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var model = await doc.GetSemanticModelAsync(ct).ConfigureAwait(false);
        var text = await doc.GetTextAsync(ct).ConfigureAwait(false);

        if (root is null || model is null)
            return Result<AnalyzeDataFlowResponse>.Fail("Could not get syntax tree or semantic model");

        // Find statements in the requested range
        var startPosResult = WorkspaceHelpers.GetSafePosition(text, request.StartLine, request.StartColumn);
        if (!startPosResult.IsSuccess)
            return Result<AnalyzeDataFlowResponse>.Fail(startPosResult.Error!.Message, startPosResult.Error.ErrorCode);

        var endPosResult = WorkspaceHelpers.GetSafePosition(text, request.EndLine, request.EndColumn);
        if (!endPosResult.IsSuccess)
            return Result<AnalyzeDataFlowResponse>.Fail(endPosResult.Error!.Message, endPosResult.Error.ErrorCode);

        if (startPosResult.Value > endPosResult.Value)
            return Result<AnalyzeDataFlowResponse>.Fail("Start position must be before end position", "INVALID_POSITION");

        var requestSpan = TextSpan.FromBounds(startPosResult.Value, endPosResult.Value);

        // Find the first and last statements within the range.
        // Exclude BlockSyntax so we only get leaf-level statements, then group
        // by parent to ensure all belong to the same statement list.
        var statementsInRange = root.DescendantNodes()
            .OfType<StatementSyntax>()
            .Where(s => s is not BlockSyntax
                && (requestSpan.Contains(s.Span) || requestSpan.IntersectsWith(s.Span)))
            .OrderBy(s => s.SpanStart)
            .ToList();

        if (statementsInRange.Count == 0)
            return Result<AnalyzeDataFlowResponse>.Fail("No statements found in the specified range");

        // When multiple statements match, keep only those sharing the same parent
        // (i.e., the same statement list) to satisfy Roslyn's requirement.
        if (statementsInRange.Count > 1)
        {
            var groups = statementsInRange.GroupBy(s => s.Parent).ToList();
            if (groups.Count > 1)
                return Result<AnalyzeDataFlowResponse>.Fail("Range spans multiple statement contexts; narrow the range to a single block");
            statementsInRange = groups[0].OrderBy(s => s.SpanStart).ToList();
        }

        var firstStatement = statementsInRange.First();
        var lastStatement = statementsInRange.Last();

        DataFlowAnalysis? dataFlow;
        if (ReferenceEquals(firstStatement, lastStatement))
        {
            dataFlow = model.AnalyzeDataFlow(firstStatement);
        }
        else
        {
            dataFlow = model.AnalyzeDataFlow(firstStatement, lastStatement);
        }

        if (dataFlow is null || !dataFlow.Succeeded)
            return Result<AnalyzeDataFlowResponse>.Fail("Data flow analysis failed for the specified range");

        var info = new DataFlowInfo(
            VariablesDeclared: dataFlow.VariablesDeclared.Select(s => s.Name).ToList(),
            DataFlowsIn: dataFlow.DataFlowsIn.Select(s => s.Name).ToList(),
            DataFlowsOut: dataFlow.DataFlowsOut.Select(s => s.Name).ToList(),
            ReadInside: dataFlow.ReadInside.Select(s => s.Name).ToList(),
            WrittenInside: dataFlow.WrittenInside.Select(s => s.Name).ToList(),
            ReadOutside: dataFlow.ReadOutside.Select(s => s.Name).ToList(),
            WrittenOutside: dataFlow.WrittenOutside.Select(s => s.Name).ToList(),
            AlwaysAssigned: dataFlow.AlwaysAssigned.Select(s => s.Name).ToList(),
            Captured: dataFlow.Captured.Select(s => s.Name).ToList(),
            UnsafeAddressTaken: dataFlow.UnsafeAddressTaken.Select(s => s.Name).ToList());

        var analyzedSpan = TextSpan.FromBounds(firstStatement.SpanStart, lastStatement.Span.End);
        var analyzedRange = RoslynMapper.ToCodeRange(analyzedSpan, text);


        return new AnalyzeDataFlowResponse(info, analyzedRange);
    }

    // ── 9. impact_analysis ──

    public async Task<Result<ImpactAnalysisResponse>> ImpactAnalysisAsync(
        ImpactAnalysisRequest request, CancellationToken ct = default)
    {
        var (doc, symbol, error) = await _helpers.ResolveAsync(request.FilePath, request.Line, request.Column, ct).ConfigureAwait(false);
        if (error != null) return Result<ImpactAnalysisResponse>.Fail(error);

        var solution = _workspace.CurrentSolution;
        if (solution is null)
            return Result<ImpactAnalysisResponse>.Fail("No solution loaded");
        var visited = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var impactNodes = new List<ImpactNode>();
        var frontier = new List<ISymbol> { symbol! };
        visited.Add(symbol!);

        int maxDepth = Math.Min(Math.Max(1, request.Depth), ValidationLimits.MaxImpactDepth);
        int depthReached = 0;

        for (int depth = 1; depth <= maxDepth; depth++)
        {
            ct.ThrowIfCancellationRequested();
            var nextFrontier = new List<ISymbol>();
            if (impactNodes.Count >= ValidationLimits.MaxImpactNodes) break;

            foreach (var current in frontier)
            {
                ct.ThrowIfCancellationRequested();
                var refGroups = await SymbolFinder.FindReferencesAsync(current, solution, ct).ConfigureAwait(false);

                var allLocations = refGroups.SelectMany(g => g.Locations);
                var byDocument = allLocations.GroupBy(loc => loc.Document.Id);

                foreach (var docGroup in byDocument)
                {
                    ct.ThrowIfCancellationRequested();
                    var refDoc = solution.GetDocument(docGroup.Key);
                    if (refDoc is null) continue;

                    var model = await refDoc.GetSemanticModelAsync(ct).ConfigureAwait(false);
                    var refRoot = await refDoc.GetSyntaxRootAsync(ct).ConfigureAwait(false);
                    if (model is null || refRoot is null) continue;

                    foreach (var refLoc in docGroup)
                    {
                        ct.ThrowIfCancellationRequested();
                        var node = refRoot.FindToken(refLoc.Location.SourceSpan.Start).Parent;
                        var enclosingSymbol = GetEnclosingSymbol(model, node, ct);

                        if (enclosingSymbol != null && visited.Add(enclosingSymbol))
                        {
                            var encLoc = enclosingSymbol.Locations.FirstOrDefault(l => l.IsInSource);
                            var encCodeLoc = encLoc != null ? RoslynMapper.ToCodeLocation(encLoc) : null;
                            impactNodes.Add(new ImpactNode(
                                RoslynMapper.ToSymbolInfo(enclosingSymbol), encCodeLoc, depth));
                            nextFrontier.Add(enclosingSymbol);
                            if (nextFrontier.Count >= ValidationLimits.MaxFrontierWidth) break;
                            if (impactNodes.Count >= ValidationLimits.MaxImpactNodes) break;
                        }
                    }
                    if (impactNodes.Count >= ValidationLimits.MaxImpactNodes) break;
                }
            }

            if (nextFrontier.Count > 0)
                depthReached = depth;
            frontier = nextFrontier;
            if (frontier.Count == 0) break;
        }


        return new ImpactAnalysisResponse(
            RoslynMapper.ToSymbolInfo(symbol!),
            PagingHelper.Page(impactNodes, request.Page, request.PageSize),
            depthReached);
    }

    private static ISymbol? GetEnclosingSymbol(SemanticModel model, SyntaxNode? node, CancellationToken ct)
    {
        while (node != null)
        {
            var sym = model.GetDeclaredSymbol(node, ct);
            if (sym is IMethodSymbol or IPropertySymbol or IEventSymbol or IFieldSymbol or INamedTypeSymbol)
                return sym;
            node = node.Parent;
        }
        return null;
    }
}
