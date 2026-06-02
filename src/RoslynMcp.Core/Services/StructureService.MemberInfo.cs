using Microsoft.CodeAnalysis;
using RoslynMcp.Core.Helpers;
using RoslynMcp.Shared;
using RoslynMcp.Shared.Contracts.Structure;

namespace RoslynMcp.Core.Services;

public partial class StructureService
{
    // ── 6. get_constructor_parameters ──

    public async Task<Result<ConstructorParametersResponse>> GetConstructorParametersAsync(
        GetConstructorParametersRequest request, CancellationToken ct = default)
    {
        var (type, error) = await _helpers.ResolveTypeAsync(
            request.TypeName, request.FilePath, request.Line, request.Column, ct).ConfigureAwait(false);
        if (error != null) return Result<ConstructorParametersResponse>.Fail(error);

        var constructors = new List<ConstructorSummary>();
        foreach (var ctor in type!.Constructors)
        {
            if (ctor.IsImplicitlyDeclared && ctor.Parameters.Length == 0)
                continue; // skip implicit default ctor

            ct.ThrowIfCancellationRequested();
            var parameters = ctor.Parameters.Select(RoslynMapper.ToParameterInfo).ToList();
            var location = ctor.Locations.FirstOrDefault(l => l.IsInSource);
            var codeLocation = location != null ? RoslynMapper.ToCodeLocation(location) : null;

            constructors.Add(new ConstructorSummary(
                ctor.DeclaredAccessibility.ToString(), parameters, codeLocation));
        }

        return new ConstructorParametersResponse(RoslynMapper.ToSymbolInfo(type!), constructors);
    }

    // ── 7. get_overloads ──

    public async Task<Result<OverloadsResponse>> GetOverloadsAsync(
        GetOverloadsRequest request, CancellationToken ct = default)
    {
        var (doc, symbol, error) = await _helpers.ResolveAsync(request.FilePath, request.Line, request.Column, ct).ConfigureAwait(false);
        if (error != null) return Result<OverloadsResponse>.Fail(error);

        // Capture solution from the resolved document to avoid TOCTOU race
        // (if _workspace.CurrentSolution is read separately, the solution may have
        // changed since ResolveAsync accessed it, and the symbol may not exist).
        var solution = doc!.Project.Solution;

        if (symbol is not IMethodSymbol methodSymbol)
            return Result<OverloadsResponse>.Fail("Symbol at position is not a method");

        var containingType = methodSymbol.ContainingType;
        if (containingType is null)
            return Result<OverloadsResponse>.Fail("Method has no containing type");

        var overloads = containingType.GetMembers(methodSymbol.Name)
            .OfType<IMethodSymbol>()
            .ToList();

        var items = new List<OverloadItem>();
        foreach (var overload in overloads)
        {
            ct.ThrowIfCancellationRequested();
            var parameters = overload.Parameters.Select(RoslynMapper.ToParameterInfo).ToList();
            var location = overload.Locations.FirstOrDefault(l => l.IsInSource);
            var codeLocation = location != null ? RoslynMapper.ToCodeLocation(location) : null;

            string? contextLine = null;
            if (request.IncludeContext && location != null)
            {
                var ovrDoc = _helpers.GetDocumentByPath(solution, location.GetLineSpan().Path);
                if (ovrDoc != null)
                {
                    var text = await ovrDoc.GetTextAsync(ct).ConfigureAwait(false);
                    contextLine = RoslynMapper.GetContextLine(text, location.GetLineSpan().StartLinePosition.Line);
                }
            }

            items.Add(new OverloadItem(
                Signature: overload.ToDisplayString(),
                Parameters: parameters,
                ReturnType: overload.ReturnType.ToDisplayString(),
                Location: codeLocation,
                ContextLine: contextLine));
        }

        return new OverloadsResponse(RoslynMapper.ToSymbolInfo(methodSymbol), items);
    }

    // ── 8. get_accessibility ──

    public async Task<Result<AccessibilityResponse>> GetAccessibilityAsync(
        GetAccessibilityRequest request, CancellationToken ct = default)
    {
        var (doc, symbol, error) = await _helpers.ResolveAsync(request.FilePath, request.Line, request.Column, ct).ConfigureAwait(false);
        if (error != null) return Result<AccessibilityResponse>.Fail(error);

        var declared = symbol!.DeclaredAccessibility;
        var effective = ComputeEffectiveAccessibility(symbol!);


        return new AccessibilityResponse(
            RoslynMapper.ToSymbolInfo(symbol!),
            declared.ToString(),
            effective.ToString());
    }

    #region Private Helpers

    private static Accessibility ComputeEffectiveAccessibility(ISymbol symbol)
    {
        var effective = symbol.DeclaredAccessibility;
        var current = symbol.ContainingType;

        while (current != null)
        {
            effective = CombineAccessibility(effective, current.DeclaredAccessibility);
            current = current.ContainingType;
        }

        return effective;
    }

    private static Accessibility CombineAccessibility(Accessibility member, Accessibility container)
    {
        // Public doesn't restrict
        if (container == Accessibility.Public) return member;
        if (member == Accessibility.Public) return container;

        // Private absorbs all
        if (container == Accessibility.Private || member == Accessibility.Private)
            return Accessibility.Private;

        // Same → same
        if (member == container) return member;

        // ProtectedOrInternal is the least restrictive non-public.
        // Intersecting it with anything yields the other.
        if (member == Accessibility.ProtectedOrInternal) return container;
        if (container == Accessibility.ProtectedOrInternal) return member;

        // Remaining combinations of Protected, Internal, ProtectedAndInternal
        // all collapse to ProtectedAndInternal (the intersection of Protected ∩ Internal)
        return Accessibility.ProtectedAndInternal;
    }

    #endregion
}
