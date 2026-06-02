using System.Collections.Concurrent;
using RoslynMcp.Core.Helpers;
using RoslynMcp.Shared.Contracts.Common;
using Xunit;

namespace RoslynMcp.Core.Tests.Helpers;

public class PagingHelperTests
{
    // Helper: simple enrichment that doubles the int
    private static Task<int> DoubleAsync(int stub, CancellationToken ct) =>
        Task.FromResult(stub * 2);

    [Fact]
    public async Task PageAndEnrichAsync_EnrichesOnlyPagedItems()
    {
        var stubs = Enumerable.Range(0, 100).ToList();
        var callCount = 0;
        Task<int> CountingEnrich(int stub, CancellationToken ct)
        {
            Interlocked.Increment(ref callCount);
            return Task.FromResult(stub * 2);
        }

        var result = await PagingHelper.PageAndEnrichAsync<int, int>(
            stubs, page: 0, pageSize: 10, CountingEnrich);

        Assert.Equal(10, result.Items.Count);
        Assert.Equal(100, result.TotalCount);
        Assert.Equal(10, callCount); // NOT 100
        Assert.Equal(0, result.Items[0]);   // 0 * 2
        Assert.Equal(18, result.Items[9]);  // 9 * 2
    }

    [Fact]
    public async Task PageAndEnrichAsync_RespectsPageOffset()
    {
        var stubs = Enumerable.Range(0, 50).ToList();

        var result = await PagingHelper.PageAndEnrichAsync<int, int>(
            stubs, page: 2, pageSize: 10, DoubleAsync);

        Assert.Equal(10, result.Items.Count);
        Assert.Equal(50, result.TotalCount);
        Assert.Equal(2, result.Page);
        Assert.Equal(40, result.Items[0]);  // 20 * 2
        Assert.Equal(58, result.Items[9]);  // 29 * 2
    }

    [Fact]
    public async Task PageAndEnrichAsync_CancellationRespected_PreCancelled()
    {
        var stubs = Enumerable.Range(0, 100).ToList();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            PagingHelper.PageAndEnrichAsync<int, int>(
                stubs, page: 0, pageSize: 10, DoubleAsync, ct: cts.Token));
    }

    [Fact]
    public async Task PageAndEnrichAsync_CancellationRespected_DuringEnrichment()
    {
        var stubs = Enumerable.Range(0, 10).ToList();
        using var cts = new CancellationTokenSource();

        Task<int> CancellingEnrich(int stub, CancellationToken ct)
        {
            // Cancel after first call starts; parallel tasks will see the token
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(stub);
        }

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            PagingHelper.PageAndEnrichAsync<int, int>(
                stubs, page: 0, pageSize: 10, CancellingEnrich, ct: cts.Token));
    }

    [Fact]
    public async Task PageAndEnrichAsync_EmptyList()
    {
        var result = await PagingHelper.PageAndEnrichAsync<int, int>(
            Array.Empty<int>(), page: 0, pageSize: 10, DoubleAsync);

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task PageAndEnrichAsync_LargePageNumber_NoOverflow()
    {
        var stubs = Enumerable.Range(0, 1000).ToList();

        // page * pageSize would overflow int if not handled
        var result = await PagingHelper.PageAndEnrichAsync<int, int>(
            stubs, page: int.MaxValue, pageSize: 100, DoubleAsync);

        // Should clamp to end — no items returned
        Assert.Empty(result.Items);
        Assert.Equal(1000, result.TotalCount);
    }

    [Fact]
    public async Task PageAndEnrichAsync_EnrichmentFailure_SkipsItem_TotalCountStable()
    {
        var stubs = Enumerable.Range(0, 10).ToList();
        var failedIndices = new ConcurrentBag<int>();

        Task<int> FailOnFive(int stub, CancellationToken ct)
        {
            if (stub == 5)
                throw new InvalidOperationException("enrichment failed");
            return Task.FromResult(stub * 2);
        }

        var result = await PagingHelper.PageAndEnrichAsync<int, int>(
            stubs, page: 0, pageSize: 10, FailOnFive,
            onEnrichmentFailed: (i, _) => failedIndices.Add(i));

        // 9 items enriched, but TotalCount remains stable at original stub count
        Assert.Equal(9, result.Items.Count);
        Assert.Equal(10, result.TotalCount);
        Assert.Equal(1, result.FailureCount);
        Assert.Single(failedIndices);
        Assert.Contains(5, failedIndices);
        // Verify item 5 (value=10) is missing from results
        Assert.DoesNotContain(10, result.Items);
    }

    [Fact]
    public async Task PageAndEnrichAsync_TotalCountStable_AcrossPages()
    {
        var stubs = Enumerable.Range(0, 20).ToList();

        Task<int> FailOnOdd(int stub, CancellationToken ct)
        {
            if (stub % 2 != 0)
                throw new InvalidOperationException("odd");
            return Task.FromResult(stub);
        }

        var page0 = await PagingHelper.PageAndEnrichAsync<int, int>(
            stubs, page: 0, pageSize: 10, FailOnOdd);
        var page1 = await PagingHelper.PageAndEnrichAsync<int, int>(
            stubs, page: 1, pageSize: 10, FailOnOdd);

        // TotalCount must be identical regardless of per-page failures
        Assert.Equal(20, page0.TotalCount);
        Assert.Equal(20, page1.TotalCount);
        Assert.Equal(page0.TotalCount, page1.TotalCount);
    }

    [Fact]
    public async Task PageAndEnrichAsync_ParallelExecution()
    {
        var stubs = Enumerable.Range(0, 5).ToList();
        var concurrency = 0;
        var maxConcurrency = 0;

        async Task<int> SlowEnrich(int stub, CancellationToken ct)
        {
            var current = Interlocked.Increment(ref concurrency);
            // Track peak concurrency
            int observed;
            do
            {
                observed = maxConcurrency;
            } while (current > observed && Interlocked.CompareExchange(ref maxConcurrency, current, observed) != observed);

            await Task.Delay(50, ct);
            Interlocked.Decrement(ref concurrency);
            return stub * 2;
        }

        var result = await PagingHelper.PageAndEnrichAsync<int, int>(
            stubs, page: 0, pageSize: 5, SlowEnrich);

        Assert.Equal(5, result.Items.Count);
        // With parallel execution, multiple tasks should overlap
        Assert.True(maxConcurrency > 1, $"Expected parallel execution, max concurrency was {maxConcurrency}");
    }

    [Fact]
    public async Task PageAndEnrichAsync_PreservesOrderDespiteParallelism()
    {
        var stubs = Enumerable.Range(0, 10).ToList();
        var rng = new Random(42);

        async Task<int> RandomDelayEnrich(int stub, CancellationToken ct)
        {
            await Task.Delay(rng.Next(1, 20), ct);
            return stub * 10;
        }

        var result = await PagingHelper.PageAndEnrichAsync<int, int>(
            stubs, page: 0, pageSize: 10, RandomDelayEnrich);

        Assert.Equal(10, result.Items.Count);
        for (int i = 0; i < 10; i++)
            Assert.Equal(i * 10, result.Items[i]);
    }

    [Fact]
    public void ClampPageSize_ValidatesMinimum()
    {
        Assert.Throws<ArgumentException>(() => PagingHelper.ClampPageSize(10, min: 0));
        Assert.Throws<ArgumentException>(() => PagingHelper.ClampPageSize(10, min: -1));
    }

    [Fact]
    public void ClampPageSize_ValidatesMaxGreaterThanMin()
    {
        Assert.Throws<ArgumentException>(() => PagingHelper.ClampPageSize(10, min: 5, max: 3));
    }

    [Fact]
    public void ClampPageSize_AllowsCustomMax()
    {
        Assert.Equal(500, PagingHelper.ClampPageSize(500, max: 1000));
        Assert.Equal(1000, PagingHelper.ClampPageSize(2000, max: 1000));
    }

    [Fact]
    public void ClampPageSize_DefaultMax()
    {
        Assert.Equal(PagingHelper.DefaultMaxPageSize, PagingHelper.ClampPageSize(int.MaxValue));
    }

    [Fact]
    public void Page_NullItems_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PagingHelper.Page<int>(null!, 0, 10));
        Assert.Throws<ArgumentNullException>(() =>
            PagingHelper.Page<int>((IEnumerable<int>)null!, 0, 10));
    }

    [Fact]
    public void HasMore_NoOverflow_WithLargePageValues()
    {
        var result = new PagedResult<int>(
            Array.Empty<int>(), TotalCount: 100, Page: int.MaxValue, PageSize: 100);

        // Should not overflow; (int.MaxValue + 1) * 100 would overflow int
        Assert.False(result.HasMore);
    }

    [Fact]
    public void FailureCount_DefaultsToZero()
    {
        var result = new PagedResult<int>(new[] { 1, 2, 3 }, 3, 0, 10);
        Assert.Equal(0, result.FailureCount);
    }
}
