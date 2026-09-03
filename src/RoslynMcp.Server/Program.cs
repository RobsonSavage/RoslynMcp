using Microsoft.Build.Locator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;
using RoslynMcp.Core.Helpers;
using RoslynMcp.Core.Helpers.Migrations;
using RoslynMcp.Core.Services;
using RoslynMcp.Server.Providers;
using RoslynMcp.Server.Services;
using RoslynMcp.Shared;
using RoslynMcp.Shared.Contracts.Graph;
using Serilog;

// ── Logging ──

var logDir = Environment.GetEnvironmentVariable("ROSLYNMCP_LOG_DIR");
if (string.IsNullOrWhiteSpace(logDir))
    logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RoslynMcp", "logs");

try
{
    Directory.CreateDirectory(logDir);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed to create log directory: {ex.Message}, falling back to temp");
    logDir = Path.Combine(Path.GetTempPath(), "RoslynMcp", "logs");
    Directory.CreateDirectory(logDir);
}
var logPath = Path.Combine(logDir, $"server-{DateTime.UtcNow:yyyyMMdd}.log");

var logger = ServerLogging.CreateLogger(logPath);

Log.Logger = logger;
logger.Information("RoslynMcp Server starting");

try
{
    // ── Parse CLI args ──

    var commandLineSolutionPath = ParseArg(args, "--solution-path");
    var environmentSolutionPath = Environment.GetEnvironmentVariable("ROSLYNMCP_SOLUTION_PATH");
    var hasExplicitSolutionPin = !string.IsNullOrWhiteSpace(commandLineSolutionPath)
        || !string.IsNullOrWhiteSpace(environmentSolutionPath);
    var solutionPath = commandLineSolutionPath
        ?? environmentSolutionPath
        ?? SolutionDiscovery.Discover(Directory.GetCurrentDirectory(), logger);

    if (string.IsNullOrWhiteSpace(solutionPath))
    {
        logger.Fatal("No solution found. Pass --solution-path <path>, set ROSLYNMCP_SOLUTION_PATH, or start Claude inside a git repository that contains a .sln/.slnx file");
        Console.Error.WriteLine("Error: No solution resolved from CWD. Use --solution-path <path>, set ROSLYNMCP_SOLUTION_PATH, or start Claude inside a git repository that contains a .sln/.slnx file.");
        return 2;
    }

    var msbuildPath = ParseArg(args, "--msbuild-path");
    var importFrom = ParseArg(args, "--import-from");
    var warmUp = args.Contains("--warm-up", StringComparer.OrdinalIgnoreCase);

    // ── Register MSBuild (must be before any MSBuildWorkspace usage) ──

    if (!string.IsNullOrWhiteSpace(msbuildPath))
    {
        logger.Information("Using MSBuild from: {MsBuildPath}", msbuildPath);
        MSBuildLocator.RegisterMSBuildPath(msbuildPath);
    }
    else
    {
        MSBuildLocator.RegisterDefaults();
    }

    // ── Initialize workspace ──

    logger.Information("Opening solution: {SolutionPath}", solutionPath);
    await using var workspaceProvider = await MsBuildWorkspaceProvider.CreateAsync(solutionPath, logger, warmUp);

    // -- Initialize solution-scoped runtime --

    var migrations = new IMigration[]
    {
        new V1_MemoryTables(), new V2_GraphTables(), new V3_KBTables(), new V4_GraphProvenance()
    };
    await using var solutionRuntime = await SolutionRuntime.CreateAsync(
        workspaceProvider,
        solutionPath,
        migrations,
        logger);
    var configManager = solutionRuntime.Config;
    var workspaceSelection = new WorkspaceSelectionService(
        workspaceProvider,
        solutionRuntime,
        configManager,
        hasExplicitSolutionPin,
        logger);

    var retentionConfig = configManager.Get("logging.file_retention_days");
    var retentionDays = int.TryParse(retentionConfig.Value, out var rd) ? rd
        : int.TryParse(retentionConfig.DefaultValue, out var rdd) ? rdd
        : 7;
    ServerLogging.Prune(logDir, retentionDays, logger);

    // ── Build host with DI ──

    var builder = Host.CreateEmptyApplicationBuilder(settings: null);

    builder.Services.AddSerilog(logger);

    // Infrastructure singletons
    builder.Services.AddSingleton<Serilog.ILogger>(logger);
    builder.Services.AddSingleton<IWorkspaceProvider>(workspaceProvider);
    builder.Services.AddSingleton<ISqliteConnectionPool>(solutionRuntime);
    builder.Services.AddSingleton<ISolutionContextSwitcher>(solutionRuntime);
    builder.Services.AddSingleton<IWorkspaceSelectionService>(workspaceSelection);
    var symbolResolver = new SymbolResolver(logger);
    builder.Services.AddSingleton(symbolResolver);
    builder.Services.AddSingleton<IWorkspaceHelpers>(new WorkspaceHelpers(workspaceProvider, symbolResolver));

    if (importFrom != null)
    {
        var forceImport = args.Contains("--force", StringComparer.OrdinalIgnoreCase);
        var result = configManager.ImportFromV1(importFrom, forceImport, logger);
        logger.Information("Config import from {Path}: {Result}", importFrom, result);
    }

    builder.Services.AddSingleton(configManager);
    builder.Services.AddSingleton<IToolResultMapper>(new DefaultToolResultMapper(workspaceProvider));
    builder.Services.AddSingleton<IToolMetricsService>(new ToolMetricsService());

    // Core services (transient - created per tool class resolution)
    builder.Services.AddTransient(sp => new SearchService(
        sp.GetRequiredService<IWorkspaceProvider>(),
        sp.GetRequiredService<IWorkspaceHelpers>(),
        sp.GetRequiredService<Serilog.ILogger>()));

    builder.Services.AddTransient(sp => new StructureService(
        sp.GetRequiredService<IWorkspaceProvider>(),
        sp.GetRequiredService<IWorkspaceHelpers>(),
        sp.GetRequiredService<Serilog.ILogger>()));

    builder.Services.AddTransient(sp => new UtilService(
        sp.GetRequiredService<IWorkspaceProvider>(),
        sp.GetRequiredService<IWorkspaceHelpers>(),
        sp.GetRequiredService<ConfigManager>(),
        sp.GetRequiredService<IWorkspaceSelectionService>(),
        sp.GetRequiredService<Serilog.ILogger>()));

    builder.Services.AddTransient(sp => new AnalyzeService(
        sp.GetRequiredService<IWorkspaceProvider>(),
        sp.GetRequiredService<IWorkspaceHelpers>(),
        sp.GetRequiredService<Serilog.ILogger>()));

    builder.Services.AddTransient(sp => new RefactoringService(
        sp.GetRequiredService<IWorkspaceProvider>(),
        sp.GetRequiredService<IWorkspaceHelpers>(),
        sp.GetRequiredService<Serilog.ILogger>()));

    builder.Services.AddTransient(sp => new MemoryService(
        sp.GetRequiredService<ISqliteConnectionPool>(),
        sp.GetRequiredService<Serilog.ILogger>()));

    // Singleton so the stale/rebuilt version counters mean something: as a transient every tool
    // call got a fresh instance, which reported the graph stale unconditionally.
    var graphService = new GraphService(solutionRuntime, logger, workspaceProvider);
    builder.Services.AddSingleton(graphService);

    builder.Services.AddTransient(sp => new KBService(
        sp.GetRequiredService<ISqliteConnectionPool>(),
        sp.GetRequiredService<Serilog.ILogger>()));

    builder.Services.AddTransient(sp => new ApolloService(
        sp.GetRequiredService<IWorkspaceProvider>(),
        sp.GetRequiredService<Serilog.ILogger>()));

    // ── Idle workspace unload ──

    // No config file, or no such key, or an unparseable one: fall back to the declared default
    // rather than silently disabling idle unload.
    var idleConfig = configManager.Get("workspace.idle_unload_minutes");
    var idleMinutes = int.TryParse(idleConfig.Value, out var im) ? im
        : int.TryParse(idleConfig.DefaultValue, out var dm) ? dm
        : 0;
    workspaceProvider.StartIdleMonitor(TimeSpan.FromMinutes(idleMinutes));

    // ── Disk staleness ──

    var watchConfig = configManager.Get("workspace.watch_files");
    var watchFiles = bool.TryParse(watchConfig.Value, out var wf) ? wf
        : !bool.TryParse(watchConfig.DefaultValue, out var wd) || wd;
    if (watchFiles)
        workspaceProvider.StartFileWatcher();
    else
        logger.Information("File watching disabled; tools may answer from a stale snapshot");

    // ── Dependency graph ──

    // The graph is derived from the project list and project references, so a solution reload
    // invalidates all of it. Rebuilding walks the project graph without compiling anything.
    var rebuildConfig = configManager.Get("graph.auto_rebuild");
    var autoRebuildGraph = bool.TryParse(rebuildConfig.Value, out var ar) ? ar
        : bool.TryParse(rebuildConfig.DefaultValue, out var ad) && ad;

    void RebuildGraphInBackground()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var contextLease = await solutionRuntime.EnterReadAsync();
                var result = await graphService.RebuildAsync(new GraphRebuildRequest(FullRebuild: true));
                if (!result.IsSuccess)
                    logger.Warning("Dependency graph rebuild failed: {Error}", result.Error?.Message);
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "Dependency graph rebuild failed");
            }
        });
    }

    if (autoRebuildGraph)
    {
        RebuildGraphInBackground();
        workspaceProvider.SolutionChanged += (_, e) =>
        {
            // A null NewSolution is an idle unload; the reload that follows raises its own event.
            if (e.NewSolution != null)
                RebuildGraphInBackground();
        };
        logger.Information("Dependency graph auto-rebuild enabled");
    }

    // MCP server
    builder.Services
        .AddMcpServer(options => options.ServerInstructions =
            "Before the first Roslyn tool call in a workspace, and after any working-directory or git-worktree change, call set_solution_root with rootPath set to the client's absolute current workspace directory. " +
            "Do not retry when it reports that following is disabled by an explicit solution pin. A successful switch also changes the solution-scoped config and SQLite memory, knowledge-base, and graph data.")
        .WithStdioServerTransport()
        .WithToolsFromAssembly()
        .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, ct) =>
        {
            var toolName = context.Params?.Name;
            var needsWorkspace = NeedsWorkspace(toolName);
            IDisposable? contextLease = null;
            var requestEntered = false;
            try
            {
                // Solution-selection tools take the runtime write lease inside SolutionRuntime.
                // Every other tool takes a read lease so no caller can observe a mixed context.
                if (toolName is not ("set_solution_path" or "set_solution_root"))
                    contextLease = await solutionRuntime.EnterReadAsync(ct);

                // Reload the workspace if it was idle-unloaded, and hold the in-flight count for
                // the duration so the sweep cannot unload underneath this call.
                await workspaceProvider.BeginRequestAsync(needsWorkspace, ct);
                requestEntered = true;

                // Push any on-disk edits into the workspace before the tool reads it. Held inside
                // the in-flight guard so a sweep cannot unload mid-refresh.
                if (needsWorkspace)
                    await workspaceProvider.SyncPendingChangesAsync(ct);

                return await next(context, ct);
            }
            finally
            {
                if (requestEntered)
                    workspaceProvider.ExitRequest();
                contextLease?.Dispose();
            }
        }));

    // ── Run ──

    await builder.Build().RunAsync();
}
catch (Exception ex)
{
    logger.Fatal(ex, "RoslynMcp Server terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

return 0;

// ── Helpers ──

// Tools backed only by SQLite (memory, KB, sessions, config) never touch the Roslyn workspace, so
// they must not trigger a reload - otherwise a memory_store after an idle period pays a full
// solution load. get_workspace_status is excluded so it can report the unloaded state instead of
// ending it, and set_solution_path so it does not load the outgoing solution just to replace it.
static bool NeedsWorkspace(string? toolName)
{
    if (string.IsNullOrEmpty(toolName)) return true;

    if (toolName.StartsWith("memory_", StringComparison.Ordinal)
        || toolName.StartsWith("kb_", StringComparison.Ordinal)
        || toolName.StartsWith("session_", StringComparison.Ordinal)
        || toolName.StartsWith("config_", StringComparison.Ordinal))
        return false;

    return toolName is not ("tool_enabled" or "get_workspace_status" or "set_solution_path" or "set_solution_root");
}

// Note: values starting with "--" are treated as flags, not values. Use --key=value syntax for such values.
static string? ParseArg(string[] args, string name)
{
    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];

        // Support --key=value syntax
        if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            return arg[(name.Length + 1)..];

        // Support --key value syntax
        if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase)
            && i + 1 < args.Length
            && !args[i + 1].StartsWith("--"))
            return args[i + 1];
    }
    return null;
}
