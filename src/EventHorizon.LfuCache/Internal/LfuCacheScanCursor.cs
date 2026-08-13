namespace EventHorizon.LfuCache.Internal;

internal sealed class LfuCacheScanCursor<TKey, TValue> : IDisposable
    where TKey : notnull
{
    public LfuCacheScanCursor(
        LfuCacheStoreState<TKey, TValue> state,
        IEnumerator<KeyValuePair<TKey, CacheEntry<TValue>>> enumerator)
    {
        State = state;
        Enumerator = enumerator;
    }

    public LfuCacheStoreState<TKey, TValue> State { get; }

    public IEnumerator<KeyValuePair<TKey, CacheEntry<TValue>>> Enumerator { get; }

    public void Dispose()
    {
        Enumerator.Dispose();
    }
}
