namespace EventHorizon.LfuCache;

/// <summary>Represents a point-in-time snapshot of cache counters and capacity.</summary>
/// <param name="Hits">The number of successful reads.</param>
/// <param name="Misses">The number of unsuccessful reads, including expired reads.</param>
/// <param name="Evictions">The number of entries removed to enforce capacity.</param>
/// <param name="Expirations">The number of expired entries physically removed.</param>
/// <param name="EvictionBatches">The number of completed capacity-eviction batches.</param>
/// <param name="Count">The number of physically stored entries.</param>
/// <param name="Capacity">The configured entry capacity.</param>
public readonly record struct LfuCacheStats(
    long Hits,
    long Misses,
    long Evictions,
    long Expirations,
    long EvictionBatches,
    int Count,
    int Capacity);
