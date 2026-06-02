using RoslynMcp.Core.Helpers;
using RoslynMcp.Core.Helpers.Migrations;
using RoslynMcp.Core.Services;
using RoslynMcp.Shared.Contracts.Memory;
using Serilog;
using Xunit;

namespace RoslynMcp.Core.Tests.Services;

public class MemoryServiceTests : IAsyncDisposable, IAsyncLifetime
{
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();
    private readonly string _dbPath;
    private readonly SqliteConnectionPool _pool;
    private readonly MemoryService _service;

    public MemoryServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "RoslynMcpTests", Guid.NewGuid().ToString(), "test.db");
        _pool = new SqliteConnectionPool(_dbPath, logger: _logger);
        _service = new MemoryService(_pool, _dbPath, _logger);
    }

    public async Task InitializeAsync()
    {
        var runner = new MigrationRunner(_pool, _dbPath, new IMigration[] { new V1_MemoryTables() }, _logger);
        await runner.RunAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _pool.DisposeAsync();
        try { Directory.Delete(Path.GetDirectoryName(_dbPath)!, true); } catch { }
    }

    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    // ────── 1. SessionStart_CreatesSession ──────

    [Fact]
    public async Task SessionStart_CreatesSession()
    {
        var result = await _service.SessionStartAsync(new SessionStartRequest(SessionName: "test-session"));
        Assert.True(result.IsSuccess, result.Error?.Message);

        var session = result.Value!;
        Assert.False(string.IsNullOrEmpty(session.SessionId));
        Assert.True(session.StartedAt > DateTime.UtcNow.AddMinutes(-1), "StartedAt should be recent");
        Assert.True(session.StartedAt <= DateTime.UtcNow.AddSeconds(5), "StartedAt should not be in the future");
    }

    // ────── 2. SessionEnd_SetsEndedAt ──────

    [Fact]
    public async Task SessionEnd_SetsEndedAt()
    {
        // Start a session
        var startResult = await _service.SessionStartAsync(new SessionStartRequest(SessionName: "to-end"));
        Assert.True(startResult.IsSuccess, startResult.Error?.Message);
        var sessionId = startResult.Value!.SessionId;

        // End the session
        var endResult = await _service.SessionEndAsync(new SessionEndRequest(sessionId));
        Assert.True(endResult.IsSuccess, endResult.Error?.Message);

        var ended = endResult.Value!;
        Assert.Equal(sessionId, ended.SessionId);
        Assert.True(ended.EndedAt > DateTime.UtcNow.AddMinutes(-1), "EndedAt should be recent");
        Assert.Equal(0, ended.EntryCount);
    }

    // ────── 3. SessionList_ReturnsActiveSessions ──────

    [Fact]
    public async Task SessionList_ReturnsActiveSessions()
    {
        // Start two sessions
        var s1 = await _service.SessionStartAsync(new SessionStartRequest(SessionName: "active-1"));
        Assert.True(s1.IsSuccess, s1.Error?.Message);

        var s2 = await _service.SessionStartAsync(new SessionStartRequest(SessionName: "ended-2"));
        Assert.True(s2.IsSuccess, s2.Error?.Message);

        // End the second session
        var endResult = await _service.SessionEndAsync(new SessionEndRequest(s2.Value!.SessionId));
        Assert.True(endResult.IsSuccess, endResult.Error?.Message);

        // ActiveOnly=true should return only 1
        var activeResult = await _service.SessionListAsync(new SessionListRequest(ActiveOnly: true));
        Assert.True(activeResult.IsSuccess, activeResult.Error?.Message);
        var activeSession = Assert.Single(activeResult.Value!.Sessions);
        Assert.Equal("active-1", activeSession.SessionName);

        // ActiveOnly=false should return both
        var allResult = await _service.SessionListAsync(new SessionListRequest(ActiveOnly: false));
        Assert.True(allResult.IsSuccess, allResult.Error?.Message);
        Assert.Equal(2, allResult.Value!.Sessions.Count);
    }

    // ────── 4. Store_And_Retrieve_ByKey ──────

    [Fact]
    public async Task Store_And_Retrieve_ByKey()
    {
        var storeResult = await _service.StoreAsync(new MemoryStoreRequest(
            Key: "test-key",
            Value: "test-value",
            Category: "test-cat",
            Tags: new[] { "tag1", "tag2" },
            Metadata: "{\"extra\":1}"));
        Assert.True(storeResult.IsSuccess, storeResult.Error?.Message);
        Assert.Equal("test-key", storeResult.Value!.Key);
        Assert.True(storeResult.Value.Id > 0);

        var retrieveResult = await _service.RetrieveAsync(new MemoryRetrieveRequest(Key: "test-key"));
        Assert.True(retrieveResult.IsSuccess, retrieveResult.Error?.Message);

        var entry = retrieveResult.Value!.Entry;
        Assert.NotNull(entry);
        Assert.Equal("test-key", entry.Key);
        Assert.Equal("test-value", entry.Value);
        Assert.Equal("test-cat", entry.Category);
        Assert.Contains("tag1", entry.Tags);
        Assert.Contains("tag2", entry.Tags);
        Assert.Equal("{\"extra\":1}", entry.Metadata);
    }

    // ────── 5. Store_And_Retrieve_ById ──────

    [Fact]
    public async Task Store_And_Retrieve_ById()
    {
        var storeResult = await _service.StoreAsync(new MemoryStoreRequest(
            Key: "id-lookup-key",
            Value: "id-lookup-value"));
        Assert.True(storeResult.IsSuccess, storeResult.Error?.Message);

        var id = storeResult.Value!.Id;

        var retrieveResult = await _service.RetrieveAsync(new MemoryRetrieveRequest(Id: id));
        Assert.True(retrieveResult.IsSuccess, retrieveResult.Error?.Message);

        var entry = retrieveResult.Value!.Entry;
        Assert.NotNull(entry);
        Assert.Equal(id, entry.Id);
        Assert.Equal("id-lookup-key", entry.Key);
        Assert.Equal("id-lookup-value", entry.Value);
    }

    // ────── 6. Update_ChangesValue ──────

    [Fact]
    public async Task Update_ChangesValue()
    {
        var storeResult = await _service.StoreAsync(new MemoryStoreRequest(
            Key: "update-key",
            Value: "original-value"));
        Assert.True(storeResult.IsSuccess, storeResult.Error?.Message);
        var id = storeResult.Value!.Id;

        // Update the value
        var updateResult = await _service.UpdateAsync(new MemoryUpdateRequest(
            Id: id,
            Value: "updated-value"));
        Assert.True(updateResult.IsSuccess, updateResult.Error?.Message);
        Assert.Equal(id, updateResult.Value!.Id);

        // Retrieve and verify
        var retrieveResult = await _service.RetrieveAsync(new MemoryRetrieveRequest(Id: id));
        Assert.True(retrieveResult.IsSuccess, retrieveResult.Error?.Message);

        var entry = retrieveResult.Value!.Entry;
        Assert.NotNull(entry);
        Assert.Equal("updated-value", entry.Value);
        Assert.NotNull(entry.UpdatedAt);
    }

    // ────── 7. Delete_ByKey_RemovesEntry ──────

    [Fact]
    public async Task Delete_ByKey_RemovesEntry()
    {
        var storeResult = await _service.StoreAsync(new MemoryStoreRequest(
            Key: "delete-me",
            Value: "doomed"));
        Assert.True(storeResult.IsSuccess, storeResult.Error?.Message);

        var deleteResult = await _service.DeleteAsync(new MemoryDeleteRequest(Key: "delete-me"));
        Assert.True(deleteResult.IsSuccess, deleteResult.Error?.Message);
        Assert.Equal(1, deleteResult.Value!.DeletedCount);

        // Retrieve should return null entry
        var retrieveResult = await _service.RetrieveAsync(new MemoryRetrieveRequest(Key: "delete-me"));
        Assert.True(retrieveResult.IsSuccess, retrieveResult.Error?.Message);
        Assert.Null(retrieveResult.Value!.Entry);
    }

    // ────── 8. Search_FindsByKeyOrValue ──────

    [Fact]
    public async Task Search_FindsByKeyOrValue()
    {
        // Store 3 entries: "alpha" appears in key of first, value of second, neither of third
        await StoreAndAssert("alpha-key", "unrelated-value-1");
        await StoreAndAssert("other-key", "contains-alpha-here");
        await StoreAndAssert("gamma-key", "gamma-value");

        var searchResult = await _service.SearchAsync(new MemorySearchRequest(Query: "alpha"));
        Assert.True(searchResult.IsSuccess, searchResult.Error?.Message);

        Assert.Equal(2, searchResult.Value!.Results.Count);

        var keys = searchResult.Value.Results.Select(r => r.Key).ToList();
        Assert.Contains("alpha-key", keys);
        Assert.Contains("other-key", keys);
    }

    // ────── 9. List_WithPagination ──────

    [Fact]
    public async Task List_WithPagination()
    {
        // Store 5 entries
        for (var i = 1; i <= 5; i++)
            await StoreAndAssert($"page-key-{i}", $"page-value-{i}");

        // Page 0, size 2
        var page0 = await _service.ListAsync(new MemoryListRequest(Page: 0, PageSize: 2));
        Assert.True(page0.IsSuccess, page0.Error?.Message);
        Assert.Equal(2, page0.Value!.Entries.Count);
        Assert.Equal(5, page0.Value.TotalCount);
        Assert.Equal(0, page0.Value.Page);
        Assert.Equal(2, page0.Value.PageSize);

        // Page 1, size 2
        var page1 = await _service.ListAsync(new MemoryListRequest(Page: 1, PageSize: 2));
        Assert.True(page1.IsSuccess, page1.Error?.Message);
        Assert.Equal(2, page1.Value!.Entries.Count);
        Assert.Equal(5, page1.Value.TotalCount);

        // Verify no overlap between pages
        var page0Keys = page0.Value.Entries.Select(e => e.Key).ToHashSet();
        var page1Keys = page1.Value.Entries.Select(e => e.Key).ToHashSet();
        Assert.Empty(page0Keys.Intersect(page1Keys));
    }

    // ────── 10. Stats_ReturnsCorrectCounts ──────

    [Fact]
    public async Task Stats_ReturnsCorrectCounts()
    {
        // Store 3 entries in 2 categories
        await StoreAndAssert("stat-1", "val-1", category: "cat-x");
        await StoreAndAssert("stat-2", "val-2", category: "cat-x");
        await StoreAndAssert("stat-3", "val-3", category: "cat-y");

        // Start a session
        var sessionResult = await _service.SessionStartAsync(new SessionStartRequest(SessionName: "stat-session"));
        Assert.True(sessionResult.IsSuccess, sessionResult.Error?.Message);

        var statsResult = await _service.StatsAsync(new MemoryStatsRequest());
        Assert.True(statsResult.IsSuccess, statsResult.Error?.Message);

        var stats = statsResult.Value!;
        Assert.Equal(3, stats.TotalEntries);
        Assert.Equal(2, stats.Categories.Count);
        Assert.Contains("cat-x", stats.Categories);
        Assert.Contains("cat-y", stats.Categories);
        Assert.Equal(1, stats.SessionCount);
    }

    // ────── 11. Export_And_Import ──────

    [Fact]
    public async Task Export_And_Import()
    {
        // Store 2 entries
        await StoreAndAssert("export-1", "value-1", category: "exp");
        await StoreAndAssert("export-2", "value-2", category: "exp");

        // Export
        var exportResult = await _service.ExportAsync(new MemoryExportRequest());
        Assert.True(exportResult.IsSuccess, exportResult.Error?.Message);
        Assert.Equal(2, exportResult.Value!.EntryCount);

        var json = exportResult.Value.Data;
        Assert.False(string.IsNullOrEmpty(json));

        // Create a second service on a fresh DB
        var dbPath2 = Path.Combine(Path.GetTempPath(), "RoslynMcpTests", Guid.NewGuid().ToString(), "test2.db");
        await using var pool2 = new SqliteConnectionPool(dbPath2, logger: _logger);
        var runner2 = new MigrationRunner(pool2, dbPath2, new IMigration[] { new V1_MemoryTables() }, _logger);
        await runner2.RunAsync();
        var service2 = new MemoryService(pool2, dbPath2, _logger);

        // Import
        var importResult = await service2.ImportAsync(new MemoryImportRequest(Data: json));
        Assert.True(importResult.IsSuccess, importResult.Error?.Message);
        Assert.Equal(2, importResult.Value!.ImportedCount);
        Assert.Equal(0, importResult.Value.SkippedCount);
        Assert.Empty(importResult.Value.Errors);

        // Verify entries exist in second DB
        var listResult = await service2.ListAsync(new MemoryListRequest());
        Assert.True(listResult.IsSuccess, listResult.Error?.Message);
        Assert.Equal(2, listResult.Value!.TotalCount);

        // Cleanup second DB
        try { Directory.Delete(Path.GetDirectoryName(dbPath2)!, true); } catch { }
    }

    // ────── 12. Cleanup_RemovesByCategory ──────

    [Fact]
    public async Task Cleanup_RemovesByCategory()
    {
        // Store 3 entries: 2 in "cat-a", 1 in "cat-b"
        await StoreAndAssert("cleanup-1", "val-1", category: "cat-a");
        await StoreAndAssert("cleanup-2", "val-2", category: "cat-a");
        await StoreAndAssert("cleanup-3", "val-3", category: "cat-b");

        // Cleanup cat-a
        var cleanupResult = await _service.CleanupAsync(new MemoryCleanupRequest(Category: "cat-a"));
        Assert.True(cleanupResult.IsSuccess, cleanupResult.Error?.Message);
        Assert.Equal(2, cleanupResult.Value!.RemovedCount);
        Assert.False(cleanupResult.Value.DryRun);

        // List all remaining entries
        var listResult = await _service.ListAsync(new MemoryListRequest());
        Assert.True(listResult.IsSuccess, listResult.Error?.Message);
        Assert.Equal(1, listResult.Value!.TotalCount);
        Assert.Equal("cat-b", listResult.Value.Entries[0].Category);
    }

    // ────── 13. Store_SqlInjectionAttempt_StoresLiteralString ──────

    [Fact]
    public async Task Store_SqlInjectionAttempt_StoresLiteralString()
    {
        var maliciousKey = "'; DROP TABLE Sessions; --";
        var storeResult = await _service.StoreAsync(new MemoryStoreRequest(
            Key: maliciousKey,
            Value: "test",
            Category: "test"));
        Assert.True(storeResult.IsSuccess, storeResult.Error?.Message);

        // Verify the key was stored literally
        var retrieveResult = await _service.RetrieveAsync(new MemoryRetrieveRequest(Key: maliciousKey));
        Assert.True(retrieveResult.IsSuccess, retrieveResult.Error?.Message);
        Assert.NotNull(retrieveResult.Value!.Entry);
        Assert.Equal(maliciousKey, retrieveResult.Value.Entry.Key);

        // Verify Sessions table still exists
        var sessionResult = await _service.SessionListAsync(new SessionListRequest());
        Assert.True(sessionResult.IsSuccess, "Sessions table should still exist after injection attempt");
    }

    #region Helpers

    private async Task StoreAndAssert(string key, string value, string? category = null)
    {
        var result = await _service.StoreAsync(new MemoryStoreRequest(
            Key: key, Value: value, Category: category));
        Assert.True(result.IsSuccess, result.Error?.Message);
    }

    #endregion
}
