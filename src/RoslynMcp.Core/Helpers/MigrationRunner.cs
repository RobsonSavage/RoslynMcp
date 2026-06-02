using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Serilog;

namespace RoslynMcp.Core.Helpers;

public interface IMigration
{
    int Version { get; }
    string Description { get; }
    string Sql { get; }
}

/// <summary>
/// Applies IMigration instances in version order. Each migration runs in a transaction.
/// Stores checksums in SchemaVersion table to detect tampered migrations.
/// Pre-migration backup: memory.db.backup.v{N}.{timestamp}. Keeps last 5 backups.
/// </summary>
public class MigrationRunner
{
    private readonly ISqliteConnectionPool _pool;
    private readonly string _dbPath;
    private readonly IMigration[] _migrations;
    private readonly ILogger _logger;
    private const int MaxBackups = 5;

    public MigrationRunner(
        ISqliteConnectionPool pool,
        string dbPath,
        IEnumerable<IMigration> migrations,
        ILogger? logger = null)
    {
        _pool = pool;
        _dbPath = dbPath;
        _migrations = migrations.OrderBy(m => m.Version).ToArray();
        _logger = logger ?? Log.Logger;
    }

    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        await using var lease = await _pool.AcquireWriterAsync(TimeSpan.FromSeconds(30), ct);
        var conn = lease.Connection;

        EnsureSchemaVersionTable(conn);

        var currentVersion = GetCurrentVersion(conn);
        ValidateExistingChecksums(conn, currentVersion);

        var pending = _migrations.Where(m => m.Version > currentVersion).ToArray();
        if (pending.Length == 0)
        {
            _logger.Information("Database schema is up to date at V{Version}", currentVersion);
            return 0;
        }

        CreateBackup(conn, currentVersion);

        var applied = 0;
        foreach (var migration in pending)
        {
            ct.ThrowIfCancellationRequested();

            var checksum = ComputeChecksum(migration.Sql);
            _logger.Information("Applying migration V{Version}: {Description}",
                migration.Version, migration.Description);

            using var tx = conn.BeginTransaction();
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = migration.Sql;
                    cmd.ExecuteNonQuery();
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"
                        INSERT INTO SchemaVersion (Version, Description, Checksum)
                        VALUES ($version, $description, $checksum);";
                    cmd.Parameters.AddWithValue("$version", migration.Version);
                    cmd.Parameters.AddWithValue("$description", migration.Description);
                    cmd.Parameters.AddWithValue("$checksum", checksum);
                    cmd.ExecuteNonQuery();
                }

                tx.Commit();
                applied++;
                _logger.Information("Migration V{Version} applied successfully", migration.Version);
            }
            catch
            {
                tx.Rollback();
                _logger.Error("Migration V{Version} failed, rolled back", migration.Version);
                throw;
            }
        }

        return applied;
    }

    public async Task<int> GetCurrentVersionAsync(CancellationToken ct = default)
    {
        await using var lease = await _pool.AcquireReaderAsync(TimeSpan.FromSeconds(10), ct);
        var conn = lease.Connection;

        if (!TableExists(conn, "SchemaVersion"))
            return 0;

        return GetCurrentVersion(conn);
    }

    public async Task<bool> CheckIntegrityAsync(CancellationToken ct = default)
    {
        await using var lease = await _pool.AcquireWriterAsync(TimeSpan.FromSeconds(30), ct);
        var conn = lease.Connection;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check;";
        var result = (string?)cmd.ExecuteScalar();
        return result == "ok";
    }

    public async Task<bool> RepairAsync(CancellationToken ct = default)
    {
        // Hold the writer lease for the entire repair operation so no other
        // connections can access the database file during restore/delete.
        await using var lease = await _pool.AcquireWriterAsync(TimeSpan.FromSeconds(60), ct);

        // Step 1: VACUUM + REINDEX
        try
        {
            var conn = lease.Connection;

            using var vacuumCmd = conn.CreateCommand();
            vacuumCmd.CommandText = "VACUUM;";
            vacuumCmd.ExecuteNonQuery();

            using var reindexCmd = conn.CreateCommand();
            reindexCmd.CommandText = "REINDEX;";
            reindexCmd.ExecuteNonQuery();

            using var checkCmd = conn.CreateCommand();
            checkCmd.CommandText = "PRAGMA integrity_check;";
            var result = (string?)checkCmd.ExecuteScalar();

            if (result == "ok")
            {
                _logger.Information("Database repaired successfully via VACUUM + REINDEX");
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "VACUUM/REINDEX repair failed");
        }

        // Step 2: Restore from backup (inside writer lease to prevent concurrent access)
        var restored = RestoreFromBackup();
        if (restored)
        {
            _logger.Information("Database restored from backup");
            return true;
        }

        // Step 3: No backup — recreate
        _logger.Error("No viable backup found. Recreating empty database");
        try
        {
            File.Delete(_dbPath);
            // Caller should re-run migrations after this
            return false;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to delete corrupt database");
            return false;
        }
    }

    private static void EnsureSchemaVersionTable(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS SchemaVersion (
                Version     INTEGER PRIMARY KEY,
                Description TEXT,
                AppliedAt   DATETIME DEFAULT CURRENT_TIMESTAMP,
                Checksum    TEXT
            );";
        cmd.ExecuteNonQuery();
    }

    private static int GetCurrentVersion(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(Version) FROM SchemaVersion;";
        var result = cmd.ExecuteScalar();
        return result is DBNull || result is null ? 0 : Convert.ToInt32(result);
    }

    private static bool TableExists(SqliteConnection conn, string tableName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;";
        cmd.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    private void ValidateExistingChecksums(SqliteConnection conn, int currentVersion)
    {
        if (currentVersion == 0) return;

        foreach (var migration in _migrations.Where(m => m.Version <= currentVersion))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Checksum FROM SchemaVersion WHERE Version = $version;";
            cmd.Parameters.AddWithValue("$version", migration.Version);
            var storedChecksum = (string?)cmd.ExecuteScalar();

            if (storedChecksum == null) continue;

            var expectedChecksum = ComputeChecksum(migration.Sql);
            if (!string.Equals(storedChecksum, expectedChecksum, StringComparison.OrdinalIgnoreCase))
            {
                var msg = $"Migration V{migration.Version} checksum mismatch. " +
                          $"Expected: {expectedChecksum}, Stored: {storedChecksum}. " +
                          "Migration may have been tampered with.";
                _logger.Error(msg);
                throw new InvalidOperationException(msg);
            }
        }
    }

    private void CreateBackup(SqliteConnection writerConn, int currentVersion)
    {
        if (!File.Exists(_dbPath)) return;

        try
        {
            try
            {
                // Use the existing pool writer connection for WAL checkpoint
                // instead of opening a new connection that bypasses the pool.
                using var ckCmd = writerConn.CreateCommand();
                ckCmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                ckCmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "WAL checkpoint before backup failed");
            }

            var timestamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmss");
            var backupPath = $"{_dbPath}.backup.v{currentVersion}.{timestamp}";
            File.Copy(_dbPath, backupPath, overwrite: true);
            _logger.Information("Created backup: {BackupPath}", backupPath);

            PruneBackups();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to create pre-migration backup");
        }
    }

    private static readonly Regex s_backupPattern = new(
        @"\.backup\.v(\d+)\.((\d{8})T(\d{6}))$", RegexOptions.Compiled);

    private void PruneBackups()
    {
        var dir = Path.GetDirectoryName(_dbPath);
        var baseName = Path.GetFileName(_dbPath);
        if (dir == null || baseName == null) return;

        // Sort by version (numeric) then timestamp (numeric) descending,
        // so that higher versions and later timestamps come first.
        var backups = Directory.GetFiles(dir, $"{baseName}.backup.*")
            .Select(f =>
            {
                var match = s_backupPattern.Match(f);
                return new
                {
                    Path = f,
                    Version = match.Success ? int.Parse(match.Groups[1].Value) : 0,
                    Timestamp = match.Success ? match.Groups[2].Value : ""
                };
            })
            .OrderByDescending(b => b.Version)
            .ThenByDescending(b => b.Timestamp, StringComparer.Ordinal)
            .Skip(MaxBackups)
            .ToArray();

        foreach (var old in backups)
        {
            try { File.Delete(old.Path); }
            catch (Exception ex) { _logger.Warning(ex, "Failed to delete old backup: {Path}", old.Path); }
        }
    }

    private bool RestoreFromBackup()
    {
        var dir = Path.GetDirectoryName(_dbPath);
        var baseName = Path.GetFileName(_dbPath);
        if (dir == null || baseName == null) return false;

        var backups = Directory.GetFiles(dir, $"{baseName}.backup.*")
            .OrderByDescending(f => f)
            .ToArray();

        foreach (var backup in backups)
        {
            try
            {
                File.Copy(backup, _dbPath, overwrite: true);
                try { File.Delete($"{_dbPath}-wal"); } catch { }
                try { File.Delete($"{_dbPath}-shm"); } catch { }
                return true;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to restore from backup: {Path}", backup);
            }
        }

        return false;
    }

    internal static string ComputeChecksum(string sql)
    {
        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(sql);
        var hash = sha.ComputeHash(bytes);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }
}
