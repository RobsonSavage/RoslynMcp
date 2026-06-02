using System.ComponentModel.DataAnnotations;
using RoslynMcp.Shared.Contracts.Common;

namespace RoslynMcp.Shared.Contracts.Memory;

// ── session_start ──

public record SessionStartRequest(string? SessionName = null, string? Metadata = null);
public record SessionStartResponse(string SessionId, DateTime StartedAt);

// ── session_end ──

public record SessionEndRequest(string SessionId);
public record SessionEndResponse(string SessionId, DateTime EndedAt, int EntryCount);

// ── session_list ──

public record SessionListRequest(bool ActiveOnly = false);
public record SessionInfo(string SessionId, string? SessionName, DateTime StartedAt, DateTime? EndedAt, int EntryCount);
public record SessionListResponse(IReadOnlyList<SessionInfo> Sessions);

// ── memory_store ──

public record MemoryStoreRequest(
    string Key,
    string Value,
    string? Category = null,
    IReadOnlyList<string>? Tags = null,
    string? SessionId = null,
    string? Metadata = null);
public record MemoryStoreResponse(long Id, string Key, DateTime StoredAt);

// ── memory_retrieve ──

public record MemoryRetrieveRequest(string? Key = null, long? Id = null) : IValidatableObject
{
    public static MemoryRetrieveRequest ByKey(string key) => new(Key: key);
    public static MemoryRetrieveRequest ById(long id) => new(Id: id);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Key is null && Id is null)
            yield return new ValidationResult(
                "At least one of Key or Id must be provided",
                new[] { nameof(Key), nameof(Id) });
    }
}
public record MemoryEntry(
    long Id,
    string Key,
    string Value,
    string? Category,
    IReadOnlyList<string> Tags,
    string? Metadata,
    DateTime StoredAt,
    DateTime? UpdatedAt);
public record MemoryRetrieveResponse(MemoryEntry? Entry);

// ── memory_search ──

public record MemorySearchRequest(
    string Query,
    string? Category = null,
    IReadOnlyList<string>? Tags = null,
    int Limit = 20);
public record MemorySearchResult(
    long Id,
    string Key,
    string Value,
    string? Category,
    IReadOnlyList<string> Tags,
    double Relevance);
public record MemorySearchResponse(IReadOnlyList<MemorySearchResult> Results, int TotalCount);

// ── memory_update ──

public record MemoryUpdateRequest(
    long Id,
    string? Value = null,
    string? Category = null,
    IReadOnlyList<string>? Tags = null,
    string? Metadata = null);
public record MemoryUpdateResponse(long Id, DateTime UpdatedAt);

// ── memory_delete ──

public record MemoryDeleteRequest(long? Id = null, string? Key = null) : IValidatableObject
{
    public static MemoryDeleteRequest ByKey(string key) => new(Key: key);
    public static MemoryDeleteRequest ById(long id) => new(Id: id);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Key is null && Id is null)
            yield return new ValidationResult(
                "At least one of Key or Id must be provided",
                new[] { nameof(Key), nameof(Id) });
    }
}
public record MemoryDeleteResponse(int DeletedCount);

// ── memory_list ──

public record MemoryListRequest(
    string? Category = null,
    string? SessionId = null,
    int Page = 0,
    int PageSize = 20);
public record MemoryListResponse(
    IReadOnlyList<MemoryEntry> Entries,
    int TotalCount,
    int Page,
    int PageSize);

// ── memory_consolidate ──

public record MemoryConsolidateRequest(string? Category = null, int? OlderThanDays = null);
public record MemoryConsolidateResponse(int ConsolidatedCount, int RemovedCount);

// ── memory_export ──

public record MemoryExportRequest(string Format = ExportFormat.Json, string? Category = null, int MaxResults = 10000);
public record MemoryExportResponse(string Data, string Format, int EntryCount);

// ── memory_import ──

/// <summary>
/// Import memory entries from a serialized string.
/// </summary>
/// <param name="Data">Serialized data to import. Maximum 10 MB (10,485,760 characters).</param>
public record MemoryImportRequest(
    [property: MaxLength(10_485_760)] string Data,
    string Format = ExportFormat.Json,
    string MergeStrategy = MergeStrategyValues.Skip);
public record MemoryImportResponse(
    int ImportedCount,
    int SkippedCount,
    IReadOnlyList<string> Errors);

// ── memory_stats ──

public record MemoryStatsRequest();
public record MemoryStatsResponse(
    int TotalEntries,
    IReadOnlyList<string> Categories,
    int SessionCount,
    long DbSizeBytes);

// ── memory_cleanup ──

public record MemoryCleanupRequest(
    int? OlderThanDays = null,
    string? Category = null,
    bool DryRun = false);
public record MemoryCleanupResponse(int RemovedCount, long FreedBytes, bool DryRun);
