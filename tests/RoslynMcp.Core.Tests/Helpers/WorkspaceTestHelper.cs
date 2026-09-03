using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Core.Helpers;
using RoslynMcp.Core.Services;
using RoslynMcp.Shared;
using RoslynMcp.Shared.Contracts.Util;

namespace RoslynMcp.Core.Tests.Helpers;

/// <summary>
/// Builder for creating AdhocWorkspace instances for unit tests.
/// Documents get predictable file paths: C:\test\{ProjectName}\{FileName}
/// </summary>
public sealed class WorkspaceTestHelper : IDisposable
{
    private readonly AdhocWorkspace _workspace;
    private readonly Dictionary<string, ProjectId> _projects = new(StringComparer.OrdinalIgnoreCase);

    public WorkspaceTestHelper()
    {
        _workspace = new AdhocWorkspace();
    }

    public AdhocWorkspace Workspace => _workspace;

    /// <summary>
    /// Sets the solution file path on the workspace. Must be called before AddProject/AddDocument.
    /// Uses reflection because AdhocWorkspace doesn't expose Solution.WithFilePath.
    /// </summary>
    public WorkspaceTestHelper WithSolutionPath(string solutionPath)
    {
        // AdhocWorkspace auto-creates a solution without a file path.
        // Clear it and re-create with the desired path.
        // WARNING: Uses non-public Roslyn API. May break on Roslyn updates. Monitor after package upgrades.
        var clearMethod = typeof(Workspace).GetMethod("ClearSolution",
            BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
        if (clearMethod is null)
            throw new InvalidOperationException(
                "Roslyn API changed: Workspace.ClearSolution(NonPublic|Instance) no longer exists. " +
                "Update WorkspaceTestHelper to use the new API or dispose/recreate the workspace.");
        clearMethod.Invoke(_workspace, null);
        _workspace.AddSolution(
            SolutionInfo.Create(SolutionId.CreateNewId(), VersionStamp.Create(), solutionPath));
        return this;
    }

    public WorkspaceTestHelper AddProject(string name, params string[] projectReferences)
    {
        var projectId = ProjectId.CreateNewId(name);
        var projectInfo = ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            name,
            name,
            LanguageNames.CSharp,
            metadataReferences: DefaultReferences,
            parseOptions: new CSharpParseOptions(LanguageVersion.Latest),
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        _workspace.AddProject(projectInfo);
        _projects[name] = projectId;

        foreach (var refName in projectReferences)
        {
            if (_projects.TryGetValue(refName, out var refId))
            {
                _workspace.TryApplyChanges(
                    _workspace.CurrentSolution.AddProjectReference(
                        projectId, new ProjectReference(refId)));
            }
        }

        return this;
    }

    public WorkspaceTestHelper AddDocument(string projectName, string fileName, string source)
    {
        if (!_projects.TryGetValue(projectName, out var projectId))
            throw new ArgumentException($"Project not found: {projectName}");

        var filePath = GetFilePath(projectName, fileName);
        var docId = DocumentId.CreateNewId(projectId, fileName);
        var docInfo = DocumentInfo.Create(
            docId,
            fileName,
            loader: TextLoader.From(
                TextAndVersion.Create(SourceText.From(source), VersionStamp.Create(), filePath)),
            filePath: filePath);

        _workspace.AddDocument(docInfo);
        return this;
    }

    public Document? GetDocument(string fileName)
    {
        foreach (var project in _workspace.CurrentSolution.Projects)
            foreach (var doc in project.Documents)
                if (doc.FilePath?.EndsWith(fileName, StringComparison.OrdinalIgnoreCase) == true
                    || doc.Name == fileName)
                    return doc;
        return null;
    }

    public static string GetFilePath(string projectName, string fileName) =>
        $@"C:\test\{projectName}\{fileName}";

    /// <summary>
    /// Finds a token in source code and returns its 0-based (line, column).
    /// Searches for the Nth occurrence (1-based) of the token.
    /// </summary>
    public static (int Line, int Column) FindPosition(string source, string token, int occurrence = 1)
    {
        int index = -1;
        for (int i = 0; i < occurrence; i++)
        {
            index = source.IndexOf(token, index + 1, StringComparison.Ordinal);
            if (index < 0)
                throw new ArgumentException(
                    $"Token '{token}' occurrence {occurrence} not found in source");
        }

        int line = 0;
        int lastNewline = -1;
        for (int i = 0; i < index; i++)
        {
            if (source[i] == '\n')
            {
                line++;
                lastNewline = i;
            }
        }
        int column = index - lastNewline - 1;
        return (line, column);
    }

    public IWorkspaceProvider CreateProvider() => new TestWorkspaceProvider(_workspace);

    public IWorkspaceHelpers CreateHelpers(Serilog.ILogger? logger = null)
    {
        var provider = CreateProvider();
        return new WorkspaceHelpers(provider, new SymbolResolver(logger ?? Serilog.Core.Logger.None));
    }

    public void Dispose() => _workspace.Dispose();

    private static readonly Lazy<MetadataReference[]> s_defaultReferences = new(() =>
    {
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var refs = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        };

        foreach (var dll in new[]
        {
            "System.Runtime.dll",
            "System.Collections.dll",
            "System.Linq.dll",
            "System.Threading.Tasks.dll",
            "System.Console.dll",
            "netstandard.dll",
            "System.ComponentModel.Annotations.dll",
            "System.ComponentModel.dll",
            "System.ComponentModel.Primitives.dll",
            "System.ObjectModel.dll",
        })
        {
            var path = Path.Combine(runtimeDir, dll);
            if (File.Exists(path))
                refs.Add(MetadataReference.CreateFromFile(path));
        }

        return refs.ToArray();
    });

    private static MetadataReference[] DefaultReferences => s_defaultReferences.Value;
}

public sealed class TestWorkspaceProvider : IWorkspaceProvider
{
    private readonly AdhocWorkspace _workspace;

    public TestWorkspaceProvider(AdhocWorkspace workspace) => _workspace = workspace;

    public bool HasSolution => true;
    public Solution? CurrentSolution => _workspace.CurrentSolution;
    public string SolutionDirectory => Path.GetTempPath();

#pragma warning disable CS0067
    public event EventHandler<SolutionChangedEventArgs>? SolutionChanged;
#pragma warning restore CS0067

    public Task<Document?> GetDocumentAsync(string filePath, ProjectId? projectId = null, CancellationToken ct = default)
    {
        foreach (var project in _workspace.CurrentSolution.Projects)
        {
            if (projectId != null && project.Id != projectId) continue;
            foreach (var doc in project.Documents)
            {
                if (string.Equals(doc.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult<Document?>(doc);
            }
        }
        return Task.FromResult<Document?>(null);
    }

    public Task<IReadOnlyList<Document>> GetDocumentsAsync(string filePath, CancellationToken ct = default)
    {
        var docs = new List<Document>();
        foreach (var project in _workspace.CurrentSolution.Projects)
            foreach (var doc in project.Documents)
                if (string.Equals(doc.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                    docs.Add(doc);
        return Task.FromResult<IReadOnlyList<Document>>(docs);
    }

    public Task<Project?> GetProjectAsync(string projectName, CancellationToken ct = default)
    {
        var project = _workspace.CurrentSolution.Projects
            .FirstOrDefault(p => string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(project);
    }

    public Task<bool> TryReloadDocumentAsync(string filePath, CancellationToken ct = default)
    {
        // In tests, "reload" replaces the document text with the current text (no-op effectively).
        // For real reload tests, use UpdateDocumentText on the helper.
        foreach (var project in _workspace.CurrentSolution.Projects)
        {
            foreach (var doc in project.Documents)
            {
                if (string.Equals(doc.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                {
                    // Document exists - simulate successful reload
                    return Task.FromResult(true);
                }
            }
        }
        return Task.FromResult(false);
    }

    public Task<bool> ReloadSolutionAsync(string solutionPath, bool warmUp = false, CancellationToken ct = default)
        => throw new NotSupportedException("ReloadSolutionAsync is not supported in test workspace");
}

public sealed class TestWorkspaceSelectionService : IWorkspaceSelectionService
{
    public Task<Result<SetSolutionPathResponse>> SetSolutionPathAsync(
        SetSolutionPathRequest request,
        CancellationToken ct = default) =>
        throw new NotSupportedException("Solution switching is not supported by this test");

    public Task<Result<SetSolutionRootResponse>> SetSolutionRootAsync(
        SetSolutionRootRequest request,
        CancellationToken ct = default) =>
        throw new NotSupportedException("Workspace root switching is not supported by this test");
}
