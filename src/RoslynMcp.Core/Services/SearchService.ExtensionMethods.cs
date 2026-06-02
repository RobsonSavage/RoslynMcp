using Microsoft.CodeAnalysis;
using RoslynMcp.Core.Helpers;
using RoslynMcp.Shared;
using RoslynMcp.Shared.Contracts.Search;

namespace RoslynMcp.Core.Services;

public partial class SearchService
{
    // ── 10. find_extension_methods ──

    public async Task<Result<FindExtensionMethodsResponse>> FindExtensionMethodsAsync(
        FindExtensionMethodsRequest request, CancellationToken ct = default)
    {
        var (targetType, typeError) = await _helpers.ResolveTypeAsync(
            request.TypeName, request.FilePath, request.Line, request.Column, ct).ConfigureAwait(false);
        if (typeError != null) return Result<FindExtensionMethodsResponse>.Fail(typeError);

        var solution = _workspace.CurrentSolution;
        if (solution is null)
        {
            _logger.Warning("{MethodName}: No solution loaded", nameof(FindExtensionMethodsAsync));
            return Result<FindExtensionMethodsResponse>.Fail("No solution loaded");
        }

        // Pass 1: Cheap pre-filter + stub collection
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var compatibleTypeNames = BuildCompatibleTypeNames(targetType!);
        var stubs = new List<ExtensionMethodStub>();

        foreach (var project in solution.Projects)
        {
            ct.ThrowIfCancellationRequested();
            var compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
            if (compilation is null) continue;

            foreach (var methodSym in FindExtensionMethodSymbols(compilation.GlobalNamespace, ct))
            {
                if (stubs.Count >= PagingHelper.MaxResults) break;
                ct.ThrowIfCancellationRequested();

                if (!IsExtensionMethodCandidate(methodSym, compatibleTypeNames)) continue;

                var location = methodSym.Locations.FirstOrDefault(l => l.IsInSource);
                if (location is null) continue;
                var codeLocation = RoslynMapper.ToCodeLocation(location);
                if (codeLocation is null) continue;

                var doc = solution.GetDocument(location.SourceTree);
                if (doc is null) continue;

                stubs.Add(new ExtensionMethodStub(
                    codeLocation, doc.Id,
                    GetMetadataName(methodSym.ContainingType),
                    methodSym.Name,
                    methodSym.Arity,
                    methodSym.Parameters.Length));
            }
            if (stubs.Count >= PagingHelper.MaxResults) break;
        }

        // Pass 2: Enrich requested page
        var snapshotSolution = solution;
        var capturedTargetType = targetType!;
        sw.Restart();

        var result = await PagingHelper.PageAndEnrichAsync(
            stubs, request.Page, request.PageSize,
            (ExtensionMethodStub stub, CancellationToken ct2) =>
                EnrichExtensionMethodAsync(stub, capturedTargetType, snapshotSolution, ct2),
            (i, ex) => _logger.Warning(ex, "find_extension_methods enrichment failed at {Index}", i),
            ct).ConfigureAwait(false);


        return new FindExtensionMethodsResponse(capturedTargetType.ToDisplayString(), result);
    }

    private async Task<ExtensionMethodItem> EnrichExtensionMethodAsync(
        ExtensionMethodStub stub, ITypeSymbol targetType, Solution solution, CancellationToken ct)
    {
        var doc = solution.GetDocument(stub.DocumentId)
            ?? throw new InvalidOperationException(
                $"Document not found for stub: {stub.ContainingTypeMetadataName}.{stub.MethodName}");

        var compilation = await doc.Project.GetCompilationAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Compilation failed for project: {doc.Project.Name}");

        ct.ThrowIfCancellationRequested();

        var containingType = compilation.GetTypeByMetadataName(stub.ContainingTypeMetadataName)
            ?? throw new InvalidOperationException(
                $"Type not found: {stub.ContainingTypeMetadataName}");

        // Disambiguate overloads by name + arity + parameter count
        var method = containingType.GetMembers(stub.MethodName)
            .OfType<IMethodSymbol>()
            .FirstOrDefault(m => m.IsExtensionMethod
                && m.Arity == stub.Arity
                && m.Parameters.Length == stub.ParameterCount)
            ?? throw new InvalidOperationException(
                $"Method not found: {stub.ContainingTypeMetadataName}.{stub.MethodName} " +
                $"(arity={stub.Arity}, params={stub.ParameterCount})");

        ct.ThrowIfCancellationRequested();

        // ReduceExtensionMethod — final precise compatibility check
        // Null = Pass 1 false positive (cheap filter matched but type inference fails)
        var reduced = method.ReduceExtensionMethod(targetType)
            ?? throw new InvalidOperationException(
                $"ReduceExtensionMethod returned null for {stub.MethodName} on {targetType.Name}");

        // Use REDUCED symbol (has `this` param removed, generic types bound)
        return new ExtensionMethodItem(
            RoslynMapper.ToSymbolInfo(reduced), stub.Location,
            method.Parameters[0].Type.ToDisplayString());
    }

    private static HashSet<string> BuildCompatibleTypeNames(ITypeSymbol targetType)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var current = targetType;
        while (current != null)
        {
            names.Add(current.OriginalDefinition.ToDisplayString());
            current = current.BaseType;
        }
        foreach (var iface in targetType.AllInterfaces)
            names.Add(iface.OriginalDefinition.ToDisplayString());
        return names;
    }

    private static bool IsExtensionMethodCandidate(IMethodSymbol method, HashSet<string> compatibleTypeNames)
    {
        var firstParamType = method.Parameters[0].Type;
        if (firstParamType.TypeKind == TypeKind.TypeParameter)
            return true; // Generic constraint filtering too complex for Pass 1
        return compatibleTypeNames.Contains(firstParamType.OriginalDefinition.ToDisplayString());
    }

    private static IEnumerable<IMethodSymbol> FindExtensionMethodSymbols(INamespaceSymbol ns, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        foreach (var member in ns.GetMembers())
        {
            if (member is INamespaceSymbol childNs)
            {
                foreach (var m in FindExtensionMethodSymbols(childNs, ct))
                    yield return m;
            }
            else if (member is INamedTypeSymbol type && type.IsStatic && type.MightContainExtensionMethods)
            {
                foreach (var m in type.GetMembers().OfType<IMethodSymbol>())
                {
                    if (m.IsExtensionMethod)
                        yield return m;
                }
            }
        }
    }
}
