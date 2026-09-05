using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMcp.Shared;
using RoslynMcp.Shared.Contracts.Common;
using RoslynMcp.Shared.Contracts.Structure;

namespace RoslynMcp.Core.Services;

public partial class StructureService
{
    // ── 1. get_solution_structure ──

    public Task<Result<SolutionStructureResponse>> GetSolutionStructureAsync(
        GetSolutionStructureRequest request, CancellationToken ct = default)
    {
        var solution = _workspace.CurrentSolution;
        if (solution is null)
        {
            _logger.Warning("get_solution_structure: No solution loaded");
            return Task.FromResult(Result<SolutionStructureResponse>.Fail("No solution loaded"));
        }
        var projects = new List<ProjectSummary>();

        foreach (var project in solution.Projects)
        {
            ct.ThrowIfCancellationRequested();
            projects.Add(MapProjectSummary(project));
        }

        return Task.FromResult(Result<SolutionStructureResponse>.Ok(
            new SolutionStructureResponse(_workspace.SolutionPath ?? solution.FilePath ?? "", projects)));
    }

    // ── 2. get_project_structure ──

    public async Task<Result<ProjectStructureResponse>> GetProjectStructureAsync(
        GetProjectStructureRequest request, CancellationToken ct = default)
    {
        var solution = _workspace.CurrentSolution;
        if (solution is null)
        {
            _logger.Warning("get_project_structure: No solution loaded");
            return Result<ProjectStructureResponse>.Fail("No solution loaded");
        }
        var project = solution.Projects.FirstOrDefault(p =>
            string.Equals(p.Name, request.ProjectName, StringComparison.OrdinalIgnoreCase));

        if (project is null)
            return Result<ProjectStructureResponse>.Fail($"Project not found: {request.ProjectName}");

        var documents = new List<DocumentSummary>();
        if (request.IncludeDocuments)
        {
            foreach (var doc in project.Documents)
            {
                ct.ThrowIfCancellationRequested();
                int? lineCount = null;

                var relativePath = doc.FilePath != null && project.FilePath != null
                    ? GetRelativePath(doc.FilePath, Path.GetDirectoryName(project.FilePath)!)
                    : null;

                documents.Add(new DocumentSummary(doc.FilePath ?? doc.Name, relativePath, lineCount));
            }
        }

        var projectRefs = new List<ProjectReferenceInfo>();
        foreach (var projRef in project.ProjectReferences)
        {
            var refProject = solution.GetProject(projRef.ProjectId);
            if (refProject != null)
                projectRefs.Add(new ProjectReferenceInfo(refProject.Name, refProject.FilePath ?? ""));
        }

        var nugetRefs = new List<NuGetReferenceInfo>();
        foreach (var metaRef in project.MetadataReferences)
        {
            if (metaRef is PortableExecutableReference peRef && peRef.FilePath != null)
            {
                var name = Path.GetFileNameWithoutExtension(peRef.FilePath);
                nugetRefs.Add(new NuGetReferenceInfo(name));
            }
        }


        return new ProjectStructureResponse(
            MapProjectSummary(project), documents, projectRefs, nugetRefs);
    }

    // ── 4. get_dependency_graph ──

    public Task<Result<DependencyGraphResponse>> GetDependencyGraphAsync(
        GetDependencyGraphRequest request, CancellationToken ct = default)
    {
        var solution = _workspace.CurrentSolution;
        if (solution is null)
            return Task.FromResult(Result<DependencyGraphResponse>.Fail("No solution loaded"));
        var projectMap = solution.Projects.ToDictionary(p => p.Id);
        var nodes = new List<DependencyNode>();
        var visited = new HashSet<ProjectId>();

        IEnumerable<Project> roots;
        if (request.ProjectName != null)
        {
            var root = solution.Projects.FirstOrDefault(p =>
                string.Equals(p.Name, request.ProjectName, StringComparison.OrdinalIgnoreCase));
            if (root is null)
                return Task.FromResult(Result<DependencyGraphResponse>.Fail($"Project not found: {request.ProjectName}"));
            roots = new[] { root };
        }
        else
        {
            roots = solution.Projects;
        }

        foreach (var project in roots)
        {
            ct.ThrowIfCancellationRequested();
            WalkDependencies(project, projectMap, nodes, visited, 0, request.Depth, ct);
        }

        return Task.FromResult(Result<DependencyGraphResponse>.Ok(new DependencyGraphResponse(nodes)));
    }

    #region Private Helpers

    private static ProjectSummary MapProjectSummary(Project project)
    {
        var outputType = project.CompilationOptions switch
        {
            CSharpCompilationOptions opts => opts.OutputKind.ToString(),
            _ => null
        };

        return new ProjectSummary(
            Name: project.Name,
            FilePath: project.FilePath ?? "",
            TargetFramework: null, // Not directly available from Roslyn API
            OutputType: outputType,
            DocumentCount: project.Documents.Count());
    }

    private static void WalkDependencies(
        Project project,
        Dictionary<ProjectId, Project> projectMap,
        List<DependencyNode> nodes,
        HashSet<ProjectId> visited,
        int currentDepth,
        int maxDepth,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (maxDepth >= 0 && currentDepth > maxDepth) return;
        if (!visited.Add(project.Id)) return;

        var dependsOn = new List<string>();
        foreach (var projRef in project.ProjectReferences)
        {
            if (projectMap.TryGetValue(projRef.ProjectId, out var refProject))
                dependsOn.Add(refProject.Name);
        }

        nodes.Add(new DependencyNode(project.Name, dependsOn));

        foreach (var projRef in project.ProjectReferences)
        {
            if (projectMap.TryGetValue(projRef.ProjectId, out var refProject))
                WalkDependencies(refProject, projectMap, nodes, visited, currentDepth + 1, maxDepth, ct);
        }
    }

    private static string GetRelativePath(string fullPath, string basePath)
    {
        if (!basePath.EndsWith(Path.DirectorySeparatorChar.ToString()))
            basePath += Path.DirectorySeparatorChar;

        if (fullPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
            return fullPath.Substring(basePath.Length);

        return fullPath;
    }

    #endregion
}
