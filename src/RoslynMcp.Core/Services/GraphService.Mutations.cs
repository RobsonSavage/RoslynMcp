using RoslynMcp.Shared;
using RoslynMcp.Shared.Contracts.Graph;

namespace RoslynMcp.Core.Services;

public partial class GraphService
{
    // ── 1. graph_add_node ──

    public async Task<Result<GraphAddNodeResponse>> AddNodeAsync(GraphAddNodeRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await using var lease = await _pool.AcquireWriterAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var conn = lease.Connection;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO GraphNodes (Id, Type, Label, Properties) VALUES ($id, $type, $label, $properties)";
            cmd.Parameters.AddWithValue("$id", request.Id);
            cmd.Parameters.AddWithValue("$type", request.Type);
            cmd.Parameters.AddWithValue("$label", request.Label ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$properties", request.Properties ?? (object)DBNull.Value);

            var rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (rows > 0)
                Interlocked.Increment(ref _mutationVersion);

            return new GraphAddNodeResponse(request.Id, Created: rows > 0);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Error(ex, "graph_add_node failed for {Id}", request.Id);
            return Result<GraphAddNodeResponse>.Fail(ex.Message);
        }
    }

    // ── 2. graph_add_edge ──

    public async Task<Result<GraphAddEdgeResponse>> AddEdgeAsync(GraphAddEdgeRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await using var lease = await _pool.AcquireWriterAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var conn = lease.Connection;

            // Validate that both source and target nodes exist
            using (var checkCmd = conn.CreateCommand())
            {
                checkCmd.CommandText = "SELECT COUNT(*) FROM GraphNodes WHERE Id IN ($sourceId, $targetId)";
                checkCmd.Parameters.AddWithValue("$sourceId", request.SourceId);
                checkCmd.Parameters.AddWithValue("$targetId", request.TargetId);
                var foundCount = Convert.ToInt32(await checkCmd.ExecuteScalarAsync(ct).ConfigureAwait(false));

                if (foundCount < 2)
                {
                    // Determine which node(s) are missing for a clear error message
                    var missing = new List<string>(2);
                    using var detailCmd = conn.CreateCommand();
                    detailCmd.CommandText = "SELECT Id FROM GraphNodes WHERE Id IN ($sourceId, $targetId)";
                    detailCmd.Parameters.AddWithValue("$sourceId", request.SourceId);
                    detailCmd.Parameters.AddWithValue("$targetId", request.TargetId);
                    var existingIds = new HashSet<string>();
                    using var reader = await detailCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                        existingIds.Add(reader.GetString(0));

                    if (!existingIds.Contains(request.SourceId))
                        missing.Add($"SourceId '{request.SourceId}'");
                    if (!existingIds.Contains(request.TargetId))
                        missing.Add($"TargetId '{request.TargetId}'");

                    return Result<GraphAddEdgeResponse>.Fail(
                        $"Cannot add edge: node(s) not found: {string.Join(", ", missing)}");
                }
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO GraphEdges (SourceId, TargetId, Type, Label, Properties)
                VALUES ($sourceId, $targetId, $type, $label, $properties)
                """;
            cmd.Parameters.AddWithValue("$sourceId", request.SourceId);
            cmd.Parameters.AddWithValue("$targetId", request.TargetId);
            cmd.Parameters.AddWithValue("$type", request.Type);
            cmd.Parameters.AddWithValue("$label", request.Label ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$properties", request.Properties ?? (object)DBNull.Value);

            var rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            long id = 0;
            if (rows > 0)
            {
                using var idCmd = conn.CreateCommand();
                idCmd.CommandText = "SELECT last_insert_rowid()";
                id = (long)(await idCmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;

                Interlocked.Increment(ref _mutationVersion);
            }

            return new GraphAddEdgeResponse(id, Created: rows > 0);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Error(ex, "graph_add_edge failed for {Source}->{Target}", request.SourceId, request.TargetId);
            return Result<GraphAddEdgeResponse>.Fail(ex.Message);
        }
    }

    // ── 3. graph_remove_node ──

    public async Task<Result<GraphRemoveNodeResponse>> RemoveNodeAsync(GraphRemoveNodeRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await using var lease = await _pool.AcquireWriterAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var conn = lease.Connection;

            int removedEdges = 0;

            if (request.Cascade)
            {
                using var edgeCmd = conn.CreateCommand();
                edgeCmd.CommandText = "DELETE FROM GraphEdges WHERE SourceId=$id OR TargetId=$id";
                edgeCmd.Parameters.AddWithValue("$id", request.Id);
                removedEdges = await edgeCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            else
            {
                // When Cascade=false, reject if connected edges exist to prevent orphaned edges
                using var countCmd = conn.CreateCommand();
                countCmd.CommandText = "SELECT COUNT(*) FROM GraphEdges WHERE SourceId=$id OR TargetId=$id";
                countCmd.Parameters.AddWithValue("$id", request.Id);
                var connectedEdgeCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
                if (connectedEdgeCount > 0)
                {
                    return Result<GraphRemoveNodeResponse>.Fail(
                        $"Cannot remove node '{request.Id}': {connectedEdgeCount} connected edge(s) exist. Use Cascade=true to also remove connected edges.");
                }
            }

            using var nodeCmd = conn.CreateCommand();
            nodeCmd.CommandText = "DELETE FROM GraphNodes WHERE Id=$id";
            nodeCmd.Parameters.AddWithValue("$id", request.Id);
            int removedNodes = await nodeCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            if (removedNodes + removedEdges > 0)
                Interlocked.Increment(ref _mutationVersion);

            return new GraphRemoveNodeResponse(removedNodes, removedEdges);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Error(ex, "graph_remove_node failed for {Id}", request.Id);
            return Result<GraphRemoveNodeResponse>.Fail(ex.Message);
        }
    }
}
