using RoslynMcp.Core.Helpers;
using RoslynMcp.Core.Helpers.Migrations;
using RoslynMcp.Core.Services;
using RoslynMcp.Shared.Contracts.KB;
using Serilog;
using Xunit;

namespace RoslynMcp.Core.Tests.Services;

public class KBServiceTests : IAsyncDisposable, IAsyncLifetime
{
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();
    private readonly string _dbPath;
    private readonly SqliteConnectionPool _pool;
    private readonly KBService _service;

    public KBServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "RoslynMcpTests", Guid.NewGuid().ToString(), "test.db");
        _pool = new SqliteConnectionPool(_dbPath, logger: _logger);
        _service = new KBService(_pool, _logger);
    }

    public async Task InitializeAsync()
    {
        var runner = new MigrationRunner(_pool, _dbPath,
            new IMigration[] { new V1_MemoryTables(), new V2_GraphTables(), new V3_KBTables() }, _logger);
        await runner.RunAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _pool.DisposeAsync();
        try { Directory.Delete(Path.GetDirectoryName(_dbPath)!, true); } catch { }
    }

    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    // ── Helper ──

    private async Task<long> AddEntryAsync(string title, string content, string? category = null, string[]? tags = null)
    {
        var request = new KBAddRequest(title, content, category, tags);
        var result = await _service.AddAsync(request);
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value!.Id;
    }

    // ────── 1. Add_And_Get_ReturnsEntry ──────

    [Fact]
    public async Task Add_And_Get_ReturnsEntry()
    {
        // Arrange
        var title = "Test";
        var content = "Hello world";
        var category = "docs";
        var tags = new[] { "tag1", "tag2" };

        // Act
        var addResult = await _service.AddAsync(new KBAddRequest(title, content, category, tags));
        Assert.True(addResult.IsSuccess, addResult.Error?.Message);
        var id = addResult.Value!.Id;

        var getResult = await _service.GetAsync(new KBGetRequest(id));
        Assert.True(getResult.IsSuccess, getResult.Error?.Message);

        // Assert
        var entry = getResult.Value!.Entry;
        Assert.NotNull(entry);
        Assert.Equal(id, entry.Id);
        Assert.Equal(title, entry.Title);
        Assert.Equal(content, entry.Content);
        Assert.Equal(category, entry.Category);
        Assert.Contains("tag1", entry.Tags);
        Assert.Contains("tag2", entry.Tags);
        Assert.True(entry.CreatedAt <= DateTime.UtcNow.AddMinutes(1));
        Assert.Null(entry.UpdatedAt);
    }

    // ────── 2. Update_ChangesTitle ──────

    [Fact]
    public async Task Update_ChangesTitle()
    {
        // Arrange
        var id = await AddEntryAsync("Original Title", "Some content", "docs");

        // Act
        var updateResult = await _service.UpdateAsync(new KBUpdateRequest(id, Title: "Updated Title"));
        Assert.True(updateResult.IsSuccess, updateResult.Error?.Message);

        var getResult = await _service.GetAsync(new KBGetRequest(id));
        Assert.True(getResult.IsSuccess, getResult.Error?.Message);

        // Assert
        var entry = getResult.Value!.Entry;
        Assert.NotNull(entry);
        Assert.Equal("Updated Title", entry.Title);
        Assert.NotNull(entry.UpdatedAt);
        Assert.Equal(id, updateResult.Value!.Id);
    }

    // ────── 3. Delete_RemovesEntry ──────

    [Fact]
    public async Task Delete_RemovesEntry()
    {
        // Arrange
        var id = await AddEntryAsync("To Delete", "Content to delete");

        // Act
        var deleteResult = await _service.DeleteAsync(new KBDeleteRequest(id));
        Assert.True(deleteResult.IsSuccess, deleteResult.Error?.Message);
        Assert.True(deleteResult.Value!.Deleted);

        var getResult = await _service.GetAsync(new KBGetRequest(id));
        Assert.True(getResult.IsSuccess, getResult.Error?.Message);

        // Assert
        Assert.Null(getResult.Value!.Entry);
    }

    // ────── 4. List_WithPagination ──────

    [Fact]
    public async Task List_WithPagination()
    {
        // Arrange
        for (int i = 1; i <= 5; i++)
        {
            await AddEntryAsync($"Entry {i}", $"Content {i}", "general");
        }

        // Act - page 0, pageSize 2
        var page0Result = await _service.ListAsync(new KBListRequest(Page: 0, PageSize: 2));
        Assert.True(page0Result.IsSuccess, page0Result.Error?.Message);

        // Assert page 0
        Assert.Equal(2, page0Result.Value!.Results.Items.Count);
        Assert.Equal(5, page0Result.Value.Results.TotalCount);

        // Act - page 2, pageSize 2 (entries 5-6, only 1 exists)
        var page2Result = await _service.ListAsync(new KBListRequest(Page: 2, PageSize: 2));
        Assert.True(page2Result.IsSuccess, page2Result.Error?.Message);

        // Assert page 2
        Assert.Single(page2Result.Value!.Results.Items);
        Assert.Equal(5, page2Result.Value.Results.TotalCount);
    }

    // ────── 5. Search_FindsByTitle ──────

    [Fact]
    public async Task Search_FindsByTitle()
    {
        // Arrange
        await AddEntryAsync("Introduction to C#", "Learn the basics of C# programming");
        await AddEntryAsync("Advanced Python", "Deep dive into Python internals");
        await AddEntryAsync("C# Design Patterns", "Common design patterns in C#");

        // Act
        var searchResult = await _service.SearchAsync(new KBSearchRequest("C#"));
        Assert.True(searchResult.IsSuccess, searchResult.Error?.Message);

        // Assert
        Assert.Equal(2, searchResult.Value!.Results.Count);
    }

    // ────── 6. Search_WithCategoryFilter ──────

    [Fact]
    public async Task Search_WithCategoryFilter()
    {
        // Arrange
        await AddEntryAsync("Tutorial One", "First tutorial content", "tutorials");
        await AddEntryAsync("Tutorial Two", "Second tutorial content", "tutorials");
        await AddEntryAsync("Reference Doc", "Reference content", "reference");

        // Act
        var searchResult = await _service.SearchAsync(new KBSearchRequest("content", Category: "tutorials"));
        Assert.True(searchResult.IsSuccess, searchResult.Error?.Message);

        // Assert
        Assert.Equal(2, searchResult.Value!.Results.Count);
        foreach (var r in searchResult.Value.Results)
        {
            Assert.Equal("tutorials", r.Category);
        }
    }

    // ────── 7. Search_LikeFallback ──────

    [Fact]
    public async Task Search_LikeFallback()
    {
        // Arrange
        await AddEntryAsync("Fallback Test", "This should be found via LIKE", "misc");

        // Act — force LIKE fallback with UseFts=false
        var searchResult = await _service.SearchAsync(new KBSearchRequest("Fallback", UseFts: false));
        Assert.True(searchResult.IsSuccess, searchResult.Error?.Message);

        // Assert
        Assert.False(searchResult.Value!.UsedFts);
        Assert.True(searchResult.Value.Results.Count > 0, "LIKE fallback should return results");
        Assert.Equal("Fallback Test", searchResult.Value.Results[0].Title);
    }

    // ────── 8. Related_FindsByCategoryAndTags ──────

    [Fact]
    public async Task Related_FindsByCategoryAndTags()
    {
        // Arrange
        var idA = await AddEntryAsync("Entry A", "Content A", "docs", new[] { "csharp" });
        var idB = await AddEntryAsync("Entry B", "Content B", "docs", new[] { "csharp", "patterns" });
        var idC = await AddEntryAsync("Entry C", "Content C", "other", new[] { "python" });

        // Act
        var relatedResult = await _service.RelatedAsync(new KBRelatedRequest(idA));
        Assert.True(relatedResult.IsSuccess, relatedResult.Error?.Message);

        // Assert
        var related = relatedResult.Value!.Related;
        Assert.True(related.Count >= 1, "Should find at least one related entry");

        var relB = related.FirstOrDefault(r => r.Id == idB);
        var relC = related.FirstOrDefault(r => r.Id == idC);

        Assert.NotNull(relB);
        // B shares category ("docs") + tag ("csharp") => relevance 0.8
        // C shares neither category nor tags => relevance 0.0, not included
        Assert.True(relB.Relevance > 0, "Entry B should have positive relevance");

        if (relC != null)
        {
            Assert.True(relB.Relevance > relC.Relevance,
                $"B relevance ({relB.Relevance}) should exceed C relevance ({relC.Relevance})");
        }
    }

    // ────── 9. Stats_ReturnsCorrectInfo ──────

    [Fact]
    public async Task Stats_ReturnsCorrectInfo()
    {
        // Arrange
        await AddEntryAsync("Stats Entry 1", "Content 1", "catA");
        await AddEntryAsync("Stats Entry 2", "Content 2", "catA");
        await AddEntryAsync("Stats Entry 3", "Content 3", "catB");

        // Act
        var statsResult = await _service.StatsAsync(new KBStatsRequest());
        Assert.True(statsResult.IsSuccess, statsResult.Error?.Message);

        // Assert
        var stats = statsResult.Value!;
        Assert.Equal(3, stats.TotalEntries);
        Assert.Equal(2, stats.Categories.Count);
        Assert.Contains("catA", stats.Categories);
        Assert.Contains("catB", stats.Categories);
        Assert.True(stats.DbSizeBytes > 0, "DbSizeBytes should be greater than zero");
    }

    // ────── 10. Search_Fts_UsesFullText ──────

    [Fact]
    public async Task Search_Fts_UsesFullText()
    {
        // Arrange — entries with distinct content for FTS matching
        await AddEntryAsync("Quantum Computing", "Exploring qubits and entanglement in quantum systems");
        await AddEntryAsync("Classical Algorithms", "Sorting and searching in traditional computing");
        await AddEntryAsync("Quantum Mechanics", "The physics of quantum particles and wave functions");

        // Act — request FTS search
        var searchResult = await _service.SearchAsync(new KBSearchRequest("quantum", UseFts: true));
        Assert.True(searchResult.IsSuccess, searchResult.Error?.Message);

        var response = searchResult.Value!;

        if (response.UsedFts)
        {
            // FTS5 available — verify results are ordered by relevance (descending)
            Assert.True(response.Results.Count >= 2, "FTS should find at least 2 'quantum' entries");

            for (int i = 1; i < response.Results.Count; i++)
            {
                Assert.True(response.Results[i - 1].Relevance >= response.Results[i].Relevance,
                    $"Results should be ordered by relevance descending: " +
                    $"[{i - 1}]={response.Results[i - 1].Relevance}, [{i}]={response.Results[i].Relevance}");
            }
        }
        else
        {
            // FTS5 not available — LIKE fallback should still find results
            Assert.True(response.Results.Count >= 2, "LIKE fallback should find at least 2 'quantum' entries");
        }
    }

    // ────── 11. Add_SqlInjectionAttempt_StoresLiteralString ──────

    [Fact]
    public async Task Add_SqlInjectionAttempt_StoresLiteralString()
    {
        var maliciousTitle = "'; DROP TABLE KBEntries; --";
        var result = await _service.AddAsync(new KBAddRequest(maliciousTitle, "test content"));
        Assert.True(result.IsSuccess, result.Error?.Message);

        // Verify the title was stored literally
        var getResult = await _service.GetAsync(new KBGetRequest(result.Value!.Id));
        Assert.True(getResult.IsSuccess, getResult.Error?.Message);
        Assert.NotNull(getResult.Value!.Entry);
        Assert.Equal(maliciousTitle, getResult.Value.Entry.Title);

        // Verify KBEntries table still exists via stats
        var statsResult = await _service.StatsAsync(new KBStatsRequest());
        Assert.True(statsResult.IsSuccess, "KBEntries table should still exist after injection attempt");
    }
}
