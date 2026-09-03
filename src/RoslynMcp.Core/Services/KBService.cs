using System.Text.Json;
using Microsoft.Data.Sqlite;
using RoslynMcp.Core.Helpers;
using RoslynMcp.Core.Helpers.Migrations;
using RoslynMcp.Shared;
using RoslynMcp.Shared.Contracts.KB;
using Serilog;

namespace RoslynMcp.Core.Services;

public class KBService : IAsyncDisposable
{
    private readonly ISqliteConnectionPool _pool;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _ftsInitSemaphore = new(1, 1);
    private bool _ftsAvailable;
    private bool _ftsInitialized;

    public KBService(ISqliteConnectionPool pool, ILogger logger)
    {
        _pool = pool;
        _logger = logger;
    }

    // ── FTS5 Initialization ──

    private async Task EnsureFts5Async(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _ftsInitialized)) return;

        await _ftsInitSemaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ftsInitialized) return;

            await using var lease = await _pool.AcquireWriterAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var conn = lease.Connection;

            try
            {
                // Check if FTS5 is available in this SQLite build
                using var checkCmd = conn.CreateCommand();
                checkCmd.CommandText = "SELECT sqlite_compileoption_used('ENABLE_FTS5');";
                var result = await checkCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                var hasFts5 = result is long val && val == 1;

                if (hasFts5)
                {
                    try
                    {
                        using var ftsCmd = conn.CreateCommand();
                        ftsCmd.CommandText = V3_KBTables.Fts5Sql;
                        await ftsCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                        _ftsAvailable = true;
                        _logger.Information("FTS5 virtual table and triggers created successfully");
                    }
                    catch (Exception ex)
                    {
                        _ftsAvailable = false;
                        _logger.Warning(ex, "FTS5 table creation failed; full-text search disabled");
                    }
                }
                else
                {
                    _ftsAvailable = false;
                    _logger.Information("FTS5 not available in this SQLite build; using LIKE fallback");
                }
            }
            catch (Exception ex)
            {
                _ftsAvailable = false;
                _logger.Warning(ex, "FTS5 availability check failed");
            }
            finally
            {
                _ftsInitialized = true;
            }
        }
        finally
        {
            _ftsInitSemaphore.Release();
        }
    }

    // ── 1. kb_add ──

    public async Task<Result<KBAddResponse>> AddAsync(KBAddRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await EnsureFts5Async(ct).ConfigureAwait(false);

            await using var lease = await _pool.AcquireWriterAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var conn = lease.Connection;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO KBEntries (Title, Content, Category, Tags, Metadata)
                VALUES ($title, $content, $category, $tags, $metadata);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$title", request.Title);
            cmd.Parameters.AddWithValue("$content", request.Content);
            cmd.Parameters.AddWithValue("$category", request.Category ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$tags", JsonHelpers.SerializeTags(request.Tags));
            cmd.Parameters.AddWithValue("$metadata", request.Metadata ?? (object)DBNull.Value);

            var id = (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;

            // Read back the CreatedAt
            using var readCmd = conn.CreateCommand();
            readCmd.CommandText = "SELECT CreatedAt FROM KBEntries WHERE Id = $id;";
            readCmd.Parameters.AddWithValue("$id", id);
            var createdAtStr = (string)(await readCmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
            var createdAt = DateTimeHelpers.ParseUtcDateTime(createdAtStr);

            return Result<KBAddResponse>.Ok(new KBAddResponse(id, createdAt));
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "kb_add failed");
            return Result<KBAddResponse>.Fail(ex.Message);
        }
    }

    // ── 2. kb_get ──

    public async Task<Result<KBGetResponse>> GetAsync(KBGetRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await using var lease = await _pool.AcquireReaderAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var conn = lease.Connection;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Id, Title, Content, Category, Tags, Metadata, CreatedAt, UpdatedAt FROM KBEntries WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$id", request.Id);

            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            KBEntry? entry = null;
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                entry = ReadKBEntry(reader);
            }

            return Result<KBGetResponse>.Ok(new KBGetResponse(entry));
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "kb_get failed for Id={Id}", request.Id);
            return Result<KBGetResponse>.Fail(ex.Message);
        }
    }

    // ── 3. kb_update ──

    public async Task<Result<KBUpdateResponse>> UpdateAsync(KBUpdateRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Early return if no mutable fields were provided - avoid unnecessary DB round-trip
        if (request.Title is null && request.Content is null && request.Category is null &&
            request.Tags is null && request.Metadata is null)
        {
            return Result<KBUpdateResponse>.Ok(new KBUpdateResponse(request.Id, DateTime.UtcNow));
        }

        try
        {
            await using var lease = await _pool.AcquireWriterAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var conn = lease.Connection;

            // Build dynamic SET clause for non-null fields
            var setClauses = new List<string>();
            var parameters = new List<SqliteParameter>();

            if (request.Title != null)
            {
                setClauses.Add("Title = $title");
                parameters.Add(new SqliteParameter("$title", request.Title));
            }
            if (request.Content != null)
            {
                setClauses.Add("Content = $content");
                parameters.Add(new SqliteParameter("$content", request.Content));
            }
            if (request.Category != null)
            {
                setClauses.Add("Category = $category");
                parameters.Add(new SqliteParameter("$category", request.Category));
            }
            if (request.Tags != null)
            {
                setClauses.Add("Tags = $tags");
                parameters.Add(new SqliteParameter("$tags", JsonHelpers.SerializeTags(request.Tags)));
            }
            if (request.Metadata != null)
            {
                setClauses.Add("Metadata = $metadata");
                parameters.Add(new SqliteParameter("$metadata", request.Metadata));
            }

            // Always update the timestamp
            setClauses.Add("UpdatedAt = datetime('now')");

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"UPDATE KBEntries SET {string.Join(", ", setClauses)} WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$id", request.Id);
            foreach (var p in parameters)
                cmd.Parameters.Add(p);

            var rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (rows == 0)
            {
                _logger.Warning("kb_update: Entry not found, Id={Id}", request.Id);
                return Result<KBUpdateResponse>.Fail($"Entry not found: {request.Id}");
            }

            // Read back UpdatedAt
            using var readCmd = conn.CreateCommand();
            readCmd.CommandText = "SELECT UpdatedAt FROM KBEntries WHERE Id = $id;";
            readCmd.Parameters.AddWithValue("$id", request.Id);
            var updatedAtStr = (string)(await readCmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
            var updatedAt = DateTimeHelpers.ParseUtcDateTime(updatedAtStr);

            return Result<KBUpdateResponse>.Ok(new KBUpdateResponse(request.Id, updatedAt));
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "kb_update failed for Id={Id}", request.Id);
            return Result<KBUpdateResponse>.Fail(ex.Message);
        }
    }

    // ── 4. kb_delete ──

    public async Task<Result<KBDeleteResponse>> DeleteAsync(KBDeleteRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await using var lease = await _pool.AcquireWriterAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var conn = lease.Connection;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM KBEntries WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$id", request.Id);

            var rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            return Result<KBDeleteResponse>.Ok(new KBDeleteResponse(rows > 0));
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "kb_delete failed for Id={Id}", request.Id);
            return Result<KBDeleteResponse>.Fail(ex.Message);
        }
    }

    // ── 5. kb_list ──

    public async Task<Result<KBListResponse>> ListAsync(KBListRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            if (request.Page < 0 || request.PageSize <= 0 || request.PageSize > 10000)
                return Result<KBListResponse>.Fail("Invalid paging: Page must be >= 0, PageSize must be 1-10000");

            await using var lease = await _pool.AcquireReaderAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var conn = lease.Connection;

            // Count total
            using var countCmd = conn.CreateCommand();
            if (request.Category != null)
            {
                countCmd.CommandText = "SELECT COUNT(*) FROM KBEntries WHERE Category = $category;";
                countCmd.Parameters.AddWithValue("$category", request.Category);
            }
            else
            {
                countCmd.CommandText = "SELECT COUNT(*) FROM KBEntries;";
            }
            var totalCount = (int)(long)(await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;

            // Fetch page
            using var cmd = conn.CreateCommand();
            var offset = request.Page * request.PageSize;
            if (request.Category != null)
            {
                cmd.CommandText = """
                    SELECT Id, Title, Content, Category, Tags, Metadata, CreatedAt, UpdatedAt
                    FROM KBEntries
                    WHERE Category = $category
                    ORDER BY Id DESC
                    LIMIT $limit OFFSET $offset;
                    """;
                cmd.Parameters.AddWithValue("$category", request.Category);
            }
            else
            {
                cmd.CommandText = """
                    SELECT Id, Title, Content, Category, Tags, Metadata, CreatedAt, UpdatedAt
                    FROM KBEntries
                    ORDER BY Id DESC
                    LIMIT $limit OFFSET $offset;
                    """;
            }
            cmd.Parameters.AddWithValue("$limit", request.PageSize);
            cmd.Parameters.AddWithValue("$offset", offset);

            var entries = new List<KBEntry>();
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                entries.Add(ReadKBEntry(reader));
            }


            return Result<KBListResponse>.Ok(new KBListResponse(entries, totalCount, request.Page, request.PageSize));
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "kb_list failed");
            return Result<KBListResponse>.Fail(ex.Message);
        }
    }

    // ── 6. kb_search ──

    public async Task<Result<KBSearchResponse>> SearchAsync(KBSearchRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await EnsureFts5Async(ct).ConfigureAwait(false);

            if (request.Limit <= 0 || request.Limit > 10000)
                return Result<KBSearchResponse>.Fail("Limit must be between 1 and 10000");

            await using var lease = await _pool.AcquireReaderAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var conn = lease.Connection;

            var results = new List<KBSearchResult>();
            bool usedFts = false;

            if (_ftsAvailable && request.UseFts)
            {
                usedFts = true;
                var sanitizedQuery = SanitizeFtsQuery(request.Query);

                using var cmd = conn.CreateCommand();

                // Build WHERE clause for optional filters
                var extraWhere = BuildSearchFilters(cmd, request.Category, request.Tags);

                cmd.CommandText = $"""
                    SELECT e.Id, e.Title, e.Category, e.Tags, rank,
                           snippet(KBEntries_fts, 1, '**', '**', '...', 32) AS Snippet
                    FROM KBEntries_fts f
                    JOIN KBEntries e ON e.Id = f.rowid
                    WHERE KBEntries_fts MATCH $query
                    {extraWhere}
                    ORDER BY rank
                    LIMIT $limit;
                    """;
                cmd.Parameters.AddWithValue("$query", sanitizedQuery);
                cmd.Parameters.AddWithValue("$limit", request.Limit);

                using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var id = reader.GetInt64(0);
                    var title = reader.GetString(1);
                    var category = reader.IsDBNull(2) ? null : reader.GetString(2);
                    var tags = JsonHelpers.DeserializeTags(reader.IsDBNull(3) ? null : reader.GetString(3));
                    var rank = reader.GetDouble(4);
                    var snippet = reader.IsDBNull(5) ? null : reader.GetString(5);

                    // FTS5 rank is negative, lower = better match; invert for relevance
                    var relevance = -rank;

                    results.Add(new KBSearchResult(id, title, category, tags, relevance, snippet));
                }
            }
            else
            {
                // LIKE fallback
                using var cmd = conn.CreateCommand();

                var extraWhere = BuildSearchFilters(cmd, request.Category, request.Tags);

                cmd.CommandText = $"""
                    SELECT e.Id, e.Title, e.Content, e.Category, e.Tags
                    FROM KBEntries e
                    WHERE (e.Title LIKE '%' || $query || '%' ESCAPE '\' OR e.Content LIKE '%' || $query || '%' ESCAPE '\')
                    {extraWhere}
                    LIMIT $limit;
                    """;
                cmd.Parameters.AddWithValue("$query", EscapeLikeWildcards(request.Query));
                cmd.Parameters.AddWithValue("$limit", request.Limit);

                using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var id = reader.GetInt64(0);
                    var title = reader.GetString(1);
                    var content = reader.GetString(2);
                    var category = reader.IsDBNull(3) ? null : reader.GetString(3);
                    var tags = JsonHelpers.DeserializeTags(reader.IsDBNull(4) ? null : reader.GetString(4));

                    // Relevance: 1.0 for title match, 0.5 for content-only match
                    var relevance = title.IndexOf(request.Query, StringComparison.OrdinalIgnoreCase) >= 0
                        ? 1.0
                        : 0.5;

                    results.Add(new KBSearchResult(id, title, category, tags, relevance, null));
                }
            }


            return Result<KBSearchResponse>.Ok(new KBSearchResponse(results, results.Count, usedFts));
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "kb_search failed for Query={Query}", request.Query);
            return Result<KBSearchResponse>.Fail(ex.Message);
        }
    }

    // ── 7. kb_related ──

    public async Task<Result<KBRelatedResponse>> RelatedAsync(KBRelatedRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await using var lease = await _pool.AcquireReaderAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var conn = lease.Connection;

            // Get the source entry
            using var srcCmd = conn.CreateCommand();
            srcCmd.CommandText = "SELECT Id, Title, Content, Category, Tags, Metadata, CreatedAt, UpdatedAt FROM KBEntries WHERE Id = $id;";
            srcCmd.Parameters.AddWithValue("$id", request.Id);

            KBEntry? sourceEntry = null;
            using (var srcReader = await srcCmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                if (await srcReader.ReadAsync(ct).ConfigureAwait(false))
                    sourceEntry = ReadKBEntry(srcReader);
            }

            if (sourceEntry == null)
            {
                _logger.Warning("kb_related: Source entry not found, Id={Id}", request.Id);
                return Result<KBRelatedResponse>.Fail($"Entry not found: {request.Id}");
            }

            // Find related entries by category or overlapping tags
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT Id, Title, Category, Tags
                FROM KBEntries
                WHERE Id != $id
                  AND ($category IS NULL OR Category = $category)
                ORDER BY Id DESC
                LIMIT 1000;
                """;
            cmd.Parameters.AddWithValue("$id", request.Id);
            cmd.Parameters.AddWithValue("$category", (object?)sourceEntry.Category ?? DBNull.Value);

            var candidates = new List<(long Id, string Title, string? Category, List<string> Tags)>();
            using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    candidates.Add((
                        reader.GetInt64(0),
                        reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        JsonHelpers.DeserializeTags(reader.IsDBNull(3) ? null : reader.GetString(3))
                    ));
                }
            }

            var sourceTags = new HashSet<string>(sourceEntry.Tags, StringComparer.OrdinalIgnoreCase);
            var scored = new List<KBSearchResult>();

            foreach (var (id, title, category, tags) in candidates)
            {
                double relevance = 0.0;

                // Same category = 0.5
                if (sourceEntry.Category != null && category != null &&
                    string.Equals(sourceEntry.Category, category, StringComparison.OrdinalIgnoreCase))
                {
                    relevance += 0.5;
                }

                // Each shared tag = 0.3
                foreach (var tag in tags)
                {
                    if (sourceTags.Contains(tag))
                        relevance += 0.3;
                }

                // Cap at 1.0
                relevance = Math.Min(relevance, 1.0);

                if (relevance > 0.0)
                {
                    scored.Add(new KBSearchResult(id, title, category, tags, relevance, null));
                }
            }

            // Sort by relevance descending, take top N
            var related = scored
                .OrderByDescending(r => r.Relevance)
                .Take(request.Limit)
                .ToList();

            return Result<KBRelatedResponse>.Ok(new KBRelatedResponse(related, "tag_category_overlap"));
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "kb_related failed for Id={Id}", request.Id);
            return Result<KBRelatedResponse>.Fail(ex.Message);
        }
    }

    // ── 8. kb_stats ──

    public async Task<Result<KBStatsResponse>> StatsAsync(KBStatsRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await EnsureFts5Async(ct).ConfigureAwait(false);

            await using var lease = await _pool.AcquireReaderAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var conn = lease.Connection;

            // Total entries
            using var countCmd = conn.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM KBEntries;";
            var totalEntries = (int)(long)(await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;

            // Distinct categories
            using var catCmd = conn.CreateCommand();
            catCmd.CommandText = "SELECT DISTINCT Category FROM KBEntries WHERE Category IS NOT NULL;";
            var categories = new List<string>();
            using (var reader = await catCmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    categories.Add(reader.GetString(0));
                }
            }

            // DB size
            long dbSizeBytes = 0;
            try
            {
                var fi = new FileInfo(_pool.DatabasePath);
                if (fi.Exists)
                    dbSizeBytes = fi.Length;
            }
            catch
            {
                // File might not exist yet or be inaccessible
            }


            return Result<KBStatsResponse>.Ok(new KBStatsResponse(totalEntries, categories, _ftsAvailable, dbSizeBytes));
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "kb_stats failed");
            return Result<KBStatsResponse>.Fail(ex.Message);
        }
    }

    public ValueTask DisposeAsync()
    {
        _ftsInitSemaphore.Dispose();
        return default;
    }

    #region Private Helpers

    private static string EscapeLikeWildcards(string input)
        => SqliteHelpers.EscapeLikeWildcards(input);

    private static KBEntry ReadKBEntry(SqliteDataReader reader)
    {
        var id = reader.GetInt64(0);
        var title = reader.GetString(1);
        var content = reader.GetString(2);
        var category = reader.IsDBNull(3) ? null : reader.GetString(3);
        var tags = JsonHelpers.DeserializeTags(reader.IsDBNull(4) ? null : reader.GetString(4));
        var metadata = reader.IsDBNull(5) ? null : reader.GetString(5);
        var createdAt = DateTimeHelpers.ParseUtcDateTime(reader.GetString(6));
        var updatedAt = reader.IsDBNull(7) ? (DateTime?)null : DateTimeHelpers.ParseUtcDateTime(reader.GetString(7));

        return new KBEntry(id, title, content, category, tags, metadata, createdAt, updatedAt);
    }

    /// <summary>
    /// Sanitize a query string for FTS5 MATCH syntax.
    /// Escapes double quotes to prevent FTS5 syntax errors.
    /// </summary>
    private static string SanitizeFtsQuery(string query)
    {
        // Wrap in double quotes to treat as literal phrase.
        // Escape internal double quotes by doubling them.
        var escaped = query.Replace("\"", "\"\"");
        return $"\"{escaped}\"";
    }

    /// <summary>
    /// Builds optional WHERE clause fragments for category and tag filtering.
    /// Adds parameters to the command as needed.
    /// </summary>
    private static string BuildSearchFilters(SqliteCommand cmd, string? category, IReadOnlyList<string>? tags)
    {
        var clauses = new List<string>();

        if (category != null)
        {
            clauses.Add("AND e.Category = $category");
            cmd.Parameters.AddWithValue("$category", category);
        }

        if (tags != null && tags.Count > 0)
        {
            // Filter entries whose Tags JSON contains all specified tags (using LIKE)
            for (int i = 0; i < tags.Count; i++)
            {
                var paramName = $"$tag{i}";
                clauses.Add($"AND e.Tags LIKE {paramName} ESCAPE '\\'");
                cmd.Parameters.AddWithValue(paramName, $"%\"{EscapeLikeWildcards(tags[i])}\"%");
            }
        }

        return clauses.Count > 0 ? "\n" + string.Join("\n", clauses) : "";
    }

    #endregion
}
