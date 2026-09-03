using RoslynMcp.Server.Services;
using Serilog;
using Xunit;

namespace RoslynMcp.Integration;

public sealed class ServerLoggingTest : IDisposable
{
    private readonly string _logDirectory = Path.Combine(
        Path.GetTempPath(),
        "RoslynMcpTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void LoggerIncludesProcessId()
    {
        Directory.CreateDirectory(_logDirectory);
        var logPath = Path.Combine(_logDirectory, "server-test.log");

        using (var logger = ServerLogging.CreateLogger(logPath))
            logger.Information("test event");

        var rolledPath = Assert.Single(Directory.GetFiles(_logDirectory, "server-test*.log"));
        var content = File.ReadAllText(rolledPath);
        Assert.Contains($"pid={Environment.ProcessId}", content);
        Assert.Contains("test event", content);
    }

    [Fact]
    public void PruneDeletesExpiredServerLogsOnly()
    {
        Directory.CreateDirectory(_logDirectory);
        var now = new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);
        var expired = CreateFile("server-20260801.log", now.AddDays(-8));
        var current = CreateFile("server-20260903.log", now.AddDays(-6));
        var unrelated = CreateFile("other.log", now.AddDays(-30));
        using var logger = new LoggerConfiguration().CreateLogger();

        var pruned = ServerLogging.Prune(_logDirectory, retentionDays: 7, logger, now);

        Assert.Equal(1, pruned);
        Assert.False(File.Exists(expired));
        Assert.True(File.Exists(current));
        Assert.True(File.Exists(unrelated));
    }

    public void Dispose()
    {
        try { Directory.Delete(_logDirectory, recursive: true); } catch { }
    }

    private string CreateFile(string name, DateTime lastWriteTimeUtc)
    {
        var path = Path.Combine(_logDirectory, name);
        File.WriteAllText(path, name);
        File.SetLastWriteTimeUtc(path, lastWriteTimeUtc);
        return path;
    }
}
