using System.Text;
using Microsoft.Data.Sqlite;
using RoslynMcp.Shared;
using RoslynMcp.Shared.Contracts.Graph;

namespace RoslynMcp.Core.Services;

public partial class GraphService
{
    // ── 4. graph_query_neighbors ──

    public async Task<Result<GraphQueryNeighborsResponse>> QueryNeighborsAsync(GraphQueryNeighborsRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await using var lease = await _pool.AcquireReaderAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var conn = lease.Connection;

            var allowedEdgeTypes = request.EdgeType != null ? new HashSet<string> { request.EdgeType } : null;
            var (nodes, edges, truncated) = await BfsAsync(conn, request.NodeId, request.Depth, request.Direction, allowedEdgeTypes, ct).ConfigureAwait(false);

            return new GraphQueryNeighborsResponse(nodes, edges, truncated);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Error(ex, "graph_query_neighbors failed for {NodeId}", request.NodeId);
            return Result<GraphQueryNeighborsResponse>.Fail(ex.Message);
        }
    }

    // ── 5. graph_query_path ──

    public async Task<Result<GraphQueryPathResponse>> QueryPathAsync(GraphQueryPathRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await using var lease = await _pool.AcquireReaderAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var conn = lease.Connection;

            var maxDepth = Math.Min(request.MaxDepth, ValidationLimits.MaxPathSearchDepth);
            var (paths, truncated) = await FindAllPathsAsync(conn, request.SourceId, request.TargetId, maxDepth, ct).ConfigureAwait(false);

            int? shortestLength = paths.Count > 0 ? paths.Min(p => p.Length) : null;

            return new GraphQueryPathResponse(paths, shortestLength, truncated);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Error(ex, "graph_query_path failed for {Source}->{Target}", request.SourceId, request.TargetId);
            return Result<GraphQueryPathResponse>.Fail(ex.Message);
        }
    }

    // ── 6. graph_query_subgraph ──

    public async Task<Result<GraphQuerySubgraphResponse>> QuerySubgraphAsync(GraphQuerySubgraphRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await using var lease = await _pool.AcquireReaderAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var conn = lease.Connection;

            var allowedEdgeTypes = request.EdgeTypes is { Count: > 0 }
                ? new HashSet<string>(request.EdgeTypes, StringComparer.OrdinalIgnoreCase)
                : null;

            var (nodes, edges, truncated) = await BfsAsync(conn, request.RootId, request.Depth, "both", allowedEdgeTypes, ct).ConfigureAwait(false);

            return new GraphQuerySubgraphResponse(nodes, edges, truncated);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Error(ex, "graph_query_subgraph failed for {RootId}", request.RootId);
            return Result<GraphQuerySubgraphResponse>.Fail(ex.Message);
        }
    }

    // ── 7. graph_impact ──

    public async Task<Result<GraphImpactResponse>> ImpactAsync(GraphImpactRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await using var lease = await _pool.AcquireReaderAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var conn = lease.Connection;

            var (nodes, edges, truncated) = await BfsAsync(conn, request.NodeId, ValidationLimits.MaxGraphImpactDepth, request.Direction, allowedEdgeTypes: null, ct).ConfigureAwait(false);

            var impactPaths = BuildImpactPaths(request.NodeId, nodes, edges, request.Direction, ct);

            // Exclude the start node from impacted nodes
            var impactedNodes = nodes.Where(n => n.Id != request.NodeId).ToList();

            return new GraphImpactResponse(impactedNodes, impactPaths, truncated);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Error(ex, "graph_impact failed for {NodeId}", request.NodeId);
            return Result<GraphImpactResponse>.Fail(ex.Message);
        }
    }

    #region BFS & Path Search

    /// <summary>
    /// Iterative frontier-based BFS. Fetches edges per level from SQLite instead of
    /// bulk pre-fetching, ensuring startId and all reachable nodes are always found
    /// regardless of graph size.
    /// </summary>
    private async Task<(List<GraphNode> Nodes, List<GraphEdge> Edges, bool IsTruncated)> BfsAsync(
        SqliteConnection conn, string startId, int maxDepth, string direction,
        ISet<string>? allowedEdgeTypes, CancellationToken ct = default)
    {
        var maxNodes = _maxBfsNodes;
        var maxEdges = ValidationLimits.MaxBfsEdges;
        var isTruncated = false;

        var visitedNodes = new HashSet<string> { startId };
        var collectedEdges = new List<GraphEdge>();
        var visitedEdgeIds = new HashSet<long>();
        var currentFrontier = new HashSet<string> { startId };

        for (int depth = 0; depth < maxDepth && currentFrontier.Count > 0 && !isTruncated; depth++)
        {
            ct.ThrowIfCancellationRequested();

            var frontierEdges = await FetchEdgesForFrontierAsync(
                conn, currentFrontier, direction, allowedEdgeTypes, ct).ConfigureAwait(false);
            var nextFrontier = new HashSet<string>();

            foreach (var edge in frontierEdges)
            {
                if (!visitedEdgeIds.Add(edge.Id))
                    continue;

                collectedEdges.Add(edge);
                if (collectedEdges.Count >= maxEdges)
                {
                    isTruncated = true;
                    break;
                }

                // Discover neighbors based on traversal direction
                if (direction != "incoming" && visitedNodes.Add(edge.TargetId))
                {
                    nextFrontier.Add(edge.TargetId);
                    if (visitedNodes.Count >= maxNodes) { isTruncated = true; break; }
                }
                if (!isTruncated && direction != "outgoing" && visitedNodes.Add(edge.SourceId))
                {
                    nextFrontier.Add(edge.SourceId);
                    if (visitedNodes.Count >= maxNodes) { isTruncated = true; break; }
                }
            }

            currentFrontier = nextFrontier;
        }

        // Fetch node data for all visited IDs
        var nodeMap = await FetchNodesByIdsAsync(conn, visitedNodes, ct).ConfigureAwait(false);
        var collectedNodes = new List<GraphNode>(nodeMap.Count);
        foreach (var id in visitedNodes)
        {
            if (nodeMap.TryGetValue(id, out var node))
                collectedNodes.Add(node);
        }

        // Prune edges whose endpoints don't have node data in the response
        if (isTruncated || nodeMap.Count < visitedNodes.Count)
        {
            collectedEdges = collectedEdges
                .Where(e => nodeMap.ContainsKey(e.SourceId) && nodeMap.ContainsKey(e.TargetId))
                .ToList();
        }

        if (isTruncated)
        {
            _logger.Warning("BFS truncated from {StartId}: {NodeCount} nodes, {EdgeCount} edges (caps: nodes={MaxNodes}, edges={MaxEdges})",
                startId, collectedNodes.Count, collectedEdges.Count, maxNodes, maxEdges);
        }

        return (collectedNodes, collectedEdges, isTruncated);
    }

    /// <summary>
    /// Find all paths between two nodes using BFS with on-demand neighbor loading.
    /// Neighbors are fetched per node and cached to avoid repeated queries.
    /// </summary>
    private async Task<(List<GraphPath> Paths, bool IsTruncated)> FindAllPathsAsync(
        SqliteConnection conn, string sourceId, string targetId, int maxDepth, CancellationToken ct = default)
    {
        const int maxPaths = 10;
        var maxQueueSize = ValidationLimits.MaxBfsQueueSize;
        var isTruncated = false;

        var adjacencyCache = new Dictionary<string, List<string>>();

        var paths = new List<GraphPath>();
        var queue = new Queue<(string NodeId, List<string> Path, HashSet<string> Visited)>();
        queue.Enqueue((sourceId, new List<string> { sourceId }, new HashSet<string> { sourceId }));

        while (queue.Count > 0 && paths.Count < maxPaths)
        {
            ct.ThrowIfCancellationRequested();

            if (queue.Count > maxQueueSize)
            {
                isTruncated = true;
                break;
            }

            var (currentId, currentPath, visited) = queue.Dequeue();

            if (currentPath.Count - 1 > maxDepth)
                continue;

            if (currentId == targetId && currentPath.Count > 1)
            {
                paths.Add(new GraphPath(currentPath.ToList(), currentPath.Count - 1));
                continue;
            }

            if (currentPath.Count - 1 >= maxDepth)
                continue;

            if (!adjacencyCache.TryGetValue(currentId, out var neighbors))
            {
                neighbors = await FetchOutgoingNeighborIdsAsync(conn, currentId, ct).ConfigureAwait(false);
                adjacencyCache[currentId] = neighbors;
            }

            foreach (var neighborId in neighbors)
            {
                // Allow reaching the target even if visited, but prevent other cycles
                if (visited.Contains(neighborId) && neighborId != targetId)
                    continue;

                var newPath = new List<string>(currentPath) { neighborId };
                var newVisited = new HashSet<string>(visited) { neighborId };
                queue.Enqueue((neighborId, newPath, newVisited));
            }
        }

        if (isTruncated)
        {
            _logger.Warning("FindAllPaths truncated {Source}->{Target}: {PathCount} paths found (queue cap={MaxQueue})",
                sourceId, targetId, paths.Count, maxQueueSize);
        }

        return (paths, isTruncated);
    }

    /// <summary>
    /// Build impact paths from the start node to each reachable node using BFS parent tracking.
    /// </summary>
    private static List<GraphPath> BuildImpactPaths(
        string startId, List<GraphNode> nodes, List<GraphEdge> edges, string direction, CancellationToken ct = default)
    {
        // Build adjacency map from collected edges
        var adjacency = new Dictionary<string, List<string>>();
        foreach (var edge in edges)
        {
            if (direction == "both" || direction == "outgoing")
            {
                AddToAdjacency(adjacency, edge.SourceId, edge.TargetId);
            }
            if (direction == "both" || direction == "incoming")
            {
                AddToAdjacency(adjacency, edge.TargetId, edge.SourceId);
            }
        }

        // BFS to reconstruct paths from startId to each node
        var paths = new List<GraphPath>();
        var parentMap = new Dictionary<string, string>();
        var visited = new HashSet<string> { startId };
        var queue = new Queue<string>();
        queue.Enqueue(startId);

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            var current = queue.Dequeue();
            if (adjacency.TryGetValue(current, out var neighbors))
            {
                foreach (var neighbor in neighbors)
                {
                    if (visited.Add(neighbor))
                    {
                        parentMap[neighbor] = current;
                        queue.Enqueue(neighbor);
                    }
                }
            }
        }

        // Reconstruct path for each impacted node
        foreach (var node in nodes)
        {
            if (node.Id == startId) continue;
            if (!parentMap.ContainsKey(node.Id)) continue;

            var path = new List<string>();
            var current = node.Id;
            while (current != null)
            {
                path.Add(current);
                parentMap.TryGetValue(current, out current!);
            }
            path.Reverse();
            paths.Add(new GraphPath(path, path.Count - 1));
        }

        return paths;
    }

    private static void AddToAdjacency(Dictionary<string, List<string>> adj, string from, string to)
    {
        if (!adj.TryGetValue(from, out var list))
        {
            list = new List<string>();
            adj[from] = list;
        }
        list.Add(to);
    }

    #endregion

    #region Data Access Helpers

    /// <summary>Fetch nodes by a set of IDs, batched to avoid SQL parameter limits.</summary>
    private static async Task<Dictionary<string, GraphNode>> FetchNodesByIdsAsync(
        SqliteConnection conn, HashSet<string> ids, CancellationToken ct)
    {
        var result = new Dictionary<string, GraphNode>(ids.Count);
        var idList = new List<string>(ids);

        for (int batch = 0; batch < idList.Count; batch += ValidationLimits.MaxSqlParameters)
        {
            ct.ThrowIfCancellationRequested();
            var batchSize = Math.Min(ValidationLimits.MaxSqlParameters, idList.Count - batch);
            using var cmd = conn.CreateCommand();
            var sb = new StringBuilder();
            for (int i = 0; i < batchSize; i++)
            {
                if (i > 0) sb.Append(", ");
                var p = $"$n{i}";
                sb.Append(p);
                cmd.Parameters.AddWithValue(p, idList[batch + i]);
            }
            cmd.CommandText = $"SELECT Id, Type, Label, Properties FROM GraphNodes WHERE Id IN ({sb})";
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var node = ReadNode(reader);
                result[node.Id] = node;
            }
        }
        return result;
    }

    /// <summary>Fetch edges touching frontier nodes in the specified direction.</summary>
    private static async Task<List<GraphEdge>> FetchEdgesForFrontierAsync(
        SqliteConnection conn, HashSet<string> frontier, string direction,
        ISet<string>? allowedEdgeTypes, CancellationToken ct)
    {
        if (direction is not ("outgoing" or "incoming" or "both"))
            throw new ArgumentException($"Invalid direction '{direction}'. Must be 'outgoing', 'incoming', or 'both'.", nameof(direction));

        var edges = new List<GraphEdge>();
        var frontierList = new List<string>(frontier);

        for (int batch = 0; batch < frontierList.Count; batch += ValidationLimits.MaxSqlParameters)
        {
            ct.ThrowIfCancellationRequested();
            var batchSize = Math.Min(ValidationLimits.MaxSqlParameters, frontierList.Count - batch);
            using var cmd = conn.CreateCommand();
            var idParams = new StringBuilder();
            for (int i = 0; i < batchSize; i++)
            {
                if (i > 0) idParams.Append(", ");
                var p = $"$n{i}";
                idParams.Append(p);
                cmd.Parameters.AddWithValue(p, frontierList[batch + i]);
            }
            var idList = idParams.ToString();

            var sql = new StringBuilder("SELECT Id, SourceId, TargetId, Type, Label, Properties FROM GraphEdges WHERE ");
            if (direction == "outgoing")
                sql.Append($"SourceId IN ({idList})");
            else if (direction == "incoming")
                sql.Append($"TargetId IN ({idList})");
            else
                sql.Append($"(SourceId IN ({idList}) OR TargetId IN ({idList}))");

            // Edge type filter
            if (allowedEdgeTypes is { Count: > 0 })
            {
                if (allowedEdgeTypes.Count == 1)
                {
                    sql.Append(" AND Type = $edgeType");
                    cmd.Parameters.AddWithValue("$edgeType", allowedEdgeTypes.First());
                }
                else
                {
                    sql.Append(" AND Type IN (");
                    int idx = 0;
                    foreach (var et in allowedEdgeTypes)
                    {
                        if (idx > 0) sql.Append(", ");
                        var p = $"$et{idx}";
                        sql.Append(p);
                        cmd.Parameters.AddWithValue(p, et);
                        idx++;
                    }
                    sql.Append(')');
                }
            }

            cmd.CommandText = sql.ToString();
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                edges.Add(ReadEdge(reader));
        }
        return edges;
    }

    /// <summary>Fetch outgoing neighbor IDs for a single node.</summary>
    private static async Task<List<string>> FetchOutgoingNeighborIdsAsync(
        SqliteConnection conn, string nodeId, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT TargetId FROM GraphEdges WHERE SourceId = $id";
        cmd.Parameters.AddWithValue("$id", nodeId);
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<string>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            result.Add(reader.GetString(0));
        return result;
    }

    #endregion
}
