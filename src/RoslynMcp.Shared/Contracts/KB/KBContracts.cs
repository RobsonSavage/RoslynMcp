using RoslynMcp.Shared.Contracts.Common;

namespace RoslynMcp.Shared.Contracts.KB;

// ── kb_add ──

public record KBAddRequest(
    string Title,
    string Content,
    string? Category = null,
    IReadOnlyList<string>? Tags = null,
    string? Metadata = null);
public record KBAddResponse(long Id, DateTime CreatedAt);

// ── kb_get ──

public record KBGetRequest(long Id);
public record KBEntry(
    long Id,
    string Title,
    string Content,
    string? Category,
    IReadOnlyList<string> Tags,
    string? Metadata,
    DateTime CreatedAt,
    DateTime? UpdatedAt);
public record KBGetResponse(KBEntry? Entry);

// ── kb_update ──

public record KBUpdateRequest(
    long Id,
    string? Title = null,
    string? Content = null,
    string? Category = null,
    IReadOnlyList<string>? Tags = null,
    string? Metadata = null);
public record KBUpdateResponse(long Id, DateTime UpdatedAt);

// ── kb_delete ──

public record KBDeleteRequest(long Id);
public record KBDeleteResponse(bool Deleted);

// ── kb_list ──

public record KBListRequest(string? Category = null, int Page = 0, int PageSize = 20);
public record KBListResponse(PagedResult<KBEntry> Results)
{
    /// <summary>Backward-compatible constructor matching the legacy (Entries, TotalCount, Page, PageSize) shape.</summary>
    public KBListResponse(IReadOnlyList<KBEntry> Entries, int TotalCount, int Page, int PageSize)
        : this(new PagedResult<KBEntry>(Entries, TotalCount, Page, PageSize)) { }
}

// ── kb_search ──

public record KBSearchRequest(
    string Query,
    string? Category = null,
    IReadOnlyList<string>? Tags = null,
    int Limit = 20,
    bool UseFts = true);
public record KBSearchResult(
    long Id,
    string Title,
    string? Category,
    IReadOnlyList<string> Tags,
    double Relevance,
    string? Snippet);
public record KBSearchResponse(IReadOnlyList<KBSearchResult> Results, int TotalCount, bool UsedFts);

// ── kb_related ──

public record KBRelatedRequest(long Id, int Limit = 5);
public record KBRelatedResponse(IReadOnlyList<KBSearchResult> Related, string Method);

// ── kb_stats ──

public record KBStatsRequest();
public record KBStatsResponse(
    int TotalEntries,
    IReadOnlyList<string> Categories,
    bool FtsAvailable,
    long DbSizeBytes);
