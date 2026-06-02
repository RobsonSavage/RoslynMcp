using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Core.Helpers;
using RoslynMcp.Shared;
using RoslynMcp.Shared.Contracts.Common;
using RoslynMcp.Shared.Contracts.Search;

namespace RoslynMcp.Core.Services;

public partial class SearchService
{
    // ── 6. find_overrides ──

    public async Task<Result<FindOverridesResponse>> FindOverridesAsync(
        FindOverridesRequest request, CancellationToken ct = default)
    {
        var (doc, symbol, error) = await _helpers.ResolveAsync(request.FilePath, request.Line, request.Column, ct).ConfigureAwait(false);
        if (error != null) return Result<FindOverridesResponse>.Fail(error);

        var solution = _workspace.CurrentSolution;
        if (solution is null)
        {
            _logger.Warning("{MethodName}: No solution loaded", nameof(FindOverridesAsync));
            return Result<FindOverridesResponse>.Fail("No solution loaded");
        }

        var stubs = new List<OverrideStub>();

        if (symbol is IMethodSymbol or IPropertySymbol or IEventSymbol)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            IEnumerable<ISymbol> overrides;
            if (symbol!.ContainingType?.TypeKind == TypeKind.Interface)
            {
                overrides = await SymbolFinder.FindImplementationsAsync(symbol, solution, cancellationToken: ct).ConfigureAwait(false);
            }
            else
            {
                overrides = await SymbolFinder.FindOverridesAsync(symbol, solution, cancellationToken: ct).ConfigureAwait(false);
            }

            foreach (var ovr in overrides)
            {
                if (stubs.Count >= PagingHelper.MaxResults) break;
                ct.ThrowIfCancellationRequested();
                var location = ovr.Locations.FirstOrDefault(l => l.IsInSource);
                if (location is null) continue;

                var codeLocation = RoslynMapper.ToCodeLocation(location);
                if (codeLocation is null) continue;

                var lineSpan = location.GetLineSpan();
                stubs.Add(new OverrideStub(
                    RoslynMapper.ToSymbolInfo(ovr), codeLocation,
                    ovr.ContainingType?.ToDisplayString(), lineSpan.Path, lineSpan.StartLinePosition.Line));
            }


            var snapshotSolution = solution;
            var includeCtx = request.IncludeContext;
            sw.Restart();

            var result = await PagingHelper.PageAndEnrichAsync(
                stubs, request.Page, request.PageSize,
                (OverrideStub stub, CancellationToken ct2) => EnrichOverrideAsync(stub, snapshotSolution, includeCtx, ct2),
                (i, ex) => _logger.Warning("find_overrides enrichment failed at {Index}: {Error}", i, ex.Message),
                ct).ConfigureAwait(false);


            return new FindOverridesResponse(RoslynMapper.ToSymbolInfo(symbol!), result);
        }

        return new FindOverridesResponse(
            RoslynMapper.ToSymbolInfo(symbol!),
            PagingHelper.Page(new List<OverrideItem>(), request.Page, request.PageSize));
    }

    private async Task<OverrideItem> EnrichOverrideAsync(
        OverrideStub stub, Solution solution, bool includeContext, CancellationToken ct)
    {
        string? contextLine = null;
        if (includeContext && stub.FilePath != null)
        {
            var ovrDoc = WorkspaceHelpers.GetDocumentByPath(solution, stub.FilePath);
            if (ovrDoc != null)
            {
                var text = await ovrDoc.GetTextAsync(ct).ConfigureAwait(false);
                contextLine = RoslynMapper.GetContextLine(text, stub.StartLine);
            }
        }
        return new OverrideItem(stub.Symbol, stub.Location, stub.ContainingType, contextLine);
    }

    // ── 7. find_derived_types ──

    public async Task<Result<FindDerivedTypesResponse>> FindDerivedTypesAsync(
        FindDerivedTypesRequest request, CancellationToken ct = default)
    {
        var (doc, symbol, error) = await _helpers.ResolveAsync(request.FilePath, request.Line, request.Column, ct).ConfigureAwait(false);
        if (error != null) return Result<FindDerivedTypesResponse>.Fail(error);

        if (symbol is not INamedTypeSymbol typeSymbol)
            return Result<FindDerivedTypesResponse>.Fail("Symbol at position is not a type");

        var solution = _workspace.CurrentSolution;
        if (solution is null)
        {
            _logger.Warning("{MethodName}: No solution loaded", nameof(FindDerivedTypesAsync));
            return Result<FindDerivedTypesResponse>.Fail("No solution loaded");
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();

        IEnumerable<INamedTypeSymbol> derived;
        if (typeSymbol.TypeKind == TypeKind.Interface)
        {
            var impls = await SymbolFinder.FindImplementationsAsync(typeSymbol, solution, cancellationToken: ct).ConfigureAwait(false);
            derived = impls.OfType<INamedTypeSymbol>();
        }
        else
        {
            derived = await SymbolFinder.FindDerivedClassesAsync(typeSymbol, solution, cancellationToken: ct).ConfigureAwait(false);
        }

        var stubs = new List<DerivedTypeStub>();
        foreach (var d in derived)
        {
            if (stubs.Count >= PagingHelper.MaxResults) break;
            ct.ThrowIfCancellationRequested();
            var location = d.Locations.FirstOrDefault(l => l.IsInSource);
            if (location is null) continue;

            var codeLocation = RoslynMapper.ToCodeLocation(location);
            if (codeLocation is null) continue;

            bool isDirect = SymbolEqualityComparer.Default.Equals(d.BaseType, typeSymbol)
                || d.Interfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, typeSymbol));

            var lineSpan = location.GetLineSpan();
            stubs.Add(new DerivedTypeStub(
                RoslynMapper.ToSymbolInfo(d), codeLocation, isDirect, lineSpan.Path, lineSpan.StartLinePosition.Line));
        }


        var snapshotSolution = solution;
        var includeCtx = request.IncludeContext;
        sw.Restart();

        var result = await PagingHelper.PageAndEnrichAsync(
            stubs, request.Page, request.PageSize,
            (DerivedTypeStub stub, CancellationToken ct2) => EnrichDerivedTypeAsync(stub, snapshotSolution, includeCtx, ct2),
            (i, ex) => _logger.Warning("find_derived_types enrichment failed at {Index}: {Error}", i, ex.Message),
            ct).ConfigureAwait(false);


        return new FindDerivedTypesResponse(RoslynMapper.ToSymbolInfo(symbol!), result);
    }

    private async Task<DerivedTypeItem> EnrichDerivedTypeAsync(
        DerivedTypeStub stub, Solution solution, bool includeContext, CancellationToken ct)
    {
        string? contextLine = null;
        if (includeContext && stub.FilePath != null)
        {
            var derivedDoc = WorkspaceHelpers.GetDocumentByPath(solution, stub.FilePath);
            if (derivedDoc != null)
            {
                var text = await derivedDoc.GetTextAsync(ct).ConfigureAwait(false);
                contextLine = RoslynMapper.GetContextLine(text, stub.StartLine);
            }
        }
        return new DerivedTypeItem(stub.Symbol, stub.Location, stub.IsDirect, contextLine);
    }

    // ── 8. find_base_members ──

    private static class BaseMemberKinds
    {
        public const string Override = "Override";
        public const string InterfaceImplementation = "InterfaceImplementation";
    }

    public async Task<Result<FindBaseMembersResponse>> FindBaseMembersAsync(
        FindBaseMembersRequest request, CancellationToken ct = default)
    {
        var (doc, symbol, error) = await _helpers.ResolveAsync(request.FilePath, request.Line, request.Column, ct).ConfigureAwait(false);
        if (error != null) return Result<FindBaseMembersResponse>.Fail(error);

        var items = new List<BaseMemberItem>();

        // Walk override chain upward
        ISymbol? overridden = symbol switch
        {
            IMethodSymbol m => m.OverriddenMethod,
            IPropertySymbol p => p.OverriddenProperty,
            IEventSymbol e => e.OverriddenEvent,
            _ => null
        };

        while (overridden != null)
        {
            if (items.Count >= PagingHelper.MaxResults) break;
            ct.ThrowIfCancellationRequested();
            var location = overridden.Locations.FirstOrDefault(l => l.IsInSource);
            var codeLocation = location != null ? RoslynMapper.ToCodeLocation(location) : null;
            items.Add(new BaseMemberItem(RoslynMapper.ToSymbolInfo(overridden), codeLocation, BaseMemberKinds.Override));

            overridden = overridden switch
            {
                IMethodSymbol m => m.OverriddenMethod,
                IPropertySymbol p => p.OverriddenProperty,
                IEventSymbol e => e.OverriddenEvent,
                _ => null
            };
        }

        // Find interface members this symbol implements
        if (symbol?.ContainingType != null)
        {
            foreach (var iface in symbol.ContainingType.AllInterfaces)
            {
                if (items.Count >= PagingHelper.MaxResults) break;
                foreach (var ifaceMember in iface.GetMembers())
                {
                    if (items.Count >= PagingHelper.MaxResults) break;
                    var impl = symbol.ContainingType.FindImplementationForInterfaceMember(ifaceMember);
                    if (impl != null && SymbolEqualityComparer.Default.Equals(impl, symbol))
                    {
                        var location = ifaceMember.Locations.FirstOrDefault(l => l.IsInSource);
                        var codeLocation = location != null ? RoslynMapper.ToCodeLocation(location) : null;
                        items.Add(new BaseMemberItem(
                            RoslynMapper.ToSymbolInfo(ifaceMember), codeLocation, BaseMemberKinds.InterfaceImplementation));
                    }
                }
            }
        }

        return new FindBaseMembersResponse(
            RoslynMapper.ToSymbolInfo(symbol!),
            PagingHelper.Page(items, request.Page, request.PageSize));
    }
}
