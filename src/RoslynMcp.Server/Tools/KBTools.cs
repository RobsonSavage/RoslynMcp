using RoslynMcp.Shared.Contracts.KB;

namespace RoslynMcp.Server.Tools;

[McpServerToolType]
public class KBTools
{
    private readonly KBService _service;
    private readonly IToolResultMapper _mapper;
    private readonly ILogger _logger;
    private readonly IToolMetricsService _metrics;

    public KBTools(KBService service, IToolResultMapper mapper, ILogger logger, IToolMetricsService metrics)
    {
        _service = service;
        _mapper = mapper;
        _logger = logger;
        _metrics = metrics;
    }

    // ── 1. kb_add ──

    [McpServerTool(Name = "kb_add"), Description("Add a new knowledge base entry")]
    public async Task<CallToolResult> KBAdd(
        [Description("Title of the KB entry")] string title,
        [Description("Content/body of the KB entry")] string content,
        [Description("Optional category for grouping")] string? category = null,
        [Description("Optional tags for filtering")] string[]? tags = null,
        [Description("Optional JSON metadata")] string? metadata = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(title)) return _mapper.Error("title is required");
            if (string.IsNullOrWhiteSpace(content)) return _mapper.Error("content is required");
            var request = new KBAddRequest(title, content, category, tags, metadata);
            var result = await _service.AddAsync(request, ct);
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
            _metrics.Record("kb_add", sw.Elapsed, isError);
        }
    }

    // ── 2. kb_get ──

    [McpServerTool(Name = "kb_get"), Description("Retrieve a knowledge base entry by ID")]
    public async Task<CallToolResult> KBGet(
        [Description("ID of the KB entry to retrieve")] long id,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (id <= 0) return _mapper.Error("id must be > 0");
            var request = new KBGetRequest(id);
            var result = await _service.GetAsync(request, ct);
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
            _metrics.Record("kb_get", sw.Elapsed, isError);
        }
    }

    // ── 3. kb_update ──

    [McpServerTool(Name = "kb_update"), Description("Update an existing knowledge base entry")]
    public async Task<CallToolResult> KBUpdate(
        [Description("ID of the KB entry to update")] long id,
        [Description("New title")] string? title = null,
        [Description("New content/body")] string? content = null,
        [Description("New category")] string? category = null,
        [Description("New tags")] string[]? tags = null,
        [Description("New JSON metadata")] string? metadata = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (id <= 0) return _mapper.Error("id must be > 0");
            var request = new KBUpdateRequest(id, title, content, category, tags, metadata);
            var result = await _service.UpdateAsync(request, ct);
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
            _metrics.Record("kb_update", sw.Elapsed, isError);
        }
    }

    // ── 4. kb_delete ──

    [McpServerTool(Name = "kb_delete"), Description("Delete a knowledge base entry by ID")]
    public async Task<CallToolResult> KBDelete(
        [Description("ID of the KB entry to delete")] long id,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (id <= 0) return _mapper.Error("id must be > 0");
            var request = new KBDeleteRequest(id);
            var result = await _service.DeleteAsync(request, ct);
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
            _metrics.Record("kb_delete", sw.Elapsed, isError);
        }
    }

    // ── 5. kb_list ──

    [McpServerTool(Name = "kb_list"), Description("List knowledge base entries with optional filtering and paging")]
    public async Task<CallToolResult> KBList(
        [Description("Optional category filter")] string? category = null,
        [Description("Page number (zero-based)")] int page = 0,
        [Description("Number of entries per page")] int pageSize = 20,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (page < 0) return _mapper.Error("page must be >= 0");
            if (pageSize <= 0 || pageSize > ValidationLimits.MaxPageSize) return _mapper.Error("pageSize must be 1-200");
            var request = new KBListRequest(category, page, pageSize);
            var result = await _service.ListAsync(request, ct);
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
            _metrics.Record("kb_list", sw.Elapsed, isError);
        }
    }

    // ── 6. kb_search ──

    [McpServerTool(Name = "kb_search"), Description("Search knowledge base entries by text")]
    public async Task<CallToolResult> KBSearch(
        [Description("Search query text")] string query,
        [Description("Optional category filter")] string? category = null,
        [Description("Optional tags filter")] string[]? tags = null,
        [Description("Maximum number of results to return")] int limit = 20,
        [Description("Whether to use full-text search if available")] bool useFts = true,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(query)) return _mapper.Error("query is required");
            if (limit <= 0 || limit > 200) return _mapper.Error("limit must be 1-200");
            var request = new KBSearchRequest(query, category, tags, limit, useFts);
            var result = await _service.SearchAsync(request, ct);
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
            _metrics.Record("kb_search", sw.Elapsed, isError);
        }
    }

    // ── 7. kb_related ──

    [McpServerTool(Name = "kb_related"), Description("Find related knowledge base entries by category and tag overlap")]
    public async Task<CallToolResult> KBRelated(
        [Description("ID of the KB entry to find related entries for")] long id,
        [Description("Maximum number of related entries to return")] int limit = 5,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (id <= 0) return _mapper.Error("id must be > 0");
            if (limit <= 0 || limit > 200) return _mapper.Error("limit must be 1-200");
            var request = new KBRelatedRequest(id, limit);
            var result = await _service.RelatedAsync(request, ct);
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
            _metrics.Record("kb_related", sw.Elapsed, isError);
        }
    }

    // ── 8. kb_stats ──

    [McpServerTool(Name = "kb_stats"), Description("Get knowledge base statistics")]
    public async Task<CallToolResult> KBStats(
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            var request = new KBStatsRequest();
            var result = await _service.StatsAsync(request, ct);
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
            _metrics.Record("kb_stats", sw.Elapsed, isError);
        }
    }
}
