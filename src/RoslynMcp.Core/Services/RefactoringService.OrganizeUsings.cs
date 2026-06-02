using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Shared;
using RoslynMcp.Shared.Contracts.Refactor;

namespace RoslynMcp.Core.Services;

public partial class RefactoringService
{
    // ── 3. organize_usings ──

    public async Task<Result<OrganizeUsingsResponse>> OrganizeUsingsAsync(
        OrganizeUsingsRequest request, CancellationToken ct = default)
    {
        try
        {
            var solution = _workspace.CurrentSolution;
            if (solution is null)
                return Result<OrganizeUsingsResponse>.Fail("No solution loaded");

            var doc = await _workspace.GetDocumentAsync(request.FilePath, ct: ct).ConfigureAwait(false);
            if (doc is null)
                return Result<OrganizeUsingsResponse>.Fail($"Document not found: {request.FilePath}");

            var root = await doc.GetSyntaxRootAsync(ct).ConfigureAwait(false);
            if (root is null)
                return Result<OrganizeUsingsResponse>.Fail("Could not get syntax root");

            var compilationUnit = root as CompilationUnitSyntax;
            if (compilationUnit is null)
                return Result<OrganizeUsingsResponse>.Fail("Document root is not a compilation unit");

            var existingUsings = compilationUnit.Usings;
            if (existingUsings.Count == 0)
            {
                return new OrganizeUsingsResponse(request.FilePath, 0, 0, Array.Empty<string>());
            }

            var removedUsings = new List<string>();
            var remainingUsings = new List<UsingDirectiveSyntax>();

            if (request.RemoveUnused)
            {
                var model = await doc.GetSemanticModelAsync(ct).ConfigureAwait(false);
                if (model is null)
                    return Result<OrganizeUsingsResponse>.Fail("Could not get semantic model");

                var referencedNamespaces = CollectReferencedNamespaces(model, root, ct);

                foreach (var u in existingUsings)
                {
                    ct.ThrowIfCancellationRequested();

                    // Keep static usings and alias usings always
                    if (u.StaticKeyword.RawKind != 0 || u.Alias != null)
                    {
                        remainingUsings.Add(u);
                        continue;
                    }

                    var nameInfo = model.GetSymbolInfo(u.Name!, ct);
                    if (nameInfo.Symbol is INamespaceSymbol ns)
                    {
                        // Check if the namespace has any members referenced in this file
                        bool isUsed = referencedNamespaces.Contains(ns);
                        if (isUsed)
                        {
                            remainingUsings.Add(u);
                        }
                        else
                        {
                            removedUsings.Add(u.ToString().Trim());
                        }
                    }
                    else
                    {
                        // If we can't resolve the namespace, keep the using to be safe
                        remainingUsings.Add(u);
                    }
                }
            }
            else
            {
                remainingUsings.AddRange(existingUsings);
            }

            int sortedCount = 0;
            if (request.Sort && remainingUsings.Count > 1)
            {
                var sorted = remainingUsings
                    .OrderBy(u => u.StaticKeyword.RawKind != 0 ? 1 : 0) // non-static first
                    .ThenBy(u => u.Alias != null ? 1 : 0) // non-alias first
                    .ThenBy(u =>
                    {
                        var name = u.Name?.ToString() ?? "";
                        return name.StartsWith("System") ? 0 : 1;
                    })
                    .ThenBy(u => u.Name?.ToString() ?? "")
                    .ToList();

                // Count how many changed position
                for (int i = 0; i < remainingUsings.Count; i++)
                {
                    if (remainingUsings[i].Name?.ToString() != sorted[i].Name?.ToString())
                        sortedCount++;
                }
                remainingUsings = sorted;
            }


            var sortedUsingNames = remainingUsings
                .Select(u => u.Name?.ToString() ?? "")
                .Where(n => !string.IsNullOrEmpty(n))
                .ToList();

            return new OrganizeUsingsResponse(
                request.FilePath,
                removedUsings.Count,
                sortedCount,
                removedUsings,
                sortedUsingNames);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Error(ex, "organize_usings failed for {FilePath}", request.FilePath);
            return Result<OrganizeUsingsResponse>.Fail("Failed to organize usings: " + ex.Message);
        }
    }

    // ── OrganizeUsings helpers ──

    private static HashSet<INamespaceSymbol> CollectReferencedNamespaces(
        SemanticModel model, SyntaxNode root, CancellationToken ct)
    {
        var namespaces = new HashSet<INamespaceSymbol>(SymbolEqualityComparer.Default);
        foreach (var identifier in root.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            ct.ThrowIfCancellationRequested();
            if (identifier.FirstAncestorOrSelf<UsingDirectiveSyntax>() != null)
                continue;
            var info = model.GetSymbolInfo(identifier, ct);
            var ns = info.Symbol?.ContainingNamespace;
            if (ns != null)
                namespaces.Add(ns);
        }
        return namespaces;
    }
}
