using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Core.Helpers;
using RoslynMcp.Shared;
using RoslynMcp.Shared.Contracts.Common;
using RoslynMcp.Shared.Contracts.Search;
using SymbolInfo = RoslynMcp.Shared.Contracts.Common.SymbolInfo;

namespace RoslynMcp.Core.Services;

public partial class SearchService
{
    /// <summary>
    /// Shared template for symbol-based search methods that follow the pattern:
    /// resolve symbol -> null-check solution -> collect stubs -> page+enrich -> log timings.
    /// Eliminates the repeated boilerplate across find_references, find_implementations,
    /// find_callers, find_overrides, find_derived_types, etc.
    /// </summary>
    private async Task<Result<TResponse>> ExecuteSymbolSearchAsync<TStub, TResult, TResponse>(
        string filePath, int line, int column, int page, int pageSize,
        string operationName,
        Func<ISymbol, Solution, CancellationToken, Task<IReadOnlyList<TStub>>> collectStubs,
        Func<TStub, Solution, CancellationToken, Task<TResult>> enrichStub,
        Func<SymbolInfo, PagedResult<TResult>, TResponse> buildResponse,
        CancellationToken ct = default)
        where TResponse : notnull
    {
        var (doc, symbol, error) = await _helpers.ResolveAsync(filePath, line, column, ct).ConfigureAwait(false);
        if (error != null) return Result<TResponse>.Fail(error);

        var solution = _workspace.CurrentSolution;
        if (solution is null)
        {
            _logger.Warning("{MethodName}: No solution loaded", operationName);
            return Result<TResponse>.Fail("No solution loaded");
        }

        // Pass 1: Collect stubs
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var stubs = await collectStubs(symbol!, solution, ct).ConfigureAwait(false);

        // Pass 2: Enrich requested page (uses same solution snapshot as pass 1)
        var snapshotSolution = solution;
        sw.Restart();
        var result = await PagingHelper.PageAndEnrichAsync(
            stubs, page, pageSize,
            (TStub stub, CancellationToken ct2) => enrichStub(stub, snapshotSolution, ct2),
            (i, ex) => _logger.Warning("{Operation} enrichment failed at {Index}: {Error}", operationName, i, ex.Message),
            ct).ConfigureAwait(false);


        return Result<TResponse>.Ok(buildResponse(RoslynMapper.ToSymbolInfo(symbol!), result));
    }

    // ── 1. find_references ──

    public async Task<Result<FindReferencesResponse>> FindReferencesAsync(
        FindReferencesRequest request, CancellationToken ct = default)
    {
        return await ExecuteSymbolSearchAsync<ReferenceStub, ReferenceItem, FindReferencesResponse>(
            request.FilePath, request.Line, request.Column, request.Page, request.PageSize,
            "find_references",
            async (symbol, solution, ct2) =>
            {
                var refGroups = await SymbolFinder.FindReferencesAsync(symbol, solution, ct2).ConfigureAwait(false);
                var stubs = new List<ReferenceStub>();
                foreach (var group in refGroups)
                {
                    foreach (var loc in group.Locations)
                    {
                        if (stubs.Count >= PagingHelper.MaxResults) break;
                        ct2.ThrowIfCancellationRequested();
                        var span = loc.Location.GetLineSpan();
                        var codeLocation = RoslynMapper.ToCodeLocation(span);
                        if (codeLocation is null) continue;
                        if (loc.Document is null || !loc.Location.IsInSource) continue;
                        stubs.Add(new ReferenceStub(codeLocation, loc.Document.Id, loc.Location.SourceSpan.Start));
                    }
                    if (stubs.Count >= PagingHelper.MaxResults) break;
                }
                return stubs;
            },
            (stub, solution2, ct2) => EnrichReferenceAsync(stub, solution2, request.IncludeContext, ct2),
            (symbolInfo, pagedResult) => new FindReferencesResponse(symbolInfo, pagedResult),
            ct).ConfigureAwait(false);
    }

    private async Task<ReferenceItem> EnrichReferenceAsync(
        ReferenceStub stub, Solution solution, bool includeContext, CancellationToken ct)
    {
        string? contextLine = null, containingMember = null, containingType = null;
        bool isWrite = false;

        if (stub.DocumentId is not null)
        {
            var refDoc = solution.GetDocument(stub.DocumentId);
            if (refDoc != null)
            {
                var root = await refDoc.GetSyntaxRootAsync(ct).ConfigureAwait(false);
                var node = root?.FindToken(stub.SourceSpanStart).Parent;
                if (node != null)
                    isWrite = RoslynMapper.IsWriteAccess(node);
                if (includeContext)
                {
                    var text = await refDoc.GetTextAsync(ct).ConfigureAwait(false);
                    contextLine = RoslynMapper.GetContextLine(text, stub.Location.StartLine);
                    var model = await refDoc.GetSemanticModelAsync(ct).ConfigureAwait(false);
                    if (model != null && node != null)
                        (containingMember, containingType) = RoslynMapper.GetEnclosingDeclaration(model, node, ct);
                }
            }
        }

        return new ReferenceItem(stub.Location, containingMember, containingType, contextLine, isWrite);
    }

    // ── 2. find_implementations ──

    public async Task<Result<FindImplementationsResponse>> FindImplementationsAsync(
        FindImplementationsRequest request, CancellationToken ct = default)
    {
        return await ExecuteSymbolSearchAsync<ImplementationStub, ImplementationItem, FindImplementationsResponse>(
            request.FilePath, request.Line, request.Column, request.Page, request.PageSize,
            "find_implementations",
            async (symbol, solution, ct2) =>
            {
                // FindImplementationsAsync works for interfaces/interface members.
                // For classes, use FindDerivedClassesAsync instead.
                IEnumerable<ISymbol> implementations;
                if (symbol is INamedTypeSymbol { TypeKind: TypeKind.Class } classSymbol)
                    implementations = (await SymbolFinder.FindDerivedClassesAsync(classSymbol, solution, cancellationToken: ct2).ConfigureAwait(false)).Cast<ISymbol>();
                else
                    implementations = await SymbolFinder.FindImplementationsAsync(symbol, solution, cancellationToken: ct2).ConfigureAwait(false);

                var stubs = new List<ImplementationStub>();
                foreach (var impl in implementations)
                {
                    if (stubs.Count >= PagingHelper.MaxResults) break;
                    ct2.ThrowIfCancellationRequested();
                    var location = impl.Locations.FirstOrDefault(l => l.IsInSource);
                    if (location is null) continue;
                    var codeLocation = RoslynMapper.ToCodeLocation(location);
                    if (codeLocation is null) continue;
                    var lineSpan = location.GetLineSpan();
                    stubs.Add(new ImplementationStub(RoslynMapper.ToSymbolInfo(impl), codeLocation, lineSpan.Path, lineSpan.StartLinePosition.Line));
                }
                return stubs;
            },
            (stub, solution2, ct2) => EnrichImplementationAsync(stub, solution2, request.IncludeContext, ct2),
            (symbolInfo, pagedResult) => new FindImplementationsResponse(symbolInfo, pagedResult),
            ct).ConfigureAwait(false);
    }

    private async Task<ImplementationItem> EnrichImplementationAsync(
        ImplementationStub stub, Solution solution, bool includeContext, CancellationToken ct)
    {
        string? contextLine = null;
        if (includeContext && stub.FilePath != null)
        {
            var implDoc = WorkspaceHelpers.GetDocumentByPath(solution, stub.FilePath);
            if (implDoc != null)
            {
                var text = await implDoc.GetTextAsync(ct).ConfigureAwait(false);
                contextLine = RoslynMapper.GetContextLine(text, stub.StartLine);
            }
        }
        return new ImplementationItem(stub.Symbol, stub.Location, contextLine);
    }

    // ── 3. find_callers ──

    public async Task<Result<FindCallersResponse>> FindCallersAsync(
        FindCallersRequest request, CancellationToken ct = default)
    {
        return await ExecuteSymbolSearchAsync<CallerStub, CallerItem, FindCallersResponse>(
            request.FilePath, request.Line, request.Column, request.Page, request.PageSize,
            "find_callers",
            async (symbol, solution, ct2) =>
            {
                var callerInfos = await SymbolFinder.FindCallersAsync(symbol, solution, ct2).ConfigureAwait(false);
                var stubs = new List<CallerStub>();
                foreach (var caller in callerInfos)
                {
                    ct2.ThrowIfCancellationRequested();
                    foreach (var location in caller.Locations)
                    {
                        if (stubs.Count >= PagingHelper.MaxResults) break;
                        if (!location.IsInSource) continue;
                        var codeLocation = RoslynMapper.ToCodeLocation(location);
                        if (codeLocation is null) continue;
                        var lineSpan = location.GetLineSpan();
                        stubs.Add(new CallerStub(RoslynMapper.ToSymbolInfo(caller.CallingSymbol), codeLocation, caller.IsDirect, lineSpan.Path, lineSpan.StartLinePosition.Line));
                    }
                    if (stubs.Count >= PagingHelper.MaxResults) break;
                }
                return stubs;
            },
            (stub, solution2, ct2) => EnrichCallerAsync(stub, solution2, request.IncludeContext, ct2),
            (symbolInfo, pagedResult) => new FindCallersResponse(symbolInfo, pagedResult),
            ct).ConfigureAwait(false);
    }

    private async Task<CallerItem> EnrichCallerAsync(
        CallerStub stub, Solution solution, bool includeContext, CancellationToken ct)
    {
        string? contextLine = null;
        if (includeContext && stub.FilePath != null)
        {
            var callerDoc = WorkspaceHelpers.GetDocumentByPath(solution, stub.FilePath);
            if (callerDoc != null)
            {
                var text = await callerDoc.GetTextAsync(ct).ConfigureAwait(false);
                contextLine = RoslynMapper.GetContextLine(text, stub.StartLine);
            }
        }
        return new CallerItem(stub.CallingSymbol, stub.Location, contextLine, stub.IsDirect);
    }

    // ── 4. find_callees ──

    public async Task<Result<FindCalleesResponse>> FindCalleesAsync(
        FindCalleesRequest request, CancellationToken ct = default)
    {
        var (doc, symbol, error) = await _helpers.ResolveAsync(request.FilePath, request.Line, request.Column, ct).ConfigureAwait(false);
        if (error != null) return Result<FindCalleesResponse>.Fail(error);

        if (symbol is not IMethodSymbol methodSymbol)
            return Result<FindCalleesResponse>.Fail("Symbol at position is not a method");

        var declRef = methodSymbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (declRef is null)
            return Result<FindCalleesResponse>.Fail("Method body not available in source");

        var solution = _workspace.CurrentSolution;
        if (solution is null)
        {
            _logger.Warning("{MethodName}: No solution loaded", nameof(FindCalleesAsync));
            return Result<FindCalleesResponse>.Fail("No solution loaded");
        }
        var declDoc = WorkspaceHelpers.GetDocumentByPath(solution, declRef.SyntaxTree.FilePath);
        if (declDoc is null)
            return Result<FindCalleesResponse>.Fail($"Document not found: {declRef.SyntaxTree.FilePath}");

        var model = await declDoc.GetSemanticModelAsync(ct).ConfigureAwait(false);
        if (model is null)
            return Result<FindCalleesResponse>.Fail("Could not get semantic model");

        var declNode = await declRef.GetSyntaxAsync(ct).ConfigureAwait(false);
        var walker = new CalleeCollector(model, ct);
        walker.Visit(declNode);

        // Pass 1: Build stubs (lightweight -- no text fetching)
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var stubs = new List<CalleeStub>();
        foreach (var (callee, callSite) in walker.Callees)
        {
            if (stubs.Count >= PagingHelper.MaxResults) break;
            ct.ThrowIfCancellationRequested();
            var span = callSite.GetLineSpan();
            var location = RoslynMapper.ToCodeLocation(span);
            if (location is null) continue;

            stubs.Add(new CalleeStub(
                RoslynMapper.ToSymbolInfo(callee), location, declDoc.Id, span.StartLinePosition.Line));
        }

        // Pass 2: Enrich requested page
        var snapshotSolution = solution;
        var includeCtx = request.IncludeContext;
        sw.Restart();

        var result = await PagingHelper.PageAndEnrichAsync(
            stubs, request.Page, request.PageSize,
            (CalleeStub stub, CancellationToken ct2) => EnrichCalleeAsync(stub, snapshotSolution, includeCtx, ct2),
            (i, ex) => _logger.Warning("find_callees enrichment failed at {Index}: {Error}", i, ex.Message),
            ct).ConfigureAwait(false);


        return new FindCalleesResponse(RoslynMapper.ToSymbolInfo(symbol!), result);
    }

    private async Task<CalleeItem> EnrichCalleeAsync(
        CalleeStub stub, Solution solution, bool includeContext, CancellationToken ct)
    {
        string? contextLine = null;
        if (includeContext && stub.DocumentId is not null)
        {
            var calleeDoc = solution.GetDocument(stub.DocumentId);
            if (calleeDoc != null)
            {
                var text = await calleeDoc.GetTextAsync(ct).ConfigureAwait(false);
                contextLine = RoslynMapper.GetContextLine(text, stub.StartLine);
            }
        }
        return new CalleeItem(stub.CalleeSymbol, stub.Location, contextLine);
    }

    // ── 5. find_definition ──

    public async Task<Result<FindDefinitionResponse>> FindDefinitionAsync(
        FindDefinitionRequest request, CancellationToken ct = default)
    {
        var (doc, symbol, error) = await _helpers.ResolveAsync(request.FilePath, request.Line, request.Column, ct).ConfigureAwait(false);
        if (error != null) return Result<FindDefinitionResponse>.Fail(error);

        var solution = _workspace.CurrentSolution;
        if (solution is null)
        {
            _logger.Warning("{MethodName}: No solution loaded", nameof(FindDefinitionAsync));
            return Result<FindDefinitionResponse>.Fail("No solution loaded");
        }
        var items = new List<DefinitionItem>();
        foreach (var location in symbol!.Locations)
        {
            ct.ThrowIfCancellationRequested();
            if (location.IsInSource)
            {
                var codeLocation = RoslynMapper.ToCodeLocation(location);
                if (codeLocation is null) continue;

                string? sourceText = null;
                var lineSpan = location.GetLineSpan();
                var defDoc = WorkspaceHelpers.GetDocumentByPath(solution, lineSpan.Path);
                if (defDoc != null)
                {
                    var text = await defDoc.GetTextAsync(ct).ConfigureAwait(false);
                    sourceText = RoslynMapper.GetContextLine(text, lineSpan.StartLinePosition.Line);
                }

                items.Add(new DefinitionItem(codeLocation, sourceText, IsMetadataDefinition: false));
            }
            else if (location.IsInMetadata)
            {
                items.Add(new DefinitionItem(
                    new CodeLocation(location.MetadataModule?.Name ?? "metadata", 0, 0, 0, 0),
                    null, IsMetadataDefinition: true));
            }
        }

        return new FindDefinitionResponse(
            RoslynMapper.ToSymbolInfo(symbol!),
            new PagedResult<DefinitionItem>(items, items.Count, 0, items.Count));
    }
}
