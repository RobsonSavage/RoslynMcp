using RoslynMcp.Core.Helpers;
using Xunit;

namespace RoslynMcp.Core.Tests.Services;

public class ToolMetricsServiceTests
{
    // --- Test 1: Record and retrieve basic metrics ---

    [Fact]
    public void Record_TracksInvocationCount()
    {
        var svc = new ToolMetricsService();

        svc.Record("find_references", TimeSpan.FromMilliseconds(100));
        svc.Record("find_references", TimeSpan.FromMilliseconds(200));
        svc.Record("find_references", TimeSpan.FromMilliseconds(150));

        var snapshots = svc.GetAllSnapshots();
        Assert.True(snapshots.ContainsKey("find_references"));
        Assert.Equal(3, snapshots["find_references"].Invocations);
        Assert.Equal(0, snapshots["find_references"].Errors);
    }

    // --- Test 2: Error counting ---

    [Fact]
    public void Record_TracksErrorCount()
    {
        var svc = new ToolMetricsService();

        svc.Record("text_search", TimeSpan.FromMilliseconds(50));
        svc.Record("text_search", TimeSpan.FromMilliseconds(30), isError: true);
        svc.Record("text_search", TimeSpan.FromMilliseconds(40));

        var snap = svc.GetAllSnapshots()["text_search"];
        Assert.Equal(3, snap.Invocations);
        Assert.Equal(1, snap.Errors);
    }

    // --- Test 3: Latency percentiles ---

    [Fact]
    public void Record_ComputesLatencyPercentiles()
    {
        var svc = new ToolMetricsService();

        // 100 samples: 1ms, 2ms, ..., 100ms
        for (int i = 1; i <= 100; i++)
            svc.Record("get_type_info", TimeSpan.FromMilliseconds(i));

        var snap = svc.GetAllSnapshots()["get_type_info"];

        // p50 should be around 51ms, p95 around 95ms, p99 around 99ms
        Assert.InRange(snap.LatencyP50Ms, 49, 53);
        Assert.InRange(snap.LatencyP95Ms, 93, 97);
        Assert.InRange(snap.LatencyP99Ms, 97, 100);
    }

    // --- Test 4: Multiple tools tracked independently ---

    [Fact]
    public void Record_TracksMultipleToolsIndependently()
    {
        var svc = new ToolMetricsService();

        svc.Record("tool_a", TimeSpan.FromMilliseconds(10));
        svc.Record("tool_b", TimeSpan.FromMilliseconds(20));
        svc.Record("tool_b", TimeSpan.FromMilliseconds(30));

        var snapshots = svc.GetAllSnapshots();
        Assert.Equal(2, snapshots.Count);
        Assert.Equal(1, snapshots["tool_a"].Invocations);
        Assert.Equal(2, snapshots["tool_b"].Invocations);
    }

    // --- Test 5: Empty metrics returns empty dictionary ---

    [Fact]
    public void GetAllSnapshots_EmptyWhenNoRecordings()
    {
        var svc = new ToolMetricsService();
        var snapshots = svc.GetAllSnapshots();
        Assert.Empty(snapshots);
    }

    // --- Test 6: Sliding window caps at 300 samples ---

    [Fact]
    public void SlidingWindow_CapsAt300Samples()
    {
        var svc = new ToolMetricsService();

        // Record 500 samples — window should only keep last 300
        for (int i = 1; i <= 500; i++)
            svc.Record("overflow_test", TimeSpan.FromMilliseconds(i));

        var snap = svc.GetAllSnapshots()["overflow_test"];
        Assert.Equal(500, snap.Invocations);

        // p50 of samples 201-500 should be around 351ms
        Assert.InRange(snap.LatencyP50Ms, 349, 353);
    }

    // --- Test 7: Thread safety under concurrent access ---

    [Fact]
    public async Task Record_ThreadSafe()
    {
        var svc = new ToolMetricsService();
        var tasks = new Task[10];

        for (int t = 0; t < 10; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < 100; i++)
                    svc.Record("concurrent_tool", TimeSpan.FromMilliseconds(1));
            });
        }

        await Task.WhenAll(tasks);

        var snap = svc.GetAllSnapshots()["concurrent_tool"];
        Assert.Equal(1000, snap.Invocations);
    }

    // --- Test 8: Zero-duration recording ---

    [Fact]
    public void Record_ZeroDuration()
    {
        var svc = new ToolMetricsService();
        svc.Record("fast_tool", TimeSpan.Zero);

        var snap = svc.GetAllSnapshots()["fast_tool"];
        Assert.Equal(1, snap.Invocations);
        Assert.Equal(0, snap.LatencyP50Ms);
    }
}
