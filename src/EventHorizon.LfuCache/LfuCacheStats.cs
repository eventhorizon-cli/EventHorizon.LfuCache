namespace EventHorizon.LfuCache;

/// <summary>Represents a snapshot of cache counters and capacity.</summary>
/// <remarks>
/// Counter values are sampled while the cache may be processing operations. Under concurrent activity, the values are
/// internally consistent totals but are not a linearizable point-in-time observation.
/// </remarks>
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
