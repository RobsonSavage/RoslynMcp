using RoslynMcp.Shared.Contracts.Util;

namespace RoslynMcp.Server.Tools;

[McpServerToolType]
public class UtilTools
{
    private readonly UtilService _service;
    private readonly IToolResultMapper _mapper;
    private readonly ILogger _logger;
    private readonly IToolMetricsService _metrics;

    public UtilTools(UtilService service, IToolResultMapper mapper, ILogger logger, IToolMetricsService metrics)
    {
        _service = service;
        _mapper = mapper;
        _logger = logger;
        _metrics = metrics;
    }

    // ── 1. validate_text ──

    [McpServerTool(Name = "validate_text"), Description("Validate C# source text against a file's project for compilation errors")]
    public async Task<CallToolResult> ValidateText(
        [Description("Path to the C# file to validate against")] string filePath,
        [Description("Full C# source text to validate")] string text,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath)) return _mapper.Error("filePath is required");
            if (string.IsNullOrWhiteSpace(text)) return _mapper.Error("text is required");
            var request = new ValidateTextRequest(filePath, text);
            var result = await _service.ValidateTextAsync(request, ct);
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
            _metrics.Record("validate_text", sw.Elapsed, isError);
        }
    }

    // ── 2. reload_file ──

    [McpServerTool(Name = "reload_file"), Description("Reload a document from disk into the workspace")]
    public async Task<CallToolResult> ReloadFile(
        [Description("Path to the C# file to reload")] string filePath,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath)) return _mapper.Error("filePath is required");
            var request = new ReloadFileRequest(filePath);
            var result = await _service.ReloadFileAsync(request, ct);
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
            _metrics.Record("reload_file", sw.Elapsed, isError);
        }
    }

    // ── 3. get_workspace_status ──

    [McpServerTool(Name = "get_workspace_status"), Description("Get workspace health status including project count, document count, and diagnostics summary")]
    public async Task<CallToolResult> GetWorkspaceStatus(
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            var request = new GetWorkspaceStatusRequest();
            var result = await _service.GetWorkspaceStatusAsync(request, ct);
            if (result.IsSuccess)
            {
                var snapshots = _metrics.GetAllSnapshots();
                var metricsDict = new Dictionary<string, object>();
                foreach (var kvp in snapshots)
                    metricsDict[kvp.Key] = kvp.Value;
                var enriched = result.Value! with { Metrics = metricsDict };
                return _mapper.Success(enriched);
            }
            isError = true;
            return _mapper.Error(result.Error?.Message ?? "Unknown error");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            isError = true;
            return _mapper.Exception(ex, _logger);
        }
        finally
        {
            _metrics.Record("get_workspace_status", sw.Elapsed, isError);
        }
    }

    // ── 4. get_errors ──

    [McpServerTool(Name = "get_errors"), Description("Get compilation errors, optionally filtered by file or project")]
    public async Task<CallToolResult> GetErrors(
        [Description("Filter by file path")] string? filePath = null,
        [Description("Filter by project name")] string? projectName = null,
        [Description("Results per page")] int pageSize = 5,
        [Description("0-based page number")] int page = 0,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (pageSize <= 0 || pageSize > ValidationLimits.MaxPageSize) return _mapper.Error("pageSize must be 1-200");
            if (page < 0) return _mapper.Error("page must be >= 0");
            var request = new GetErrorsRequest(filePath, projectName, pageSize, page);
            var result = await _service.GetErrorsAsync(request, ct);
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
            _metrics.Record("get_errors", sw.Elapsed, isError);
        }
    }

    // ── 5. get_warnings ──

    [McpServerTool(Name = "get_warnings"), Description("Get compilation warnings, optionally filtered by file or project")]
    public async Task<CallToolResult> GetWarnings(
        [Description("Filter by file path")] string? filePath = null,
        [Description("Filter by project name")] string? projectName = null,
        [Description("Results per page")] int pageSize = 5,
        [Description("0-based page number")] int page = 0,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (pageSize <= 0 || pageSize > ValidationLimits.MaxPageSize) return _mapper.Error("pageSize must be 1-200");
            if (page < 0) return _mapper.Error("page must be >= 0");
            var request = new GetWarningsRequest(filePath, projectName, pageSize, page);
            var result = await _service.GetWarningsAsync(request, ct);
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
            _metrics.Record("get_warnings", sw.Elapsed, isError);
        }
    }

    // ── 6. get_quick_fixes ──

    [McpServerTool(Name = "get_quick_fixes"), Description("Get available quick fixes (code actions) at a given position")]
    public async Task<CallToolResult> GetQuickFixes(
        [Description("Path to the C# file")] string filePath,
        [Description("0-based line number")] int line,
        [Description("0-based column number")] int column,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath)) return _mapper.Error("filePath is required");
            if (line < 0) return _mapper.Error("line must be >= 0");
            if (column < 0) return _mapper.Error("column must be >= 0");
            var request = new GetQuickFixesRequest(filePath, line, column);
            var result = await _service.GetQuickFixesAsync(request, ct);
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
            _metrics.Record("get_quick_fixes", sw.Elapsed, isError);
        }
    }

    // ── 7. suggest_refactorings ──

    [McpServerTool(Name = "suggest_refactorings"), Description("Suggest applicable refactorings at a given code position")]
    public async Task<CallToolResult> SuggestRefactorings(
        [Description("Path to the C# file")] string filePath,
        [Description("0-based line number")] int line,
        [Description("0-based column number")] int column,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath)) return _mapper.Error("filePath is required");
            if (line < 0) return _mapper.Error("line must be >= 0");
            if (column < 0) return _mapper.Error("column must be >= 0");
            var request = new SuggestRefactoringsRequest(filePath, line, column);
            var result = await _service.SuggestRefactoringsAsync(request, ct);
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
            _metrics.Record("suggest_refactorings", sw.Elapsed, isError);
        }
    }

    // ── 8. get_full_context ──

    [McpServerTool(Name = "get_full_context"), Description("Get a recursive caller/callee context tree for a symbol")]
    public async Task<CallToolResult> GetFullContext(
        [Description("Path to the C# file")] string filePath,
        [Description("0-based line number")] int line,
        [Description("0-based column number")] int column,
        [Description("Maximum depth to traverse")] int depth = 2,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath)) return _mapper.Error("filePath is required");
            if (line < 0) return _mapper.Error("line must be >= 0");
            if (column < 0) return _mapper.Error("column must be >= 0");
            if (depth < 0 || depth > ValidationLimits.MaxRecursionDepth) return _mapper.Error("depth must be 0-10");
            var request = new GetFullContextRequest(filePath, line, column, depth);
            var result = await _service.GetFullContextAsync(request, ct);
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
            _metrics.Record("get_full_context", sw.Elapsed, isError);
        }
    }

    // ── 9. set_solution_path ──

    [McpServerTool(Name = "set_solution_path"), Description("Switch the workspace to a different .sln or .slnx solution file")]
    public async Task<CallToolResult> SetSolutionPath(
        [Description("Absolute or relative path to the .sln or .slnx file")] string solutionPath,
        [Description("Warm up compilations after loading")] bool warmUp = false,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(solutionPath)) return _mapper.Error("solutionPath is required");
            var request = new SetSolutionPathRequest(solutionPath, warmUp);
            var result = await _service.SetSolutionPathAsync(request, ct);
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
            _metrics.Record("set_solution_path", sw.Elapsed, isError);
        }
    }

    // ── 10. set_solution_root ──

    [McpServerTool(Name = "set_solution_root"), Description("Discover and follow the solution in a client workspace root unless solution selection is pinned")]
    public async Task<CallToolResult> SetSolutionRoot(
        [Description("Absolute path to the client's current workspace directory")] string rootPath,
        [Description("Warm up compilations after loading")] bool warmUp = false,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(rootPath)) return _mapper.Error("rootPath is required");
            var request = new SetSolutionRootRequest(rootPath, warmUp);
            var result = await _service.SetSolutionRootAsync(request, ct);
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
            _metrics.Record("set_solution_root", sw.Elapsed, isError);
        }
    }

    // ── 11. config_get ──

    [McpServerTool(Name = "config_get"), Description("Get the current value of a configuration setting")]
    public async Task<CallToolResult> ConfigGet(
        [Description("Configuration key to read")] string key,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(key)) return _mapper.Error("key is required");
            var request = new ConfigGetRequest(key);
            var result = await _service.ConfigGetAsync(request, ct);
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
            _metrics.Record("config_get", sw.Elapsed, isError);
        }
    }

    // ── 12. config_set ──

    [McpServerTool(Name = "config_set"), Description("Set a configuration value")]
    public async Task<CallToolResult> ConfigSet(
        [Description("Configuration key to set")] string key,
        [Description("Value to assign")] string value,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(key)) return _mapper.Error("key is required");
            if (string.IsNullOrWhiteSpace(value)) return _mapper.Error("value is required");
            var request = new ConfigSetRequest(key, value);
            var result = await _service.ConfigSetAsync(request, ct);
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
            _metrics.Record("config_set", sw.Elapsed, isError);
        }
    }

    // ── 13. config_list ──

    [McpServerTool(Name = "config_list"), Description("List all configuration entries with their current values")]
    public async Task<CallToolResult> ConfigList(
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            var request = new ConfigListRequest();
            var result = await _service.ConfigListAsync(request, ct);
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
            _metrics.Record("config_list", sw.Elapsed, isError);
        }
    }

    // ── 14. tool_enabled ──

    [McpServerTool(Name = "tool_enabled"), Description("Check or toggle whether a specific tool is enabled")]
    public async Task<CallToolResult> ToolEnabled(
        [Description("Name of the tool")] string toolName,
        [Description("Set to true/false to enable/disable, or null to query")] bool? enabled = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(toolName)) return _mapper.Error("toolName is required");
            var request = new ToolEnabledRequest(toolName, enabled);
            var result = await _service.ToolEnabledAsync(request, ct);
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
            _metrics.Record("tool_enabled", sw.Elapsed, isError);
        }
    }
}
