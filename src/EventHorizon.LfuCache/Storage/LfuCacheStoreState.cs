using System.Collections.Concurrent;

namespace EventHorizon.LfuCache.Storage;

internal sealed class LfuCacheStoreState<TKey, TValue>
    where TKey : notnull
{
    public ConcurrentDictionary<TKey, CacheEntry<TValue>> Entries { get; } = new();

    public long Count;
}
