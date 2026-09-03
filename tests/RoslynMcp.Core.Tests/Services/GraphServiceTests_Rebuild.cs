using Microsoft.Data.Sqlite;
using RoslynMcp.Core.Helpers;
using RoslynMcp.Core.Helpers.Migrations;
using RoslynMcp.Core.Services;
using RoslynMcp.Core.Tests.Helpers;
using RoslynMcp.Shared.Contracts.Graph;
using Serilog;
using Xunit;

namespace RoslynMcp.Core.Tests.Services;

/// <summary>
/// Covers what a rebuild must do to the persisted graph: produce ids that survive a reload, and
/// leave nodes and edges the user added through graph_add_node / graph_add_edge alone.
/// </summary>
public class GraphServiceTests_Rebuild : IAsyncDisposable, IAsyncLifetime
{
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();
    private readonly string _dbPath;
    private readonly SqliteConnectionPool _pool;
    private readonly WorkspaceTestHelper _helper;
    private readonly GraphService _service;

    public GraphServiceTests_Rebuild()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "RoslynMcpTests", Guid.NewGuid().ToString(), "test.db");
        _pool = new SqliteConnectionPool(_dbPath, logger: _logger);

        _helper = new WorkspaceTestHelper()
            .AddProject("Core")
            .AddProject("Server", "Core");

        _service = new GraphService(_pool, _logger, _helper.CreateProvider());
    }

    public async Task InitializeAsync()
    {
        var runner = new MigrationRunner(_pool, _dbPath,
            new IMigration[] { new V1_MemoryTables(), new V2_GraphTables(), new V3_KBTables(), new V4_GraphProvenance() },
            _logger);
        await runner.RunAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _helper.Dispose();
        await _pool.DisposeAsync();
        try { Directory.Delete(Path.GetDirectoryName(_dbPath)!, true); } catch { }
    }

    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    private async Task<int> CountAsync(string sql)
    {
        await using var lease = await _pool.AcquireReaderAsync(TimeSpan.FromSeconds(5));
        using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    /// <summary>
    /// The reason a reload used to orphan the whole graph: nodes were keyed by a ProjectId GUID
    /// minted fresh on every solution load, so a second rebuild appended a new disconnected set.
    /// </summary>
    [Fact]
    public async Task RebuildIsIdempotent_SoAReloadDoesNotDuplicateTheGraph()
    {
        var first = await _service.RebuildAsync(new GraphRebuildRequest(FullRebuild: true));
        Assert.True(first.IsSuccess, first.Error?.Message);

        var nodesAfterFirst = await CountAsync("SELECT COUNT(*) FROM GraphNodes");
        var edgesAfterFirst = await CountAsync("SELECT COUNT(*) FROM GraphEdges");
        Assert.Equal(2, nodesAfterFirst);
        Assert.Equal(1, edgesAfterFirst);

        var second = await _service.RebuildAsync(new GraphRebuildRequest(FullRebuild: true));
        Assert.True(second.IsSuccess, second.Error?.Message);

        Assert.Equal(nodesAfterFirst, await CountAsync("SELECT COUNT(*) FROM GraphNodes"));
        Assert.Equal(edgesAfterFirst, await CountAsync("SELECT COUNT(*) FROM GraphEdges"));
    }

    /// <summary>
    /// A rebuild now runs by itself after every solution load, so it must not take the user's own
    /// nodes and edges with it.
    /// </summary>
    [Fact]
    public async Task FullRebuild_KeepsUserAuthoredNodesAndEdges()
    {
        await _service.AddNodeAsync(new GraphAddNodeRequest("note:auth", "Note", "auth flow"));
        await _service.AddNodeAsync(new GraphAddNodeRequest("note:cache", "Note", "cache flow"));
        await _service.AddEdgeAsync(new GraphAddEdgeRequest("note:auth", "note:cache", "relates-to"));

        var result = await _service.RebuildAsync(new GraphRebuildRequest(FullRebuild: true));
        Assert.True(result.IsSuccess, result.Error?.Message);

        Assert.Equal(2, await CountAsync("SELECT COUNT(*) FROM GraphNodes WHERE Type = 'Note'"));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM GraphEdges WHERE Type = 'relates-to'"));
        Assert.Equal(2, await CountAsync("SELECT COUNT(*) FROM GraphNodes WHERE IsDerived = 1"));
    }

    /// <summary>
    /// IsStale is what a caller uses to decide whether to trust the graph, so a rebuild must
    /// clear it and a later mutation must set it again.
    /// </summary>
    [Fact]
    public async Task StaleFlagClearsOnRebuildAndReturnsOnMutation()
    {
        var beforeRebuild = await _service.StatsAsync(new GraphStatsRequest());
        Assert.True(beforeRebuild.Value!.IsStale);

        await _service.RebuildAsync(new GraphRebuildRequest(FullRebuild: true));

        var afterRebuild = await _service.StatsAsync(new GraphStatsRequest());
        Assert.False(afterRebuild.Value!.IsStale);

        await _service.AddNodeAsync(new GraphAddNodeRequest("note:new", "Note"));

        var afterMutation = await _service.StatsAsync(new GraphStatsRequest());
        Assert.True(afterMutation.Value!.IsStale);
    }
}
