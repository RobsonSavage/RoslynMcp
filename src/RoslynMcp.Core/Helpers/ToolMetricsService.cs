using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace RoslynMcp.Core.Helpers;

/// <summary>
/// Tracks per-tool invocation counts, error counts, and latency percentiles.
/// Thread-safe. Counters reset daily at midnight UTC.
/// Latency uses a sliding window of 300 samples per tool (~440 KB total for 94 tools).
/// </summary>
public interface IToolMetricsService
{
    void Record(string toolName, TimeSpan duration, bool isError = false);
    IReadOnlyDictionary<string, ToolMetricSnapshot> GetAllSnapshots();
}

public record ToolMetricSnapshot(
    long Invocations,
    long Errors,
    double LatencyP50Ms,
    double LatencyP95Ms,
    double LatencyP99Ms);

public sealed class ToolMetricsService : IToolMetricsService
{
    private readonly ConcurrentDictionary<string, ToolMetrics> _metrics = new();

    public void Record(string toolName, TimeSpan duration, bool isError = false)
    {
        if (string.IsNullOrEmpty(toolName)) throw new ArgumentNullException(nameof(toolName));
        _metrics.GetOrAdd(toolName, _ => new ToolMetrics()).Record(duration, isError);
    }

    public IReadOnlyDictionary<string, ToolMetricSnapshot> GetAllSnapshots()
    {
        var result = new Dictionary<string, ToolMetricSnapshot>();
        foreach (var kvp in _metrics)
        {
            var p = kvp.Value.Latency.GetPercentiles();
            result[kvp.Key] = new ToolMetricSnapshot(
                kvp.Value.Invocations, kvp.Value.Errors,
                p.P50Ms, p.P95Ms, p.P99Ms);
        }
        return result;
    }
}

internal sealed class ToolMetrics
{
    private long _invocations;
    private long _errors;
    private long _resetDay;
    private readonly object _resetLock = new();

    public SlidingWindowLatency Latency { get; } = new();

    public long Invocations => Interlocked.Read(ref _invocations);
    public long Errors => Interlocked.Read(ref _errors);

    public void Record(TimeSpan duration, bool isError)
    {
        ResetIfNewDay();
        Interlocked.Increment(ref _invocations);
        if (isError) Interlocked.Increment(ref _errors);
        Latency.Add(duration);
    }

    private void ResetIfNewDay()
    {
        var today = DateTime.UtcNow.Ticks / TimeSpan.TicksPerDay;
        var lastDay = Interlocked.Read(ref _resetDay);
        if (today > lastDay)
        {
            lock (_resetLock)
            {
                // Re-check under lock to prevent double-reset
                lastDay = Interlocked.Read(ref _resetDay);
                if (today <= lastDay) return;

                Interlocked.Exchange(ref _resetDay, today);
                Interlocked.Exchange(ref _invocations, 0);
                Interlocked.Exchange(ref _errors, 0);
                Latency.Reset();
            }
        }
    }
}

internal sealed class SlidingWindowLatency
{
    private readonly long[] _buffer;
    private int _index;
    private int _count;
    private readonly object _lock = new();

    public SlidingWindowLatency(int capacity = 300)
        => _buffer = new long[capacity];

    public void Reset()
    {
        lock (_lock) { _count = 0; _index = 0; }
    }

    public void Add(TimeSpan duration)
    {
        lock (_lock)
        {
            _buffer[_index] = duration.Ticks;
            _index = (_index + 1) % _buffer.Length;
            if (_count < _buffer.Length) _count++;
        }
    }

    public LatencyPercentiles GetPercentiles()
    {
        lock (_lock)
        {
            if (_count == 0) return new(0, 0, 0);
            var sorted = new long[_count];
            for (int i = 0; i < _count; i++)
            {
                int idx = (_index - _count + i + _buffer.Length) % _buffer.Length;
                sorted[i] = _buffer[idx];
            }
            Array.Sort(sorted);
            return new(
                TicksToMs(sorted[Percentile(_count, 0.50)]),
                TicksToMs(sorted[Percentile(_count, 0.95)]),
                TicksToMs(sorted[Percentile(_count, 0.99)]));
        }
    }

    private static int Percentile(int count, double p)
        => Math.Min((int)Math.Round((count - 1) * p), count - 1);

    private static double TicksToMs(long ticks)
        => Math.Round(TimeSpan.FromTicks(ticks).TotalMilliseconds, 1);
}

public record LatencyPercentiles(double P50Ms, double P95Ms, double P99Ms);
