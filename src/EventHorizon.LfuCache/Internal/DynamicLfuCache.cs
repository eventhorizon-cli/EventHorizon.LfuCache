namespace EventHorizon.LfuCache.Internal;

internal sealed class DynamicLfuCache : ILfuCache
{
    private readonly LfuCacheRegistry _registry;

    public DynamicLfuCache(string keyspace, LfuCacheRegistry registry)
    {
        Keyspace = keyspace;
        _registry = registry;
    }

    public string Keyspace { get; }

    public bool TryGet<TKey, TValue>(TKey key, out TValue value)
        where TKey : notnull
    {
        return Typed<TKey, TValue>().TryGet(key, out value);
    }

    public void Set<TKey, TValue>(TKey key, TValue value, TimeSpan? expiry = null)
        where TKey : notnull
    {
        Typed<TKey, TValue>().Set(key, value, expiry);
    }

    public TValue GetOrAdd<TKey, TValue>(TKey key, Func<TKey, TValue> factory, TimeSpan? expiry = null)
        where TKey : notnull
    {
        return Typed<TKey, TValue>().GetOrAdd(key, factory, expiry);
    }

    public ValueTask<TValue> GetOrAddAsync<TKey, TValue>(
        TKey key,
        Func<TKey, CancellationToken, ValueTask<TValue>> factory,
        TimeSpan? expiry = null,
        CancellationToken ct = default)
        where TKey : notnull
    {
        return Typed<TKey, TValue>().GetOrAddAsync(key, factory, expiry, ct);
    }

    public bool Remove<TKey, TValue>(TKey key)
        where TKey : notnull
    {
        return Typed<TKey, TValue>().Remove(key);
    }

    public void Clear()
    {
        RequiredHandle().Clear();
    }

    public LfuCacheStats GetStats()
    {
        return RequiredHandle().GetStats();
    }

    private ILfuCache<TKey, TValue> Typed<TKey, TValue>()
        where TKey : notnull
    {
        return _registry.Resolve<TKey, TValue>(Keyspace) ?? throw TypeMismatch<TKey, TValue>();
    }

    private ILfuCacheHandle RequiredHandle()
    {
        return _registry.Resolve(Keyspace)
            ?? throw new InvalidOperationException($"LFU cache keyspace '{Keyspace}' has not been initialized.");
    }

    private InvalidOperationException TypeMismatch<TKey, TValue>()
    {
        var registered = _registry.Resolve(Keyspace);
        var registeredTypes = registered is null
            ? "no initialized store"
            : $"<{registered.KeyType.Name}, {registered.ValueType.Name}>";

        return new InvalidOperationException(
            $"LFU cache keyspace '{Keyspace}' is backed by {registeredTypes}, not " +
            $"<{typeof(TKey).Name}, {typeof(TValue).Name}>. Use the registered types, or register " +
            $"services.AddLfuCache<{typeof(TKey).Name}, {typeof(TValue).Name}>(\"another-keyspace\").");
    }
}
