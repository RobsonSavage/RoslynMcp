using System.Data;
using RoslynMcp.Core.Helpers;
using RoslynMcp.Core.Helpers.Migrations;
using Microsoft.Data.Sqlite;
using Serilog;
using Xunit;

namespace RoslynMcp.Core.Tests.Services;

public class InfrastructureTests : IAsyncDisposable
{
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();
    private readonly string _testDir;

    public InfrastructureTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "RoslynMcpTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDir);
    }

    public async ValueTask DisposeAsync()
    {
        try { Directory.Delete(_testDir, true); } catch { }
        await Task.CompletedTask;
    }

    private string NewDbPath() => Path.Combine(_testDir, $"{Guid.NewGuid()}.db");

    private static IMigration[] AllMigrations =>
    [
        new V1_MemoryTables(),
        new V2_GraphTables(),
        new V3_KBTables()
    ];

    // ────── 1. Pool_AcquireReader_ReturnsOpenConnection ──────

    [Fact]
    public async Task Pool_AcquireReader_ReturnsOpenConnection()
    {
        var dbPath = NewDbPath();
        await using var pool = new SqliteConnectionPool(dbPath, logger: _logger);

        await using var lease = await pool.AcquireReaderAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ConnectionState.Open, lease.Connection.State);
    }

    // ────── 2. Pool_AcquireWriter_ReturnsExclusive ──────

    [Fact]
    public async Task Pool_AcquireWriter_ReturnsExclusive()
    {
        var dbPath = NewDbPath();
        await using var pool = new SqliteConnectionPool(dbPath, logger: _logger);

        var lease = await pool.AcquireWriterAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ConnectionState.Open, lease.Connection.State);
        Assert.True(pool.WriterInUse);

        lease.Dispose();

        Assert.False(pool.WriterInUse);
    }

    // ────── 3. Pool_MultipleReaders_Concurrent ──────

    [Fact]
    public async Task Pool_MultipleReaders_Concurrent()
    {
        var dbPath = NewDbPath();
        await using var pool = new SqliteConnectionPool(dbPath, maxReaders: 4, logger: _logger);

        var readers = new PooledConnection[4];
        for (var i = 0; i < 4; i++)
        {
            readers[i] = await pool.AcquireReaderAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(4, pool.ActiveReaderCount);

        foreach (var reader in readers)
        {
            reader.Dispose();
        }

        Assert.Equal(0, pool.ActiveReaderCount);
    }

    // ────── 4. Pool_DoubleDispose_DoesNotThrow ──────

    [Fact]
    public async Task Pool_DoubleDispose_DoesNotThrow()
    {
        var dbPath = NewDbPath();
        await using var pool = new SqliteConnectionPool(dbPath, logger: _logger);

        var lease = await pool.AcquireReaderAsync(TimeSpan.FromSeconds(5));

        // First dispose (sync)
        lease.Dispose();

        // Second dispose (async) — should not throw
        await lease.DisposeAsync();

        // Third dispose (sync again) — should not throw
        lease.Dispose();
    }

    // ────── 5. Migration_AppliesInOrder ──────

    [Fact]
    public async Task Migration_AppliesInOrder()
    {
        var dbPath = NewDbPath();
        await using var pool = new SqliteConnectionPool(dbPath, logger: _logger);
        var runner = new MigrationRunner(pool, dbPath, AllMigrations, _logger);

        var applied = await runner.RunAsync();

        Assert.Equal(3, applied);

        var version = await runner.GetCurrentVersionAsync();
        Assert.Equal(3, version);

        // Verify SchemaVersion table has exactly 3 rows
        await using var lease = await pool.AcquireReaderAsync(TimeSpan.FromSeconds(5));
        using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM SchemaVersion;";
        var rowCount = Convert.ToInt32(cmd.ExecuteScalar());
        Assert.Equal(3, rowCount);
    }

    // ────── 6. Migration_Idempotent ──────

    [Fact]
    public async Task Migration_Idempotent()
    {
        var dbPath = NewDbPath();
        await using var pool = new SqliteConnectionPool(dbPath, logger: _logger);
        var runner = new MigrationRunner(pool, dbPath, AllMigrations, _logger);

        // First run applies all 3
        var firstRun = await runner.RunAsync();
        Assert.Equal(3, firstRun);

        // Second run applies 0 — already up to date
        var secondRun = await runner.RunAsync();
        Assert.Equal(0, secondRun);

        var version = await runner.GetCurrentVersionAsync();
        Assert.Equal(3, version);
    }

    // ────── 7. Migration_ChecksumMismatch_ThrowsError ──────

    private sealed class TamperedV1 : IMigration
    {
        public int Version => 1;
        public string Description => "Tampered";
        public string Sql => "CREATE TABLE IF NOT EXISTS Dummy (Id INTEGER);";
    }

    [Fact]
    public async Task Migration_ChecksumMismatch_ThrowsError()
    {
        var dbPath = NewDbPath();
        await using var pool = new SqliteConnectionPool(dbPath, logger: _logger);

        // Apply real V1 first
        var realRunner = new MigrationRunner(pool, dbPath, [new V1_MemoryTables()], _logger);
        await realRunner.RunAsync();

        // Now create a runner with tampered V1 + V2
        var tamperedRunner = new MigrationRunner(
            pool, dbPath,
            [new TamperedV1(), new V2_GraphTables()],
            _logger);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => tamperedRunner.RunAsync());
        Assert.Contains("checksum mismatch", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ────── 8. Migration_CreatesBackup ──────

    [Fact]
    public async Task Migration_CreatesBackup()
    {
        var dbPath = NewDbPath();
        await using var pool = new SqliteConnectionPool(dbPath, logger: _logger);

        // Apply V1 first so the DB file exists and has a version
        var runnerV1 = new MigrationRunner(pool, dbPath, [new V1_MemoryTables()], _logger);
        await runnerV1.RunAsync();

        // Apply V2 — this should create a backup at v1
        var runnerV1V2 = new MigrationRunner(
            pool, dbPath,
            [new V1_MemoryTables(), new V2_GraphTables()],
            _logger);
        await runnerV1V2.RunAsync();

        // Look for the backup file matching *.backup.v1.*
        var dir = Path.GetDirectoryName(dbPath)!;
        var baseName = Path.GetFileName(dbPath);
        var backups = Directory.GetFiles(dir, $"{baseName}.backup.v1.*");
        Assert.True(backups.Length > 0, "Expected at least one backup file matching *.backup.v1.*");
    }

    // ────── 9. IntegrityCheck_PassesOnGoodDb ──────

    [Fact]
    public async Task IntegrityCheck_PassesOnGoodDb()
    {
        var dbPath = NewDbPath();
        await using var pool = new SqliteConnectionPool(dbPath, logger: _logger);
        var runner = new MigrationRunner(pool, dbPath, AllMigrations, _logger);
        await runner.RunAsync();

        var isHealthy = await runner.CheckIntegrityAsync();

        Assert.True(isHealthy);
    }

    // ────── 10. IntegrityCheck_FailsOnCorruptDb ──────

    [Fact]
    public async Task IntegrityCheck_FailsOnCorruptDb()
    {
        var dbPath = NewDbPath();

        // Set up and populate the DB, then dispose the pool to release all handles.
        // We must also clear the ADO.NET connection pool so SQLite fully releases
        // the file lock on Windows.
        {
            await using var setupPool = new SqliteConnectionPool(dbPath, logger: _logger);
            var runner = new MigrationRunner(setupPool, dbPath, AllMigrations, _logger);
            await runner.RunAsync();
        }

        // Clear ADO.NET's internal connection pool cache so all file handles are released
        SqliteConnection.ClearAllPools();

        // Small delay to ensure OS releases file handles
        await Task.Delay(100);

        // Corrupt the SQLite header (bytes 0-15 contain magic string) and data pages
        var bytes = new byte[4096];
        new Random(42).NextBytes(bytes);
        using (var fs = new FileStream(dbPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            fs.Seek(0, SeekOrigin.Begin);
            fs.Write(bytes, 0, bytes.Length);
        }

        // Also corrupt the WAL file if it exists, to prevent WAL recovery
        var walPath = dbPath + "-wal";
        if (File.Exists(walPath))
        {
            using var walFs = new FileStream(walPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            walFs.Seek(0, SeekOrigin.Begin);
            walFs.Write(bytes, 0, bytes.Length);
        }

        // Delete the SHM file to prevent shared memory from masking corruption
        var shmPath = dbPath + "-shm";
        if (File.Exists(shmPath))
        {
            try { File.Delete(shmPath); } catch { }
        }

        // Re-create pool on the corrupted file and attempt integrity check.
        // Corruption may cause the pool constructor or integrity check to fail.
        // Either outcome confirms the infrastructure handles the situation.
        bool integrityPassed;
        try
        {
            await using var corruptPool = new SqliteConnectionPool(dbPath, logger: _logger);
            var corruptRunner = new MigrationRunner(corruptPool, dbPath, AllMigrations, _logger);
            integrityPassed = await corruptRunner.CheckIntegrityAsync();
        }
        catch (SqliteException)
        {
            // Pool failed to open or WAL init failed on corrupt file — this is acceptable
            integrityPassed = false;
        }

        Assert.False(integrityPassed, "Integrity check should detect corruption");
    }
}
