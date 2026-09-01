using System.Diagnostics.CodeAnalysis;

namespace EventHorizon.LfuCache.Storage;

internal sealed class TypedLfuCacheFacade<TKey, TValue>(ILfuCache<TKey, TValue> store) : ILfuCache<TKey, TValue>
    where TKey : notnull
{
    private readonly ILfuCache<TKey, TValue> _store = store;

    public string Keyspace => _store.Keyspace;

    public int Count => _store.Count;

    public bool TryGet(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        return _store.TryGet(key, out value);
    }

    public void Set(TKey key, TValue value, TimeSpan? expiry = null)
    {
        _store.Set(key, value, expiry);
    }

    public TValue GetOrAdd(TKey key, Func<TKey, TValue> factory, TimeSpan? expiry = null)
    {
        return _store.GetOrAdd(key, factory, expiry);
    }

    public ValueTask<TValue> GetOrAddAsync(
        TKey key,
        Func<TKey, CancellationToken, ValueTask<TValue>> factory,
        TimeSpan? expiry = null,
        CancellationToken cancellationToken = default)
    {
        return _store.GetOrAddAsync(key, factory, expiry, cancellationToken);
    }

    public bool Remove(TKey key)
    {
        return _store.Remove(key);
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
