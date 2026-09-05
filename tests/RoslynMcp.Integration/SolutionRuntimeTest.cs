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

    [Fact]
    public async Task FirstSelectionBindsAnUnselectedRuntimeWithoutCreatingTemporaryState()
    {
        EnsureMsBuildRegistered();
        var targetSolution = CreateSlnxFile("first-selection");
        var targetDataDirectory = DataDirectory(targetSolution);

        await using var provider = await MsBuildWorkspaceProvider.CreateAsync(
            solutionPath: null,
            _logger);
        provider.StartFileWatcher();
        await using var runtime = await SolutionRuntime.CreateAsync(
            provider,
            solutionPath: null,
            Migrations(),
            _logger);

        Assert.Null(provider.CurrentSolution);
        Assert.Null(provider.SolutionPath);
        Assert.Null(provider.WatchedDirectory);
        Assert.Null(runtime.Config.ConfigDirectory);
        Assert.False(Directory.Exists(targetDataDirectory));
        Assert.Throws<InvalidOperationException>(() => _ = runtime.DatabasePath);

        var response = await runtime.SwitchAsync(targetSolution, warmUp: false);

        Assert.Null(response.PreviousSolutionPath);
        Assert.Equal(Path.GetFullPath(targetSolution), provider.SolutionPath);
        Assert.Equal(Path.GetDirectoryName(targetSolution), provider.WatchedDirectory);
        Assert.Equal(Path.Combine(targetDataDirectory, "roslyn-mcp.db"), runtime.DatabasePath);
        Assert.True(File.Exists(runtime.DatabasePath));
    }

    [Fact]
    public async Task SelectorsUseTheRetainedSolutionPathAfterIdleUnload()
    {
        EnsureMsBuildRegistered();
        var initialSolution = CreateSlnxFile("idle-selection");
        var targetSolution = CreateSlnxFile("after-idle-selection");
        var initialRoot = Path.GetDirectoryName(initialSolution)!;
        Directory.CreateDirectory(Path.Combine(initialRoot, ".git"));

        await using var provider = await MsBuildWorkspaceProvider.CreateAsync(initialSolution, _logger);
        await using var runtime = await SolutionRuntime.CreateAsync(
            provider,
            initialSolution,
            Migrations(),
            _logger);
        using var selection = new WorkspaceSelectionService(provider, runtime, _logger);

        Assert.True(await provider.UnloadAsync());

        var sameRoot = await selection.SetSolutionRootAsync(
            new RoslynMcp.Shared.Contracts.Util.SetSolutionRootRequest(initialRoot));
        Assert.True(sameRoot.IsSuccess, sameRoot.Error?.Message);
        Assert.False(sameRoot.Value!.Changed);
        Assert.Equal(Path.GetFullPath(initialSolution), sameRoot.Value.SolutionPath);

        var switched = await selection.SetSolutionPathAsync(
            new RoslynMcp.Shared.Contracts.Util.SetSolutionPathRequest(targetSolution));
        Assert.True(switched.IsSuccess, switched.Error?.Message);
        Assert.Equal(Path.GetFullPath(initialSolution), switched.Value!.PreviousSolutionPath);
    }

    [Fact]
    public async Task FailedFirstSelectionLeavesRuntimeUnselectedAndRetryable()
    {
        var targetSolution = CreateSolutionFile("failed-first-selection");
        using var workspace = new SwitchingWorkspaceProvider(solutionPath: null)
        {
            FailNextReload = true
        };
        await using var runtime = await SolutionRuntime.CreateAsync(
            workspace,
            solutionPath: null,
            Migrations(),
            _logger);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.SwitchAsync(targetSolution, warmUp: false));

        Assert.Null(workspace.SolutionPath);
        Assert.Null(runtime.Config.ConfigDirectory);
        Assert.Throws<InvalidOperationException>(() => _ = runtime.DatabasePath);

        var retried = await runtime.SwitchAsync(targetSolution, warmUp: false);
        Assert.Equal(Path.GetFullPath(targetSolution), retried.SolutionPath);
        Assert.Null(retried.PreviousSolutionPath);
    }

    [Fact]
    public async Task SolutionChangedSubscriberFailureDoesNotSplitTheRuntimeContext()
    {
        EnsureMsBuildRegistered();
        var initialSolution = CreateSlnxFile("subscriber-initial");
        var targetSolution = CreateSlnxFile("subscriber-target");

        await using var provider = await MsBuildWorkspaceProvider.CreateAsync(initialSolution, _logger);
        await using var runtime = await SolutionRuntime.CreateAsync(
            provider,
            initialSolution,
            Migrations(),
            _logger);
        provider.SolutionChanged += (_, _) => throw new InvalidOperationException("simulated subscriber failure");

        var response = await runtime.SwitchAsync(targetSolution, warmUp: false);

        Assert.Equal(Path.GetFullPath(targetSolution), response.SolutionPath);
        Assert.Equal(Path.GetFullPath(targetSolution), provider.SolutionPath);
        Assert.Equal(Path.Combine(DataDirectory(targetSolution), "roslyn-mcp.db"), runtime.DatabasePath);
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

        private string? _solutionPath;

        public SwitchingWorkspaceProvider(string? solutionPath)
        {
            _solutionPath = solutionPath;
            _workspace = solutionPath == null ? new AdhocWorkspace() : CreateWorkspace(solutionPath);
        }

        public bool FailNextReload { get; set; }
        public bool HasSolution => _solutionPath != null;
        public Solution? CurrentSolution => _solutionPath == null ? null : _workspace.CurrentSolution;
        public string? SolutionPath => _solutionPath;
        public string? SolutionDirectory => _solutionPath == null ? null : Path.GetDirectoryName(_solutionPath);
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

            var oldSolution = CurrentSolution;
            var oldWorkspace = _workspace;
            _workspace = CreateWorkspace(solutionPath);
            _solutionPath = Path.GetFullPath(solutionPath);
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
