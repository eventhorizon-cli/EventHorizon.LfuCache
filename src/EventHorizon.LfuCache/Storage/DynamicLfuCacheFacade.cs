using System.Diagnostics.CodeAnalysis;

namespace EventHorizon.LfuCache.Storage;

internal sealed class DynamicLfuCacheFacade(ILfuCache store) : ILfuCache
{
    private readonly ILfuCache _store = store;

    public string Keyspace => _store.Keyspace;

    public bool TryGet<TKey, TValue>(TKey key, [MaybeNullWhen(false)] out TValue value)
        where TKey : notnull
    {
        return _store.TryGet(key, out value);
    }

    public void Set<TKey, TValue>(TKey key, TValue value, TimeSpan? expiry = null)
        where TKey : notnull
    {
        _store.Set(key, value, expiry);
    }

    public TValue GetOrAdd<TKey, TValue>(TKey key, Func<TKey, TValue> factory, TimeSpan? expiry = null)
        where TKey : notnull
    {
        return _store.GetOrAdd(key, factory, expiry);
    }

    public ValueTask<TValue> GetOrAddAsync<TKey, TValue>(
        TKey key,
        Func<TKey, CancellationToken, ValueTask<TValue>> factory,
        TimeSpan? expiry = null,
        CancellationToken cancellationToken = default)
        where TKey : notnull
    {
        return _store.GetOrAddAsync(key, factory, expiry, cancellationToken);
    }

    public bool Remove<TKey, TValue>(TKey key)
        where TKey : notnull
    {
        return _store.Remove<TKey, TValue>(key);
    }

    public void Clear()
    {
        _store.Clear();
    }

    public LfuCacheStats GetStats()
    {
        return _store.GetStats();
    }
}
