using RoslynMcp.Shared.Contracts.Search;

namespace RoslynMcp.Server.Tools;

[McpServerToolType]
public class SearchTools
{
    private readonly SearchService _service;
    private readonly IToolResultMapper _mapper;
    private readonly ILogger _logger;
    private readonly IToolMetricsService _metrics;

    public SearchTools(SearchService service, IToolResultMapper mapper, ILogger logger, IToolMetricsService metrics)
    {
        _service = service;
        _mapper = mapper;
        _logger = logger;
        _metrics = metrics;
    }

    // ── 1. find_references ──

    [McpServerTool(Name = "find_references"), Description("Find all references to a symbol at a given position")]
    public async Task<CallToolResult> FindReferences(
        [Description("Path to the C# file")] string filePath,
        [Description("0-based line number")] int line,
        [Description("0-based column number")] int column,
        [Description("Include surrounding context lines")] bool includeContext = false,
        [Description("Results per page")] int pageSize = 5,
        [Description("0-based page number")] int page = 0,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath)) return _mapper.Error("filePath is required");
            if (line < 0) return _mapper.Error("line must be >= 0");
            if (column < 0) return _mapper.Error("column must be >= 0");
            if (pageSize <= 0 || pageSize > ValidationLimits.MaxPageSize) return _mapper.Error("pageSize must be 1-200");
            if (page < 0) return _mapper.Error("page must be >= 0");
            var request = new FindReferencesRequest(filePath, line, column, includeContext, pageSize, page);
            var result = await _service.FindReferencesAsync(request, ct);
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
            _metrics.Record("find_references", sw.Elapsed, isError);
        }
    }

    // ── 2. find_implementations ──

    [McpServerTool(Name = "find_implementations"), Description("Find all implementations of an interface or abstract member")]
    public async Task<CallToolResult> FindImplementations(
        [Description("Path to the C# file")] string filePath,
        [Description("0-based line number")] int line,
        [Description("0-based column number")] int column,
        [Description("Include surrounding context lines")] bool includeContext = false,
        [Description("Results per page")] int pageSize = 5,
        [Description("0-based page number")] int page = 0,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath)) return _mapper.Error("filePath is required");
            if (line < 0) return _mapper.Error("line must be >= 0");
            if (column < 0) return _mapper.Error("column must be >= 0");
            if (pageSize <= 0 || pageSize > ValidationLimits.MaxPageSize) return _mapper.Error("pageSize must be 1-200");
            if (page < 0) return _mapper.Error("page must be >= 0");
            var request = new FindImplementationsRequest(filePath, line, column, includeContext, pageSize, page);
            var result = await _service.FindImplementationsAsync(request, ct);
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
            _metrics.Record("find_implementations", sw.Elapsed, isError);
        }
    }

    // ── 3. find_callers ──

    [McpServerTool(Name = "find_callers"), Description("Find all callers of a method or property")]
    public async Task<CallToolResult> FindCallers(
        [Description("Path to the C# file")] string filePath,
        [Description("0-based line number")] int line,
        [Description("0-based column number")] int column,
        [Description("Include surrounding context lines")] bool includeContext = false,
        [Description("Results per page")] int pageSize = 5,
        [Description("0-based page number")] int page = 0,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath)) return _mapper.Error("filePath is required");
            if (line < 0) return _mapper.Error("line must be >= 0");
            if (column < 0) return _mapper.Error("column must be >= 0");
            if (pageSize <= 0 || pageSize > ValidationLimits.MaxPageSize) return _mapper.Error("pageSize must be 1-200");
            if (page < 0) return _mapper.Error("page must be >= 0");
            var request = new FindCallersRequest(filePath, line, column, includeContext, pageSize, page);
            var result = await _service.FindCallersAsync(request, ct);
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
            _metrics.Record("find_callers", sw.Elapsed, isError);
        }
    }

    // ── 4. find_callees ──

    [McpServerTool(Name = "find_callees"), Description("Find all methods called by a given method")]
    public async Task<CallToolResult> FindCallees(
        [Description("Path to the C# file")] string filePath,
        [Description("0-based line number")] int line,
        [Description("0-based column number")] int column,
        [Description("Include surrounding context lines")] bool includeContext = false,
        [Description("Results per page")] int pageSize = 5,
        [Description("0-based page number")] int page = 0,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath)) return _mapper.Error("filePath is required");
            if (line < 0) return _mapper.Error("line must be >= 0");
            if (column < 0) return _mapper.Error("column must be >= 0");
            if (pageSize <= 0 || pageSize > ValidationLimits.MaxPageSize) return _mapper.Error("pageSize must be 1-200");
            if (page < 0) return _mapper.Error("page must be >= 0");
            var request = new FindCalleesRequest(filePath, line, column, includeContext, pageSize, page);
            var result = await _service.FindCalleesAsync(request, ct);
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
            _metrics.Record("find_callees", sw.Elapsed, isError);
        }
    }

    // ── 5. find_definition ──

    [McpServerTool(Name = "find_definition"), Description("Go to the definition of a symbol at a given position")]
    public async Task<CallToolResult> FindDefinition(
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
            var request = new FindDefinitionRequest(filePath, line, column);
            var result = await _service.FindDefinitionAsync(request, ct);
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
            _metrics.Record("find_definition", sw.Elapsed, isError);
        }
    }

    // ── 6. find_overrides ──

    [McpServerTool(Name = "find_overrides"), Description("Find all overrides of a virtual or abstract member")]
    public async Task<CallToolResult> FindOverrides(
        [Description("Path to the C# file")] string filePath,
        [Description("0-based line number")] int line,
        [Description("0-based column number")] int column,
        [Description("Include surrounding context lines")] bool includeContext = false,
        [Description("Results per page")] int pageSize = 5,
        [Description("0-based page number")] int page = 0,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath)) return _mapper.Error("filePath is required");
            if (line < 0) return _mapper.Error("line must be >= 0");
            if (column < 0) return _mapper.Error("column must be >= 0");
            if (pageSize <= 0 || pageSize > ValidationLimits.MaxPageSize) return _mapper.Error("pageSize must be 1-200");
            if (page < 0) return _mapper.Error("page must be >= 0");
            var request = new FindOverridesRequest(filePath, line, column, includeContext, pageSize, page);
            var result = await _service.FindOverridesAsync(request, ct);
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
            _metrics.Record("find_overrides", sw.Elapsed, isError);
        }
    }

    // ── 7. find_derived_types ──

    [McpServerTool(Name = "find_derived_types"), Description("Find all types that derive from or implement a given type")]
    public async Task<CallToolResult> FindDerivedTypes(
        [Description("Path to the C# file")] string filePath,
        [Description("0-based line number")] int line,
        [Description("0-based column number")] int column,
        [Description("Include surrounding context lines")] bool includeContext = false,
        [Description("Results per page")] int pageSize = 5,
        [Description("0-based page number")] int page = 0,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath)) return _mapper.Error("filePath is required");
            if (line < 0) return _mapper.Error("line must be >= 0");
            if (column < 0) return _mapper.Error("column must be >= 0");
            if (pageSize <= 0 || pageSize > ValidationLimits.MaxPageSize) return _mapper.Error("pageSize must be 1-200");
            if (page < 0) return _mapper.Error("page must be >= 0");
            var request = new FindDerivedTypesRequest(filePath, line, column, includeContext, pageSize, page);
            var result = await _service.FindDerivedTypesAsync(request, ct);
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
            _metrics.Record("find_derived_types", sw.Elapsed, isError);
        }
    }

    // ── 8. find_base_members ──

    [McpServerTool(Name = "find_base_members"), Description("Find base class members and interface members that a symbol implements or overrides")]
    public async Task<CallToolResult> FindBaseMembers(
        [Description("Path to the C# file")] string filePath,
        [Description("0-based line number")] int line,
        [Description("0-based column number")] int column,
        [Description("Results per page")] int pageSize = 5,
        [Description("0-based page number")] int page = 0,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath)) return _mapper.Error("filePath is required");
            if (line < 0) return _mapper.Error("line must be >= 0");
            if (column < 0) return _mapper.Error("column must be >= 0");
            var request = new FindBaseMembersRequest(filePath, line, column, pageSize, page);
            var result = await _service.FindBaseMembersAsync(request, ct);
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
            _metrics.Record("find_base_members", sw.Elapsed, isError);
        }
    }

    // ── 9. find_entry_points ──

    [McpServerTool(Name = "find_entry_points"), Description("Find entry points such as Main methods and API controllers in the solution")]
    public async Task<CallToolResult> FindEntryPoints(
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
            var request = new FindEntryPointsRequest(projectName, pageSize, page);
            var result = await _service.FindEntryPointsAsync(request, ct);
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
            _metrics.Record("find_entry_points", sw.Elapsed, isError);
        }
    }

    // ── 10. find_extension_methods ──

    [McpServerTool(Name = "find_extension_methods"), Description("Find extension methods applicable to a given type")]
    public async Task<CallToolResult> FindExtensionMethods(
        [Description("Fully qualified type name to find extensions for")] string? typeName = null,
        [Description("Path to the C# file containing the type")] string? filePath = null,
        [Description("0-based line number of the type")] int? line = null,
        [Description("0-based column number of the type")] int? column = null,
        [Description("Results per page")] int pageSize = 5,
        [Description("0-based page number")] int page = 0,
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
            if (pageSize <= 0 || pageSize > ValidationLimits.MaxPageSize) return _mapper.Error("pageSize must be 1-200");
            if (page < 0) return _mapper.Error("page must be >= 0");
            var request = new FindExtensionMethodsRequest(typeName, filePath, line, column, pageSize, page);
            var result = await _service.FindExtensionMethodsAsync(request, ct);
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
            _metrics.Record("find_extension_methods", sw.Elapsed, isError);
        }
    }

    // ── 11. find_attribute_usages ──

    [McpServerTool(Name = "find_attribute_usages"), Description("Find all usages of a specific attribute in the solution")]
    public async Task<CallToolResult> FindAttributeUsages(
        [Description("Name of the attribute (with or without 'Attribute' suffix)")] string attributeName,
        [Description("Results per page")] int pageSize = 5,
        [Description("0-based page number")] int page = 0,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(attributeName)) return _mapper.Error("attributeName is required");
            if (pageSize <= 0 || pageSize > ValidationLimits.MaxPageSize) return _mapper.Error("pageSize must be 1-200");
            if (page < 0) return _mapper.Error("page must be >= 0");
            var request = new FindAttributeUsagesRequest(attributeName, pageSize, page);
            var result = await _service.FindAttributeUsagesAsync(request, ct);
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
            _metrics.Record("find_attribute_usages", sw.Elapsed, isError);
        }
    }

    // ── 12. find_tests_for_type ──

    [McpServerTool(Name = "find_tests_for_type"), Description("Find test classes and methods that reference a given type")]
    public async Task<CallToolResult> FindTestsForType(
        [Description("Fully qualified type name to find tests for")] string? typeName = null,
        [Description("Path to the C# file containing the type")] string? filePath = null,
        [Description("0-based line number of the type")] int? line = null,
        [Description("0-based column number of the type")] int? column = null,
        [Description("Results per page")] int pageSize = 5,
        [Description("0-based page number")] int page = 0,
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
            if (pageSize <= 0 || pageSize > ValidationLimits.MaxPageSize) return _mapper.Error("pageSize must be 1-200");
            if (page < 0) return _mapper.Error("page must be >= 0");
            var request = new FindTestsForTypeRequest(typeName, filePath, line, column, pageSize, page);
            var result = await _service.FindTestsForTypeAsync(request, ct);
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
            _metrics.Record("find_tests_for_type", sw.Elapsed, isError);
        }
    }

    // ── 13. find_event_subscribers ──

    [McpServerTool(Name = "find_event_subscribers"), Description("Find all subscribers and unsubscribers of an event")]
    public async Task<CallToolResult> FindEventSubscribers(
        [Description("Path to the C# file")] string filePath,
        [Description("0-based line number")] int line,
        [Description("0-based column number")] int column,
        [Description("Include surrounding context lines")] bool includeContext = false,
        [Description("Results per page")] int pageSize = 5,
        [Description("0-based page number")] int page = 0,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath)) return _mapper.Error("filePath is required");
            if (line < 0) return _mapper.Error("line must be >= 0");
            if (column < 0) return _mapper.Error("column must be >= 0");
            if (pageSize <= 0 || pageSize > ValidationLimits.MaxPageSize) return _mapper.Error("pageSize must be 1-200");
            if (page < 0) return _mapper.Error("page must be >= 0");
            var request = new FindEventSubscribersRequest(filePath, line, column, includeContext, pageSize, page);
            var result = await _service.FindEventSubscribersAsync(request, ct);
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
            _metrics.Record("find_event_subscribers", sw.Elapsed, isError);
        }
    }

    // ── 14. text_search ──

    [McpServerTool(Name = "text_search"), Description("Search for text or regex patterns across solution source files")]
    public async Task<CallToolResult> TextSearch(
        [Description("Text or regex pattern to search for")] string pattern,
        [Description("Treat pattern as a regular expression")] bool isRegex = false,
        [Description("Use case-sensitive matching")] bool caseSensitive = false,
        [Description("Glob pattern to filter file names (e.g. *.cs)")] string? filePattern = null,
        [Description("Filter by project name")] string? projectName = null,
        [Description("Results per page")] int pageSize = 5,
        [Description("0-based page number")] int page = 0,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(pattern)) return _mapper.Error("pattern is required");
            if (pageSize <= 0 || pageSize > ValidationLimits.MaxPageSize) return _mapper.Error("pageSize must be 1-200");
            if (page < 0) return _mapper.Error("page must be >= 0");
            var request = new TextSearchRequest(pattern, isRegex, caseSensitive, filePattern, projectName, pageSize, page);
            var result = await _service.TextSearchAsync(request, ct);
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
            _metrics.Record("text_search", sw.Elapsed, isError);
        }
    }
}
