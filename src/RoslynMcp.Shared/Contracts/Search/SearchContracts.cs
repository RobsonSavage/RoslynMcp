using System.ComponentModel.DataAnnotations;
using RoslynMcp.Shared.Contracts.Common;

namespace RoslynMcp.Shared.Contracts.Search;

// ── find_references ──

/// <param name="Line">0-based line number in the source file.</param>
/// <param name="Column">0-based column number in the source file.</param>
public record FindReferencesRequest(
    [property: Required] string FilePath,
    int Line,
    int Column,
    bool IncludeContext = false,
    int PageSize = 5,
    int Page = 0);

public record ReferenceItem(
    CodeLocation Location,
    string? ContainingMember = null,
    string? ContainingType = null,
    string? ContextLine = null,
    bool IsWriteAccess = false);

public record FindReferencesResponse(
    SymbolInfo TargetSymbol,
    PagedResult<ReferenceItem> References);

// ── find_implementations ──

/// <param name="Line">0-based line number in the source file.</param>
/// <param name="Column">0-based column number in the source file.</param>
public record FindImplementationsRequest(
    [property: Required] string FilePath,
    int Line,
    int Column,
    bool IncludeContext = false,
    int PageSize = 5,
    int Page = 0);

public record ImplementationItem(
    SymbolInfo Symbol,
    CodeLocation Location,
    string? ContextLine = null);

public record FindImplementationsResponse(
    SymbolInfo TargetSymbol,
    PagedResult<ImplementationItem> Implementations);

// ── find_callers ──

/// <param name="Line">0-based line number in the source file.</param>
/// <param name="Column">0-based column number in the source file.</param>
public record FindCallersRequest(
    [property: Required] string FilePath,
    int Line,
    int Column,
    bool IncludeContext = false,
    int PageSize = 5,
    int Page = 0);

public record CallerItem(
    SymbolInfo CallingSymbol,
    CodeLocation Location,
    string? ContextLine = null,
    bool IsDirect = true);

public record FindCallersResponse(
    SymbolInfo TargetSymbol,
    PagedResult<CallerItem> Callers);

// ── find_callees ──

/// <param name="Line">0-based line number in the source file.</param>
/// <param name="Column">0-based column number in the source file.</param>
public record FindCalleesRequest(
    [property: Required] string FilePath,
    int Line,
    int Column,
    bool IncludeContext = false,
    int PageSize = 5,
    int Page = 0);

public record CalleeItem(
    SymbolInfo CalledSymbol,
    CodeLocation CallSite,
    string? ContextLine = null);

public record FindCalleesResponse(
    SymbolInfo TargetSymbol,
    PagedResult<CalleeItem> Callees);

// ── find_definition ──

/// <param name="Line">0-based line number in the source file.</param>
/// <param name="Column">0-based column number in the source file.</param>
public record FindDefinitionRequest(
    [property: Required] string FilePath,
    int Line,
    int Column);

public record DefinitionItem(
    CodeLocation Location,
    string? SourceText = null,
    bool IsMetadataDefinition = false);

public record FindDefinitionResponse(
    SymbolInfo Symbol,
    PagedResult<DefinitionItem> Definitions);

// ── find_overrides ──

/// <param name="Line">0-based line number in the source file.</param>
/// <param name="Column">0-based column number in the source file.</param>
public record FindOverridesRequest(
    [property: Required] string FilePath,
    int Line,
    int Column,
    bool IncludeContext = false,
    int PageSize = 5,
    int Page = 0);

public record OverrideItem(
    SymbolInfo Symbol,
    CodeLocation Location,
    string? ContainingType = null,
    string? ContextLine = null);

public record FindOverridesResponse(
    SymbolInfo TargetSymbol,
    PagedResult<OverrideItem> Overrides);

// ── find_derived_types ──

/// <param name="Line">0-based line number in the source file.</param>
/// <param name="Column">0-based column number in the source file.</param>
public record FindDerivedTypesRequest(
    [property: Required] string FilePath,
    int Line,
    int Column,
    bool IncludeContext = false,
    int PageSize = 5,
    int Page = 0);

public record DerivedTypeItem(
    SymbolInfo Symbol,
    CodeLocation Location,
    bool IsDirect = true,
    string? ContextLine = null);

public record FindDerivedTypesResponse(
    SymbolInfo TargetSymbol,
    PagedResult<DerivedTypeItem> DerivedTypes);

// ── find_base_members ──

/// <param name="Line">0-based line number in the source file.</param>
/// <param name="Column">0-based column number in the source file.</param>
public record FindBaseMembersRequest(
    [property: Required] string FilePath,
    int Line,
    int Column,
    int PageSize = 5,
    int Page = 0);

public record BaseMemberItem(
    SymbolInfo Symbol,
    CodeLocation? Location,
    string RelationKind);

public record FindBaseMembersResponse(
    SymbolInfo TargetSymbol,
    PagedResult<BaseMemberItem> BaseMembers);

// ── find_entry_points ──

public record FindEntryPointsRequest(
    string? ProjectName = null,
    int PageSize = 5,
    int Page = 0);

public record EntryPointItem(
    SymbolInfo Symbol,
    CodeLocation Location,
    string Kind);

public record FindEntryPointsResponse(
    PagedResult<EntryPointItem> EntryPoints);

// ── find_extension_methods ──

/// <param name="Line">0-based line number in the source file (used with FilePath locator).</param>
/// <param name="Column">0-based column number in the source file (used with FilePath locator).</param>
public record FindExtensionMethodsRequest(
    string? TypeName = null,
    string? FilePath = null,
    int? Line = null,
    int? Column = null,
    int PageSize = 5,
    int Page = 0);

public record ExtensionMethodItem(
    SymbolInfo Symbol,
    CodeLocation Location,
    string ExtendedType,
    string? ContextLine = null);

public record FindExtensionMethodsResponse(
    string TargetType,
    PagedResult<ExtensionMethodItem> ExtensionMethods);

// ── find_attribute_usages ──

public record FindAttributeUsagesRequest(
    [property: Required] string AttributeName,
    int PageSize = 5,
    int Page = 0);

public record AttributeUsageItem(
    SymbolInfo DecoratedSymbol,
    CodeLocation Location,
    string? ContextLine = null);

public record FindAttributeUsagesResponse(
    string AttributeName,
    PagedResult<AttributeUsageItem> Usages);

// ── find_tests_for_type ──

/// <param name="Line">0-based line number in the source file (used with FilePath locator).</param>
/// <param name="Column">0-based column number in the source file (used with FilePath locator).</param>
public record FindTestsForTypeRequest(
    string? TypeName = null,
    string? FilePath = null,
    int? Line = null,
    int? Column = null,
    int PageSize = 5,
    int Page = 0);

public record TestItem(
    SymbolInfo TestClass,
    CodeLocation Location,
    string TestFramework,
    IReadOnlyList<string> TestMethodNames);

public record FindTestsForTypeResponse(
    string TargetType,
    PagedResult<TestItem> Tests);

// ── find_event_subscribers ──

/// <param name="Line">0-based line number in the source file.</param>
/// <param name="Column">0-based column number in the source file.</param>
public record FindEventSubscribersRequest(
    [property: Required] string FilePath,
    int Line,
    int Column,
    bool IncludeContext = false,
    int PageSize = 5,
    int Page = 0);

public record EventSubscriberItem(
    SymbolInfo Subscriber,
    CodeLocation Location,
    string SubscriptionKind,
    string? ContextLine = null);

public record FindEventSubscribersResponse(
    SymbolInfo TargetEvent,
    PagedResult<EventSubscriberItem> Subscribers);

// ── text_search ──

public record TextSearchRequest(
    [property: Required] string Pattern,
    bool IsRegex = false,
    bool CaseSensitive = false,
    string? FilePattern = null,
    string? ProjectName = null,
    int PageSize = 5,
    int Page = 0);

public record TextSearchMatch(
    string FilePath,
    int Line,
    int Column,
    string MatchedText,
    string? ContextLine = null);

public record TextSearchResponse(
    string Pattern,
    PagedResult<TextSearchMatch> Matches);
