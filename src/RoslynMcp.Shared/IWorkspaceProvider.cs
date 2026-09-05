using Microsoft.CodeAnalysis;

namespace RoslynMcp.Shared;

public class SolutionChangedEventArgs : EventArgs
{
    public Solution? OldSolution { get; }
    public Solution? NewSolution { get; }

    public SolutionChangedEventArgs(Solution? oldSolution, Solution? newSolution)
    {
        if (oldSolution is null && newSolution is null)
            throw new ArgumentException("At least one of oldSolution/newSolution must be non-null");
        OldSolution = oldSolution;
        NewSolution = newSolution;
    }
}

public interface IWorkspaceProvider
{
    bool HasSolution { get; }
    Solution? CurrentSolution { get; }
    string? SolutionPath { get; }
    Task<Document?> GetDocumentAsync(string filePath, ProjectId? projectId = null, CancellationToken ct = default);
    Task<IReadOnlyList<Document>> GetDocumentsAsync(string filePath, CancellationToken ct = default);
    Task<Project?> GetProjectAsync(string projectName, CancellationToken ct = default);
    event EventHandler<SolutionChangedEventArgs>? SolutionChanged;

    /// <summary>
    /// Reload a document from disk. Returns true if the document was found and updated.
    /// Host-specific: VS uses Workspace.TryApplyChanges, standalone reads from disk.
    /// </summary>
    Task<bool> TryReloadDocumentAsync(string filePath, CancellationToken ct = default);

    /// <summary>Solution directory for path resolution and security checks.</summary>
    string? SolutionDirectory { get; }

    /// <summary>
    /// Reload the workspace with a different solution file. Disposes the old workspace.
    /// Returns true if the new solution loaded successfully.
    /// </summary>
    Task<bool> ReloadSolutionAsync(string solutionPath, bool warmUp = false, CancellationToken ct = default);
}
