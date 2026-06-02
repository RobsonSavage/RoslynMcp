using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMcp.Core.Helpers;
using RoslynMcp.Shared;
using RoslynMcp.Shared.Contracts.Analyze;
using RoslynMcp.Shared.Contracts.Common;
using System.Xml.Linq;

namespace RoslynMcp.Core.Services;

public partial class AnalyzeService
{
    // ── 1. understand_type ──

    public async Task<Result<UnderstandTypeResponse>> UnderstandTypeAsync(
        UnderstandTypeRequest request, CancellationToken ct = default)
    {
        var (typeSymbol, typeError) = await _helpers.ResolveTypeAsync(
            request.TypeName, request.FilePath, request.Line, request.Column, ct).ConfigureAwait(false);
        if (typeError != null) return Result<UnderstandTypeResponse>.Fail(typeError);

        // BaseTypes chain
        var baseTypes = new List<string>();
        var currentBase = typeSymbol!.BaseType;
        while (currentBase != null && currentBase.SpecialType != SpecialType.System_Object)
        {
            baseTypes.Add(currentBase.ToDisplayString());
            currentBase = currentBase.BaseType;
        }

        // Interfaces
        var interfaces = typeSymbol.AllInterfaces
            .Select(i => i.ToDisplayString())
            .ToList();

        // Members
        var members = typeSymbol.GetMembers()
            .Where(m => !m.IsImplicitlyDeclared)
            .Select(RoslynMapper.ToMemberSummary)
            .ToList();

        // Usage count
        var solution = _workspace.CurrentSolution;
        if (solution is null)
            return Result<UnderstandTypeResponse>.Fail("No solution loaded");
        var refGroups = await SymbolFinder.FindReferencesAsync(typeSymbol, solution, ct).ConfigureAwait(false);
        int usageCount = refGroups.SelectMany(g => g.Locations).Count();

        // Location
        var loc = typeSymbol.Locations.FirstOrDefault(l => l.IsInSource);
        var codeLocation = loc != null ? RoslynMapper.ToCodeLocation(loc) : null;

        // XmlDoc summary
        string? xmlDocSummary = null;
        var rawXml = typeSymbol.GetDocumentationCommentXml(cancellationToken: ct);
        if (!string.IsNullOrWhiteSpace(rawXml))
        {
            xmlDocSummary = ParseXmlSummary(rawXml!);
        }


        return new UnderstandTypeResponse(
            RoslynMapper.ToSymbolInfo(typeSymbol),
            typeSymbol.TypeKind.ToString(),
            typeSymbol.DeclaredAccessibility.ToString(),
            baseTypes,
            interfaces,
            members,
            usageCount,
            codeLocation,
            xmlDocSummary);
    }

    // ── 3. get_type_info ──

    public async Task<Result<GetTypeInfoResponse>> GetTypeInfoAsync(
        GetTypeInfoRequest request, CancellationToken ct = default)
    {
        var (typeSymbol, typeError) = await _helpers.ResolveTypeAsync(
            request.TypeName, request.FilePath, request.Line, request.Column, ct).ConfigureAwait(false);
        if (typeError != null) return Result<GetTypeInfoResponse>.Fail(typeError);

        var members = typeSymbol!.GetMembers()
            .Where(m => !m.IsImplicitlyDeclared)
            .Select(RoslynMapper.ToMemberSummary)
            .ToList();

        var loc = typeSymbol.Locations.FirstOrDefault(l => l.IsInSource);
        var codeLocation = loc != null ? RoslynMapper.ToCodeLocation(loc) : null;


        return new GetTypeInfoResponse(
            RoslynMapper.ToSymbolInfo(typeSymbol),
            typeSymbol.TypeKind.ToString(),
            typeSymbol.DeclaredAccessibility.ToString(),
            typeSymbol.IsAbstract,
            typeSymbol.IsSealed,
            typeSymbol.IsStatic,
            typeSymbol.IsGenericType,
            typeSymbol.TypeParameters.Length,
            members,
            codeLocation);
    }

    // ── 4. get_class_hierarchy ──

    public async Task<Result<GetClassHierarchyResponse>> GetClassHierarchyAsync(
        GetClassHierarchyRequest request, CancellationToken ct = default)
    {
        var (typeSymbol, typeError) = await _helpers.ResolveTypeAsync(
            request.TypeName, request.FilePath, request.Line, request.Column, ct).ConfigureAwait(false);
        if (typeError != null) return Result<GetClassHierarchyResponse>.Fail(typeError);

        // Ancestors: walk BaseType chain upward
        var ancestors = new List<HierarchyNode>();
        var currentBase = typeSymbol!.BaseType;
        bool isDirect = true;
        while (currentBase != null && currentBase.SpecialType != SpecialType.System_Object)
        {
            ct.ThrowIfCancellationRequested();
            var baseLoc = currentBase.Locations.FirstOrDefault(l => l.IsInSource);
            var baseCodeLoc = baseLoc != null ? RoslynMapper.ToCodeLocation(baseLoc) : null;
            ancestors.Add(new HierarchyNode(RoslynMapper.ToSymbolInfo(currentBase), baseCodeLoc, isDirect));
            isDirect = false;
            currentBase = currentBase.BaseType;
        }

        // Descendants: use SymbolFinder
        var descendants = new List<HierarchyNode>();
        var solution = _workspace.CurrentSolution;
        if (solution is null)
            return Result<GetClassHierarchyResponse>.Fail("No solution loaded");

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

        int totalDescendants = 0;
        foreach (var d in derived)
        {
            ct.ThrowIfCancellationRequested();
            totalDescendants++;

            if (descendants.Count >= request.MaxDescendants)
                continue; // keep counting total but stop collecting

            var dLoc = d.Locations.FirstOrDefault(l => l.IsInSource);
            var dCodeLoc = dLoc != null ? RoslynMapper.ToCodeLocation(dLoc) : null;

            bool isDirectDescendant = SymbolEqualityComparer.Default.Equals(d.BaseType, typeSymbol)
                || d.Interfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, typeSymbol));

            descendants.Add(new HierarchyNode(RoslynMapper.ToSymbolInfo(d), dCodeLoc, isDirectDescendant));
        }


        return new GetClassHierarchyResponse(
            RoslynMapper.ToSymbolInfo(typeSymbol),
            ancestors,
            descendants,
            totalDescendants);
    }

    // ── 5. get_type_members ──

    public async Task<Result<GetTypeMembersResponse>> GetTypeMembersAsync(
        GetTypeMembersRequest request, CancellationToken ct = default)
    {
        var (typeSymbol, typeError) = await _helpers.ResolveTypeAsync(
            request.TypeName, request.FilePath, request.Line, request.Column, ct).ConfigureAwait(false);
        if (typeError != null) return Result<GetTypeMembersResponse>.Fail(typeError);

        var allMembers = new List<ISymbol>();

        if (request.IncludeInherited)
        {
            // Walk base type chain collecting members
            INamedTypeSymbol? current = typeSymbol;
            while (current != null && current.SpecialType != SpecialType.System_Object)
            {
                ct.ThrowIfCancellationRequested();
                allMembers.AddRange(current.GetMembers().Where(m => !m.IsImplicitlyDeclared));
                current = current.BaseType;
            }
        }
        else
        {
            allMembers.AddRange(typeSymbol!.GetMembers().Where(m => !m.IsImplicitlyDeclared));
        }

        // Apply kind filter
        if (request.KindFilter != null)
        {
            allMembers = allMembers.Where(m => MatchesKindFilter(m, request.KindFilter)).ToList();
        }

        var memberSummaries = allMembers.Select(RoslynMapper.ToMemberSummary).ToList();


        return new GetTypeMembersResponse(
            RoslynMapper.ToSymbolInfo(typeSymbol),
            PagingHelper.Page(memberSummaries, request.Page, request.PageSize));
    }

    private string? ParseXmlSummary(string rawXml)
    {
        try
        {
            var xmlDoc = XDocument.Parse(rawXml);
            var member = xmlDoc.Root;
            if (member is null) return null;

            var elements = member.Name.LocalName == "member"
                ? member.Elements()
                : xmlDoc.Root!.Elements();

            foreach (var el in elements)
            {
                if (el.Name.LocalName == "summary")
                {
                    var text = string.Concat(el.Nodes().Select(n => n is XElement e
                        ? e.Name.LocalName == "see"
                            ? e.Attribute("cref")?.Value?.Replace("T:", "").Replace("M:", "").Replace("P:", "") ?? ""
                            : e.Value
                        : n.ToString()));
                    return string.Join(" ", text.Split(new[] { ' ', '\r', '\n', '\t' },
                        StringSplitOptions.RemoveEmptyEntries)).Trim();
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
        }
        return null;
    }

    private static bool MatchesKindFilter(ISymbol member, string kindFilter)
    {
        return string.Equals(RoslynMapper.GetSymbolKind(member), kindFilter, StringComparison.OrdinalIgnoreCase);
    }
}
