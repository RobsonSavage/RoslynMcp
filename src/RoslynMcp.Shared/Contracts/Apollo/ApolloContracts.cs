using RoslynMcp.Shared.Contracts.Common;

namespace RoslynMcp.Shared.Contracts.Apollo;

// ── apollo_diagnose ──

public record ApolloDiagnoseRequest(
    string? FilePath = null,
    string? ErrorId = null,
    string? ErrorMessage = null);
public record DiagnosticSuggestion(string Description, string? Code, string Category, string Confidence);
public record ApolloDiagnoseResponse(
    IReadOnlyList<DiagnosticItem> Diagnostics,
    string? RootCause,
    IReadOnlyList<DiagnosticSuggestion> Suggestions);

// ── apollo_isolate ──

public record ApolloIsolateRequest(string FilePath, string? ErrorId = null);
public record ApolloIsolateResponse(CodeRange? IsolatedRange, string? SuspectedCause, string Confidence);

// ── apollo_fix ──

public record ApolloFixRequest(
    string FilePath,
    string DiagnosticId,
    int FixIndex = 0,
    bool Preview = true);
public record ApolloFixChange(string FilePath, string OldText, string NewText, CodeRange Range);
public record ApolloFixResponse(
    IReadOnlyList<ApolloFixChange> Changes,
    bool Applied,
    int NewDiagnosticCount);

// ── apollo_validate ──

public record ApolloValidateRequest(string FilePath, string? OriginalDiagnosticId = null);
public record ApolloValidateResponse(
    bool Resolved,
    IReadOnlyList<DiagnosticItem> RemainingErrors,
    IReadOnlyList<DiagnosticItem> NewErrors);
