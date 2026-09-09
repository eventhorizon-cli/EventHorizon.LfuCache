using System.Diagnostics.CodeAnalysis;

namespace EventHorizon.LfuCache.Storage;

internal sealed class DynamicLfuCacheFacade(ILfuCache store) : ILfuCache
{
    public string Keyspace => store.Keyspace;

    public bool TryGet<TKey, TValue>(TKey key, [MaybeNullWhen(false)] out TValue value)
        where TKey : notnull
    {
        return store.TryGet(key, out value);
    }

    public void Set<TKey, TValue>(TKey key, TValue value, TimeSpan? expiry = null)
        where TKey : notnull
    {
        store.Set(key, value, expiry);
    }

    public TValue GetOrAdd<TKey, TValue>(TKey key, Func<TKey, TValue> factory, TimeSpan? expiry = null)
        where TKey : notnull
    {
        return store.GetOrAdd(key, factory, expiry);
    }

    public ValueTask<TValue> GetOrAddAsync<TKey, TValue>(
        TKey key,
        Func<TKey, CancellationToken, ValueTask<TValue>> factory,
        TimeSpan? expiry = null,
        CancellationToken cancellationToken = default)
        where TKey : notnull
    {
        return store.GetOrAddAsync(key, factory, expiry, cancellationToken);
    }

    public bool Remove<TKey, TValue>(TKey key)
        where TKey : notnull
    {
        return store.Remove<TKey, TValue>(key);
    }

    public void Clear()
    {
        store.Clear();
    }

    public LfuCacheStats GetStats()
    {
        return store.GetStats();
    }
}
