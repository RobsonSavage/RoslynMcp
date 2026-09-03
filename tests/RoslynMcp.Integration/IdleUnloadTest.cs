using System.Collections.Concurrent;
using Microsoft.Build.Locator;
using RoslynMcp.Server.Providers;
using Serilog;
using Xunit;

namespace RoslynMcp.Integration;

/// <summary>
/// Covers the idle-unload path in MsBuildWorkspaceProvider: that a sweep can never unload the
/// workspace from under an in-flight tool call, and that repeated unload/reload cycles do not
/// accumulate heap. Both tests live in one class so xunit runs them sequentially - the leak
/// measurement is meaningless if the race test is churning workspaces alongside it.
/// </summary>
public sealed class IdleUnloadTest
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

    private static async Task<MsBuildWorkspaceProvider> CreateProviderAsync()
    {
        EnsureMsBuildRegistered();
        var root = FindRepoRoot();
        var logger = new LoggerConfiguration().MinimumLevel.Error().CreateLogger();
        return await MsBuildWorkspaceProvider.CreateAsync(Path.Combine(root, "RoslynMcp.sln"), logger);
    }

    private static string ProbeDocument() =>
        Path.Combine(FindRepoRoot(), "src", "RoslynMcp.Server", "Program.cs");

    /// <summary>
    /// The invariant the _inFlight counter exists to provide: once a request has entered and has
    /// observed a loaded workspace, no concurrent sweep may unload it before the request exits.
    /// Without the guard this surfaces as a null document or a disposed-workspace throw.
    /// </summary>
    [Fact]
    public async Task IdleSweep_NeverUnloadsDuringAnInFlightRequest()
    {
        await using var provider = await CreateProviderAsync();
        var docPath = ProbeDocument();
        Assert.True(File.Exists(docPath), $"probe document missing: {docPath}");

        var failures = new ConcurrentBag<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = cts.Token;

        // Workers mimic exactly what the CallTool filter does around every tool invocation.
        var workers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await provider.EnsureLoadedAsync(ct);

                    provider.EnterRequest();
                    try
                    {
                        var loadedOnEntry = provider.IsLoaded;
                        var doc = await provider.GetDocumentAsync(docPath, ct: ct);

                        if (loadedOnEntry)
                        {
                            if (!provider.IsLoaded)
                                failures.Add("workspace was unloaded during an in-flight request");
                            if (doc == null)
                                failures.Add("document resolved to null while the workspace was loaded");
                        }
                    }
                    finally
                    {
                        provider.ExitRequest();
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    failures.Add($"worker threw {ex.GetType().Name}: {ex.Message}");
                }
            }
        }, ct)).ToList();

        // Sweeper races the workers, attempting an unload as often as it can.
        workers.Add(Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await provider.UnloadAsync(ct);
                    await Task.Delay(25, ct);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    failures.Add($"sweeper threw {ex.GetType().Name}: {ex.Message}");
                }
            }
        }, ct));

        await Task.WhenAll(workers);

        Assert.True(failures.IsEmpty, string.Join(Environment.NewLine, failures.Distinct()));

        // The provider must still be usable after all that churn.
        await provider.EnsureLoadedAsync(CancellationToken.None);
        Assert.True(provider.IsLoaded);
        Assert.NotNull(provider.CurrentSolution);
    }

    /// <summary>
    /// A server on a 30 minute threshold cycles several times a day. If each reload retained part
    /// of the previous workspace the memory problem would come back slowly, so assert the unloaded
    /// baseline does not drift upward across cycles.
    /// </summary>
    [Fact]
    public async Task RepeatedUnloadReloadCycles_DoNotAccumulateHeap()
    {
        await using var provider = await CreateProviderAsync();
        var docPath = ProbeDocument();
        const int cycles = 4;

        var unloadedHeap = new long[cycles];

        for (var i = 0; i < cycles; i++)
        {
            await provider.EnsureLoadedAsync(CancellationToken.None);
            Assert.True(provider.IsLoaded);

            // Touch the workspace so the cycle realises real state, not just a project graph.
            provider.EnterRequest();
            try
            {
                var doc = await provider.GetDocumentAsync(docPath);
                Assert.NotNull(doc);
                var model = await doc!.GetSemanticModelAsync();
                Assert.NotNull(model);
            }
            finally
            {
                provider.ExitRequest();
            }

            Assert.True(await provider.UnloadAsync(), $"unload declined on cycle {i}");
            Assert.False(provider.IsLoaded);

            unloadedHeap[i] = GC.GetTotalMemory(forceFullCollection: true);
        }

        // Compare the last cycle against the first. A per-cycle leak shows as steady growth; the
        // tolerance is deliberately loose so ordinary allocator noise cannot fail the build.
        var first = unloadedHeap[0];
        var last = unloadedHeap[cycles - 1];
        var growthMb = (last - first) / (1024.0 * 1024.0);

        Assert.True(
            last <= first * 1.5,
            $"unloaded heap grew {growthMb:F1} MB across {cycles} cycles " +
            $"({string.Join(" -> ", unloadedHeap.Select(b => $"{b / (1024 * 1024)}MB"))}) - possible per-reload leak");
    }
}
