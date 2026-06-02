using RoslynMcp.Shared.Contracts.Graph;

namespace RoslynMcp.Server.Tools;

[McpServerToolType]
public class GraphTools
{
    private readonly GraphService _service;
    private readonly IToolResultMapper _mapper;
    private readonly ILogger _logger;
    private readonly IToolMetricsService _metrics;

    public GraphTools(GraphService service, IToolResultMapper mapper, ILogger logger, IToolMetricsService metrics)
    {
        _service = service;
        _mapper = mapper;
        _logger = logger;
        _metrics = metrics;
    }

    // ── 1. graph_add_node ──

    [McpServerTool(Name = "graph_add_node"), Description("Add a node to the dependency graph")]
    public async Task<CallToolResult> GraphAddNode(
        [Description("Unique identifier for the node")] string id,
        [Description("Node type (e.g. Class, Method, Project)")] string type,
        [Description("Optional display label")] string? label = null,
        [Description("Optional JSON properties")] string? properties = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(id)) return _mapper.Error("id is required");
            if (string.IsNullOrWhiteSpace(type)) return _mapper.Error("type is required");
            var request = new GraphAddNodeRequest(id, type, label, properties);
            var result = await _service.AddNodeAsync(request, ct);
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
            _metrics.Record("graph_add_node", sw.Elapsed, isError);
        }
    }

    // ── 2. graph_add_edge ──

    [McpServerTool(Name = "graph_add_edge"), Description("Add an edge between two nodes in the dependency graph")]
    public async Task<CallToolResult> GraphAddEdge(
        [Description("Source node ID")] string sourceId,
        [Description("Target node ID")] string targetId,
        [Description("Edge type (e.g. References, Inherits, Calls)")] string type,
        [Description("Optional display label")] string? label = null,
        [Description("Optional JSON properties")] string? properties = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(sourceId)) return _mapper.Error("sourceId is required");
            if (string.IsNullOrWhiteSpace(targetId)) return _mapper.Error("targetId is required");
            if (string.IsNullOrWhiteSpace(type)) return _mapper.Error("type is required");
            var request = new GraphAddEdgeRequest(sourceId, targetId, type, label, properties);
            var result = await _service.AddEdgeAsync(request, ct);
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
            _metrics.Record("graph_add_edge", sw.Elapsed, isError);
        }
    }

    // ── 3. graph_remove_node ──

    [McpServerTool(Name = "graph_remove_node"), Description("Remove a node and optionally its edges from the graph")]
    public async Task<CallToolResult> GraphRemoveNode(
        [Description("Node ID to remove")] string id,
        [Description("If true, also remove all connected edges")] bool cascade = true,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(id)) return _mapper.Error("id is required");
            var request = new GraphRemoveNodeRequest(id, cascade);
            var result = await _service.RemoveNodeAsync(request, ct);
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
            _metrics.Record("graph_remove_node", sw.Elapsed, isError);
        }
    }

    // ── 4. graph_query_neighbors ──

    [McpServerTool(Name = "graph_query_neighbors"), Description("Query neighboring nodes within a given depth")]
    public async Task<CallToolResult> GraphQueryNeighbors(
        [Description("Starting node ID")] string nodeId,
        [Description("Traversal direction: incoming, outgoing, or both")] string direction = "both",
        [Description("Optional edge type filter")] string? edgeType = null,
        [Description("Maximum traversal depth")] int depth = 1,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(nodeId)) return _mapper.Error("nodeId is required");
            if (direction is not ("incoming" or "outgoing" or "both"))
                return _mapper.Error("direction must be 'incoming', 'outgoing', or 'both'");
            if (depth < 0 || depth > ValidationLimits.MaxRecursionDepth) return _mapper.Error("depth must be 0-10");
            var request = new GraphQueryNeighborsRequest(nodeId, direction, edgeType, depth);
            var result = await _service.QueryNeighborsAsync(request, ct);
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
            _metrics.Record("graph_query_neighbors", sw.Elapsed, isError);
        }
    }

    // ── 5. graph_query_path ──

    [McpServerTool(Name = "graph_query_path"), Description("Find paths between two nodes in the graph")]
    public async Task<CallToolResult> GraphQueryPath(
        [Description("Source node ID")] string sourceId,
        [Description("Target node ID")] string targetId,
        [Description("Maximum path depth to search")] int maxDepth = 10,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(sourceId)) return _mapper.Error("sourceId is required");
            if (string.IsNullOrWhiteSpace(targetId)) return _mapper.Error("targetId is required");
            if (maxDepth < 1 || maxDepth > ValidationLimits.MaxRecursionDepth) return _mapper.Error("maxDepth must be 1-10");
            var request = new GraphQueryPathRequest(sourceId, targetId, maxDepth);
            var result = await _service.QueryPathAsync(request, ct);
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
            _metrics.Record("graph_query_path", sw.Elapsed, isError);
        }
    }

    // ── 6. graph_query_subgraph ──

    [McpServerTool(Name = "graph_query_subgraph"), Description("Extract a subgraph rooted at a node")]
    public async Task<CallToolResult> GraphQuerySubgraph(
        [Description("Root node ID")] string rootId,
        [Description("Maximum traversal depth")] int depth = 2,
        [Description("Optional edge type filters")] string[]? edgeTypes = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(rootId)) return _mapper.Error("rootId is required");
            if (depth < 0 || depth > ValidationLimits.MaxRecursionDepth) return _mapper.Error("depth must be 0-10");
            var request = new GraphQuerySubgraphRequest(rootId, depth, edgeTypes);
            var result = await _service.QuerySubgraphAsync(request, ct);
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
            _metrics.Record("graph_query_subgraph", sw.Elapsed, isError);
        }
    }

    // ── 7. graph_impact ──

    [McpServerTool(Name = "graph_impact"), Description("Analyze the impact of changes to a node")]
    public async Task<CallToolResult> GraphImpact(
        [Description("Node ID to analyze impact for")] string nodeId,
        [Description("Direction of impact: incoming, outgoing, or both")] string direction = "outgoing",
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (string.IsNullOrWhiteSpace(nodeId)) return _mapper.Error("nodeId is required");
            if (direction is not ("incoming" or "outgoing" or "both"))
                return _mapper.Error("direction must be 'incoming', 'outgoing', or 'both'");
            var request = new GraphImpactRequest(nodeId, direction);
            var result = await _service.ImpactAsync(request, ct);
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
            _metrics.Record("graph_impact", sw.Elapsed, isError);
        }
    }

    // ── 8. graph_visualize ──

    [McpServerTool(Name = "graph_visualize"), Description("Generate a visual representation of the graph")]
    public async Task<CallToolResult> GraphVisualize(
        [Description("Optional node IDs to include (null for all)")] string[]? nodeIds = null,
        [Description("Output format: mermaid or dot")] string format = "mermaid",
        [Description("Maximum number of nodes to include")] int maxNodes = 50,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            if (format is not ("mermaid" or "dot"))
                return _mapper.Error("format must be 'mermaid' or 'dot'");
            if (maxNodes <= 0 || maxNodes > 500) return _mapper.Error("maxNodes must be 1-500");
            var request = new GraphVisualizeRequest(nodeIds, format, maxNodes);
            var result = await _service.VisualizeAsync(request, ct);
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
            _metrics.Record("graph_visualize", sw.Elapsed, isError);
        }
    }

    // ── 9. graph_stats ──

    [McpServerTool(Name = "graph_stats"), Description("Get graph statistics including node and edge counts")]
    public async Task<CallToolResult> GraphStats(
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            var request = new GraphStatsRequest();
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
            _metrics.Record("graph_stats", sw.Elapsed, isError);
        }
    }

    // ── 10. graph_rebuild ──

    [McpServerTool(Name = "graph_rebuild"), Description("Rebuild the dependency graph from the current solution")]
    public async Task<CallToolResult> GraphRebuild(
        [Description("If true, clear all existing nodes and edges before rebuilding")] bool fullRebuild = false,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool isError = false;
        try
        {
            var request = new GraphRebuildRequest(fullRebuild);
            var result = await _service.RebuildAsync(request, ct);
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
            _metrics.Record("graph_rebuild", sw.Elapsed, isError);
        }
    }
}
