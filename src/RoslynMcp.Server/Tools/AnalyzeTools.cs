using RoslynMcp.Shared.Contracts.Analyze;

namespace RoslynMcp.Server.Tools;

[McpServerToolType]
public class AnalyzeTools
{
    private readonly AnalyzeService _service;
    private readonly IToolResultMapper _mapper;
    private readonly ILogger _logger;
    private readonly IToolMetricsService _metrics;

    public AnalyzeTools(AnalyzeService service, IToolResultMapper mapper, ILogger logger, IToolMetricsService metrics)
    {
        _service = service;
        _mapper = mapper;
        _logger = logger;
        _metrics = metrics;
    }

    // ── 1. understand_type ──

    [McpServerTool(Name = "understand_type"), Description("Get comprehensive understanding of a type including members, hierarchy, and usage")]
    public async Task<CallToolResult> UnderstandType(
        [Description("Fully-qualified type name")] string? typeName = null,
        [Description("Path to C# file")] string? filePath = null,
        [Description("0-based line number")] int? line = null,
        [Description("0-based column number")] int? column = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(typeName) && string.IsNullOrWhiteSpace(filePath))
                return _mapper.Error("At least typeName or filePath must be provided");
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                var pathError = ToolValidation.ValidateFilePath(filePath);
                if (pathError != null) return _mapper.Error(pathError);
            }
            var request = new UnderstandTypeRequest(typeName, filePath, line, column);
            var result = await _service.UnderstandTypeAsync(request, ct);
            if (!result.IsSuccess)
            {
                isError = true;
                return _mapper.Error(result.Error?.Message ?? "Unknown error");
            }
            return _mapper.Success(result.Value);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            isError = true;
            return _mapper.Exception(ex, _logger);
        }
        finally
        {
            _metrics.Record("understand_type", sw.Elapsed, isError);
        }
    }

    // ── 2. understand_method ──

    [McpServerTool(Name = "understand_method"), Description("Analyze a method including signature, metrics, callers, and callees")]
    public async Task<CallToolResult> UnderstandMethod(
        [Description("Path to C# file")] string filePath,
        [Description("0-based line number")] int line,
        [Description("0-based column number")] int column,
        [Description("Depth of caller chain to traverse")] int callerDepth = 1,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath)) return _mapper.Error("filePath is required");
            var understandMethodPathError = ToolValidation.ValidateFilePath(filePath);
            if (understandMethodPathError != null) return _mapper.Error(understandMethodPathError);
            if (line < 0) return _mapper.Error("line must be >= 0");
            if (column < 0) return _mapper.Error("column must be >= 0");
            if (callerDepth < 0 || callerDepth > ValidationLimits.MaxRecursionDepth) return _mapper.Error("callerDepth must be 0-10");
            var request = new UnderstandMethodRequest(filePath, line, column, callerDepth);
            var result = await _service.UnderstandMethodAsync(request, ct);
            if (!result.IsSuccess)
            {
                isError = true;
                return _mapper.Error(result.Error?.Message ?? "Unknown error");
            }
            return _mapper.Success(result.Value);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            isError = true;
            return _mapper.Exception(ex, _logger);
        }
        finally
        {
            _metrics.Record("understand_method", sw.Elapsed, isError);
        }
    }

    // ── 3. get_type_info ──

    [McpServerTool(Name = "get_type_info"), Description("Get basic type information including kind, accessibility, and members")]
    public async Task<CallToolResult> GetTypeInfo(
        [Description("Fully-qualified type name")] string? typeName = null,
        [Description("Path to C# file")] string? filePath = null,
        [Description("0-based line number")] int? line = null,
        [Description("0-based column number")] int? column = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(typeName) && string.IsNullOrWhiteSpace(filePath))
                return _mapper.Error("At least typeName or filePath must be provided");
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                var pathError = ToolValidation.ValidateFilePath(filePath);
                if (pathError != null) return _mapper.Error(pathError);
            }
            var request = new GetTypeInfoRequest(typeName, filePath, line, column);
            var result = await _service.GetTypeInfoAsync(request, ct);
            if (!result.IsSuccess)
            {
                isError = true;
                return _mapper.Error(result.Error?.Message ?? "Unknown error");
            }
            return _mapper.Success(result.Value);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            isError = true;
            return _mapper.Exception(ex, _logger);
        }
        finally
        {
            _metrics.Record("get_type_info", sw.Elapsed, isError);
        }
    }

    // ── 4. get_class_hierarchy ──

    [McpServerTool(Name = "get_class_hierarchy"), Description("Get inheritance hierarchy including ancestors and descendants")]
    public async Task<CallToolResult> GetClassHierarchy(
        [Description("Fully-qualified type name")] string? typeName = null,
        [Description("Path to C# file")] string? filePath = null,
        [Description("0-based line number")] int? line = null,
        [Description("0-based column number")] int? column = null,
        [Description("Maximum descendants to return (default 50, max 500)")] int maxDescendants = 50,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(typeName) && string.IsNullOrWhiteSpace(filePath))
                return _mapper.Error("At least typeName or filePath must be provided");
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                var pathError = ToolValidation.ValidateFilePath(filePath);
                if (pathError != null) return _mapper.Error(pathError);
            }
            maxDescendants = Math.Clamp(maxDescendants, 1, ValidationLimits.MaxDescendants);
            var request = new GetClassHierarchyRequest(typeName, filePath, line, column, maxDescendants);
            var result = await _service.GetClassHierarchyAsync(request, ct);
            if (!result.IsSuccess)
            {
                isError = true;
                return _mapper.Error(result.Error?.Message ?? "Unknown error");
            }
            return _mapper.Success(result.Value);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            isError = true;
            return _mapper.Exception(ex, _logger);
        }
        finally
        {
            _metrics.Record("get_class_hierarchy", sw.Elapsed, isError);
        }
    }

    // ── 5. get_type_members ──

    [McpServerTool(Name = "get_type_members"), Description("List members of a type with optional filtering and pagination")]
    public async Task<CallToolResult> GetTypeMembers(
        [Description("Fully-qualified type name")] string? typeName = null,
        [Description("Path to C# file")] string? filePath = null,
        [Description("0-based line number")] int? line = null,
        [Description("0-based column number")] int? column = null,
        [Description("Filter by member kind (Method, Property, Field, Event)")] string? kindFilter = null,
        [Description("Include inherited members")] bool includeInherited = false,
        [Description("Number of results per page")] int pageSize = 5,
        [Description("0-based page number")] int page = 0,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (pageSize < 1 || pageSize > ValidationLimits.MaxPageSize) return _mapper.Error("pageSize must be 1-200");
            if (page < 0) return _mapper.Error("page must be >= 0");
            var request = new GetTypeMembersRequest(typeName, filePath, line, column, kindFilter, includeInherited, pageSize, page);
            var result = await _service.GetTypeMembersAsync(request, ct);
            if (!result.IsSuccess)
            {
                isError = true;
                return _mapper.Error(result.Error?.Message ?? "Unknown error");
            }
            return _mapper.Success(result.Value);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            isError = true;
            return _mapper.Exception(ex, _logger);
        }
        finally
        {
            _metrics.Record("get_type_members", sw.Elapsed, isError);
        }
    }

    // ── 6. get_method_body ──

    [McpServerTool(Name = "get_method_body"), Description("Retrieve the source code body of a method")]
    public async Task<CallToolResult> GetMethodBody(
        [Description("Path to C# file")] string filePath,
        [Description("0-based line number")] int line,
        [Description("0-based column number")] int column,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath)) return _mapper.Error("filePath is required");
            var getMethodBodyPathError = ToolValidation.ValidateFilePath(filePath);
            if (getMethodBodyPathError != null) return _mapper.Error(getMethodBodyPathError);
            if (line < 0) return _mapper.Error("line must be >= 0");
            if (column < 0) return _mapper.Error("column must be >= 0");
            var request = new GetMethodBodyRequest(filePath, line, column);
            var result = await _service.GetMethodBodyAsync(request, ct);
            if (!result.IsSuccess)
            {
                isError = true;
                return _mapper.Error(result.Error?.Message ?? "Unknown error");
            }
            return _mapper.Success(result.Value);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            isError = true;
            return _mapper.Exception(ex, _logger);
        }
        finally
        {
            _metrics.Record("get_method_body", sw.Elapsed, isError);
        }
    }

    // ── 7. get_code_metrics ──

    [McpServerTool(Name = "get_code_metrics"), Description("Calculate code metrics like cyclomatic complexity and maintainability index")]
    public async Task<CallToolResult> GetCodeMetrics(
        [Description("Fully-qualified type name")] string? typeName = null,
        [Description("Path to C# file")] string? filePath = null,
        [Description("0-based line number")] int? line = null,
        [Description("0-based column number")] int? column = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(typeName) && string.IsNullOrWhiteSpace(filePath))
                return _mapper.Error("At least typeName or filePath must be provided");
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                var pathError = ToolValidation.ValidateFilePath(filePath);
                if (pathError != null) return _mapper.Error(pathError);
            }
            var request = new GetCodeMetricsRequest(typeName, filePath, line, column);
            var result = await _service.GetCodeMetricsAsync(request, ct);
            if (!result.IsSuccess)
            {
                isError = true;
                return _mapper.Error(result.Error?.Message ?? "Unknown error");
            }
            return _mapper.Success(result.Value);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            isError = true;
            return _mapper.Exception(ex, _logger);
        }
        finally
        {
            _metrics.Record("get_code_metrics", sw.Elapsed, isError);
        }
    }

    // ── 8. analyze_data_flow ──

    [McpServerTool(Name = "analyze_data_flow"), Description("Analyze data flow within a code range showing variable reads, writes, and captures")]
    public async Task<CallToolResult> AnalyzeDataFlow(
        [Description("Path to C# file")] string filePath,
        [Description("0-based start line")] int startLine,
        [Description("0-based start column")] int startColumn,
        [Description("0-based end line")] int endLine,
        [Description("0-based end column")] int endColumn,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath)) return _mapper.Error("filePath is required");
            var analyzeDataFlowPathError = ToolValidation.ValidateFilePath(filePath);
            if (analyzeDataFlowPathError != null) return _mapper.Error(analyzeDataFlowPathError);
            if (startLine < 0) return _mapper.Error("startLine must be >= 0");
            if (startColumn < 0) return _mapper.Error("startColumn must be >= 0");
            if (endLine < 0) return _mapper.Error("endLine must be >= 0");
            if (endColumn < 0) return _mapper.Error("endColumn must be >= 0");
            if (endLine < startLine || (endLine == startLine && endColumn < startColumn)) return _mapper.Error("End position must be after start position");
            var request = new AnalyzeDataFlowRequest(filePath, startLine, startColumn, endLine, endColumn);
            var result = await _service.AnalyzeDataFlowAsync(request, ct);
            if (!result.IsSuccess)
            {
                isError = true;
                return _mapper.Error(result.Error?.Message ?? "Unknown error");
            }
            return _mapper.Success(result.Value);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            isError = true;
            return _mapper.Exception(ex, _logger);
        }
        finally
        {
            _metrics.Record("analyze_data_flow", sw.Elapsed, isError);
        }
    }

    // ── 9. impact_analysis ──

    [McpServerTool(Name = "impact_analysis"), Description("Analyze transitive impact of changes to a symbol")]
    public async Task<CallToolResult> ImpactAnalysis(
        [Description("Path to C# file")] string filePath,
        [Description("0-based line number")] int line,
        [Description("0-based column number")] int column,
        [Description("Depth of transitive impact to traverse")] int depth = 2,
        [Description("Number of results per page")] int pageSize = 5,
        [Description("0-based page number")] int page = 0,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath)) return _mapper.Error("filePath is required");
            var impactAnalysisPathError = ToolValidation.ValidateFilePath(filePath);
            if (impactAnalysisPathError != null) return _mapper.Error(impactAnalysisPathError);
            if (line < 0) return _mapper.Error("line must be >= 0");
            if (column < 0) return _mapper.Error("column must be >= 0");
            if (depth < 0 || depth > ValidationLimits.MaxImpactDepth) return _mapper.Error($"depth must be 0-{ValidationLimits.MaxImpactDepth}");
            if (pageSize < 1 || pageSize > ValidationLimits.MaxPageSize) return _mapper.Error("pageSize must be 1-200");
            if (page < 0) return _mapper.Error("page must be >= 0");
            var request = new ImpactAnalysisRequest(filePath, line, column, depth, pageSize, page);
            var result = await _service.ImpactAnalysisAsync(request, ct);
            if (!result.IsSuccess)
            {
                isError = true;
                return _mapper.Error(result.Error?.Message ?? "Unknown error");
            }
            return _mapper.Success(result.Value);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            isError = true;
            return _mapper.Exception(ex, _logger);
        }
        finally
        {
            _metrics.Record("impact_analysis", sw.Elapsed, isError);
        }
    }

    // ── 10. find_unused_code ──

    [McpServerTool(Name = "find_unused_code"), Description("Find unused private code symbols within a project or file")]
    public async Task<CallToolResult> FindUnusedCode(
        [Description("Project name to scope the search")] string? projectName = null,
        [Description("Path to C# file to scope the search")] string? filePath = null,
        [Description("Filter by member kind (Method, Property, Field, Event)")] string? kindFilter = null,
        [Description("Number of results per page")] int pageSize = 5,
        [Description("0-based page number")] int page = 0,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                var pathError = ToolValidation.ValidateFilePath(filePath);
                if (pathError != null) return _mapper.Error(pathError);
            }
            if (pageSize < 1 || pageSize > ValidationLimits.MaxPageSize) return _mapper.Error("pageSize must be 1-200");
            if (page < 0) return _mapper.Error("page must be >= 0");
            var request = new FindUnusedCodeRequest(projectName, filePath, kindFilter, pageSize, page);
            var result = await _service.FindUnusedCodeAsync(request, ct);
            if (!result.IsSuccess)
            {
                isError = true;
                return _mapper.Error(result.Error?.Message ?? "Unknown error");
            }
            return _mapper.Success(result.Value);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            isError = true;
            return _mapper.Exception(ex, _logger);
        }
        finally
        {
            _metrics.Record("find_unused_code", sw.Elapsed, isError);
        }
    }

    // ── 11. find_async_issues ──

    [McpServerTool(Name = "find_async_issues"), Description("Scan for async/await anti-patterns like async void, missing await, and sync-over-async")]
    public async Task<CallToolResult> FindAsyncIssues(
        [Description("Project name to scope the search")] string? projectName = null,
        [Description("Path to C# file to scope the search")] string? filePath = null,
        [Description("Number of results per page")] int pageSize = 5,
        [Description("0-based page number")] int page = 0,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                var pathError = ToolValidation.ValidateFilePath(filePath);
                if (pathError != null) return _mapper.Error(pathError);
            }
            if (pageSize < 1 || pageSize > ValidationLimits.MaxPageSize) return _mapper.Error("pageSize must be 1-200");
            if (page < 0) return _mapper.Error("page must be >= 0");
            var request = new FindAsyncIssuesRequest(projectName, filePath, pageSize, page);
            var result = await _service.FindAsyncIssuesAsync(request, ct);
            if (!result.IsSuccess)
            {
                isError = true;
                return _mapper.Error(result.Error?.Message ?? "Unknown error");
            }
            return _mapper.Success(result.Value);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            isError = true;
            return _mapper.Exception(ex, _logger);
        }
        finally
        {
            _metrics.Record("find_async_issues", sw.Elapsed, isError);
        }
    }

    // ── 12. find_performance_issues ──

    [McpServerTool(Name = "find_performance_issues"), Description("Scan for performance anti-patterns like string concat in loops and LINQ in hot paths")]
    public async Task<CallToolResult> FindPerformanceIssues(
        [Description("Project name to scope the search")] string? projectName = null,
        [Description("Path to C# file to scope the search")] string? filePath = null,
        [Description("Number of results per page")] int pageSize = 5,
        [Description("0-based page number")] int page = 0,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                var pathError = ToolValidation.ValidateFilePath(filePath);
                if (pathError != null) return _mapper.Error(pathError);
            }
            if (pageSize < 1 || pageSize > ValidationLimits.MaxPageSize) return _mapper.Error("pageSize must be 1-200");
            if (page < 0) return _mapper.Error("page must be >= 0");
            var request = new FindPerformanceIssuesRequest(projectName, filePath, pageSize, page);
            var result = await _service.FindPerformanceIssuesAsync(request, ct);
            if (!result.IsSuccess)
            {
                isError = true;
                return _mapper.Error(result.Error?.Message ?? "Unknown error");
            }
            return _mapper.Success(result.Value);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            isError = true;
            return _mapper.Exception(ex, _logger);
        }
        finally
        {
            _metrics.Record("find_performance_issues", sw.Elapsed, isError);
        }
    }

    // ── 13. analyze_operations ──

    [McpServerTool(Name = "analyze_operations"), Description("Analyze the IOperation tree for a symbol to understand compiler-level semantics")]
    public async Task<CallToolResult> AnalyzeOperations(
        [Description("Path to C# file")] string filePath,
        [Description("0-based line number")] int line,
        [Description("0-based column number")] int column,
        [Description("Maximum depth of operation tree to traverse")] int maxDepth = 3,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath)) return _mapper.Error("filePath is required");
            var analyzeOpsPathError = ToolValidation.ValidateFilePath(filePath);
            if (analyzeOpsPathError != null) return _mapper.Error(analyzeOpsPathError);
            if (line < 0) return _mapper.Error("line must be >= 0");
            if (column < 0) return _mapper.Error("column must be >= 0");
            if (maxDepth < 0 || maxDepth > ValidationLimits.MaxRecursionDepth) return _mapper.Error("maxDepth must be 0-10");
            var request = new AnalyzeOperationsRequest(filePath, line, column, maxDepth);
            var result = await _service.AnalyzeOperationsAsync(request, ct);
            if (!result.IsSuccess)
            {
                isError = true;
                return _mapper.Error(result.Error?.Message ?? "Unknown error");
            }
            return _mapper.Success(result.Value);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            isError = true;
            return _mapper.Exception(ex, _logger);
        }
        finally
        {
            _metrics.Record("analyze_operations", sw.Elapsed, isError);
        }
    }
}
