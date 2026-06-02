using System.Diagnostics;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.Data.Sqlite;
using RoslynMcp.Shared;
using RoslynMcp.Shared.Contracts.Graph;

namespace RoslynMcp.Core.Services;

public partial class GraphService
{
    // ── 8. graph_visualize ──

    public async Task<Result<GraphVisualizeResponse>> VisualizeAsync(GraphVisualizeRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            if (request.MaxNodes <= 0 || request.MaxNodes > 10000)
                return Result<GraphVisualizeResponse>.Fail("MaxNodes must be between 1 and 10000");

            await using var lease = await _pool.AcquireReaderAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var conn = lease.Connection;

            List<GraphNode> nodes;
            List<GraphEdge> edges;

            if (request.NodeIds is { Count: > 0 })
            {
                (nodes, edges) = await GetSubgraphForNodesAsync(conn, request.NodeIds, request.MaxNodes, ct).ConfigureAwait(false);
            }
            else
            {
                (nodes, edges) = await GetAllNodesAndEdgesAsync(conn, request.MaxNodes, ct).ConfigureAwait(false);
            }

            string content;
            string format = request.Format.ToLowerInvariant();

            if (format == "dot")
            {
                content = GenerateDot(nodes, edges);
            }
            else
            {
                format = "mermaid";
                content = GenerateMermaid(nodes, edges);
            }

            return new GraphVisualizeResponse(content, format, nodes.Count, edges.Count);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Error(ex, "graph_visualize failed");
            return Result<GraphVisualizeResponse>.Fail(ex.Message);
        }
    }

    // ── 9. graph_stats ──

    public async Task<Result<GraphStatsResponse>> StatsAsync(GraphStatsRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await using var lease = await _pool.AcquireReaderAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var conn = lease.Connection;

            int nodeCount = 0, edgeCount = 0;

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM GraphNodes";
                nodeCount = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM GraphEdges";
                edgeCount = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
            }

            var nodeTypes = new List<string>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT DISTINCT Type FROM GraphNodes";
                using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    nodeTypes.Add(reader.GetString(0));
                }
            }

            var edgeTypes = new List<string>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT DISTINCT Type FROM GraphEdges";
                using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    edgeTypes.Add(reader.GetString(0));
                }
            }

            return new GraphStatsResponse(nodeCount, edgeCount, nodeTypes, edgeTypes, IsStale);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Error(ex, "graph_stats failed");
            return Result<GraphStatsResponse>.Fail(ex.Message);
        }
    }

    // ── 10. graph_rebuild ──

    public async Task<Result<GraphRebuildResponse>> RebuildAsync(GraphRebuildRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            if (_workspace is null)
                return Result<GraphRebuildResponse>.Fail("No workspace provider available");

            var sw = Stopwatch.StartNew();

            var solution = _workspace.CurrentSolution;
            if (solution is null)
                return Result<GraphRebuildResponse>.Fail("No solution currently loaded");

            // Build node and edge data in memory first, outside the writer lock,
            // so that reads are not blocked during solution traversal.
            var nodeData = new List<(string Id, string Type, string Label)>();
            var edgeData = new List<(string SourceId, string TargetId, string Type)>();

            foreach (var project in solution.Projects)
            {
                ct.ThrowIfCancellationRequested();
                nodeData.Add((project.Id.Id.ToString(), "Project", project.Name));

                foreach (var projectRef in project.ProjectReferences)
                {
                    edgeData.Add((project.Id.Id.ToString(), projectRef.ProjectId.Id.ToString(), "ProjectReference"));
                }
            }

            // Acquire writer lock only for the database writes
            await using var lease = await _pool.AcquireWriterAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var conn = lease.Connection;

            int nodeCount = 0;
            int edgeCount = 0;

            var snapshot = Interlocked.Read(ref _mutationVersion);

            using var tx = conn.BeginTransaction();
            try
            {
                if (request.FullRebuild)
                {
                    using var delEdges = conn.CreateCommand();
                    delEdges.Transaction = tx;
                    delEdges.CommandText = "DELETE FROM GraphEdges";
                    await delEdges.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                    using var delNodes = conn.CreateCommand();
                    delNodes.Transaction = tx;
                    delNodes.CommandText = "DELETE FROM GraphNodes";
                    await delNodes.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                // Reuse a single command for all node inserts
                using (var nodeCmd = conn.CreateCommand())
                {
                    nodeCmd.Transaction = tx;
                    nodeCmd.CommandText = "INSERT OR IGNORE INTO GraphNodes (Id, Type, Label, Properties) VALUES ($id, $type, $label, $properties)";
                    var pId = nodeCmd.Parameters.Add("$id", SqliteType.Text);
                    var pType = nodeCmd.Parameters.Add("$type", SqliteType.Text);
                    var pLabel = nodeCmd.Parameters.Add("$label", SqliteType.Text);
                    nodeCmd.Parameters.AddWithValue("$properties", (object)DBNull.Value);

                    foreach (var (id, type, label) in nodeData)
                    {
                        pId.Value = id;
                        pType.Value = type;
                        pLabel.Value = label;
                        nodeCount += await nodeCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }
                }

                // Reuse a single command for all edge inserts
                using (var edgeCmd = conn.CreateCommand())
                {
                    edgeCmd.Transaction = tx;
                    edgeCmd.CommandText = """
                        INSERT OR IGNORE INTO GraphEdges (SourceId, TargetId, Type, Label, Properties)
                        VALUES ($sourceId, $targetId, $type, $label, $properties)
                        """;
                    var pSourceId = edgeCmd.Parameters.Add("$sourceId", SqliteType.Text);
                    var pTargetId = edgeCmd.Parameters.Add("$targetId", SqliteType.Text);
                    var pEdgeType = edgeCmd.Parameters.Add("$type", SqliteType.Text);
                    edgeCmd.Parameters.AddWithValue("$label", (object)DBNull.Value);
                    edgeCmd.Parameters.AddWithValue("$properties", (object)DBNull.Value);

                    foreach (var (sourceId, targetId, type) in edgeData)
                    {
                        pSourceId.Value = sourceId;
                        pTargetId.Value = targetId;
                        pEdgeType.Value = type;
                        edgeCount += await edgeCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }
                }

                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                _logger.Error("Graph rebuild failed, rolled back transaction");
                throw;
            }

            sw.Stop();
            Interlocked.Exchange(ref _rebuiltVersion, snapshot);

            var duration = sw.Elapsed.TotalSeconds < 1
                ? $"{sw.Elapsed.TotalMilliseconds:F0}ms"
                : $"{sw.Elapsed.TotalSeconds:F2}s";

            _logger.Information("graph_rebuild: {Nodes} nodes, {Edges} edges in {Duration}",
                nodeCount, edgeCount, duration);
            return new GraphRebuildResponse(nodeCount, edgeCount, duration);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Error(ex, "graph_rebuild failed");
            return Result<GraphRebuildResponse>.Fail(ex.Message);
        }
    }

    #region Utility Helpers

    private async Task<(List<GraphNode> Nodes, List<GraphEdge> Edges)> GetSubgraphForNodesAsync(
        SqliteConnection conn, IReadOnlyList<string> nodeIds, int maxNodes, CancellationToken ct = default)
    {
        // Batch-load nodes
        var nodes = new List<GraphNode>();
        for (int batch = 0; batch < nodeIds.Count && nodes.Count < maxNodes; batch += ValidationLimits.MaxSqlParameters)
        {
            var batchIds = nodeIds.Skip(batch).Take(Math.Min(ValidationLimits.MaxSqlParameters, maxNodes - nodes.Count)).ToList();
            using var cmd = conn.CreateCommand();
            var placeholders = new StringBuilder();
            for (int i = 0; i < batchIds.Count; i++)
            {
                if (i > 0) placeholders.Append(", ");
                var paramName = $"$n{i}";
                placeholders.Append(paramName);
                cmd.Parameters.AddWithValue(paramName, batchIds[i]);
            }
            cmd.CommandText = $"SELECT Id, Type, Label, Properties FROM GraphNodes WHERE Id IN ({placeholders})";
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                nodes.Add(ReadNode(reader));
            }
        }

        // Batch-load edges between the requested nodes
        var edges = new List<GraphEdge>();
        if (nodes.Count > 0)
        {
            var nodeIdSet = new HashSet<string>(nodes.Select(n => n.Id), StringComparer.Ordinal);
            for (int batch = 0; batch < nodes.Count; batch += ValidationLimits.MaxSqlParameters)
            {
                var batchNodes = nodes.Skip(batch).Take(ValidationLimits.MaxSqlParameters).ToList();
                using var cmd = conn.CreateCommand();
                var placeholders = new StringBuilder();
                for (int i = 0; i < batchNodes.Count; i++)
                {
                    if (i > 0) placeholders.Append(", ");
                    var paramName = $"$n{i}";
                    placeholders.Append(paramName);
                    cmd.Parameters.AddWithValue(paramName, batchNodes[i].Id);
                }
                cmd.CommandText = $"SELECT Id, SourceId, TargetId, Type, Label, Properties FROM GraphEdges WHERE SourceId IN ({placeholders})";
                using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var edge = ReadEdge(reader);
                    if (nodeIdSet.Contains(edge.TargetId))
                        edges.Add(edge);
                }
            }
        }

        return (nodes, edges);
    }

    private async Task<(List<GraphNode> Nodes, List<GraphEdge> Edges)> GetAllNodesAndEdgesAsync(
        SqliteConnection conn, int maxNodes, CancellationToken ct = default)
    {
        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, Type, Label, Properties FROM GraphNodes LIMIT $limit";
            cmd.Parameters.AddWithValue("$limit", maxNodes);

            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                nodes.Add(ReadNode(reader));
            }
        }

        if (nodes.Count > 0)
        {
            var nodeIdSet = new HashSet<string>(nodes.Select(n => n.Id), StringComparer.Ordinal);
            for (int batch = 0; batch < nodes.Count; batch += ValidationLimits.MaxSqlParameters)
            {
                var batchNodes = nodes.Skip(batch).Take(ValidationLimits.MaxSqlParameters).ToList();
                using var cmd = conn.CreateCommand();
                var placeholders = new StringBuilder();
                for (int i = 0; i < batchNodes.Count; i++)
                {
                    if (i > 0) placeholders.Append(", ");
                    var paramName = $"$n{i}";
                    placeholders.Append(paramName);
                    cmd.Parameters.AddWithValue(paramName, batchNodes[i].Id);
                }
                cmd.CommandText = $"SELECT Id, SourceId, TargetId, Type, Label, Properties FROM GraphEdges WHERE SourceId IN ({placeholders})";
                using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var edge = ReadEdge(reader);
                    if (nodeIdSet.Contains(edge.TargetId))
                        edges.Add(edge);
                }
            }
        }

        return (nodes, edges);
    }

    private static string GenerateMermaid(List<GraphNode> nodes, List<GraphEdge> edges)
    {
        var sb = new StringBuilder();
        sb.AppendLine("graph TD");

        foreach (var node in nodes)
        {
            var label = EscapeMermaid(node.Label ?? node.Id);
            sb.AppendLine($"  {SanitizeMermaidId(node.Id)}[\"{label}\"]");
        }

        foreach (var edge in edges)
        {
            var sourceId = SanitizeMermaidId(edge.SourceId);
            var targetId = SanitizeMermaidId(edge.TargetId);

            if (!string.IsNullOrEmpty(edge.Label))
                sb.AppendLine($"  {sourceId} -->|\"{EscapeMermaid(edge.Label!)}\"|{targetId}");
            else
                sb.AppendLine($"  {sourceId} --> {targetId}");
        }

        return sb.ToString().TrimEnd();
    }

    private static string GenerateDot(List<GraphNode> nodes, List<GraphEdge> edges)
    {
        var sb = new StringBuilder();
        sb.AppendLine("digraph G {");

        foreach (var node in nodes)
        {
            var label = EscapeDot(node.Label ?? node.Id);
            sb.AppendLine($"  \"{EscapeDot(node.Id)}\" [label=\"{label}\"];");
        }

        foreach (var edge in edges)
        {
            var label = edge.Type;
            sb.AppendLine($"  \"{EscapeDot(edge.SourceId)}\" -> \"{EscapeDot(edge.TargetId)}\" [label=\"{EscapeDot(label)}\"];");
        }

        sb.AppendLine("}");
        return sb.ToString().TrimEnd();
    }

    private static GraphNode ReadNode(SqliteDataReader reader)
    {
        return new GraphNode(
            Id: reader.GetString(0),
            Type: reader.GetString(1),
            Label: reader.IsDBNull(2) ? null : reader.GetString(2),
            Properties: reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    private static GraphEdge ReadEdge(SqliteDataReader reader)
    {
        return new GraphEdge(
            Id: reader.GetInt64(0),
            SourceId: reader.GetString(1),
            TargetId: reader.GetString(2),
            Type: reader.GetString(3),
            Label: reader.IsDBNull(4) ? null : reader.GetString(4),
            Properties: reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    private static string SanitizeMermaidId(string id)
    {
        // Mermaid IDs must be alphanumeric/underscores; replace other chars
        var sb = new StringBuilder(id.Length);
        foreach (var c in id)
        {
            sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        }
        return sb.ToString();
    }

    private static string EscapeMermaid(string text)
    {
        // Mermaid treats many characters as syntax when inside labels.
        // By wrapping labels in double-quotes (done by callers), we only need
        // to escape the quote character itself and collapse newlines.
        // Also escape #, which Mermaid interprets as a special entity prefix.
        // Note: # must be escaped first, before " is replaced with #quot;
        return text
            .Replace("#", "#35;")
            .Replace("\"", "#quot;")
            .Replace("\n", " ");
    }

    private static string EscapeDot(string text)
    {
        return text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
    }

    #endregion
}
