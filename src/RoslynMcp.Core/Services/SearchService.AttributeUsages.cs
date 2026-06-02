using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMcp.Core.Helpers;
using RoslynMcp.Shared;
using RoslynMcp.Shared.Contracts.Search;

namespace RoslynMcp.Core.Services;

public partial class SearchService
{
    // ── 11. find_attribute_usages ──

    public async Task<Result<FindAttributeUsagesResponse>> FindAttributeUsagesAsync(
        FindAttributeUsagesRequest request, CancellationToken ct = default)
    {
        var solution = _workspace.CurrentSolution;
        if (solution is null)
        {
            _logger.Warning("{MethodName}: No solution loaded", nameof(FindAttributeUsagesAsync));
            return Result<FindAttributeUsagesResponse>.Fail("No solution loaded");
        }
        var attrName = request.AttributeName;
        var attrNameWithSuffix = attrName.EndsWith("Attribute", StringComparison.OrdinalIgnoreCase) ? attrName : attrName + "Attribute";

        // Find the attribute type (pass 0 - one-time cost)
        INamedTypeSymbol? attrType = null;
        foreach (var project in solution.Projects)
        {
            ct.ThrowIfCancellationRequested();
            var compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
            if (compilation is null) continue;

            attrType = compilation.GetTypeByMetadataName(attrNameWithSuffix)
                ?? compilation.GetTypeByMetadataName(attrName);

            if (attrType is null)
            {
                // Use declaration name index — O(1) lookup vs O(N) full syntax-tree scan
                attrType = compilation.GetSymbolsWithName(attrNameWithSuffix, SymbolFilter.Type, ct)
                    .OfType<INamedTypeSymbol>()
                    .FirstOrDefault()
                    ?? compilation.GetSymbolsWithName(attrName, SymbolFilter.Type, ct)
                        .OfType<INamedTypeSymbol>()
                        .FirstOrDefault();
            }
            if (attrType != null) break;
        }

        if (attrType is null)
            return Result<FindAttributeUsagesResponse>.Fail($"Attribute type not found: {request.AttributeName}");

        var refs = await SymbolFinder.FindReferencesAsync(attrType, solution, ct).ConfigureAwait(false);

        // Pass 1: Build stubs
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var stubs = new List<AttributeUsageStub>();
        foreach (var group in refs)
        {
            foreach (var loc in group.Locations)
            {
                if (stubs.Count >= PagingHelper.MaxResults) break;
                ct.ThrowIfCancellationRequested();
                if (!loc.Location.IsInSource) continue;
                if (loc.Document is null) continue;
                if (IsDefinitionLocation(loc.Location, group.Definition)) continue;
                var span = loc.Location.GetLineSpan();
                var codeLocation = RoslynMapper.ToCodeLocation(span);
                if (codeLocation is null) continue;
                stubs.Add(new AttributeUsageStub(codeLocation, loc.Document.Id, loc.Location.SourceSpan.Start));
            }
            if (stubs.Count >= PagingHelper.MaxResults) break;
        }

        var snapshotSolution = solution;
        sw.Restart();

        // Pass 2: Enrich requested page
        var result = await PagingHelper.PageAndEnrichAsync(
            stubs, request.Page, request.PageSize,
            (AttributeUsageStub stub, CancellationToken ct2) => EnrichAttributeUsageAsync(stub, snapshotSolution, ct2),
            (i, ex) => _logger.Warning(ex, "find_attribute_usages enrichment failed at {Index}", i),
            ct).ConfigureAwait(false);


        return new FindAttributeUsagesResponse(request.AttributeName, result);
    }

    private async Task<AttributeUsageItem> EnrichAttributeUsageAsync(
        AttributeUsageStub stub, Solution solution, CancellationToken ct)
    {
        ISymbol? decoratedSymbol = null;
        if (stub.DocumentId is not null)
        {
            var refDoc = solution.GetDocument(stub.DocumentId);
            if (refDoc != null)
            {
                var root = await refDoc.GetSyntaxRootAsync(ct).ConfigureAwait(false);
                var node = root?.FindToken(stub.SourceSpanStart).Parent;
                var attrSyntax = node?.FirstAncestorOrSelf<AttributeSyntax>();
                var attrList = attrSyntax?.Parent as AttributeListSyntax;
                var decorated = attrList?.Parent;

                if (decorated != null)
                {
                    var model = await refDoc.GetSemanticModelAsync(ct).ConfigureAwait(false);
                    if (model != null)
                        decoratedSymbol = model.GetDeclaredSymbol(decorated, ct);
                }
            }
        }

        var symbolInfo = decoratedSymbol != null
            ? RoslynMapper.ToSymbolInfo(decoratedSymbol)
            : UnknownSymbol;
        return new AttributeUsageItem(symbolInfo, stub.Location);
    }

    private static bool IsDefinitionLocation(Location refLocation, ISymbol definition)
    {
        foreach (var defLoc in definition.Locations)
        {
            if (defLoc.IsInSource
                && defLoc.SourceTree == refLocation.SourceTree
                && defLoc.SourceSpan.Contains(refLocation.SourceSpan))
                return true;
        }
        return false;
    }
}
