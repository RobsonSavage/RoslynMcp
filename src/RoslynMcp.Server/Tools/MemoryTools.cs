using RoslynMcp.Shared.Contracts.Memory;

namespace RoslynMcp.Server.Tools;

[McpServerToolType]
public class MemoryTools
{
    private readonly MemoryService _service;
    private readonly IToolResultMapper _mapper;
    private readonly ILogger _logger;
    private readonly IToolMetricsService _metrics;

    public MemoryTools(MemoryService service, IToolResultMapper mapper, ILogger logger, IToolMetricsService metrics)
    {
        _service = service;
        _mapper = mapper;
        _logger = logger;
        _metrics = metrics;
    }

    // ── 1. session_start ──

    [McpServerTool(Name = "session_start"), Description("Start a new memory session")]
    public async Task<CallToolResult> SessionStart(
        [Description("Optional name for the session")] string? sessionName = null,
        [Description("Optional JSON metadata")] string? metadata = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            var request = new SessionStartRequest(sessionName, metadata);
            var result = await _service.SessionStartAsync(request, ct);
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
            _metrics.Record("session_start", sw.Elapsed, isError);
        }
    }

    // ── 2. session_end ──

    [McpServerTool(Name = "session_end"), Description("End an active memory session")]
    public async Task<CallToolResult> SessionEnd(
        [Description("The session ID to end")] string sessionId,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return _mapper.Error("sessionId is required");
            var request = new SessionEndRequest(sessionId);
            var result = await _service.SessionEndAsync(request, ct);
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
            _metrics.Record("session_end", sw.Elapsed, isError);
        }
    }

    // ── 3. session_list ──

    [McpServerTool(Name = "session_list"), Description("List memory sessions")]
    public async Task<CallToolResult> SessionList(
        [Description("If true, only return active (non-ended) sessions")] bool activeOnly = false,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            var request = new SessionListRequest(activeOnly);
            var result = await _service.SessionListAsync(request, ct);
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
            _metrics.Record("session_list", sw.Elapsed, isError);
        }
    }

    // ── 4. memory_store ──

    [McpServerTool(Name = "memory_store"), Description("Store a new memory entry")]
    public async Task<CallToolResult> MemoryStore(
        [Description("Unique key for the memory entry")] string key,
        [Description("Value/content to store")] string value,
        [Description("Optional category for grouping")] string? category = null,
        [Description("Optional tags for filtering")] string[]? tags = null,
        [Description("Optional session ID to associate with")] string? sessionId = null,
        [Description("Optional JSON metadata")] string? metadata = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(key)) return _mapper.Error("key is required");
            if (string.IsNullOrWhiteSpace(value)) return _mapper.Error("value is required");
            var request = new MemoryStoreRequest(key, value, category, tags, sessionId, metadata);
            var result = await _service.StoreAsync(request, ct);
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
            _metrics.Record("memory_store", sw.Elapsed, isError);
        }
    }

    // ── 5. memory_retrieve ──

    [McpServerTool(Name = "memory_retrieve"), Description("Retrieve a memory entry by key or ID")]
    public async Task<CallToolResult> MemoryRetrieve(
        [Description("Key of the memory entry to retrieve")] string? key = null,
        [Description("ID of the memory entry to retrieve")] long? id = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(key) && id is null) return _mapper.Error("key or id must be provided");
            var request = new MemoryRetrieveRequest(key, id);
            var result = await _service.RetrieveAsync(request, ct);
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
            _metrics.Record("memory_retrieve", sw.Elapsed, isError);
        }
    }

    // ── 6. memory_search ──

    [McpServerTool(Name = "memory_search"), Description("Search memory entries by text, category, or tags")]
    public async Task<CallToolResult> MemorySearch(
        [Description("Search query text")] string query,
        [Description("Optional category filter")] string? category = null,
        [Description("Optional tags filter")] string[]? tags = null,
        [Description("Maximum number of results to return")] int limit = 20,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(query)) return _mapper.Error("query is required");
            if (limit <= 0 || limit > 200) return _mapper.Error("limit must be 1-200");
            var request = new MemorySearchRequest(query, category, tags, limit);
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
            _metrics.Record("memory_search", sw.Elapsed, isError);
        }
    }

    // ── 7. memory_update ──

    [McpServerTool(Name = "memory_update"), Description("Update an existing memory entry")]
    public async Task<CallToolResult> MemoryUpdate(
        [Description("ID of the memory entry to update")] long id,
        [Description("New value/content")] string? value = null,
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
            var request = new MemoryUpdateRequest(id, value, category, tags, metadata);
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
            _metrics.Record("memory_update", sw.Elapsed, isError);
        }
    }

    // ── 8. memory_delete ──

    [McpServerTool(Name = "memory_delete"), Description("Delete a memory entry by ID or key")]
    public async Task<CallToolResult> MemoryDelete(
        [Description("ID of the memory entry to delete")] long? id = null,
        [Description("Key of the memory entry to delete")] string? key = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (id is null && string.IsNullOrWhiteSpace(key)) return _mapper.Error("id or key must be provided");
            var request = new MemoryDeleteRequest(id, key);
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
            _metrics.Record("memory_delete", sw.Elapsed, isError);
        }
    }

    // ── 9. memory_list ──

    [McpServerTool(Name = "memory_list"), Description("List memory entries with optional filtering and paging")]
    public async Task<CallToolResult> MemoryList(
        [Description("Optional category filter")] string? category = null,
        [Description("Optional session ID filter")] string? sessionId = null,
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
            var request = new MemoryListRequest(category, sessionId, page, pageSize);
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
            _metrics.Record("memory_list", sw.Elapsed, isError);
        }
    }

    // ── 10. memory_consolidate ──

    [McpServerTool(Name = "memory_consolidate"), Description("Consolidate duplicate memory entries, keeping the newest")]
    public async Task<CallToolResult> MemoryConsolidate(
        [Description("Optional category filter")] string? category = null,
        [Description("Only consolidate entries older than this many days")] int? olderThanDays = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            var request = new MemoryConsolidateRequest(category, olderThanDays);
            var result = await _service.ConsolidateAsync(request, ct);
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
            _metrics.Record("memory_consolidate", sw.Elapsed, isError);
        }
    }

    // ── 11. memory_export ──

    [McpServerTool(Name = "memory_export"), Description("Export memory entries as JSON")]
    public async Task<CallToolResult> MemoryExport(
        [Description("Export format (default: json)")] string format = "json",
        [Description("Optional category filter")] string? category = null,
        [Description("Maximum number of entries to export (1-100000, default: 10000)")] int maxResults = 10000,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            var request = new MemoryExportRequest(format, category, maxResults);
            var result = await _service.ExportAsync(request, ct);
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
            _metrics.Record("memory_export", sw.Elapsed, isError);
        }
    }

    // ── 12. memory_import ──

    [McpServerTool(Name = "memory_import"), Description("Import memory entries from JSON data")]
    public async Task<CallToolResult> MemoryImport(
        [Description("JSON data containing memory entries to import")] string data,
        [Description("Import format (default: json)")] string format = "json",
        [Description("Merge strategy: skip, replace, or error")] string mergeStrategy = "skip",
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(data)) return _mapper.Error("data is required");
            if (data.Length > 10_000_000) return _mapper.Error("Import data exceeds 10MB limit");
            var request = new MemoryImportRequest(data, format, mergeStrategy);
            var result = await _service.ImportAsync(request, ct);
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
            _metrics.Record("memory_import", sw.Elapsed, isError);
        }
    }

    // ── 13. memory_stats ──

    [McpServerTool(Name = "memory_stats"), Description("Get memory store statistics")]
    public async Task<CallToolResult> MemoryStats(
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            var request = new MemoryStatsRequest();
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
            _metrics.Record("memory_stats", sw.Elapsed, isError);
        }
    }

    // ── 14. memory_cleanup ──

    [McpServerTool(Name = "memory_cleanup"), Description("Clean up old memory entries")]
    public async Task<CallToolResult> MemoryCleanup(
        [Description("Only clean entries older than this many days")] int? olderThanDays = null,
        [Description("Optional category filter")] string? category = null,
        [Description("If true, only report what would be removed without deleting")] bool dryRun = false,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            var request = new MemoryCleanupRequest(olderThanDays, category, dryRun);
            var result = await _service.CleanupAsync(request, ct);
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
            _metrics.Record("memory_cleanup", sw.Elapsed, isError);
        }
    }
}
