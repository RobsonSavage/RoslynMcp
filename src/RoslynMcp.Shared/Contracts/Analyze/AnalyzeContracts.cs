using System.ComponentModel.DataAnnotations;
using RoslynMcp.Shared.Contracts.Common;

namespace RoslynMcp.Shared.Contracts.Analyze;

/// <summary>
/// Shared type locator: provide either <see cref="TypeName"/> OR (<see cref="FilePath"/> + <see cref="Line"/>).
/// When both are provided, FilePath/Line/Column takes precedence.
/// Line and Column are 0-based.
/// </summary>
public record TypeLocator(
    string? TypeName = null,
    string? FilePath = null,
    int? Line = null,
    int? Column = null) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (TypeName is null && FilePath is null)
            yield return new ValidationResult(
                "At least one of TypeName or FilePath must be provided",
                new[] { nameof(TypeName), nameof(FilePath) });

        if (FilePath is not null && Line is null)
            yield return new ValidationResult(
                "Line is required when FilePath is specified",
                new[] { nameof(Line) });
    }
}

// ── understand_type ──

/// <summary>
/// Locator: provide either TypeName OR (FilePath + Line + Column). If both are provided, FilePath/Line/Column takes precedence.
/// </summary>
public record UnderstandTypeRequest(
    string? TypeName = null,
    string? FilePath = null,
    int? Line = null,
    int? Column = null) : IValidatableObject
{
    /// <summary>Returns the embedded <see cref="TypeLocator"/> for this request.</summary>
    public TypeLocator ToLocator() => new(TypeName, FilePath, Line, Column);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        => ToLocator().Validate(validationContext);
}

public record UnderstandTypeResponse(
    SymbolInfo Symbol,
    string TypeKind,
    string Accessibility,
    IReadOnlyList<string> BaseTypes,
    IReadOnlyList<string> Interfaces,
    IReadOnlyList<MemberSummary> Members,
    int UsageCount,
    CodeLocation? Location = null,
    string? XmlDocSummary = null);

// ── understand_method ──

/// <summary>
/// Analyzes a method at the given file position. Line/column values are 0-based.
/// </summary>
public record UnderstandMethodRequest(
    [property: Required] string FilePath,
    int Line,
    int Column,
    int CallerDepth = 1);

public record UnderstandMethodResponse(
    SymbolInfo Symbol,
    string Signature,
    string ReturnType,
    IReadOnlyList<ParameterInfo> Parameters,
    CodeMetrics Metrics,
    IReadOnlyList<SymbolInfo> Callers,
    IReadOnlyList<SymbolInfo> Callees,
    CodeLocation? Location = null,
    string? BodySource = null);

// ── get_type_info ──

/// <summary>
/// Locator: provide either TypeName OR (FilePath + Line + Column). If both are provided, FilePath/Line/Column takes precedence.
/// </summary>
public record GetTypeInfoRequest(
    string? TypeName = null,
    string? FilePath = null,
    int? Line = null,
    int? Column = null) : IValidatableObject
{
    /// <summary>Returns the embedded <see cref="TypeLocator"/> for this request.</summary>
    public TypeLocator ToLocator() => new(TypeName, FilePath, Line, Column);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        => ToLocator().Validate(validationContext);
}

public record GetTypeInfoResponse(
    SymbolInfo Symbol,
    string TypeKind,
    string Accessibility,
    bool IsAbstract,
    bool IsSealed,
    bool IsStatic,
    bool IsGeneric,
    int GenericParameterCount,
    IReadOnlyList<MemberSummary> Members,
    CodeLocation? Location = null);

// ── get_class_hierarchy ──

/// <summary>
/// Locator: provide either TypeName OR (FilePath + Line + Column). If both are provided, FilePath/Line/Column takes precedence.
/// </summary>
public record GetClassHierarchyRequest(
    string? TypeName = null,
    string? FilePath = null,
    int? Line = null,
    int? Column = null,
    int MaxDescendants = 50) : IValidatableObject
{
    /// <summary>Returns the embedded <see cref="TypeLocator"/> for this request.</summary>
    public TypeLocator ToLocator() => new(TypeName, FilePath, Line, Column);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        => ToLocator().Validate(validationContext);
}

public record HierarchyNode(
    SymbolInfo Symbol,
    CodeLocation? Location = null,
    bool IsDirect = true);

public record GetClassHierarchyResponse(
    SymbolInfo TargetType,
    IReadOnlyList<HierarchyNode> Ancestors,
    IReadOnlyList<HierarchyNode> Descendants,
    int TotalDescendants = 0);

// ── get_type_members ──

/// <summary>
/// Locator: provide either TypeName OR (FilePath + Line + Column). If both are provided, FilePath/Line/Column takes precedence.
/// </summary>
public record GetTypeMembersRequest(
    string? TypeName = null,
    string? FilePath = null,
    int? Line = null,
    int? Column = null,
    string? KindFilter = null,
    bool IncludeInherited = false,
    int PageSize = 5,
    int Page = 0) : IValidatableObject
{
    /// <summary>Returns the embedded <see cref="TypeLocator"/> for this request.</summary>
    public TypeLocator ToLocator() => new(TypeName, FilePath, Line, Column);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        => ToLocator().Validate(validationContext);
}

public record GetTypeMembersResponse(
    SymbolInfo TargetType,
    PagedResult<MemberSummary> Members);

// ── get_method_body ──

/// <summary>
/// Retrieves the body source of a method at the given file position. Line/column values are 0-based.
/// </summary>
public record GetMethodBodyRequest(
    [property: Required] string FilePath,
    int Line,
    int Column);

public record GetMethodBodyResponse(
    SymbolInfo Symbol,
    string BodySource,
    CodeLocation Location,
    int LineCount);

// ── get_code_metrics ──

/// <summary>
/// Locator: provide either TypeName OR (FilePath + Line + Column). If both are provided, FilePath/Line/Column takes precedence.
/// </summary>
public record GetCodeMetricsRequest(
    string? TypeName = null,
    string? FilePath = null,
    int? Line = null,
    int? Column = null) : IValidatableObject
{
    /// <summary>Returns the embedded <see cref="TypeLocator"/> for this request.</summary>
    public TypeLocator ToLocator() => new(TypeName, FilePath, Line, Column);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        => ToLocator().Validate(validationContext);
}

public record CodeMetrics(
    int CyclomaticComplexity,
    int LinesOfCode,
    int MaintainabilityIndex,
    int ParameterCount = 0,
    int NestingDepth = 0,
    int ReturnPoints = 0);

public record GetCodeMetricsResponse(
    SymbolInfo Symbol,
    CodeMetrics Metrics);

// ── analyze_data_flow ──

/// <summary>
/// Analyzes data flow within a code range. Line/column values are 0-based.
/// </summary>
public record AnalyzeDataFlowRequest(
    [property: Required] string FilePath,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn);

public record DataFlowInfo(
    IReadOnlyList<string> VariablesDeclared,
    IReadOnlyList<string> DataFlowsIn,
    IReadOnlyList<string> DataFlowsOut,
    IReadOnlyList<string> ReadInside,
    IReadOnlyList<string> WrittenInside,
    IReadOnlyList<string> ReadOutside,
    IReadOnlyList<string> WrittenOutside,
    IReadOnlyList<string> AlwaysAssigned,
    IReadOnlyList<string> Captured,
    IReadOnlyList<string> UnsafeAddressTaken);

public record AnalyzeDataFlowResponse(
    DataFlowInfo DataFlow,
    CodeRange AnalyzedRange);

// ── impact_analysis ──

/// <summary>
/// Analyzes transitive impact of changes to a symbol. Line/column values are 0-based.
/// </summary>
public record ImpactAnalysisRequest(
    [property: Required] string FilePath,
    int Line,
    int Column,
    int Depth = 2,
    int PageSize = 5,
    int Page = 0);

public record ImpactNode(
    SymbolInfo Symbol,
    CodeLocation? Location = null,
    int DistanceFromSource = 0);

public record ImpactAnalysisResponse(
    SymbolInfo SourceSymbol,
    PagedResult<ImpactNode> ImpactedSymbols,
    int TotalDepthReached);

// ── find_unused_code ──

/// <summary>
/// Finds unused code symbols within a project or file scope.
/// </summary>
public record FindUnusedCodeRequest(
    string? ProjectName = null,
    string? FilePath = null,
    string? KindFilter = null,
    int PageSize = 5,
    int Page = 0);

public record UnusedCodeItem(
    SymbolInfo Symbol,
    CodeLocation Location,
    string Reason);

public record FindUnusedCodeResponse(
    PagedResult<UnusedCodeItem> UnusedItems);

// ── find_async_issues ──

/// <summary>
/// Scans for async/await anti-patterns within a project or file scope.
/// </summary>
public record FindAsyncIssuesRequest(
    string? ProjectName = null,
    string? FilePath = null,
    int PageSize = 5,
    int Page = 0);

public record AsyncIssue(
    string IssueKind,
    string Message,
    SymbolInfo? Symbol,
    CodeLocation Location);

public record FindAsyncIssuesResponse(
    PagedResult<AsyncIssue> Issues);

// ── find_performance_issues ──

/// <summary>
/// Scans for performance anti-patterns within a project or file scope.
/// </summary>
public record FindPerformanceIssuesRequest(
    string? ProjectName = null,
    string? FilePath = null,
    int PageSize = 5,
    int Page = 0);

public record PerformanceIssue(
    string IssueKind,
    string Message,
    string Severity,
    CodeLocation Location,
    string? SuggestedFix = null);

public record FindPerformanceIssuesResponse(
    PagedResult<PerformanceIssue> Issues);

// ── analyze_operations ──

/// <summary>
/// Analyzes the IOperation tree for a symbol at the given file position. Line/column values are 0-based.
/// </summary>
public record AnalyzeOperationsRequest(
    [property: Required] string FilePath,
    int Line,
    int Column,
    int MaxDepth = 3);

/// <summary>
/// A node in the IOperation tree. <see cref="Children"/> recursion is bounded by
/// <see cref="AnalyzeOperationsRequest.MaxDepth"/> (default 3, maximum
/// <see cref="ValidationLimits.MaxOperationDepth"/> = 50).
/// </summary>
public record OperationNode(
    string OperationKind,
    string? Type,
    string? Syntax,
    IReadOnlyList<OperationNode> Children);

public record AnalyzeOperationsResponse(
    SymbolInfo? ContainingSymbol,
    OperationNode RootOperation);
