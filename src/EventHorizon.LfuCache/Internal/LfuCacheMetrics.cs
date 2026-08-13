using System.Diagnostics.Metrics;

namespace EventHorizon.LfuCache.Internal;

internal sealed class LfuCacheMetrics : IDisposable
{
    private readonly LfuCacheRegistry _registry;
    private readonly Meter _meter = new("EventHorizon.LfuCache");
    private readonly Counter<long> _hits;
    private readonly Counter<long> _misses;
    private readonly Counter<long> _evictions;
    private readonly Counter<long> _expirations;
    private readonly Counter<long> _evictionBatches;
    private readonly Histogram<double> _evictionDuration;
    private readonly Counter<long> _synchronousEvictions;

    public LfuCacheMetrics(LfuCacheRegistry registry)
    {
        _registry = registry;
        _hits = _meter.CreateCounter<long>("lfu_cache.hits");
        _misses = _meter.CreateCounter<long>("lfu_cache.misses");
        _evictions = _meter.CreateCounter<long>("lfu_cache.evictions");
        _expirations = _meter.CreateCounter<long>("lfu_cache.expirations");
        _evictionBatches = _meter.CreateCounter<long>("lfu_cache.eviction.batches");
        _evictionDuration = _meter.CreateHistogram<double>("lfu_cache.eviction.duration", "ms");
        _synchronousEvictions = _meter.CreateCounter<long>("lfu_cache.eviction.synchronous");
        _meter.CreateObservableGauge("lfu_cache.entries", ObserveEntries);
        _meter.CreateObservableGauge("lfu_cache.capacity", ObserveCapacity);
    }

    public void Hit(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        _hits.Add(1, tags);
    }

    public void Miss(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        _misses.Add(1, tags);
    }

    public void Evicted(long count, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        _evictions.Add(count, tags);
    }

    public void Expired(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        _expirations.Add(1, tags);
    }

    public void EvictionBatch(double elapsedMilliseconds, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        _evictionBatches.Add(1, tags);
        _evictionDuration.Record(elapsedMilliseconds, tags);
    }

    public void SynchronousEviction(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        _synchronousEvictions.Add(1, tags);
    }

    public void Dispose()
    {
        _meter.Dispose();
    }

    private IEnumerable<Measurement<int>> ObserveEntries()
    {
        foreach (var cache in _registry.All())
        {
            yield return new Measurement<int>(cache.GetStats().Count, CreateTags(cache));
        }
    }

    private IEnumerable<Measurement<int>> ObserveCapacity()
    {
        foreach (var cache in _registry.All())
        {
            yield return new Measurement<int>(cache.GetStats().Capacity, CreateTags(cache));
        }
    }

    private static KeyValuePair<string, object?>[] CreateTags(ILfuCacheHandle cache)
    {
        return
        [
            new KeyValuePair<string, object?>("keyspace", cache.Keyspace),
            new KeyValuePair<string, object?>("value_type", cache.ValueType.FullName ?? cache.ValueType.Name),
        ];
    }
}
