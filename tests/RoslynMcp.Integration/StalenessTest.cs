using Microsoft.Build.Locator;
using RoslynMcp.Server.Providers;
using Serilog;
using Xunit;

namespace RoslynMcp.Integration;

/// <summary>
/// Covers the disk-staleness path: MSBuildWorkspace holds a snapshot taken at load time, so an
/// edit made outside the workspace (the agent's own Edit/Write, another editor, a checkout) is
/// invisible until the watcher records it and a tool call syncs it in.
/// </summary>
[Collection("workspace")]
public sealed class StalenessTest
{
    private static readonly object s_locatorLock = new();

    private static void EnsureMsBuildRegistered()
    {
        lock (s_locatorLock)
        {
            if (!MSBuildLocator.IsRegistered)
                MSBuildLocator.RegisterDefaults();
        }
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "RoslynMcp.sln")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("RoslynMcp.sln not found above " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// The whole point of the feature: text written to a solution file by something other than the
    /// workspace must be visible to the next tool call without an explicit reload_file.
    /// </summary>
    [Fact]
    public async Task ExternalEditBecomesVisibleWithoutAnExplicitReload()
    {
        EnsureMsBuildRegistered();
        var root = FindRepoRoot();
        var logger = new LoggerConfiguration().MinimumLevel.Error().CreateLogger();

        // A file that is part of the solution but that nothing in this test compiles against.
        var probe = Path.Combine(root, "src", "RoslynMcp.Shared", "IWorkspaceProvider.cs");
        Assert.True(File.Exists(probe), $"probe document missing: {probe}");

        var original = await File.ReadAllTextAsync(probe);
        var marker = $"// staleness-probe-{Guid.NewGuid():N}";

        await using var provider = await MsBuildWorkspaceProvider.CreateAsync(
            Path.Combine(root, "RoslynMcp.sln"), logger);

        try
        {
            var doc = await provider.GetDocumentAsync(probe);
            Assert.NotNull(doc);
            Assert.DoesNotContain(marker, (await doc!.GetTextAsync()).ToString());

            provider.StartFileWatcher();

            await File.WriteAllTextAsync(probe, original + Environment.NewLine + marker + Environment.NewLine);

            // The watcher delivers on its own thread, so give it a bounded window to arrive.
            var visible = false;
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                await provider.SyncPendingChangesAsync();

                var refreshed = await provider.GetDocumentAsync(probe);
                if (refreshed != null && (await refreshed.GetTextAsync()).ToString().Contains(marker))
                {
                    visible = true;
                    break;
                }

                await Task.Delay(100);
            }

            Assert.True(visible, "the external edit never became visible to the workspace");

            // A sync with nothing new must not keep churning: TryApplyChanges writes the document
            // back to disk, so a missing compare-first guard shows up here as an endless refresh.
            await provider.SyncPendingChangesAsync();
            await Task.Delay(500);
            await provider.SyncPendingChangesAsync();

            var settled = await provider.GetDocumentAsync(probe);
            Assert.Contains(marker, (await settled!.GetTextAsync()).ToString());
        }
        finally
        {
            await File.WriteAllTextAsync(probe, original);
        }
    }
}
