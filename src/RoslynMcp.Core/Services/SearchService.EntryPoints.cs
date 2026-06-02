using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMcp.Core.Helpers;
using RoslynMcp.Shared;
using RoslynMcp.Shared.Contracts.Search;

namespace RoslynMcp.Core.Services;

public partial class SearchService
{
    // ── 9. find_entry_points ──

    public async Task<Result<FindEntryPointsResponse>> FindEntryPointsAsync(
        FindEntryPointsRequest request, CancellationToken ct = default)
    {
        var solution = _workspace.CurrentSolution;
        if (solution is null)
        {
            _logger.Warning("{MethodName}: No solution loaded", nameof(FindEntryPointsAsync));
            return Result<FindEntryPointsResponse>.Fail("No solution loaded");
        }
        var items = new List<EntryPointItem>();
        var controllerTypes = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

        foreach (var project in solution.Projects)
        {
            if (request.ProjectName != null &&
                !string.Equals(project.Name, request.ProjectName, StringComparison.OrdinalIgnoreCase))
                continue;

            ct.ThrowIfCancellationRequested();
            var compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
            if (compilation is null) continue;

            // Compilation entry point (Main / top-level statements)
            var entryPoint = compilation.GetEntryPoint(ct);
            if (entryPoint != null)
            {
                var location = entryPoint.Locations.FirstOrDefault(l => l.IsInSource);
                if (location != null)
                {
                    var codeLocation = RoslynMapper.ToCodeLocation(location);
                    if (codeLocation != null)
                        items.Add(new EntryPointItem(RoslynMapper.ToSymbolInfo(entryPoint), codeLocation, "Main"));
                }
            }

            // [ApiController] types — use SymbolFinder index instead of brute-force
            var apiControllerAttr = compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Mvc.ApiControllerAttribute");
            if (apiControllerAttr != null)
            {
                var attrRefs = await SymbolFinder.FindReferencesAsync(apiControllerAttr, solution, ct).ConfigureAwait(false);
                foreach (var group in attrRefs)
                {
                    foreach (var loc in group.Locations)
                    {
                        if (items.Count >= PagingHelper.MaxResults) break;
                        ct.ThrowIfCancellationRequested();
                        if (loc.Document is null) continue;

                        var refDoc = solution.GetDocument(loc.Document.Id);
                        if (refDoc is null) continue;

                        var refRoot = await refDoc.GetSyntaxRootAsync(ct).ConfigureAwait(false);
                        if (refRoot is null) continue;

                        var node = refRoot.FindToken(loc.Location.SourceSpan.Start).Parent;
                        var attrSyntax = node?.FirstAncestorOrSelf<AttributeSyntax>();
                        var classDecl = attrSyntax?.FirstAncestorOrSelf<ClassDeclarationSyntax>();
                        if (classDecl is null) continue;

                        var refModel = await refDoc.GetSemanticModelAsync(ct).ConfigureAwait(false);
                        if (refModel is null) continue;
                        var typeSym = refModel.GetDeclaredSymbol(classDecl, ct);
                        if (typeSym is null) continue;

                        var typeLocation = typeSym.Locations.FirstOrDefault(l => l.IsInSource);
                        if (typeLocation is null) continue;
                        var codeLocation = RoslynMapper.ToCodeLocation(typeLocation);
                        if (codeLocation is null) continue;

                        items.Add(new EntryPointItem(RoslynMapper.ToSymbolInfo((INamedTypeSymbol)typeSym), codeLocation, "ApiController"));
                        controllerTypes.Add((INamedTypeSymbol)typeSym);
                    }
                    if (items.Count >= PagingHelper.MaxResults) break;
                }
            }
            if (items.Count >= PagingHelper.MaxResults) break;
        }

        // Discover controllers inheriting [ApiController] from a base class
        foreach (var baseController in controllerTypes.ToList())
        {
            if (items.Count >= PagingHelper.MaxResults) break;
            ct.ThrowIfCancellationRequested();
            var derived = await SymbolFinder.FindDerivedClassesAsync(baseController, solution, cancellationToken: ct).ConfigureAwait(false);
            foreach (var derivedType in derived)
            {
                if (items.Count >= PagingHelper.MaxResults) break;
                if (controllerTypes.Contains(derivedType)) continue;
                var loc = derivedType.Locations.FirstOrDefault(l => l.IsInSource);
                if (loc is null) continue;
                var codeLocation = RoslynMapper.ToCodeLocation(loc);
                if (codeLocation is null) continue;
                items.Add(new EntryPointItem(RoslynMapper.ToSymbolInfo(derivedType), codeLocation, "ApiController"));
            }
        }

        return new FindEntryPointsResponse(PagingHelper.Page(items, request.Page, request.PageSize));
    }
}
