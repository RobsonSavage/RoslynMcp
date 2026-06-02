using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Core.Helpers;
using RoslynMcp.Shared;
using RoslynMcp.Shared.Contracts.Common;
using RoslynMcp.Shared.Contracts.Util;
using Serilog;

namespace RoslynMcp.Core.Services;

public class UtilService
{
    private readonly IWorkspaceProvider _workspace;
    private readonly IWorkspaceHelpers _helpers;
    private readonly ConfigManager _config;
    private readonly ILogger _logger;

    public UtilService(IWorkspaceProvider workspace, IWorkspaceHelpers helpers, ConfigManager config, ILogger logger)
    {
        _workspace = workspace;
        _helpers = helpers;
        _config = config;
        _logger = logger;
    }

    // ── 1. validate_text ──

    public async Task<Result<ValidateTextResponse>> ValidateTextAsync(
        ValidateTextRequest request, CancellationToken ct = default)
    {
        if (!_workspace.HasSolution)
        {
            _logger.Warning("validate_text: No solution loaded");
            return Result<ValidateTextResponse>.Fail("No solution loaded");
        }

        var doc = await _workspace.GetDocumentAsync(request.FilePath, ct: ct).ConfigureAwait(false);
        if (doc is null)
        {
            _logger.Warning("validate_text: Document not found: {FilePath}", request.FilePath);
            return Result<ValidateTextResponse>.Fail($"Document not found: {request.FilePath}");
        }

        // Fork the solution with replaced text — don't modify actual workspace
        var newText = SourceText.From(request.Text);
        var newDoc = doc.WithText(newText);
        var newProject = newDoc.Project;
        var compilation = await newProject.GetCompilationAsync(ct).ConfigureAwait(false);

        if (compilation is null)
            return Result<ValidateTextResponse>.Fail("Could not compile project");

        var tree = await newDoc.GetSyntaxTreeAsync(ct).ConfigureAwait(false);
        var diagnostics = compilation.GetDiagnostics(ct)
            .Where(d => d.Location.SourceTree == tree)
            .Where(d => d.Severity >= DiagnosticSeverity.Warning)
            .Select(MapDiagnostic)
            .ToList();

        var hasErrors = diagnostics.Any(d => d.Severity == "Error");


        return new ValidateTextResponse(request.FilePath, diagnostics, !hasErrors);
    }

    // ── 2. reload_file ──

    public async Task<Result<ReloadFileResponse>> ReloadFileAsync(
        ReloadFileRequest request, CancellationToken ct = default)
    {
        if (!_workspace.HasSolution)
            return Result<ReloadFileResponse>.Fail("No solution loaded");

        var success = await _workspace.TryReloadDocumentAsync(request.FilePath, ct).ConfigureAwait(false);

        if (success)
        {
            return new ReloadFileResponse(request.FilePath, true, "Document reloaded");
        }

        _logger.Warning("reload_file: {File} not found in workspace", request.FilePath);
        return new ReloadFileResponse(request.FilePath, false, "Document not found in workspace");
    }

    // ── 3. get_workspace_status ──

    public async Task<Result<WorkspaceStatusResponse>> GetWorkspaceStatusAsync(
        GetWorkspaceStatusRequest request, CancellationToken ct = default)
    {
        var solution = _workspace.CurrentSolution;
        if (solution is null)
            return Result<WorkspaceStatusResponse>.Fail("No solution loaded");
        int projectCount = 0;
        int documentCount = 0;
        int errorCount = 0;
        int warningCount = 0;

        foreach (var project in solution.Projects)
        {
            ct.ThrowIfCancellationRequested();
            projectCount++;
            documentCount += project.Documents.Count();

            var compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
            if (compilation is null) continue;

            foreach (var d in compilation.GetDiagnostics(ct))
            {
                if (d.Severity == DiagnosticSeverity.Error) errorCount++;
                else if (d.Severity == DiagnosticSeverity.Warning) warningCount++;
            }
        }


        return new WorkspaceStatusResponse(
            solution.FilePath ?? "",
            projectCount,
            documentCount,
            errorCount,
            warningCount,
            IsFullyLoaded: true);
    }

    // ── 4. get_errors ──

    public async Task<Result<ErrorsResponse>> GetErrorsAsync(
        GetErrorsRequest request, CancellationToken ct = default)
    {
        if (!_workspace.HasSolution)
            return Result<ErrorsResponse>.Fail("No solution loaded");

        var items = await CollectDiagnosticsAsync(DiagnosticSeverity.Error, request.FilePath, request.ProjectName, ct).ConfigureAwait(false);

        return new ErrorsResponse(PagingHelper.Page(items, request.Page, request.PageSize));
    }

    // ── 5. get_warnings ──

    public async Task<Result<WarningsResponse>> GetWarningsAsync(
        GetWarningsRequest request, CancellationToken ct = default)
    {
        if (!_workspace.HasSolution)
            return Result<WarningsResponse>.Fail("No solution loaded");

        var items = await CollectDiagnosticsAsync(DiagnosticSeverity.Warning, request.FilePath, request.ProjectName, ct).ConfigureAwait(false);

        return new WarningsResponse(PagingHelper.Page(items, request.Page, request.PageSize));
    }

    // ── 6. get_quick_fixes ──

    public async Task<Result<QuickFixesResponse>> GetQuickFixesAsync(
        GetQuickFixesRequest request, CancellationToken ct = default)
    {
        if (!_workspace.HasSolution)
        {
            _logger.Warning("get_quick_fixes: No solution loaded");
            return Result<QuickFixesResponse>.Fail("No solution loaded");
        }

        var doc = await _workspace.GetDocumentAsync(request.FilePath, ct: ct).ConfigureAwait(false);
        if (doc is null)
        {
            _logger.Warning("get_quick_fixes: Document not found: {FilePath}", request.FilePath);
            return Result<QuickFixesResponse>.Fail($"Document not found: {request.FilePath}");
        }

        var text = await doc.GetTextAsync(ct).ConfigureAwait(false);
        var posResult = WorkspaceHelpers.GetSafePosition(text, request.Line, request.Column);
        if (!posResult.IsSuccess)
            return Result<QuickFixesResponse>.Fail(posResult.Error!.Message, posResult.Error.ErrorCode);

        var position = posResult.Value;
        var span = TextSpan.FromBounds(position, position);

        // Get diagnostics at position
        var model = await doc.GetSemanticModelAsync(ct).ConfigureAwait(false);
        if (model is null)
            return Result<QuickFixesResponse>.Fail("Could not get semantic model");

        var diagnostics = model.GetDiagnostics(span, ct)
            .Where(d => d.Severity >= DiagnosticSeverity.Warning)
            .ToList();

        // Without MEF-composed CodeFixProviders, report available diagnostics as fix context.
        // Full CodeFix enumeration requires host services (Phase 4).
        var fixes = diagnostics.Select(d => new QuickFixItem(
            Title: $"Fix: {d.GetMessage()}",
            ProviderName: d.Id,
            EquivalenceKey: d.Id)).ToList();

        // Resolve symbol at position for context
        Shared.Contracts.Common.SymbolInfo? symbolInfo = null;
        var root = await doc.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        if (root != null)
        {
            var token = root.FindToken(position);
            var symbol = model.GetSymbolInfo(token.Parent!, ct).Symbol
                ?? model.GetDeclaredSymbol(token.Parent!, ct);
            if (symbol != null)
                symbolInfo = RoslynMapper.ToSymbolInfo(symbol);
        }


        return new QuickFixesResponse(symbolInfo, fixes);
    }

    // ── 7. suggest_refactorings ──

    public async Task<Result<SuggestRefactoringsResponse>> SuggestRefactoringsAsync(
        SuggestRefactoringsRequest request, CancellationToken ct = default)
    {
        if (!_workspace.HasSolution)
        {
            _logger.Warning("suggest_refactorings: No solution loaded");
            return Result<SuggestRefactoringsResponse>.Fail("No solution loaded");
        }

        var doc = await _workspace.GetDocumentAsync(request.FilePath, ct: ct).ConfigureAwait(false);
        if (doc is null)
        {
            _logger.Warning("suggest_refactorings: Document not found: {FilePath}", request.FilePath);
            return Result<SuggestRefactoringsResponse>.Fail($"Document not found: {request.FilePath}");
        }

        var text = await doc.GetTextAsync(ct).ConfigureAwait(false);
        var posResult = WorkspaceHelpers.GetSafePosition(text, request.Line, request.Column);
        if (!posResult.IsSuccess)
            return Result<SuggestRefactoringsResponse>.Fail(posResult.Error!.Message, posResult.Error.ErrorCode);

        var position = posResult.Value;

        var root = await doc.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var model = await doc.GetSemanticModelAsync(ct).ConfigureAwait(false);
        if (root is null || model is null)
            return Result<SuggestRefactoringsResponse>.Fail("Could not get syntax tree or semantic model");

        // Analyze the node at position to suggest common refactorings
        var token = root.FindToken(position);
        var node = token.Parent;
        var suggestions = new List<RefactoringSuggestion>();

        if (node != null)
        {
            var symbol = model.GetDeclaredSymbol(node, ct) ?? model.GetSymbolInfo(node, ct).Symbol;

            // Suggest based on node kind
            if (node is MethodDeclarationSyntax method)
            {
                if (method.Body != null && method.Body.Statements.Count > 10)
                    suggestions.Add(new RefactoringSuggestion("Extract Method", "RoslynMcp"));
                if (method.ParameterList.Parameters.Count > 3)
                    suggestions.Add(new RefactoringSuggestion("Introduce Parameter Object", "RoslynMcp"));
            }
            else if (node is LocalDeclarationStatementSyntax)
            {
                suggestions.Add(new RefactoringSuggestion("Inline Variable", "RoslynMcp"));
            }
            else if (node is IdentifierNameSyntax && symbol is IFieldSymbol field && !field.IsReadOnly)
            {
                suggestions.Add(new RefactoringSuggestion("Encapsulate Field", "RoslynMcp"));
            }
            else if (node.IsKind(SyntaxKind.ClassDeclaration))
            {
                suggestions.Add(new RefactoringSuggestion("Extract Interface", "RoslynMcp"));
            }
        }


        return new SuggestRefactoringsResponse(suggestions);
    }

    // ── 8. get_full_context ──

    public async Task<Result<FullContextResponse>> GetFullContextAsync(
        GetFullContextRequest request, CancellationToken ct = default)
    {
        var (doc, symbol, error) = await _helpers.ResolveAsync(request.FilePath, request.Line, request.Column, ct).ConfigureAwait(false);
        if (error != null) return Result<FullContextResponse>.Fail(error);

        var solution = _workspace.CurrentSolution;
        if (solution is null)
            return Result<FullContextResponse>.Fail("No solution loaded");

        var rootInfo = RoslynMapper.ToSymbolInfo(symbol!);
        var rootLocation = symbol!.Locations.FirstOrDefault(l => l.IsInSource);
        var rootCodeLocation = rootLocation != null
            ? RoslynMapper.ToCodeLocation(rootLocation)
            : new CodeLocation(request.FilePath, request.Line, request.Column, request.Line, request.Column);

        var visited = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        visited.Add(symbol!);

        const int MaxContextDepth = 10;
        var depth = Math.Min(request.Depth, MaxContextDepth);

        var context = await BuildContextAsync(symbol!, depth, visited, solution, ct).ConfigureAwait(false);


        return new FullContextResponse(rootInfo, context);
    }

    // ── 9. set_solution_path ──

    public async Task<Result<SetSolutionPathResponse>> SetSolutionPathAsync(
        SetSolutionPathRequest request, CancellationToken ct = default)
    {
        var previousPath = _workspace.CurrentSolution?.FilePath;

        var fullPath = Path.GetFullPath(request.SolutionPath);
        if (!File.Exists(fullPath))
            return Result<SetSolutionPathResponse>.Fail($"Solution file not found: {fullPath}");

        var ext = Path.GetExtension(fullPath);
        if (!ext.Equals(".sln", StringComparison.OrdinalIgnoreCase) &&
            !ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
            return Result<SetSolutionPathResponse>.Fail($"Not a solution file (expected .sln or .slnx): {fullPath}");

        _logger.Information("set_solution_path: switching from {Old} to {New}", previousPath, fullPath);

        await _workspace.ReloadSolutionAsync(fullPath, request.WarmUp, ct).ConfigureAwait(false);

        var solution = _workspace.CurrentSolution;
        var projectCount = solution?.ProjectIds.Count ?? 0;
        var documentCount = solution?.Projects.Sum(p => p.DocumentIds.Count) ?? 0;

        return new SetSolutionPathResponse(fullPath, projectCount, documentCount, previousPath);
    }

    // ── 10. config_get ──

    public Task<Result<ConfigGetResponse>> ConfigGetAsync(
        ConfigGetRequest request, CancellationToken ct = default)
    {
        var response = _config.Get(request.Key);

        return Task.FromResult(Result<ConfigGetResponse>.Ok(response));
    }

    // ── 11. config_set ──

    public Task<Result<ConfigSetResponse>> ConfigSetAsync(
        ConfigSetRequest request, CancellationToken ct = default)
    {
        var response = _config.Set(request.Key, request.Value, out var error);
        if (error != null)
            return Task.FromResult(Result<ConfigSetResponse>.Fail(error));

        return Task.FromResult(Result<ConfigSetResponse>.Ok(response));
    }

    // ── 12. config_list ──

    public Task<Result<ConfigListResponse>> ConfigListAsync(
        ConfigListRequest request, CancellationToken ct = default)
    {
        var response = _config.List();

        return Task.FromResult(Result<ConfigListResponse>.Ok(response));
    }

    // ── 13. tool_enabled ──

    public Task<Result<ToolEnabledResponse>> ToolEnabledAsync(
        ToolEnabledRequest request, CancellationToken ct = default)
    {
        var response = _config.ToolEnabled(request.ToolName, request.Enabled);

        return Task.FromResult(Result<ToolEnabledResponse>.Ok(response));
    }

    #region Private Helpers

    private async Task<List<DiagnosticItem>> CollectDiagnosticsAsync(
        DiagnosticSeverity severity, string? filePath, string? projectName, CancellationToken ct)
    {
        var solution = _workspace.CurrentSolution;
        if (solution is null)
            return new List<DiagnosticItem>();
        var items = new List<DiagnosticItem>();

        foreach (var project in solution.Projects)
        {
            if (projectName != null &&
                !string.Equals(project.Name, projectName, StringComparison.OrdinalIgnoreCase))
                continue;

            ct.ThrowIfCancellationRequested();
            var compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
            if (compilation is null) continue;

            foreach (var diag in compilation.GetDiagnostics(ct))
            {
                if (diag.Severity != severity) continue;
                if (items.Count >= PagingHelper.MaxResults) break;

                if (filePath != null && diag.Location.IsInSource)
                {
                    var diagPath = diag.Location.GetLineSpan().Path;
                    if (!string.Equals(diagPath, filePath, StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                items.Add(MapDiagnostic(diag));
            }
            if (items.Count >= PagingHelper.MaxResults) break;
        }

        if (items.Count >= PagingHelper.MaxResults)
            _logger.Warning("Diagnostics capped at {MaxResults}", PagingHelper.MaxResults);

        return items;
    }

    private static DiagnosticItem MapDiagnostic(Diagnostic diag)
    {
        CodeLocation location;
        if (diag.Location.IsInSource)
        {
            var span = diag.Location.GetLineSpan();
            location = new CodeLocation(
                span.Path,
                span.StartLinePosition.Line,
                span.StartLinePosition.Character,
                span.EndLinePosition.Line,
                span.EndLinePosition.Character);
        }
        else
        {
            location = new CodeLocation("", 0, 0, 0, 0);
        }

        return new DiagnosticItem(
            diag.Id,
            diag.GetMessage(),
            diag.Severity.ToString(),
            location,
            diag.Descriptor.Category);
    }

    private async Task<IReadOnlyList<ContextNode>> BuildContextAsync(
        ISymbol symbol, int depth, HashSet<ISymbol> visited,
        Solution solution, CancellationToken ct)
    {
        if (depth <= 0)
            return Array.Empty<ContextNode>();

        var nodes = new List<ContextNode>();

        // Callers
        if (symbol is IMethodSymbol || symbol is IPropertySymbol)
        {
            var callerInfos = await SymbolFinder.FindCallersAsync(symbol, solution, ct).ConfigureAwait(false);
            foreach (var caller in callerInfos)
            {
                ct.ThrowIfCancellationRequested();
                if (!visited.Add(caller.CallingSymbol)) continue;

                var location = caller.Locations.FirstOrDefault(l => l.IsInSource);
                var codeLocation = location != null
                    ? RoslynMapper.ToCodeLocation(location)
                    : null;

                if (codeLocation is null) continue;

                var children = await BuildContextAsync(caller.CallingSymbol, depth - 1, visited, solution, ct).ConfigureAwait(false);
                nodes.Add(new ContextNode(
                    RoslynMapper.ToSymbolInfo(caller.CallingSymbol),
                    codeLocation,
                    "Caller",
                    children.Count > 0 ? children : null));
            }
        }

        // Callees (for methods)
        if (symbol is IMethodSymbol methodSymbol)
        {
            var declRef = methodSymbol.DeclaringSyntaxReferences.FirstOrDefault();
            if (declRef != null)
            {
                var path = declRef.SyntaxTree.FilePath;
                var declDoc = WorkspaceHelpers.GetDocumentByPath(solution, path);
                if (declDoc != null)
                {
                    var model = await declDoc.GetSemanticModelAsync(ct).ConfigureAwait(false);
                    if (model != null)
                    {
                        var declNode = await declRef.GetSyntaxAsync(ct).ConfigureAwait(false);
                        var walker = new CalleeCollector(model, ct);
                        walker.Visit(declNode);

                        foreach (var (callee, callSite) in walker.Callees)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (!visited.Add(callee)) continue;

                            var span = callSite.GetLineSpan();
                            var codeLocation = RoslynMapper.ToCodeLocation(span);
                            if (codeLocation is null) continue;

                            var children = await BuildContextAsync(callee, depth - 1, visited, solution, ct).ConfigureAwait(false);
                            nodes.Add(new ContextNode(
                                RoslynMapper.ToSymbolInfo(callee),
                                codeLocation,
                                "Callee",
                                children.Count > 0 ? children : null));
                        }
                    }
                }
            }
        }

        return nodes;
    }

    private sealed class CalleeCollector : CSharpSyntaxWalker
    {
        private readonly SemanticModel _model;
        private readonly CancellationToken _ct;
        public List<(ISymbol Symbol, Location CallSite)> Callees { get; } = new();

        public CalleeCollector(SemanticModel model, CancellationToken ct)
        {
            _model = model;
            _ct = ct;
        }

        public override void VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            _ct.ThrowIfCancellationRequested();
            var info = _model.GetSymbolInfo(node, _ct);
            if (info.Symbol != null)
                Callees.Add((info.Symbol, node.GetLocation()));
            base.VisitInvocationExpression(node);
        }

        public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
        {
            _ct.ThrowIfCancellationRequested();
            var info = _model.GetSymbolInfo(node, _ct);
            if (info.Symbol != null)
                Callees.Add((info.Symbol, node.GetLocation()));
            base.VisitObjectCreationExpression(node);
        }
    }

    #endregion
}
