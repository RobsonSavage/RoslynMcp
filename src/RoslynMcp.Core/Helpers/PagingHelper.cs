using System;
using System.Threading;
using System.Threading.Tasks;
using RoslynMcp.Shared.Contracts.Common;

namespace RoslynMcp.Core.Helpers;

public static class PagingHelper
{
    public const int MaxResults = 10_000;
    public const int DefaultMaxPageSize = 200;

    public static PagedResult<T> Page<T>(IReadOnlyList<T> items, int page, int pageSize)
    {
        if (items == null) throw new ArgumentNullException(nameof(items));
        pageSize = ClampPageSize(pageSize);
        page = Math.Max(0, page);
        var start = (int)Math.Min((long)page * pageSize, items.Count);
        var count = Math.Min(pageSize, items.Count - start);
        var paged = new List<T>(count);
        for (int i = start; i < start + count; i++)
            paged.Add(items[i]);
        return new PagedResult<T>(paged, items.Count, page, pageSize);
    }

    public static PagedResult<T> Page<T>(IEnumerable<T> items, int page, int pageSize)
    {
        if (items == null) throw new ArgumentNullException(nameof(items));
        var list = items as IReadOnlyList<T> ?? items.ToList();
        return Page(list, page, pageSize);
    }

    public static int ClampPageSize(int pageSize, int min = 1, int max = DefaultMaxPageSize)
    {
        if (min < 1) throw new ArgumentException("min must be >= 1", nameof(min));
        if (max < min) throw new ArgumentException("max must be >= min", nameof(max));
        return Math.Max(min, Math.Min(max, pageSize));
    }

    /// <summary>
    /// Pages stubs first, then enriches only the current page in parallel.
    /// Failed enrichments are skipped; TotalCount reflects the original stub count (stable across pages).
    /// Use <see cref="PagedResult{T}.FailureCount"/> to detect per-page failures.
    /// </summary>
    public static async Task<PagedResult<TResult>> PageAndEnrichAsync<TStub, TResult>(
        IReadOnlyList<TStub> stubs,
        int page,
        int pageSize,
        Func<TStub, CancellationToken, Task<TResult>> enrichFunc,
        Action<int, Exception>? onEnrichmentFailed = null,
        CancellationToken ct = default)
    {
        if (stubs == null) throw new ArgumentNullException(nameof(stubs));
        if (enrichFunc == null) throw new ArgumentNullException(nameof(enrichFunc));

        pageSize = ClampPageSize(pageSize);
        page = Math.Max(0, page);

        if (stubs.Count == 0)
            return new PagedResult<TResult>(Array.Empty<TResult>(), 0, page, pageSize);

        var totalCount = stubs.Count;
        var start = (int)Math.Min((long)page * pageSize, totalCount);
        var count = Math.Min(pageSize, totalCount - start);

        ct.ThrowIfCancellationRequested();

        var tasks = new Task<(int index, TResult? result, Exception? error)>[count];
        for (int i = 0; i < count; i++)
        {
            int stubIndex = start + i;
            tasks[i] = EnrichOneAsync(stubs[stubIndex], stubIndex, enrichFunc, ct);
        }

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        var enriched = new List<TResult>(count);
        int failureCount = 0;
        foreach (var r in results)
        {
            if (r.error == null)
            {
                enriched.Add(r.result!);
            }
            else
            {
                failureCount++;
                onEnrichmentFailed?.Invoke(r.index, r.error);
            }
        }

        return new PagedResult<TResult>(enriched, totalCount, page, pageSize)
        {
            FailureCount = failureCount
        };
    }

    private static async Task<(int index, TResult? result, Exception? error)> EnrichOneAsync<TStub, TResult>(
        TStub stub, int index, Func<TStub, CancellationToken, Task<TResult>> enrichFunc, CancellationToken ct)
    {
        try
        {
            var result = await enrichFunc(stub, ct).ConfigureAwait(false);
            return (index, result, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return (index, default, ex); }
    }
}
