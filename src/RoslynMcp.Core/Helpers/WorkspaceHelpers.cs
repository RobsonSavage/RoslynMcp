using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Shared;
using RoslynMcp.Core.Services;

namespace RoslynMcp.Core.Helpers;

public interface IWorkspaceHelpers
{
    Task<(Document? Doc, ISymbol? Symbol, string? Error)> ResolveAsync(
        string filePath, int line, int column, CancellationToken ct);

    Task<(INamedTypeSymbol? Type, string? Error)> ResolveTypeAsync(
        string? typeName, string? filePath, int? line, int? column, CancellationToken ct,
        Solution? externalSolution = null);

    Document? GetDocumentByPath(Solution solution, string? filePath);
}

/// <summary>
/// Shared helper methods extracted from SearchService and StructureService to eliminate duplication.
/// </summary>
public class WorkspaceHelpers : IWorkspaceHelpers
{
    private readonly IWorkspaceProvider _workspace;
    private readonly SymbolResolver _symbolResolver;

    public WorkspaceHelpers(IWorkspaceProvider workspace, SymbolResolver symbolResolver)
    {
        _workspace = workspace;
        _symbolResolver = symbolResolver;
    }

    public async Task<(Document? Doc, ISymbol? Symbol, string? Error)> ResolveAsync(
        string filePath, int line, int column, CancellationToken ct)
    {
        if (!_workspace.HasSolution)
            return (null, null, "No solution loaded");

        var doc = await _workspace.GetDocumentAsync(filePath, ct: ct);
        if (doc is null)
            return (null, null, $"Document not found: {filePath}");

        var result = await _symbolResolver.ResolveSymbolAsync(doc, line, column, ct);
        if (!result.IsSuccess)
            return (doc, null, result.Error!.Message);

        return (doc, result.Value, null);
    }

    public async Task<(INamedTypeSymbol? Type, string? Error)> ResolveTypeAsync(
        string? typeName, string? filePath, int? line, int? column, CancellationToken ct,
        Solution? externalSolution = null)
    {
        if (filePath != null && line.HasValue && column.HasValue)
        {
            var (_, symbol, error) = await ResolveAsync(filePath, line.Value, column.Value, ct);
            if (error != null) return (null, error);
            var type = symbol switch
            {
                INamedTypeSymbol t => t,
                _ => symbol?.ContainingType
            };
            if (type is null) return (null, "Could not resolve type at position");
            return (type, null);
        }

        var solution = externalSolution ?? _workspace.CurrentSolution;
        if (typeName != null && solution != null)
        {
            foreach (var project in solution.Projects)
            {
                ct.ThrowIfCancellationRequested();
                var compilation = await project.GetCompilationAsync(ct);
                if (compilation is null) continue;

                var type = compilation.GetTypeByMetadataName(typeName);
                if (type != null) return (type, null);

                // Fallback: simple-name lookup via declaration index.
                // Includes referenced-assembly symbols (acceptable for this use case).
                // Cancellation propagates via ct -> OperationCanceledException (matches prior behavior).
                var fallback = compilation.GetSymbolsWithName(typeName, SymbolFilter.Type, ct)
                    .OfType<INamedTypeSymbol>()
                    .FirstOrDefault();
                if (fallback != null) return (fallback, null);
            }
            return (null, $"Type not found: {typeName}");
        }

        return (null, "Either typeName or filePath/line/column must be provided");
    }

    /// <inheritdoc />
    Document? IWorkspaceHelpers.GetDocumentByPath(Solution solution, string? filePath)
        => GetDocumentByPath(solution, filePath);

    /// <summary>
    /// Finds a document by file path using Roslyn's internal index. O(1) lookup.
    /// </summary>
    public static Document? GetDocumentByPath(Solution solution, string? filePath)
    {
        if (filePath is null) return null;
        var docIds = solution.GetDocumentIdsWithFilePath(filePath);
        return docIds.Length > 0 ? solution.GetDocument(docIds[0]) : null;
    }

    /// <summary>
    /// Safely converts 0-based line/column to a text position. Returns Result.Fail on out-of-range values.
    /// </summary>
    public static Result<int> GetSafePosition(SourceText text, int line, int column)
    {
        if (line < 0 || line >= text.Lines.Count)
            return Result<int>.Fail(
                $"Line {line} is out of range (0-{text.Lines.Count - 1})", "INVALID_POSITION");

        var lineObj = text.Lines[line];
        var lineLength = lineObj.End - lineObj.Start;
        if (column < 0 || column > lineLength)
            return Result<int>.Fail(
                $"Column {column} is out of range for line {line} (0-{lineLength})", "INVALID_POSITION");

        return text.Lines.GetPosition(new LinePosition(line, column));
    }

}
