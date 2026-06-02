using System.Security.Cryptography;
using System.Text;
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

var logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.File(
        logPath,
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7,
        fileSizeLimitBytes: 50 * 1024 * 1024)
    .WriteTo.Console(Serilog.Events.LogEventLevel.Warning, standardErrorFromLevel: Serilog.Events.LogEventLevel.Verbose)
    .CreateLogger();

Log.Logger = logger;
logger.Information("RoslynMcp Server starting");

try
{
    // ── Parse CLI args ──

    var solutionPath = ParseArg(args, "--solution-path")
        ?? Environment.GetEnvironmentVariable("ROSLYNMCP_SOLUTION_PATH")
        ?? DiscoverSolution(Directory.GetCurrentDirectory(), logger);

    if (string.IsNullOrWhiteSpace(solutionPath))
    {
        logger.Fatal("No solution found. Pass --solution-path <path>, set ROSLYNMCP_SOLUTION_PATH, or start from a directory containing a .sln/.slnx file");
        Console.Error.WriteLine("Error: No .sln/.slnx found. Use --solution-path <path>, set ROSLYNMCP_SOLUTION_PATH, or start Claude from a directory containing a .sln/.slnx file.");
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

    // ── Data directory ──

    var solutionDir = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
    var dataDir = Path.Combine(solutionDir, ".roslyn-mcp-data");

    try
    {
        Directory.CreateDirectory(dataDir);
    }
    catch (UnauthorizedAccessException)
    {
        // Read-only solution dir: fall back to temp
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(solutionDir.ToLowerInvariant()));
        var hash = Convert.ToHexString(hashBytes)[..8].ToLowerInvariant();
        dataDir = Path.Combine(Path.GetTempPath(), "RoslynMcp", hash);
        Directory.CreateDirectory(dataDir);
        logger.Warning("Solution dir read-only, using temp: {DataDir}", dataDir);
    }

    var dbPath = Path.Combine(dataDir, "roslyn-mcp.db");

    // ── Initialize workspace ──

    logger.Information("Opening solution: {SolutionPath}", solutionPath);
    await using var workspaceProvider = await MsBuildWorkspaceProvider.CreateAsync(solutionPath, logger, warmUp);

    // ── Initialize SQLite + migrations ──

    var configManager = new ConfigManager(dataDir);
    var btConfig = configManager.Get("sqlite.busy_timeout_ms");
    var busyTimeoutMs = int.TryParse(btConfig.Value ?? btConfig.DefaultValue, out var bt) ? bt : 1000;
    var csConfig = configManager.Get("sqlite.cache_size_kb");
    var cacheSizeKb = int.TryParse(csConfig.Value ?? csConfig.DefaultValue, out var cs) ? cs : 16000;
    await using var pool = new SqliteConnectionPool(dbPath, logger: logger,
        busyTimeoutMs: busyTimeoutMs, cacheSizeKb: cacheSizeKb);
    var migrations = new IMigration[] { new V1_MemoryTables(), new V2_GraphTables(), new V3_KBTables() };
    var migrationRunner = new MigrationRunner(pool, dbPath, migrations, logger);
    var applied = await migrationRunner.RunAsync();
    if (applied > 0)
        logger.Information("Applied {Count} database migration(s)", applied);

    // ── Build host with DI ──

    var builder = Host.CreateEmptyApplicationBuilder(settings: null);

    builder.Services.AddSerilog(logger);

    // Infrastructure singletons
    builder.Services.AddSingleton<Serilog.ILogger>(logger);
    builder.Services.AddSingleton<IWorkspaceProvider>(workspaceProvider);
    builder.Services.AddSingleton<ISqliteConnectionPool>(pool);
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
        dbPath,
        sp.GetRequiredService<Serilog.ILogger>()));

    builder.Services.AddTransient(sp => new GraphService(
        sp.GetRequiredService<ISqliteConnectionPool>(),
        sp.GetRequiredService<Serilog.ILogger>(),
        sp.GetRequiredService<IWorkspaceProvider>()));

    builder.Services.AddTransient(sp => new KBService(
        sp.GetRequiredService<ISqliteConnectionPool>(),
        dbPath,
        sp.GetRequiredService<Serilog.ILogger>()));

    builder.Services.AddTransient(sp => new ApolloService(
        sp.GetRequiredService<IWorkspaceProvider>(),
        sp.GetRequiredService<Serilog.ILogger>()));

    // MCP server
    builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithToolsFromAssembly();

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

// Walk up from startDir to find the git root (directory containing .git), then search for a solution within it.
static string? DiscoverSolution(string startDir, Serilog.ILogger log)
{
    // Find git root
    var gitRoot = startDir;
    var current = startDir;
    while (current != null)
    {
        if (Directory.Exists(Path.Combine(current, ".git")))
        {
            gitRoot = current;
            log.Information("Found git root: {GitRoot}", gitRoot);
            break;
        }
        var parent = Directory.GetParent(current)?.FullName;
        if (parent == null || parent == current) break;
        current = parent;
    }

    // Search recursively within the git root for *.sln or *.slnx.
    // Note: on Windows, "*.sln" glob also matches "*.slnx" due to legacy 8.3
    // wildcard behavior, so we search broadly and filter by exact extension.
    // Prefer the solution closest to git root (fewest path separators), then
    // prefer .sln over .slnx at equal depth (main solution is typically .sln).
    var allSolutionFiles = Directory.EnumerateFiles(gitRoot, "*.sln*", SearchOption.AllDirectories)
        .Where(f => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                  || f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        .Where(f => !Path.GetFileName(f).Equals("RoslynMcp.sln", StringComparison.OrdinalIgnoreCase))
        .ToList();

    var sln = allSolutionFiles
        .OrderBy(f => f.Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar))
        .ThenBy(f => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
        .FirstOrDefault();

    if (sln != null)
        log.Information("Auto-discovered solution: {SolutionPath}", sln);
    else
        log.Warning("No .sln or .slnx found under git root: {GitRoot}", gitRoot);

    return sln;
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
