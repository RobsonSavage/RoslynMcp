using System.Text.Json;
using Microsoft.Data.Sqlite;
using RoslynMcp.Core.Helpers;
using RoslynMcp.Shared;
using RoslynMcp.Shared.Contracts.Memory;
using Serilog;

namespace RoslynMcp.Core.Services;

public class MemoryService
{
    private readonly ISqliteConnectionPool _pool;
    private readonly ILogger _logger;

    public MemoryService(ISqliteConnectionPool pool, ILogger logger)
    {
        _pool = pool;
        _logger = logger;
    }

    // ── 1. session_start ──

    public async Task<Result<SessionStartResponse>> SessionStartAsync(SessionStartRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var sessionId = Guid.NewGuid().ToString("N");
            await using var lease = await _pool.AcquireWriterAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var conn = lease.Connection;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO Sessions (SessionId, SessionName, Metadata) VALUES ($id, $name, $metadata)";
            cmd.Parameters.AddWithValue("$id", sessionId);
            cmd.Parameters.AddWithValue("$name", request.SessionName ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$metadata", request.Metadata ?? (object)DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            using var readCmd = conn.CreateCommand();
            readCmd.CommandText = "SELECT StartedAt FROM Sessions WHERE SessionId = $id";
            readCmd.Parameters.AddWithValue("$id", sessionId);
            var startedAtStr = (string)(await readCmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
            var startedAt = DateTimeHelpers.ParseUtcDateTime(startedAtStr);

            return new SessionStartResponse(sessionId, startedAt);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "session_start failed");
            return Result<SessionStartResponse>.Fail(ex.Message);
        }
    }

    // ── 2. session_end ──

    public async Task<Result<SessionEndResponse>> SessionEndAsync(SessionEndRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await using var lease = await _pool.AcquireWriterAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var conn = lease.Connection;

            var endedAt = DateTime.UtcNow;

            using var updateCmd = conn.CreateCommand();
            updateCmd.CommandText = "UPDATE Sessions SET EndedAt = $endedAt WHERE SessionId = $id";
            updateCmd.Parameters.AddWithValue("$endedAt", endedAt.ToString("O"));
            updateCmd.Parameters.AddWithValue("$id", request.SessionId);
            await updateCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            using var countCmd = conn.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM MemoryEntries WHERE SessionId = $id";
            countCmd.Parameters.AddWithValue("$id", request.SessionId);
            var entryCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false));

            return new SessionEndResponse(request.SessionId, endedAt, entryCount);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "session_end failed for {SessionId}", request.SessionId);
            return Result<SessionEndResponse>.Fail(ex.Message);
        }
    }

    // ── 3. session_list ──

    public async Task<Result<SessionListResponse>> SessionListAsync(SessionListRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await using var lease = await _pool.AcquireReaderAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var conn = lease.Connection;

            using var cmd = conn.CreateCommand();
            var sql = """
                SELECT s.SessionId, s.SessionName, s.StartedAt, s.EndedAt,
                       COALESCE(e.cnt, 0) AS EntryCount
                FROM Sessions s
                LEFT JOIN (SELECT SessionId, COUNT(*) AS cnt FROM MemoryEntries GROUP BY SessionId) e
                    ON e.SessionId = s.SessionId
                """;

            if (request.ActiveOnly)
                sql += " WHERE s.EndedAt IS NULL";

            sql += " ORDER BY s.StartedAt DESC";
            cmd.CommandText = sql;

            var sessions = new List<SessionInfo>();
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                sessions.Add(new SessionInfo(
                    SessionId: reader.GetString(0),
                    SessionName: reader.IsDBNull(1) ? null : reader.GetString(1),
                    StartedAt: DateTimeHelpers.ParseUtcDateTime(reader.GetString(2)),
                    EndedAt: reader.IsDBNull(3) ? null : DateTimeHelpers.ParseUtcDateTime(reader.GetString(3)),
                    EntryCount: reader.GetInt32(4)));
            }

            return new SessionListResponse(sessions);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "session_list failed");
            return Result<SessionListResponse>.Fail(ex.Message);
        }
    }

    // ── 4. memory_store ──

    public async Task<Result<MemoryStoreResponse>> StoreAsync(MemoryStoreRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await using var lease = await _pool.AcquireWriterAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var conn = lease.Connection;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO MemoryEntries (Key, Value, Category, Tags, SessionId, Metadata)
                VALUES ($key, $value, $category, $tags, $sessionId, $metadata)
                """;
            cmd.Parameters.AddWithValue("$key", request.Key);
            cmd.Parameters.AddWithValue("$value", request.Value);
            cmd.Parameters.AddWithValue("$category", request.Category ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$tags", JsonHelpers.SerializeTags(request.Tags));
            cmd.Parameters.AddWithValue("$sessionId", request.SessionId ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$metadata", request.Metadata ?? (object)DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            using var idCmd = conn.CreateCommand();
            idCmd.CommandText = "SELECT last_insert_rowid()";
            var id = (long)(await idCmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;

            using var readCmd = conn.CreateCommand();
            readCmd.CommandText = "SELECT StoredAt FROM MemoryEntries WHERE Id = $id";
            readCmd.Parameters.AddWithValue("$id", id);
            var storedAtStr = (string)(await readCmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
            var storedAt = DateTimeHelpers.ParseUtcDateTime(storedAtStr);

            return new MemoryStoreResponse(id, request.Key, storedAt);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "memory_store failed for key={Key}", request.Key);
            return Result<MemoryStoreResponse>.Fail(ex.Message);
        }
    }

    // ── 5. memory_retrieve ──

    public async Task<Result<MemoryRetrieveResponse>> RetrieveAsync(MemoryRetrieveRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (request.Id is null && request.Key is null)
            return Result<MemoryRetrieveResponse>.Fail("Either Id or Key must be provided");

        try
        {
            await using var lease = await _pool.AcquireReaderAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var conn = lease.Connection;

            using var cmd = conn.CreateCommand();
            if (request.Id is not null)
            {
                cmd.CommandText = "SELECT Id, Key, Value, Category, Tags, Metadata, StoredAt, UpdatedAt FROM MemoryEntries WHERE Id = $id";
                cmd.Parameters.AddWithValue("$id", request.Id.Value);
            }
            else
            {
                cmd.CommandText = "SELECT Id, Key, Value, Category, Tags, Metadata, StoredAt, UpdatedAt FROM MemoryEntries WHERE Key = $key ORDER BY StoredAt DESC LIMIT 1";
                cmd.Parameters.AddWithValue("$key", request.Key!);
            }

            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            MemoryEntry? entry = null;
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
                entry = ReadMemoryEntry(reader);

            return new MemoryRetrieveResponse(entry);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "memory_retrieve failed for id={Id} key={Key}", request.Id, request.Key);
            return Result<MemoryRetrieveResponse>.Fail(ex.Message);
        }
    }

    // ── 6. memory_search ──

    public async Task<Result<MemorySearchResponse>> SearchAsync(MemorySearchRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            if (request.Limit <= 0 || request.Limit > 10000)
                return Result<MemorySearchResponse>.Fail("Limit must be between 1 and 10000");

            await using var lease = await _pool.AcquireReaderAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var conn = lease.Connection;

            using var cmd = conn.CreateCommand();
            var conditions = new List<string>();
            var relevanceCases = new List<string>();

            // Base text matching
            conditions.Add("(Key LIKE $queryLike ESCAPE '\\' OR Value LIKE $queryLike ESCAPE '\\' OR Category LIKE $queryLike ESCAPE '\\')");
            cmd.Parameters.AddWithValue("$queryLike", $"%{EscapeLikeWildcards(request.Query)}%");

            // Relevance scoring
            relevanceCases.Add("CASE WHEN Key LIKE $queryLike THEN 1.0 WHEN Value LIKE $queryLike THEN 0.7 ELSE 0.5 END");

            // Optional category filter (exact match)
            if (request.Category is not null)
            {
                conditions.Add("Category = $category");
                cmd.Parameters.AddWithValue("$category", request.Category);
            }

            // Optional tags filter (JSON contains check)
            if (request.Tags is not null)
            {
                for (var i = 0; i < request.Tags.Count; i++)
                {
                    var paramName = $"$tag{i}";
                    conditions.Add($"Tags LIKE {paramName}");
                    cmd.Parameters.AddWithValue(paramName, $"%\"{request.Tags[i]}\"%");
                }
            }

            var whereClause = string.Join(" AND ", conditions);
            var relevanceExpr = relevanceCases[0];

            cmd.CommandText = $"""
                SELECT Id, Key, Value, Category, Tags, {relevanceExpr} AS Relevance
                FROM MemoryEntries
                WHERE {whereClause}
                ORDER BY Relevance DESC
                LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$limit", request.Limit);

            var results = new List<MemorySearchResult>();
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                results.Add(new MemorySearchResult(
                    Id: reader.GetInt64(0),
                    Key: reader.GetString(1),
                    Value: reader.GetString(2),
                    Category: reader.IsDBNull(3) ? null : reader.GetString(3),
                    Tags: JsonHelpers.DeserializeTags(reader.IsDBNull(4) ? null : reader.GetString(4)),
                    Relevance: reader.GetDouble(5)));
            }

            return new MemorySearchResponse(results, results.Count);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "memory_search failed for query={Query}", request.Query);
            return Result<MemorySearchResponse>.Fail(ex.Message);
        }
    }

    // ── 7. memory_update ──

    public async Task<Result<MemoryUpdateResponse>> UpdateAsync(MemoryUpdateRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await using var lease = await _pool.AcquireWriterAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var conn = lease.Connection;

            var setClauses = new List<string>();
            using var cmd = conn.CreateCommand();

            if (request.Value is not null)
            {
                setClauses.Add("Value = $value");
                cmd.Parameters.AddWithValue("$value", request.Value);
            }

            if (request.Category is not null)
            {
                setClauses.Add("Category = $category");
                cmd.Parameters.AddWithValue("$category", request.Category);
            }

            if (request.Tags is not null)
            {
                setClauses.Add("Tags = $tags");
                cmd.Parameters.AddWithValue("$tags", JsonHelpers.SerializeTags(request.Tags));
            }

            if (request.Metadata is not null)
            {
                setClauses.Add("Metadata = $metadata");
                cmd.Parameters.AddWithValue("$metadata", request.Metadata);
            }

            setClauses.Add("UpdatedAt = datetime('now')");

            cmd.CommandText = $"UPDATE MemoryEntries SET {string.Join(", ", setClauses)} WHERE Id = $id";
            cmd.Parameters.AddWithValue("$id", request.Id);
            var rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (rows == 0)
                return Result<MemoryUpdateResponse>.Fail($"Memory entry not found: {request.Id}");

            using var readCmd = conn.CreateCommand();
            readCmd.CommandText = "SELECT UpdatedAt FROM MemoryEntries WHERE Id = $id";
            readCmd.Parameters.AddWithValue("$id", request.Id);
            var updatedAtStr = (string)(await readCmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
            var updatedAt = DateTimeHelpers.ParseUtcDateTime(updatedAtStr);

            return new MemoryUpdateResponse(request.Id, updatedAt);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "memory_update failed for id={Id}", request.Id);
            return Result<MemoryUpdateResponse>.Fail(ex.Message);
        }
    }

    // ── 8. memory_delete ──

    public async Task<Result<MemoryDeleteResponse>> DeleteAsync(MemoryDeleteRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (request.Id is null && request.Key is null)
            return Result<MemoryDeleteResponse>.Fail("Either Id or Key must be provided");

        try
        {
            await using var lease = await _pool.AcquireWriterAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var conn = lease.Connection;

            using var cmd = conn.CreateCommand();
            if (request.Id is not null)
            {
                cmd.CommandText = "DELETE FROM MemoryEntries WHERE Id = $id";
                cmd.Parameters.AddWithValue("$id", request.Id.Value);
            }
            else
            {
                cmd.CommandText = "DELETE FROM MemoryEntries WHERE Key = $key";
                cmd.Parameters.AddWithValue("$key", request.Key!);
            }

            var deleted = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            return new MemoryDeleteResponse(deleted);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "memory_delete failed for id={Id} key={Key}", request.Id, request.Key);
            return Result<MemoryDeleteResponse>.Fail(ex.Message);
        }
    }

    // ── 9. memory_list ──

    public async Task<Result<MemoryListResponse>> ListAsync(MemoryListRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            if (request.Page < 0 || request.PageSize <= 0 || request.PageSize > 10000)
                return Result<MemoryListResponse>.Fail("Invalid paging: Page must be >= 0, PageSize must be 1-10000");

            await using var lease = await _pool.AcquireReaderAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var conn = lease.Connection;

            var conditions = new List<string>();
            using var countCmd = conn.CreateCommand();
            using var selectCmd = conn.CreateCommand();

            if (request.Category is not null)
            {
                conditions.Add("Category = $category");
                countCmd.Parameters.AddWithValue("$category", request.Category);
                selectCmd.Parameters.AddWithValue("$category", request.Category);
            }

            if (request.SessionId is not null)
            {
                conditions.Add("SessionId = $sessionId");
                countCmd.Parameters.AddWithValue("$sessionId", request.SessionId);
                selectCmd.Parameters.AddWithValue("$sessionId", request.SessionId);
            }

            var whereClause = conditions.Count > 0
                ? "WHERE " + string.Join(" AND ", conditions)
                : "";

            // Get total count
            countCmd.CommandText = $"SELECT COUNT(*) FROM MemoryEntries {whereClause}";
            var totalCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false));

            // Get page
            var offset = request.Page * request.PageSize;
            selectCmd.CommandText = $"""
                SELECT Id, Key, Value, Category, Tags, Metadata, StoredAt, UpdatedAt
                FROM MemoryEntries
                {whereClause}
                ORDER BY StoredAt DESC
                LIMIT $limit OFFSET $offset
                """;
            selectCmd.Parameters.AddWithValue("$limit", request.PageSize);
            selectCmd.Parameters.AddWithValue("$offset", offset);

            var entries = new List<MemoryEntry>();
            using var reader = await selectCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                entries.Add(ReadMemoryEntry(reader));

            return new MemoryListResponse(entries, totalCount, request.Page, request.PageSize);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "memory_list failed");
            return Result<MemoryListResponse>.Fail(ex.Message);
        }
    }

    // ── 10. memory_consolidate ──

    public async Task<Result<MemoryConsolidateResponse>> ConsolidateAsync(MemoryConsolidateRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (request.OlderThanDays is < 0)
            return Result<MemoryConsolidateResponse>.Fail("OlderThanDays must be non-negative");

        try
        {
            await using var lease = await _pool.AcquireWriterAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var conn = lease.Connection;

            // Find duplicate keys: for each key with multiple entries, keep the newest
            var conditions = new List<string>();
            using var cmd = conn.CreateCommand();

            var dateFilter = "";
            if (request.OlderThanDays is not null)
            {
                dateFilter = "AND me.StoredAt < datetime('now', $olderThan)";
                cmd.Parameters.AddWithValue("$olderThan", $"-{request.OlderThanDays.Value} days");
            }

            var categoryFilter = "";
            if (request.Category is not null)
            {
                categoryFilter = "AND me.Category = $category";
                cmd.Parameters.AddWithValue("$category", request.Category);
            }

            // Delete older duplicates: entries where a newer entry with the same Key exists
            cmd.CommandText = $"""
                DELETE FROM MemoryEntries
                WHERE Id IN (
                    SELECT me.Id FROM MemoryEntries me
                    INNER JOIN (
                        SELECT Key, MAX(StoredAt) AS LatestStoredAt
                        FROM MemoryEntries
                        GROUP BY Key
                        HAVING COUNT(*) > 1
                    ) dup ON me.Key = dup.Key AND me.StoredAt < dup.LatestStoredAt
                    WHERE 1=1 {dateFilter} {categoryFilter}
                )
                """;

            var removed = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            // Retrieve SQLite changes() as a consolidation metric for logging/diagnostics
            using var countCmd = conn.CreateCommand();
            countCmd.CommandText = "SELECT changes()";
            var changesReported = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false));

            return new MemoryConsolidateResponse(ConsolidatedCount: removed, RemovedCount: changesReported);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "memory_consolidate failed");
            return Result<MemoryConsolidateResponse>.Fail(ex.Message);
        }
    }

    // ── 11. memory_export ──

    public async Task<Result<MemoryExportResponse>> ExportAsync(MemoryExportRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (request.MaxResults <= 0 || request.MaxResults > 100000)
            return Result<MemoryExportResponse>.Fail("MaxResults must be between 1 and 100000");

        try
        {
            await using var lease = await _pool.AcquireReaderAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var conn = lease.Connection;

            using var cmd = conn.CreateCommand();
            if (request.Category is not null)
            {
                cmd.CommandText = "SELECT Id, Key, Value, Category, Tags, Metadata, StoredAt, UpdatedAt FROM MemoryEntries WHERE Category = $category ORDER BY StoredAt LIMIT $maxResults";
                cmd.Parameters.AddWithValue("$category", request.Category);
            }
            else
            {
                cmd.CommandText = "SELECT Id, Key, Value, Category, Tags, Metadata, StoredAt, UpdatedAt FROM MemoryEntries ORDER BY StoredAt LIMIT $maxResults";
            }
            cmd.Parameters.AddWithValue("$maxResults", request.MaxResults);

            var entries = new List<MemoryEntry>();
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                entries.Add(ReadMemoryEntry(reader));

            var data = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });

            return new MemoryExportResponse(data, request.Format, entries.Count);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "memory_export failed");
            return Result<MemoryExportResponse>.Fail(ex.Message);
        }
    }

    // ── 12. memory_import ──

    public async Task<Result<MemoryImportResponse>> ImportAsync(MemoryImportRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            List<MemoryEntry>? entries;
            try
            {
                entries = JsonSerializer.Deserialize<List<MemoryEntry>>(request.Data);
            }
            catch (JsonException jex)
            {
                return Result<MemoryImportResponse>.Fail($"Invalid JSON: {jex.Message}");
            }

            if (entries is null || entries.Count == 0)
                return new MemoryImportResponse(0, 0, Array.Empty<string>());

            await using var lease = await _pool.AcquireWriterAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var conn = lease.Connection;

            var imported = 0;
            var skipped = 0;
            var errors = new List<string>();

            var insertVerb = request.MergeStrategy switch
            {
                "replace" => "INSERT OR REPLACE",
                "skip" => "INSERT OR IGNORE",
                "error" => "INSERT",
                _ => "INSERT OR IGNORE"
            };

            // Wrap entire import in a single transaction so partial failures roll back cleanly
            using var tx = conn.BeginTransaction();

            try
            {
                foreach (var entry in entries)
                {
                    try
                    {
                        using var cmd = conn.CreateCommand();
                        cmd.Transaction = tx;
                        cmd.CommandText = $"{insertVerb} INTO MemoryEntries (Key, Value, Category, Tags, Metadata) VALUES ($key, $value, $category, $tags, $metadata)";
                        cmd.Parameters.AddWithValue("$key", entry.Key);
                        cmd.Parameters.AddWithValue("$value", entry.Value);
                        cmd.Parameters.AddWithValue("$category", entry.Category ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("$tags", JsonHelpers.SerializeTags(entry.Tags));
                        cmd.Parameters.AddWithValue("$metadata", entry.Metadata ?? (object)DBNull.Value);

                        var affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                        if (affected > 0) imported++; else skipped++;
                    }
                    catch (SqliteException sqEx)
                    {
                        if (request.MergeStrategy == "error")
                            errors.Add($"Key '{entry.Key}': {sqEx.Message}");
                        else
                            skipped++;
                    }
                }

                tx.Commit();
            }
            catch
            {
                try { tx.Rollback(); } catch { }
                throw;
            }

            return new MemoryImportResponse(imported, skipped, errors);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "memory_import failed");
            return Result<MemoryImportResponse>.Fail(ex.Message);
        }
    }

    // ── 13. memory_stats ──

    public async Task<Result<MemoryStatsResponse>> StatsAsync(MemoryStatsRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await using var lease = await _pool.AcquireReaderAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var conn = lease.Connection;

            using var entryCountCmd = conn.CreateCommand();
            entryCountCmd.CommandText = "SELECT COUNT(*) FROM MemoryEntries";
            var totalEntries = Convert.ToInt32(await entryCountCmd.ExecuteScalarAsync(ct).ConfigureAwait(false));

            using var categoriesCmd = conn.CreateCommand();
            categoriesCmd.CommandText = "SELECT DISTINCT Category FROM MemoryEntries WHERE Category IS NOT NULL";
            var categories = new List<string>();
            using var catReader = await categoriesCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await catReader.ReadAsync(ct).ConfigureAwait(false))
                categories.Add(catReader.GetString(0));

            using var sessionCountCmd = conn.CreateCommand();
            sessionCountCmd.CommandText = "SELECT COUNT(*) FROM Sessions";
            var sessionCount = Convert.ToInt32(await sessionCountCmd.ExecuteScalarAsync(ct).ConfigureAwait(false));

            long dbSizeBytes = 0;
            try
            {
                var fi = new FileInfo(_pool.DatabasePath);
                if (fi.Exists)
                    dbSizeBytes = fi.Length;
            }
            catch
            {
                // File may not exist or be inaccessible
            }

            return new MemoryStatsResponse(totalEntries, categories, sessionCount, dbSizeBytes);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "memory_stats failed");
            return Result<MemoryStatsResponse>.Fail(ex.Message);
        }
    }

    // ── 14. memory_cleanup ──

    public async Task<Result<MemoryCleanupResponse>> CleanupAsync(MemoryCleanupRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var conditions = new List<string>();
            var parameters = new Dictionary<string, object>();

            if (request.OlderThanDays is not null)
            {
                conditions.Add("StoredAt < datetime('now', $olderThan)");
                parameters["$olderThan"] = $"-{request.OlderThanDays.Value} days";
            }

            if (request.Category is not null)
            {
                conditions.Add("Category = $category");
                parameters["$category"] = request.Category;
            }

            var whereClause = conditions.Count > 0
                ? "WHERE " + string.Join(" AND ", conditions)
                : "";

            if (request.DryRun)
            {
                await using var lease = await _pool.AcquireReaderAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                var conn = lease.Connection;

                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT COUNT(*) FROM MemoryEntries {whereClause}";
                foreach (var kvp in parameters)
                    cmd.Parameters.AddWithValue(kvp.Key, kvp.Value);

                var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));

                return new MemoryCleanupResponse(count, FreedBytes: 0, DryRun: true);
            }
            else
            {
                await using var lease = await _pool.AcquireWriterAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                var conn = lease.Connection;

                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"DELETE FROM MemoryEntries {whereClause}";
                foreach (var kvp in parameters)
                    cmd.Parameters.AddWithValue(kvp.Key, kvp.Value);

                var removed = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                return new MemoryCleanupResponse(removed, FreedBytes: 0, DryRun: false);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "memory_cleanup failed");
            return Result<MemoryCleanupResponse>.Fail(ex.Message);
        }
    }

    #region Private Helpers

    private static MemoryEntry ReadMemoryEntry(SqliteDataReader reader)
    {
        return new MemoryEntry(
            Id: reader.GetInt64(0),
            Key: reader.GetString(1),
            Value: reader.GetString(2),
            Category: reader.IsDBNull(3) ? null : reader.GetString(3),
            Tags: JsonHelpers.DeserializeTags(reader.IsDBNull(4) ? null : reader.GetString(4)),
            Metadata: reader.IsDBNull(5) ? null : reader.GetString(5),
            StoredAt: DateTimeHelpers.ParseUtcDateTime(reader.GetString(6)),
            UpdatedAt: reader.IsDBNull(7) ? null : DateTimeHelpers.ParseUtcDateTime(reader.GetString(7)));
    }

    private static string EscapeLikeWildcards(string input)
        => SqliteHelpers.EscapeLikeWildcards(input);

    #endregion
}
