namespace RoslynMcp.Shared.Contracts.Common;

/// <summary>
/// Source code location with file path and 0-based line/column positions.
/// </summary>
public record CodeLocation(
    string FilePath,
    /// <summary>0-based line number.</summary>
    int StartLine,
    /// <summary>0-based column number.</summary>
    int StartColumn,
    /// <summary>0-based line number.</summary>
    int EndLine,
    /// <summary>0-based column number.</summary>
    int EndColumn);

/// <summary>
/// Symbol identity information.
/// </summary>
public record SymbolInfo(
    string Name,
    string FullyQualifiedName,
    string Kind,
    string? ContainingType = null,
    string? ContainingNamespace = null);

/// <summary>
/// Source code range with 0-based line/column positions.
/// </summary>
public record CodeRange(
    /// <summary>0-based line number.</summary>
    int StartLine,
    /// <summary>0-based column number.</summary>
    int StartColumn,
    /// <summary>0-based line number.</summary>
    int EndLine,
    /// <summary>0-based column number.</summary>
    int EndColumn);

/// <summary>
/// Paginated result container.
/// </summary>
/// <typeparam name="T">Item type.</typeparam>
public record PagedResult<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    /// <summary>0-based page index.</summary>
    int Page,
    /// <summary>Items per page. Server caps at 200.</summary>
    int PageSize)
{
    /// <summary>Number of enrichment failures on this page (0 for non-enriched results).</summary>
    public int FailureCount { get; init; }
    public bool HasMore => ((long)Page + 1) * PageSize < TotalCount;
}
