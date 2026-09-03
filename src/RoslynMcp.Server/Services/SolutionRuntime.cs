using System.Security.Cryptography;
using System.Text;
using Nito.AsyncEx;
using RoslynMcp.Core.Helpers;
using RoslynMcp.Core.Helpers.Migrations;
using RoslynMcp.Core.Services;
using RoslynMcp.Shared;
using RoslynMcp.Shared.Contracts.Util;
using Serilog;

namespace RoslynMcp.Server.Services;

/// <summary>
/// Owns the solution-scoped workspace, configuration and database routing.
/// A solution switch takes the write lease so callers cannot observe a mixed context.
/// </summary>
public sealed class SolutionRuntime : ISqliteConnectionPool, ISolutionContextSwitcher
{
    private readonly IWorkspaceProvider _workspace;
    private readonly IReadOnlyList<IMigration> _migrations;
    private readonly ILogger _logger;
    private readonly AsyncReaderWriterLock _contextLock = new();
    private SqliteConnectionPool _pool;
    private int _disposed;

    private SolutionRuntime(
        IWorkspaceProvider workspace,
        ConfigManager config,
        SqliteConnectionPool pool,
        IReadOnlyList<IMigration> migrations,
        ILogger logger)
    {
        _workspace = workspace;
        Config = config;
        _pool = pool;
        _migrations = migrations;
        _logger = logger;
    }

    public ConfigManager Config { get; }

    public string DatabasePath => CurrentPool.DatabasePath;
    public int ActiveReaderCount => CurrentPool.ActiveReaderCount;
    public bool WriterInUse => CurrentPool.WriterInUse;

    public static async Task<SolutionRuntime> CreateAsync(
        IWorkspaceProvider workspace,
        string solutionPath,
        IReadOnlyList<IMigration> migrations,
        ILogger logger,
        CancellationToken ct = default)
    {
        var dataDir = ResolveDataDirectory(solutionPath, logger);
        var config = new ConfigManager(dataDir, logger);
        var pool = await CreatePoolAsync(dataDir, config, migrations, logger, ct).ConfigureAwait(false);
        return new SolutionRuntime(workspace, config, pool, migrations, logger);
    }

    public async Task<IDisposable> EnterReadAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var contextLease = await _contextLock.ReaderLockAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return contextLease;
        }
        catch
        {
            contextLease.Dispose();
            throw;
        }
    }

    public Task<PooledConnection> AcquireReaderAsync(TimeSpan timeout, CancellationToken ct = default) =>
        CurrentPool.AcquireReaderAsync(timeout, ct);

    public Task<PooledConnection> AcquireWriterAsync(TimeSpan timeout, CancellationToken ct = default) =>
        CurrentPool.AcquireWriterAsync(timeout, ct);

    public async Task<SetSolutionPathResponse> SwitchAsync(
        string solutionPath,
        bool warmUp,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();

        using var contextLease = await _contextLock.WriterLockAsync(ct).ConfigureAwait(false);
        ThrowIfDisposed();
        var fullPath = Path.GetFullPath(solutionPath);
        var previousPath = _workspace.CurrentSolution?.FilePath;
        var previousConfigDir = Config.ConfigDirectory;
        var dataDir = ResolveDataDirectory(fullPath, _logger);
        SqliteConnectionPool? targetPool = null;
        var committed = false;

        try
        {
            Config.SwitchDirectory(dataDir);
            targetPool = await CreatePoolAsync(dataDir, Config, _migrations, _logger, ct)
                .ConfigureAwait(false);

            _logger.Information(
                "Switching solution context from {OldSolution} to {NewSolution}",
                previousPath,
                fullPath);

            var reloaded = await _workspace.ReloadSolutionAsync(fullPath, warmUp, ct).ConfigureAwait(false);
            if (!reloaded)
                throw new InvalidOperationException($"Workspace declined solution switch to: {fullPath}");

            var oldPool = _pool;
            _pool = targetPool;
            targetPool = null;
            committed = true;
            await oldPool.DisposeAsync().ConfigureAwait(false);

            var solution = _workspace.CurrentSolution;
            var projectCount = solution?.ProjectIds.Count ?? 0;
            var documentCount = solution?.Projects.Sum(p => p.DocumentIds.Count) ?? 0;

            _logger.Information(
                "Solution context switched to {SolutionPath} with database {DatabasePath}",
                fullPath,
                _pool.DatabasePath);

            return new SetSolutionPathResponse(fullPath, projectCount, documentCount, previousPath);
        }
        finally
        {
            if (targetPool != null)
                await targetPool.DisposeAsync().ConfigureAwait(false);
            if (!committed)
                Config.SwitchDirectory(previousConfigDir);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        using var contextLease = await _contextLock.WriterLockAsync().ConfigureAwait(false);
        await _pool.DisposeAsync().ConfigureAwait(false);
    }

    private SqliteConnectionPool CurrentPool
    {
        get
        {
            ThrowIfDisposed();
            return Volatile.Read(ref _pool);
        }
    }

    private static async Task<SqliteConnectionPool> CreatePoolAsync(
        string dataDir,
        ConfigManager config,
        IReadOnlyList<IMigration> migrations,
        ILogger logger,
        CancellationToken ct)
    {
        var dbPath = Path.Combine(dataDir, "roslyn-mcp.db");
        var busyTimeoutMs = ReadInt(config, "sqlite.busy_timeout_ms", 1000);
        var cacheSizeKb = ReadInt(config, "sqlite.cache_size_kb", 16000);
        var pool = new SqliteConnectionPool(
            dbPath,
            logger: logger,
            busyTimeoutMs: busyTimeoutMs,
            cacheSizeKb: cacheSizeKb);

        try
        {
            var migrationRunner = new MigrationRunner(pool, dbPath, migrations, logger);
            var applied = await migrationRunner.RunAsync(ct).ConfigureAwait(false);
            if (applied > 0)
                logger.Information("Applied {Count} database migration(s) to {DatabasePath}", applied, dbPath);
            return pool;
        }
        catch
        {
            await pool.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static int ReadInt(ConfigManager config, string key, int fallback)
    {
        var entry = config.Get(key);
        return int.TryParse(entry.Value ?? entry.DefaultValue, out var value) ? value : fallback;
    }

    private static string ResolveDataDirectory(string solutionPath, ILogger logger)
    {
        var solutionDir = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
        var dataDir = Path.Combine(solutionDir, ".roslyn-mcp-data");

        try
        {
            Directory.CreateDirectory(dataDir);
            return dataDir;
        }
        catch (UnauthorizedAccessException)
        {
            var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(solutionDir.ToLowerInvariant()));
            var hash = Convert.ToHexString(hashBytes)[..8].ToLowerInvariant();
            dataDir = Path.Combine(Path.GetTempPath(), "RoslynMcp", hash);
            Directory.CreateDirectory(dataDir);
            logger.Warning("Solution dir read-only, using temp: {DataDir}", dataDir);
            return dataDir;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed != 0)
            throw new ObjectDisposedException(nameof(SolutionRuntime));
    }
}
