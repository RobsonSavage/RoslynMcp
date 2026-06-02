using Microsoft.Data.Sqlite;
using Nito.AsyncEx;
using Serilog;

namespace RoslynMcp.Core.Helpers;

public interface IConnectionLease
{
    Task<PooledConnection> AcquireReaderAsync(TimeSpan timeout, CancellationToken ct = default);
    Task<PooledConnection> AcquireWriterAsync(TimeSpan timeout, CancellationToken ct = default);
}

public interface IPoolDiagnostics
{
    int ActiveReaderCount { get; }
    bool WriterInUse { get; }
}

public interface ISqliteConnectionPool : IConnectionLease, IPoolDiagnostics, IAsyncDisposable { }

/// <summary>
/// Wraps a SqliteConnection with automatic lock release on dispose.
/// Interlocked.Exchange prevents double-release. Sync Dispose avoids
/// sync-over-async deadlock on VS SynchronizationContext.
/// When the owning pool is disposed, the connection is still closed but
/// the release callback safely no-ops to avoid touching disposed pool resources.
/// </summary>
public sealed class PooledConnection : IAsyncDisposable, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly Action _release;
    private readonly Func<bool> _isPoolDisposed;
    private int _disposed;

    internal PooledConnection(SqliteConnection connection, Action release, Func<bool> isPoolDisposed)
    {
        _connection = connection;
        _release = release;
        _isPoolDisposed = isPoolDisposed;
    }

    public SqliteConnection Connection => _disposed == 0
        ? _connection
        : throw new ObjectDisposedException(nameof(PooledConnection));

    public ValueTask DisposeAsync()
    {
        Dispose();
        return default;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try { _connection.Dispose(); }
            finally
            {
                // If the pool is already disposed, skip the release callback
                // to avoid releasing back to disposed semaphores/locks.
                if (!_isPoolDisposed())
                    _release();
            }
        }
    }
}

/// <summary>
/// SQLite connection pool with reader-writer coordination.
/// WAL mode, 4 concurrent readers, 1 exclusive writer.
/// Uses Nito.AsyncEx.AsyncReaderWriterLock for writer-priority draining.
/// </summary>
public sealed class SqliteConnectionPool : ISqliteConnectionPool
{
    private readonly string _connectionString;
    private readonly AsyncReaderWriterLock _rwLock = new();
    private readonly SemaphoreSlim _readerSlots;
    private readonly ILogger _logger;
    private readonly int _busyTimeoutMs;
    private readonly int _cacheSizeKb;
    private int _activeReaders;
    private int _writerInUse;
    private int _disposed;
    private long _lastOptimizeTicks = DateTime.MinValue.Ticks;
    private static readonly TimeSpan s_optimizeInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Creates a new connection pool.
    /// </summary>
    /// <param name="dbPath">Path to the SQLite database file.</param>
    /// <param name="maxReaders">Maximum concurrent reader connections.</param>
    /// <param name="logger">Optional Serilog logger.</param>
    /// <param name="busyTimeoutMs">SQLite busy timeout in milliseconds. Matches ConfigManager key sqlite.busy_timeout_ms (default 1000).</param>
    /// <param name="cacheSizeKb">SQLite cache size in KB. Matches ConfigManager key sqlite.cache_size_kb (default 16000).</param>
    public SqliteConnectionPool(string dbPath, int maxReaders = 4, ILogger? logger = null,
        int busyTimeoutMs = 1000, int cacheSizeKb = 16000)
    {
        _readerSlots = new SemaphoreSlim(maxReaders, maxReaders);
        _logger = logger ?? Log.Logger;
        _busyTimeoutMs = busyTimeoutMs;
        _cacheSizeKb = cacheSizeKb;

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };
        _connectionString = builder.ToString();

        InitializeWalMode();
    }

    private void InitializeWalMode()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL;";
        cmd.ExecuteNonQuery();
    }

    public int ActiveReaderCount => _activeReaders;
    public bool WriterInUse => _writerInUse != 0;

    internal bool IsDisposed => _disposed != 0;

    public async Task<PooledConnection> AcquireReaderAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        if (!await _readerSlots.WaitAsync(timeout, ct).ConfigureAwait(false))
            throw new TimeoutException($"Timed out waiting for reader slot after {timeout}");

        IDisposable? lockHandle = null;
        try
        {
            lockHandle = await _rwLock.ReaderLockAsync(ct).ConfigureAwait(false);

            var conn = OpenWithPragmas();
            Interlocked.Increment(ref _activeReaders);
            var captured = lockHandle;
            lockHandle = null;

            return new PooledConnection(conn, () =>
            {
                Interlocked.Decrement(ref _activeReaders);
                captured.Dispose();
                _readerSlots.Release();
            }, () => IsDisposed);
        }
        catch
        {
            lockHandle?.Dispose();
            _readerSlots.Release();
            throw;
        }
    }

    public async Task<PooledConnection> AcquireWriterAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        IDisposable? lockHandle = null;
        try
        {
            lockHandle = await _rwLock.WriterLockAsync(cts.Token).ConfigureAwait(false);

            var conn = OpenWithPragmas();
            Interlocked.Exchange(ref _writerInUse, 1);
            var captured = lockHandle;
            lockHandle = null;

            return new PooledConnection(conn, () =>
            {
                Interlocked.Exchange(ref _writerInUse, 0);
                captured.Dispose();
            }, () => IsDisposed);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out waiting for writer lock after {timeout}");
        }
        catch
        {
            lockHandle?.Dispose();
            throw;
        }
    }

    private SqliteConnection OpenWithPragmas()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            PRAGMA busy_timeout={_busyTimeoutMs};
            PRAGMA synchronous=NORMAL;
            PRAGMA cache_size=-{_cacheSizeKb};
            PRAGMA foreign_keys=ON;
            """;
        cmd.ExecuteNonQuery();

        MaybeOptimize(conn);
        return conn;
    }

    private void MaybeOptimize(SqliteConnection conn)
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastOptimizeTicks);
        if ((now - last) < s_optimizeInterval.Ticks) return;

        // Atomically swap to prevent multiple threads from running optimize concurrently.
        // If another thread already updated _lastOptimizeTicks, this CAS fails and we skip.
        if (Interlocked.CompareExchange(ref _lastOptimizeTicks, now, last) != last) return;

        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA optimize;";
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "PRAGMA optimize failed");
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return default;

        _readerSlots.Dispose();
        return default;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed != 0) throw new ObjectDisposedException(nameof(SqliteConnectionPool));
    }
}
