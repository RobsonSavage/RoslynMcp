using RoslynMcp.Core.Helpers;
using RoslynMcp.Core.Helpers.Migrations;
using RoslynMcp.Core.Services;
using RoslynMcp.Shared.Contracts.Graph;
using Serilog;
using Xunit;

namespace RoslynMcp.Core.Tests.Services;

public class GraphServiceTests : IAsyncDisposable, IAsyncLifetime
{
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();
    private readonly string _dbPath;
    private readonly SqliteConnectionPool _pool;
    private readonly GraphService _service;

    public GraphServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "RoslynMcpTests", Guid.NewGuid().ToString(), "test.db");
        _pool = new SqliteConnectionPool(_dbPath, logger: _logger);
        _service = new GraphService(_pool, _logger);
    }

    public async Task InitializeAsync()
    {
        var runner = new MigrationRunner(_pool, _dbPath,
            new IMigration[] { new V1_MemoryTables(), new V2_GraphTables() }, _logger);
        await runner.RunAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _pool.DisposeAsync();
        try { Directory.Delete(Path.GetDirectoryName(_dbPath)!, true); } catch { }
    }

    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    private async Task AddNodeAsync(string id, string type, string? label = null)
    {
        var result = await _service.AddNodeAsync(new GraphAddNodeRequest(id, type, Label: label));
        Assert.True(result.IsSuccess, result.Error?.Message);
    }

    private async Task AddEdgeAsync(string sourceId, string targetId, string type)
    {
        var result = await _service.AddEdgeAsync(new GraphAddEdgeRequest(sourceId, targetId, type));
        Assert.True(result.IsSuccess, result.Error?.Message);
    }

    // ────── 1. AddNode_CreatesNode ──────

    [Fact]
    public async Task AddNode_CreatesNode()
    {
        // First insert should create
        var result1 = await _service.AddNodeAsync(new GraphAddNodeRequest("A", "Class"));
        Assert.True(result1.IsSuccess, result1.Error?.Message);
        Assert.True(result1.Value!.Created);
        Assert.Equal("A", result1.Value.Id);

        // Duplicate insert should not create
        var result2 = await _service.AddNodeAsync(new GraphAddNodeRequest("A", "Class"));
        Assert.True(result2.IsSuccess, result2.Error?.Message);
        Assert.False(result2.Value!.Created);
    }

    // ────── 2. AddEdge_CreatesEdge ──────

    [Fact]
    public async Task AddEdge_CreatesEdge()
    {
        await AddNodeAsync("A", "Class");
        await AddNodeAsync("B", "Class");

        // First edge should create
        var result1 = await _service.AddEdgeAsync(new GraphAddEdgeRequest("A", "B", "depends"));
        Assert.True(result1.IsSuccess, result1.Error?.Message);
        Assert.True(result1.Value!.Created);
        Assert.True(result1.Value.Id > 0);

        // Duplicate edge should not create (UNIQUE constraint on SourceId, TargetId, Type)
        var result2 = await _service.AddEdgeAsync(new GraphAddEdgeRequest("A", "B", "depends"));
        Assert.True(result2.IsSuccess, result2.Error?.Message);
        Assert.False(result2.Value!.Created);
    }

    // ────── 3. RemoveNode_CascadesEdges ──────

    [Fact]
    public async Task RemoveNode_CascadesEdges()
    {
        await AddNodeAsync("A", "Class");
        await AddNodeAsync("B", "Class");
        await AddNodeAsync("C", "Class");
        await AddEdgeAsync("A", "B", "calls");
        await AddEdgeAsync("B", "C", "calls");

        // Remove B with cascade -- should remove B and both edges touching B
        var result = await _service.RemoveNodeAsync(new GraphRemoveNodeRequest("B", Cascade: true));
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(1, result.Value!.RemovedNodes);
        Assert.Equal(2, result.Value.RemovedEdges);
    }

    // ────── 4. QueryNeighbors_FindsAdjacentNodes ──────

    [Fact]
    public async Task QueryNeighbors_FindsAdjacentNodes()
    {
        await AddNodeAsync("A", "Class");
        await AddNodeAsync("B", "Class");
        await AddNodeAsync("C", "Class");
        await AddEdgeAsync("A", "B", "calls");
        await AddEdgeAsync("B", "C", "calls");

        var result = await _service.QueryNeighborsAsync(
            new GraphQueryNeighborsRequest("B", Direction: "both"));
        Assert.True(result.IsSuccess, result.Error?.Message);

        var nodeIds = result.Value!.Nodes.Select(n => n.Id).ToList();
        Assert.Contains("A", nodeIds);
        Assert.Contains("B", nodeIds);
        Assert.Contains("C", nodeIds);
    }

    // ────── 5. QueryPath_FindsShortestPath ──────

    [Fact]
    public async Task QueryPath_FindsShortestPath()
    {
        // Build: A->B->C and A->C (direct shortcut)
        await AddNodeAsync("A", "Class");
        await AddNodeAsync("B", "Class");
        await AddNodeAsync("C", "Class");
        await AddEdgeAsync("A", "B", "calls");
        await AddEdgeAsync("B", "C", "calls");
        await AddEdgeAsync("A", "C", "calls");

        var result = await _service.QueryPathAsync(
            new GraphQueryPathRequest("A", "C", MaxDepth: 5));
        Assert.True(result.IsSuccess, result.Error?.Message);

        // Should find at least 2 paths: A->C (length 1) and A->B->C (length 2)
        Assert.True(result.Value!.Paths.Count >= 2,
            $"Expected at least 2 paths, got {result.Value.Paths.Count}");

        // Shortest path should be length 1 (direct A->C)
        Assert.NotNull(result.Value.ShortestLength);
        Assert.Equal(1, result.Value.ShortestLength);
    }

    // ────── 6. QuerySubgraph_ReturnsConnectedNodes ──────

    [Fact]
    public async Task QuerySubgraph_ReturnsConnectedNodes()
    {
        // Connected component: A->B->C
        await AddNodeAsync("A", "Class");
        await AddNodeAsync("B", "Class");
        await AddNodeAsync("C", "Class");
        await AddEdgeAsync("A", "B", "calls");
        await AddEdgeAsync("B", "C", "calls");

        // Disconnected component: D->E
        await AddNodeAsync("D", "Interface");
        await AddNodeAsync("E", "Interface");
        await AddEdgeAsync("D", "E", "calls");

        var result = await _service.QuerySubgraphAsync(
            new GraphQuerySubgraphRequest("A", Depth: 2));
        Assert.True(result.IsSuccess, result.Error?.Message);

        var nodeIds = result.Value!.Nodes.Select(n => n.Id).ToList();
        Assert.Contains("A", nodeIds);
        Assert.Contains("B", nodeIds);
        Assert.Contains("C", nodeIds);

        // D and E should not be in the subgraph rooted at A
        Assert.DoesNotContain("D", nodeIds);
        Assert.DoesNotContain("E", nodeIds);
    }

    // ────── 7. Impact_FollowsOutgoingEdges ──────

    [Fact]
    public async Task Impact_FollowsOutgoingEdges()
    {
        // Build chain: A->B->C->D
        await AddNodeAsync("A", "Class");
        await AddNodeAsync("B", "Class");
        await AddNodeAsync("C", "Class");
        await AddNodeAsync("D", "Class");
        await AddEdgeAsync("A", "B", "calls");
        await AddEdgeAsync("B", "C", "calls");
        await AddEdgeAsync("C", "D", "calls");

        var result = await _service.ImpactAsync(
            new GraphImpactRequest("A", Direction: "outgoing"));
        Assert.True(result.IsSuccess, result.Error?.Message);

        var impactedIds = result.Value!.ImpactedNodes.Select(n => n.Id).ToList();
        Assert.Contains("B", impactedIds);
        Assert.Contains("C", impactedIds);
        Assert.Contains("D", impactedIds);

        // The start node should not be in impacted nodes
        Assert.DoesNotContain("A", impactedIds);
    }

    // ────── 8. Visualize_GeneratesMermaid ──────

    [Fact]
    public async Task Visualize_GeneratesMermaid()
    {
        await AddNodeAsync("A", "Class", label: "ClassA");
        await AddNodeAsync("B", "Class", label: "ClassB");
        await AddEdgeAsync("A", "B", "depends");

        var result = await _service.VisualizeAsync(
            new GraphVisualizeRequest(Format: "mermaid"));
        Assert.True(result.IsSuccess, result.Error?.Message);

        var content = result.Value!.Content;
        Assert.Contains("graph TD", content);
        Assert.Contains("A", content);
        Assert.Contains("B", content);
        Assert.Equal("mermaid", result.Value.Format);
        Assert.Equal(2, result.Value.NodeCount);
        Assert.Equal(1, result.Value.EdgeCount);
    }

    // ────── 9. Stats_ReturnsCorrectCounts ──────

    [Fact]
    public async Task Stats_ReturnsCorrectCounts()
    {
        // 3 nodes: 2 Class, 1 Interface
        await AddNodeAsync("A", "Class");
        await AddNodeAsync("B", "Class");
        await AddNodeAsync("C", "Interface");

        // 2 edges: both type "calls"
        await AddEdgeAsync("A", "B", "calls");
        await AddEdgeAsync("B", "C", "calls");

        var result = await _service.StatsAsync(new GraphStatsRequest());
        Assert.True(result.IsSuccess, result.Error?.Message);

        Assert.Equal(3, result.Value!.NodeCount);
        Assert.Equal(2, result.Value.EdgeCount);
        Assert.Equal(2, result.Value.NodeTypes.Count);
        Assert.Contains("Class", result.Value.NodeTypes);
        Assert.Contains("Interface", result.Value.NodeTypes);
        Assert.Single(result.Value.EdgeTypes);
        Assert.Contains("calls", result.Value.EdgeTypes);
        Assert.True(result.Value.IsStale);
    }

    // ────── 10. Rebuild_WithoutWorkspace_ReturnsError ──────

    [Fact]
    public async Task Rebuild_WithoutWorkspace_ReturnsError()
    {
        // Service was created without IWorkspaceProvider (null)
        var result = await _service.RebuildAsync(new GraphRebuildRequest());

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.Contains("No workspace provider", result.Error!.Message);
    }

    // ────── 11. Existing tests verify IsTruncated defaults to false ──────

    [Fact]
    public async Task QueryNeighbors_SmallGraph_IsTruncatedFalse()
    {
        await AddNodeAsync("A", "Class");
        await AddNodeAsync("B", "Class");
        await AddEdgeAsync("A", "B", "calls");

        var result = await _service.QueryNeighborsAsync(
            new GraphQueryNeighborsRequest("A", Direction: "both"));
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.False(result.Value!.IsTruncated);
    }

    // ────── 12. BFS truncation with small cap ──────

    [Fact]
    public async Task QuerySubgraph_ExceedsNodeCap_IsTruncatedTrue()
    {
        // Service with maxBfsNodes=3
        var cappedService = new GraphService(_pool, _logger, maxBfsNodes: 3);

        // Build chain: N0->N1->N2->N3->N4->N5 (6 nodes, needs cap at 3)
        for (int i = 0; i <= 5; i++)
            await AddNodeAsync($"N{i}", "Class");
        for (int i = 0; i < 5; i++)
            await AddEdgeAsync($"N{i}", $"N{i + 1}", "calls");

        var result = await cappedService.QuerySubgraphAsync(
            new GraphQuerySubgraphRequest("N0", Depth: 10));
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(result.Value!.IsTruncated);
        Assert.True(result.Value.Nodes.Count <= 3, $"Expected <=3 nodes, got {result.Value.Nodes.Count}");
    }

    // ────── 13. Edge consistency on truncation ──────

    [Fact]
    public async Task QuerySubgraph_Truncated_EdgesOnlyReferencesCollectedNodes()
    {
        var cappedService = new GraphService(_pool, _logger, maxBfsNodes: 3);

        // Build chain: X0->X1->X2->X3->X4
        for (int i = 0; i <= 4; i++)
            await AddNodeAsync($"X{i}", "Class");
        for (int i = 0; i < 4; i++)
            await AddEdgeAsync($"X{i}", $"X{i + 1}", "calls");

        var result = await cappedService.QuerySubgraphAsync(
            new GraphQuerySubgraphRequest("X0", Depth: 10));
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(result.Value!.IsTruncated);

        var nodeIds = new HashSet<string>(result.Value.Nodes.Select(n => n.Id));
        foreach (var edge in result.Value.Edges)
        {
            Assert.True(nodeIds.Contains(edge.SourceId),
                $"Dangling edge: SourceId={edge.SourceId} not in collected nodes");
            Assert.True(nodeIds.Contains(edge.TargetId),
                $"Dangling edge: TargetId={edge.TargetId} not in collected nodes");
        }
    }

    // ────── 14. BFS cancellation ──────

    [Fact]
    public async Task QuerySubgraph_CancelledToken_ThrowsOperationCancelled()
    {
        await AddNodeAsync("C1", "Class");
        await AddNodeAsync("C2", "Class");
        await AddEdgeAsync("C1", "C2", "calls");

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // pre-cancelled

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _service.QuerySubgraphAsync(new GraphQuerySubgraphRequest("C1", Depth: 2), cts.Token));
    }

    // ────── 15. Path truncation signaling ──────

    [Fact]
    public async Task QueryPath_SmallGraph_IsTruncatedFalse()
    {
        await AddNodeAsync("P1", "Class");
        await AddNodeAsync("P2", "Class");
        await AddEdgeAsync("P1", "P2", "calls");

        var result = await _service.QueryPathAsync(
            new GraphQueryPathRequest("P1", "P2", MaxDepth: 5));
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.False(result.Value!.IsTruncated);
    }

    // ────── 16. Impact truncation propagation ──────

    [Fact]
    public async Task Impact_ExceedsNodeCap_IsTruncatedTrue()
    {
        var cappedService = new GraphService(_pool, _logger, maxBfsNodes: 3);

        // Build chain: I0->I1->I2->I3->I4->I5
        for (int i = 0; i <= 5; i++)
            await AddNodeAsync($"I{i}", "Class");
        for (int i = 0; i < 5; i++)
            await AddEdgeAsync($"I{i}", $"I{i + 1}", "calls");

        var result = await cappedService.ImpactAsync(
            new GraphImpactRequest("I0", Direction: "outgoing"));
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(result.Value!.IsTruncated);
    }

    // ────── 17. AddNode_SqlInjectionAttempt_StoresLiteralString ──────

    [Fact]
    public async Task AddNode_SqlInjectionAttempt_StoresLiteralString()
    {
        var maliciousId = "'; DROP TABLE GraphNodes; --";
        var result = await _service.AddNodeAsync(new GraphAddNodeRequest(maliciousId, "Class"));
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(result.Value!.Created);

        // Verify GraphNodes table still exists via stats
        var statsResult = await _service.StatsAsync(new GraphStatsRequest());
        Assert.True(statsResult.IsSuccess, "GraphNodes table should still exist after injection attempt");
        Assert.True(statsResult.Value!.NodeCount >= 1);
    }

    // ────── 18. Visualize_GeneratesDotFormat ──────

    [Fact]
    public async Task Visualize_GeneratesDotFormat()
    {
        await AddNodeAsync("D1", "Class", label: "MyClass");
        await AddNodeAsync("D2", "Interface", label: "IMyInterface");
        await AddEdgeAsync("D1", "D2", "implements");

        var result = await _service.VisualizeAsync(
            new GraphVisualizeRequest(Format: "dot"));
        Assert.True(result.IsSuccess, result.Error?.Message);

        var content = result.Value!.Content;
        Assert.Equal("dot", result.Value.Format);
        Assert.Contains("digraph G {", content);
        Assert.Contains("MyClass", content);
        Assert.Contains("IMyInterface", content);
        Assert.Contains("implements", content);
        Assert.Equal(2, result.Value.NodeCount);
        Assert.Equal(1, result.Value.EdgeCount);
    }

    // ────── 19. QueryNeighbors_EdgeTypeFilter ──────

    [Fact]
    public async Task QueryNeighbors_EdgeTypeFilter_FiltersCorrectly()
    {
        await AddNodeAsync("F1", "Class");
        await AddNodeAsync("F2", "Class");
        await AddNodeAsync("F3", "Class");
        await AddEdgeAsync("F1", "F2", "calls");
        await AddEdgeAsync("F1", "F3", "implements");

        // Query with edge type filter "calls" should only find F2
        var result = await _service.QueryNeighborsAsync(
            new GraphQueryNeighborsRequest("F1", Direction: "outgoing", EdgeType: "calls"));
        Assert.True(result.IsSuccess, result.Error?.Message);

        var nodeIds = result.Value!.Nodes.Select(n => n.Id).ToList();
        Assert.Contains("F1", nodeIds);
        Assert.Contains("F2", nodeIds);
        Assert.DoesNotContain("F3", nodeIds);
    }

    // ────── 20. AddEdge_NonExistentNodes_Fails ──────

    [Fact]
    public async Task AddEdge_NonExistentSourceNode_ReturnsError()
    {
        await AddNodeAsync("Exists", "Class");

        var result = await _service.AddEdgeAsync(
            new GraphAddEdgeRequest("NonExistent", "Exists", "calls"));
        Assert.False(result.IsSuccess);
        Assert.Contains("SourceId", result.Error!.Message);
        Assert.Contains("not found", result.Error.Message);
    }

    [Fact]
    public async Task AddEdge_NonExistentTargetNode_ReturnsError()
    {
        await AddNodeAsync("Exists", "Class");

        var result = await _service.AddEdgeAsync(
            new GraphAddEdgeRequest("Exists", "NonExistent", "calls"));
        Assert.False(result.IsSuccess);
        Assert.Contains("TargetId", result.Error!.Message);
        Assert.Contains("not found", result.Error.Message);
    }

    [Fact]
    public async Task AddEdge_BothNodesNonExistent_ReturnsError()
    {
        var result = await _service.AddEdgeAsync(
            new GraphAddEdgeRequest("Ghost1", "Ghost2", "calls"));
        Assert.False(result.IsSuccess);
        Assert.Contains("not found", result.Error!.Message);
        Assert.Contains("SourceId", result.Error.Message);
        Assert.Contains("TargetId", result.Error.Message);
    }

    // ────── 21. RemoveNode_NoCascade_WithEdges_Fails ──────

    [Fact]
    public async Task RemoveNode_NoCascade_WithConnectedEdges_ReturnsError()
    {
        await AddNodeAsync("R1", "Class");
        await AddNodeAsync("R2", "Class");
        await AddEdgeAsync("R1", "R2", "calls");

        // Attempt to remove R1 without cascade -- should fail because it has edges
        var result = await _service.RemoveNodeAsync(
            new GraphRemoveNodeRequest("R1", Cascade: false));
        Assert.False(result.IsSuccess);
        Assert.Contains("connected edge", result.Error!.Message);
        Assert.Contains("1", result.Error.Message); // 1 connected edge

        // Verify node and edge still exist
        var stats = await _service.StatsAsync(new GraphStatsRequest());
        Assert.True(stats.IsSuccess);
        Assert.Equal(2, stats.Value!.NodeCount);
        Assert.Equal(1, stats.Value.EdgeCount);
    }

    [Fact]
    public async Task RemoveNode_NoCascade_WithoutEdges_Succeeds()
    {
        await AddNodeAsync("Lone", "Class");

        // Remove node with no edges and Cascade=false should succeed
        var result = await _service.RemoveNodeAsync(
            new GraphRemoveNodeRequest("Lone", Cascade: false));
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(1, result.Value!.RemovedNodes);
        Assert.Equal(0, result.Value.RemovedEdges);
    }
}
