namespace RoslynMcp.Shared;

/// <summary>
/// Centralized validation constants shared across tool wrappers and services.
/// </summary>
public static class ValidationLimits
{
    public const int MaxPageSize = 200;
    public const int MaxRecursionDepth = 10;
    public const int MaxDescendants = 500;
    public const int MaxImpactDepth = 5;
    public const int MaxOperationDepth = 50;
    public const int MaxImpactNodes = 1000;
    public const int MaxFrontierWidth = 100;
    public const int CallersPerDepthLevel = 10;
    public const int MaxPathSearchDepth = 5;
    public const int MaxGraphImpactDepth = 50;
    public const int MaxBfsNodes = 10_000;
    public const int MaxBfsEdges = 100_000;          // Edge cap = 10× node cap
    public const int MaxBfsQueueSize = 100_000;
    public const int MaxSqlParameters = 500;
    public const int MaxIdentifierLength = 256;
}
