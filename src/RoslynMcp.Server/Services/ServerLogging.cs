using Serilog;
using Serilog.Core;

namespace RoslynMcp.Server.Services;

internal static class ServerLogging
{
    public static Logger CreateLogger(string logPath)
    {
        return new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.WithProperty("ProcessId", Environment.ProcessId)
            .WriteTo.File(
                logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: null,
                fileSizeLimitBytes: 50 * 1024 * 1024,
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3} pid={ProcessId}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.Console(
                Serilog.Events.LogEventLevel.Warning,
                standardErrorFromLevel: Serilog.Events.LogEventLevel.Verbose)
            .CreateLogger();
    }

    public static int Prune(
        string logDirectory,
        int retentionDays,
        ILogger logger,
        DateTime? utcNow = null)
    {
        if (retentionDays < 1)
        {
            logger.Warning(
                "Invalid logging.file_retention_days value {RetentionDays}; log pruning skipped",
                retentionDays);
            return 0;
        }

        var cutoff = (utcNow ?? DateTime.UtcNow).AddDays(-retentionDays);
        var pruned = 0;

        try
        {
            foreach (var path in Directory.EnumerateFiles(logDirectory, "server-*.log", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) >= cutoff)
                        continue;

                    File.Delete(path);
                    pruned++;
                }
                catch (Exception ex)
                {
                    logger.Warning(ex, "Could not prune expired log file {LogPath}", path);
                }
            }
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Could not enumerate log files in {LogDirectory}", logDirectory);
        }

        if (pruned > 0)
            logger.Information("Pruned {Count} expired log file(s)", pruned);

        return pruned;
    }
}
