using EventHorizon.LfuCache.Storage;

namespace EventHorizon.LfuCache.Eviction;

internal readonly record struct EvictionCandidate<TKey, TValue>(TKey Key, CacheEntry<TValue> Entry)
    where TKey : notnull;
