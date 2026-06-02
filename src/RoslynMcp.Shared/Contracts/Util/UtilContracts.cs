using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using RoslynMcp.Shared.Contracts.Common;

namespace RoslynMcp.Shared.Contracts.Util;

// ── validate_text ──

public record ValidateTextRequest(
    [property: Required] string FilePath,
    [property: Required] string Text);

public record ValidateTextResponse(
    string FilePath,
    IReadOnlyList<DiagnosticItem> Diagnostics,
    bool IsValid);

// ── reload_file ──

public record ReloadFileRequest(
    [property: Required] string FilePath);

public record ReloadFileResponse(
    string FilePath,
    bool Success,
    string? Message = null);

// ── get_workspace_status ──

public record GetWorkspaceStatusRequest();

public record WorkspaceStatusResponse(
    string SolutionPath,
    int ProjectCount,
    int DocumentCount,
    int ErrorCount,
    int WarningCount,
    bool IsFullyLoaded,
    IReadOnlyDictionary<string, object>? Metrics = null);

// ── get_errors ──

public record GetErrorsRequest(
    string? FilePath = null,
    string? ProjectName = null,
    int PageSize = 5,
    int Page = 0);

public record ErrorsResponse(
    PagedResult<DiagnosticItem> Errors);

// ── get_warnings ──

public record GetWarningsRequest(
    string? FilePath = null,
    string? ProjectName = null,
    int PageSize = 5,
    int Page = 0);

public record WarningsResponse(
    PagedResult<DiagnosticItem> Warnings);

// ── get_quick_fixes ──

public record GetQuickFixesRequest(
    [property: Required] string FilePath,
    int Line,
    int Column);

public record QuickFixItem(
    string Title,
    string ProviderName,
    string? EquivalenceKey = null);

public record QuickFixesResponse(
    SymbolInfo? Symbol,
    IReadOnlyList<QuickFixItem> Fixes);

// ── suggest_refactorings ──

public record SuggestRefactoringsRequest(
    [property: Required] string FilePath,
    int Line,
    int Column);

public record RefactoringSuggestion(
    string Title,
    string ProviderName,
    string? EquivalenceKey = null);

public record SuggestRefactoringsResponse(
    IReadOnlyList<RefactoringSuggestion> Suggestions);

// ── get_full_context ──

public record GetFullContextRequest(
    [property: Required] string FilePath,
    int Line,
    int Column,
    int Depth = 2);

public record ContextNode(
    SymbolInfo Symbol,
    CodeLocation Location,
    string Relationship,
    IReadOnlyList<ContextNode>? Children = null);

public record FullContextResponse(
    SymbolInfo RootSymbol,
    IReadOnlyList<ContextNode> Context);

// ── set_solution_path ──

public record SetSolutionPathRequest(
    [property: Required] string SolutionPath,
    bool WarmUp = false);

public record SetSolutionPathResponse(
    string SolutionPath,
    int ProjectCount,
    int DocumentCount,
    string? PreviousSolutionPath = null);

// ── config_get ──

public record ConfigGetRequest(
    [property: Required] string Key);

public record ConfigGetResponse(
    string Key,
    string? Value,
    string? DefaultValue,
    string Type,
    string? Description = null);

// ── config_set ──

public record ConfigSetRequest(
    [property: Required] string Key,
    [property: Required] string Value);

public record ConfigSetResponse(
    string Key,
    string Value,
    string? PreviousValue = null);

// ── config_list ──

public record ConfigListRequest();

public record ConfigEntry(
    string Key,
    string? Value,
    string DefaultValue,
    string Type,
    string Description);

public record ConfigListResponse(
    IReadOnlyList<ConfigEntry> Entries);

// ── tool_enabled ──

public record ToolEnabledRequest(
    [property: Required] string ToolName,
    bool? Enabled = null);

public record ToolEnabledResponse(
    string ToolName,
    bool Enabled,
    bool WasChanged = false);
