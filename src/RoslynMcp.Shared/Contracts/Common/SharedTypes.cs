namespace RoslynMcp.Shared.Contracts.Common;

public record DiagnosticItem(
    string Id,
    string Message,
    string Severity,
    CodeLocation Location,
    string? Category = null);

public record MemberSummary(
    string Name,
    string Kind,
    string ReturnType,
    string Accessibility,
    bool IsStatic = false,
    bool IsAbstract = false,
    bool IsVirtual = false,
    bool IsOverride = false,
    string? Summary = null);

public record ParameterInfo(
    string Name,
    string Type,
    bool IsOptional = false,
    string? DefaultValue = null,
    bool IsParams = false,
    bool IsRef = false,
    bool IsOut = false);

public record ProjectSummary(
    string Name,
    string FilePath,
    string? TargetFramework = null,
    string? OutputType = null,
    int DocumentCount = 0);

public static class GraphDirection
{
    public const string Incoming = "incoming";
    public const string Outgoing = "outgoing";
    public const string Both = "both";
}

public static class ExportFormat
{
    public const string Json = "json";
    public const string Mermaid = "mermaid";
    public const string Dot = "dot";
}

public static class MergeStrategyValues
{
    public const string Skip = "skip";
    public const string Overwrite = "overwrite";
    public const string Merge = "merge";
    public const string Replace = "replace";
    public const string Error = "error";
}

public static class ConfidenceLevel
{
    public const string High = "High";
    public const string Medium = "Medium";
    public const string Low = "Low";
}
