namespace EventHorizon.LfuCache.Internal;

internal readonly record struct EvictionCandidate<TKey, TValue>(TKey Key, CacheEntry<TValue> Entry)
    where TKey : notnull;
