using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Operations;
using RoslynMcp.Core.Helpers;
using RoslynMcp.Shared;
using RoslynMcp.Shared.Contracts.Analyze;
using RoslynMcp.Shared.Contracts.Common;
using Contracts = RoslynMcp.Shared.Contracts.Common;

namespace RoslynMcp.Core.Services;

public partial class AnalyzeService
{
    // ── Shared helper: iterate syntax trees matching project/file filters ──

    /// <summary>
    /// Resolves the solution, filters projects by name and syntax trees by file path,
    /// then invokes <paramref name="action"/> for each matching (SemanticModel, SyntaxTree) pair.
    /// </summary>
    private async Task<string?> ForEachSyntaxTreeAsync(
        Solution solution,
        string? projectName,
        string? filePath,
        Func<SemanticModel, SyntaxTree, CancellationToken, Task<bool>> action,
        CancellationToken ct)
    {
        foreach (var project in solution.Projects)
        {
            if (projectName != null &&
                !string.Equals(project.Name, projectName, StringComparison.OrdinalIgnoreCase))
                continue;

            ct.ThrowIfCancellationRequested();

            Compilation? compilation;
            try
            {
                compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.Warning("Skipping project {Project}: GetCompilationAsync failed: {Error}", project.Name, ex.Message);
                continue;
            }
            if (compilation is null) continue;

            foreach (var tree in compilation.SyntaxTrees)
            {
                ct.ThrowIfCancellationRequested();

                if (filePath != null &&
                    !string.Equals(tree.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                    continue;

                SemanticModel model;
                try
                {
                    model = compilation.GetSemanticModel(tree);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.Warning("Skipping syntax tree {File}: GetSemanticModel failed: {Error}", tree.FilePath, ex.Message);
                    continue;
                }

                // action returns false to signal "stop iterating" (e.g., result limit reached)
                bool shouldContinue = await action(model, tree, ct).ConfigureAwait(false);
                if (!shouldContinue) return null;
            }
        }

        return null;
    }

    // ── 10. find_unused_code ──

    public async Task<Result<FindUnusedCodeResponse>> FindUnusedCodeAsync(
        FindUnusedCodeRequest request, CancellationToken ct = default)
    {
        var solution = _workspace.CurrentSolution;
        if (solution is null)
        {
            _logger.Warning("find_unused_code: No solution loaded");
            return Result<FindUnusedCodeResponse>.Fail("No solution loaded");
        }
        var items = new List<UnusedCodeItem>();

        // NOTE: N+1 query pattern — SymbolFinder.FindReferencesAsync is called per-member inside
        // a triple-nested loop (project -> syntax tree -> type member). Roslyn's FindReferencesAsync
        // operates on a single symbol and scans the entire solution each time. Batching is not
        // straightforward because the API does not support multi-symbol queries, and building a
        // custom whole-solution reference index would duplicate significant Roslyn internals.
        // The 500-item cap and cancellation token provide practical bounds.
        await ForEachSyntaxTreeAsync(solution, request.ProjectName, request.FilePath, async (model, tree, innerCt) =>
        {
            if (items.Count >= 500) return false;

            SyntaxNode root;
            try
            {
                root = await tree.GetRootAsync(innerCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.Warning("find_unused_code: Skipping tree {File}: {Error}", tree.FilePath, ex.Message);
                return true;
            }

            foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (items.Count >= 500) return false;
                var typeSymbol = model.GetDeclaredSymbol(typeDecl, innerCt) as INamedTypeSymbol;
                if (typeSymbol is null) continue;

                foreach (var member in typeSymbol.GetMembers())
                {
                    innerCt.ThrowIfCancellationRequested();
                    if (member.IsImplicitlyDeclared) continue;
                    if (member.DeclaredAccessibility != Accessibility.Private
                        && member.DeclaredAccessibility != Accessibility.Internal) continue;

                    // Apply kind filter
                    if (request.KindFilter != null && !MatchesKindFilter(member, request.KindFilter))
                        continue;

                    // Skip constructors - they may be called via reflection/DI
                    if (member is IMethodSymbol ms && ms.MethodKind == MethodKind.Constructor)
                        continue;

                    try
                    {
                        var refs = await SymbolFinder.FindReferencesAsync(member, solution, innerCt).ConfigureAwait(false);
                        var refCount = refs.SelectMany(g => g.Locations).Count();

                        if (refCount == 0)
                        {
                            var memberLoc = member.Locations.FirstOrDefault(l => l.IsInSource);
                            if (memberLoc is null) continue;
                            var codeLocation = RoslynMapper.ToCodeLocation(memberLoc);
                            if (codeLocation is null) continue;

                            items.Add(new UnusedCodeItem(
                                RoslynMapper.ToSymbolInfo(member),
                                codeLocation,
                                $"{member.DeclaredAccessibility} {RoslynMapper.GetSymbolKind(member).ToLowerInvariant()} with no references"));

                            if (items.Count >= 500) return false;
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        // Allow partial results: log and skip this symbol
                        _logger.Warning("find_unused_code: Skipping symbol {Symbol}: FindReferencesAsync failed: {Error}",
                            member.ToDisplayString(), ex.Message);
                    }
                }
            }
            return true;
        }, ct).ConfigureAwait(false);

        return new FindUnusedCodeResponse(PagingHelper.Page(items, request.Page, request.PageSize));
    }

    // ── 11. find_async_issues ──

    public async Task<Result<FindAsyncIssuesResponse>> FindAsyncIssuesAsync(
        FindAsyncIssuesRequest request, CancellationToken ct = default)
    {
        var solution = _workspace.CurrentSolution;
        if (solution is null)
        {
            _logger.Warning("find_async_issues: No solution loaded");
            return Result<FindAsyncIssuesResponse>.Fail("No solution loaded");
        }
        var issues = new List<AsyncIssue>();

        await ForEachSyntaxTreeAsync(solution, request.ProjectName, request.FilePath, async (model, tree, innerCt) =>
        {
            SyntaxNode root;
            try
            {
                root = await tree.GetRootAsync(innerCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.Warning("find_async_issues: Skipping tree {File}: {Error}", tree.FilePath, ex.Message);
                return true;
            }

            var walker = new AsyncIssueWalker(model, innerCt);
            walker.Visit(root);
            issues.AddRange(walker.Issues);
            return issues.Count < PagingHelper.MaxResults;
        }, ct).ConfigureAwait(false);

        return new FindAsyncIssuesResponse(PagingHelper.Page(issues, request.Page, request.PageSize));
    }

    // ── 12. find_performance_issues ──

    public async Task<Result<FindPerformanceIssuesResponse>> FindPerformanceIssuesAsync(
        FindPerformanceIssuesRequest request, CancellationToken ct = default)
    {
        var solution = _workspace.CurrentSolution;
        if (solution is null)
        {
            _logger.Warning("find_performance_issues: No solution loaded");
            return Result<FindPerformanceIssuesResponse>.Fail("No solution loaded");
        }
        var issues = new List<PerformanceIssue>();

        await ForEachSyntaxTreeAsync(solution, request.ProjectName, request.FilePath, async (model, tree, innerCt) =>
        {
            SyntaxNode root;
            try
            {
                root = await tree.GetRootAsync(innerCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.Warning("find_performance_issues: Skipping tree {File}: {Error}", tree.FilePath, ex.Message);
                return true;
            }

            var walker = new PerformanceIssueWalker(model, innerCt);
            walker.Visit(root);
            issues.AddRange(walker.Issues);
            return issues.Count < PagingHelper.MaxResults;
        }, ct).ConfigureAwait(false);

        return new FindPerformanceIssuesResponse(PagingHelper.Page(issues, request.Page, request.PageSize));
    }

    // ── 13. analyze_operations ──

    public async Task<Result<AnalyzeOperationsResponse>> AnalyzeOperationsAsync(
        AnalyzeOperationsRequest request, CancellationToken ct = default)
    {
        var (doc, symbol, error) = await _helpers.ResolveAsync(request.FilePath, request.Line, request.Column, ct).ConfigureAwait(false);
        if (error != null) return Result<AnalyzeOperationsResponse>.Fail(error);

        var root = await doc!.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var model = await doc.GetSemanticModelAsync(ct).ConfigureAwait(false);
        var text = await doc.GetTextAsync(ct).ConfigureAwait(false);

        if (root is null || model is null)
            return Result<AnalyzeOperationsResponse>.Fail("Could not get syntax tree or semantic model");

        var posResult = WorkspaceHelpers.GetSafePosition(text, request.Line, request.Column);
        if (!posResult.IsSuccess)
            return Result<AnalyzeOperationsResponse>.Fail(posResult.Error!.Message, posResult.Error.ErrorCode);

        var position = posResult.Value;
        var node = root.FindToken(position).Parent;

        if (node is null)
            return Result<AnalyzeOperationsResponse>.Fail("No syntax node found at position");

        // Walk up to find the containing statement or member
        var targetNode = node.FirstAncestorOrSelf<StatementSyntax>()
            ?? node.FirstAncestorOrSelf<MemberDeclarationSyntax>()
            ?? (SyntaxNode?)node;

        if (targetNode is null)
            return Result<AnalyzeOperationsResponse>.Fail("Could not find containing statement");

        var operation = model.GetOperation(targetNode, ct);
        if (operation is null)
        {
            // Try the parent node
            if (targetNode.Parent != null)
                operation = model.GetOperation(targetNode.Parent, ct);
        }

        if (operation is null)
            return Result<AnalyzeOperationsResponse>.Fail("No operation found at position");

        var clampedDepth = Math.Min(Math.Max(0, request.MaxDepth), ValidationLimits.MaxOperationDepth);
        var rootOp = BuildOperationTree(operation, clampedDepth);

        Contracts.SymbolInfo? containingSymbol = symbol != null ? RoslynMapper.ToSymbolInfo(symbol) : null;


        return new AnalyzeOperationsResponse(containingSymbol, rootOp);
    }

    /// <summary>
    /// Builds an operation tree iteratively using an explicit stack to avoid stack overflow
    /// risk with deeply nested Roslyn operation trees.
    /// </summary>
    private static OperationNode BuildOperationTree(IOperation rootOperation, int maxDepth)
    {
        // Each work item holds the operation, its depth, and the children list of the parent
        // that this node's result should be added to. The root is special-cased.
        var stack = new Stack<(IOperation Op, int Depth, List<OperationNode> ParentChildren)>();

        // We build the tree in two passes: first collect all nodes in DFS order,
        // then construct OperationNode objects bottom-up. Since OperationNode is
        // immutable (record), we use a post-order approach with an explicit stack.
        // Simpler approach: build top-down with mutable lists, then the record
        // constructor captures the list snapshot.

        var rootChildren = new List<OperationNode>();
        var rootText = rootOperation.Syntax?.ToString();
        var rootDisplay = rootText?.Length > 200 ? rootText.Substring(0, 200) + "..." : rootText;

        if (maxDepth > 0)
        {
            // Push children in reverse order so left-to-right DFS ordering is maintained
            foreach (var child in rootOperation.ChildOperations.Reverse())
            {
                stack.Push((child, 1, rootChildren));
            }
        }

        while (stack.Count > 0)
        {
            var (op, depth, parentChildren) = stack.Pop();

            var text = op.Syntax?.ToString();
            var display = text?.Length > 200 ? text.Substring(0, 200) + "..." : text;

            var children = new List<OperationNode>();

            if (depth < maxDepth)
            {
                // Push children in reverse for correct ordering
                foreach (var child in op.ChildOperations.Reverse())
                {
                    stack.Push((child, depth + 1, children));
                }
            }

            // Note: children list will be populated by subsequent iterations for
            // nodes pushed onto the stack above. Since we process DFS and push
            // children that write into this node's children list, and those children
            // are processed before we move to the next sibling (stack is LIFO),
            // the list is fully populated when the parent reads it.
            parentChildren.Add(new OperationNode(
                OperationKind: op.Kind.ToString(),
                Type: op.Type?.ToDisplayString(),
                Syntax: display,
                Children: children));
        }

        return new OperationNode(
            OperationKind: rootOperation.Kind.ToString(),
            Type: rootOperation.Type?.ToDisplayString(),
            Syntax: rootDisplay,
            Children: rootChildren);
    }
}
