using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.Data.Sqlite;
using RoslynMcp.Core.Helpers;
using RoslynMcp.Core.Helpers.Migrations;
using RoslynMcp.Core.Services;
using RoslynMcp.Server.Providers;
using RoslynMcp.Server.Services;
using RoslynMcp.Shared;
using RoslynMcp.Shared.Contracts.Graph;
using Serilog;
using Xunit;

namespace RoslynMcp.Integration;

[Collection("workspace")]
public sealed class SolutionRuntimeTest : IDisposable
{
    private static readonly object s_locatorLock = new();
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "RoslynMcpTests",
        Guid.NewGuid().ToString("N"));
    private readonly ILogger _logger = new LoggerConfiguration().MinimumLevel.Error().CreateLogger();

    [Fact]
    public async Task SwitchMovesWorkspaceConfigAndGraphDatabaseTogether()
    {
        var initialSolution = CreateSolutionFile("initial");
        var targetSolution = CreateSolutionFile("target");
        var targetConfig = new ConfigManager(DataDirectory(targetSolution));
        targetConfig.Set("timeout.default", "77", out _);

        using var workspace = new SwitchingWorkspaceProvider(initialSolution);
        await using var runtime = await SolutionRuntime.CreateAsync(
            workspace,
            initialSolution,
            Migrations(),
            _logger);
        runtime.Config.Set("timeout.default", "41", out _);

        var graph = new GraphService(runtime, _logger, workspace);
        using (await runtime.EnterReadAsync())
        {
            var added = await graph.AddNodeAsync(new GraphAddNodeRequest("initial-node", "authored"));
            Assert.True(added.IsSuccess);
        }

        var response = await runtime.SwitchAsync(targetSolution, warmUp: false);

        Assert.Equal(Path.GetFullPath(targetSolution), response.SolutionPath);
        Assert.Equal(Path.GetFullPath(initialSolution), response.PreviousSolutionPath);
        Assert.Equal(Path.GetFullPath(targetSolution), workspace.CurrentSolution!.FilePath);
        Assert.Equal(Path.Combine(DataDirectory(targetSolution), "roslyn-mcp.db"), runtime.DatabasePath);
        Assert.Equal("77", runtime.Config.Get("timeout.default").Value);

        using (await runtime.EnterReadAsync())
        {
            var added = await graph.AddNodeAsync(new GraphAddNodeRequest("target-node", "authored"));
            Assert.True(added.IsSuccess);
        }

        Assert.Equal(1, CountNode(DataDirectory(initialSolution), "initial-node"));
        Assert.Equal(0, CountNode(DataDirectory(initialSolution), "target-node"));
        Assert.Equal(0, CountNode(DataDirectory(targetSolution), "initial-node"));
        Assert.Equal(1, CountNode(DataDirectory(targetSolution), "target-node"));
    }

    [Fact]
    public async Task FailedWorkspaceReloadKeepsOriginalDataContext()
    {
        var initialSolution = CreateSolutionFile("initial-failure");
        var targetSolution = CreateSolutionFile("target-failure");

        using var workspace = new SwitchingWorkspaceProvider(initialSolution) { FailNextReload = true };
        await using var runtime = await SolutionRuntime.CreateAsync(
            workspace,
            initialSolution,
            Migrations(),
            _logger);
        runtime.Config.Set("timeout.default", "41", out _);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.SwitchAsync(targetSolution, warmUp: false));

        Assert.Equal(Path.GetFullPath(initialSolution), workspace.CurrentSolution!.FilePath);
        Assert.Equal(Path.Combine(DataDirectory(initialSolution), "roslyn-mcp.db"), runtime.DatabasePath);
        Assert.Equal("41", runtime.Config.Get("timeout.default").Value);
    }

    [Fact]
    public async Task SwitchWaitsForCurrentReaders()
    {
        var initialSolution = CreateSolutionFile("initial-lock");
        var targetSolution = CreateSolutionFile("target-lock");

        using var workspace = new SwitchingWorkspaceProvider(initialSolution);
        await using var runtime = await SolutionRuntime.CreateAsync(
            workspace,
            initialSolution,
            Migrations(),
            _logger);

        var readLease = await runtime.EnterReadAsync();
        var switching = runtime.SwitchAsync(targetSolution, warmUp: false);

        await Task.Delay(100);
        Assert.False(switching.IsCompleted);

        readLease.Dispose();
        await switching;

        Assert.Equal(Path.GetFullPath(targetSolution), workspace.CurrentSolution!.FilePath);
    }

    [Fact]
    public async Task WorkspaceWatcherMovesToTheTargetSolutionDirectory()
    {
        EnsureMsBuildRegistered();
        var initialSolution = CreateSlnxFile("initial-watcher");
        var targetSolution = CreateSlnxFile("target-watcher");

        await using var provider = await MsBuildWorkspaceProvider.CreateAsync(
            initialSolution,
            _logger);
        provider.StartFileWatcher();

        Assert.Equal(Path.GetDirectoryName(initialSolution), provider.WatchedDirectory);

        await provider.ReloadSolutionAsync(targetSolution);

        Assert.Equal(Path.GetDirectoryName(targetSolution), provider.WatchedDirectory);
    }

    public void Dispose()
    {
        (_logger as IDisposable)?.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string CreateSolutionFile(string name)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name + ".sln");
        File.WriteAllText(path, "test solution");
        return path;
    }

    private string CreateSlnxFile(string name)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name + ".slnx");
        File.WriteAllText(path, "<Solution />");
        return path;
    }

    private static void EnsureMsBuildRegistered()
    {
        lock (s_locatorLock)
        {
            if (!MSBuildLocator.IsRegistered)
                MSBuildLocator.RegisterDefaults();
        }
    }

    private static string DataDirectory(string solutionPath) =>
        Path.Combine(Path.GetDirectoryName(solutionPath)!, ".roslyn-mcp-data");

    private static IMigration[] Migrations() =>
        new IMigration[]
        {
            new V1_MemoryTables(),
            new V2_GraphTables(),
            new V3_KBTables(),
            new V4_GraphProvenance()
        };

    private static int CountNode(string dataDirectory, string id)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataDirectory, "roslyn-mcp.db"),
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM GraphNodes WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private sealed class SwitchingWorkspaceProvider : IWorkspaceProvider, IDisposable
    {
        private AdhocWorkspace _workspace;

        public SwitchingWorkspaceProvider(string solutionPath)
        {
            _workspace = CreateWorkspace(solutionPath);
        }

        public bool FailNextReload { get; set; }
        public bool HasSolution => true;
        public Solution? CurrentSolution => _workspace.CurrentSolution;
        public string SolutionDirectory => Path.GetDirectoryName(CurrentSolution!.FilePath)!;
        public event EventHandler<SolutionChangedEventArgs>? SolutionChanged;

        public Task<bool> ReloadSolutionAsync(
            string solutionPath,
            bool warmUp = false,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (FailNextReload)
            {
                FailNextReload = false;
                throw new InvalidOperationException("simulated reload failure");
            }

            var oldSolution = _workspace.CurrentSolution;
            var oldWorkspace = _workspace;
            _workspace = CreateWorkspace(solutionPath);
            oldWorkspace.Dispose();
            SolutionChanged?.Invoke(this, new SolutionChangedEventArgs(oldSolution, _workspace.CurrentSolution));
            return Task.FromResult(true);
        }

        public Task<Document?> GetDocumentAsync(
            string filePath,
            ProjectId? projectId = null,
            CancellationToken ct = default) =>
            Task.FromResult<Document?>(null);

        public Task<IReadOnlyList<Document>> GetDocumentsAsync(
            string filePath,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Document>>(Array.Empty<Document>());

        public Task<Project?> GetProjectAsync(string projectName, CancellationToken ct = default) =>
            Task.FromResult<Project?>(null);

        public Task<bool> TryReloadDocumentAsync(string filePath, CancellationToken ct = default) =>
            Task.FromResult(false);

        public void Dispose() => _workspace.Dispose();

        private static AdhocWorkspace CreateWorkspace(string solutionPath)
        {
            var workspace = new AdhocWorkspace();
            workspace.AddSolution(SolutionInfo.Create(
                SolutionId.CreateNewId(),
                VersionStamp.Create(),
                solutionPath));
            workspace.AddProject(ProjectInfo.Create(
                ProjectId.CreateNewId(),
                VersionStamp.Create(),
                "TestProject",
                "TestProject",
                LanguageNames.CSharp));
            return workspace;
        }
    }
}
