namespace EventHorizon.LfuCache;

/// <summary>Provides a typed, in-process least-frequently-used cache.</summary>
/// <typeparam name="TKey">The cache key type.</typeparam>
/// <typeparam name="TValue">The cache value type.</typeparam>
public interface ILfuCache<TKey, TValue>
    where TKey : notnull
{
    /// <summary>Gets the normalized keyspace served by this cache.</summary>
    string Keyspace { get; }

    /// <summary>Gets the number of physically stored entries, including expired entries not yet reclaimed.</summary>
    int Count { get; }

    /// <summary>Attempts to get a non-expired cached value.</summary>
    bool TryGet(TKey key, out TValue value);

    /// <summary>
    /// Sets a value. A <see langword="null"/> expiry uses the keyspace default; zero disables expiration.
    /// </summary>
    void Set(TKey key, TValue value, TimeSpan? expiry = null);

    /// <summary>Gets an existing value or creates it once for concurrent callers of the same key.</summary>
    TValue GetOrAdd(TKey key, Func<TKey, TValue> factory, TimeSpan? expiry = null);

    /// <summary>Asynchronously gets an existing value or creates it once for concurrent callers of the same key.</summary>
    ValueTask<TValue> GetOrAddAsync(
        TKey key,
        Func<TKey, CancellationToken, ValueTask<TValue>> factory,
        TimeSpan? expiry = null,
        CancellationToken ct = default);

    /// <summary>Removes a value if it is present.</summary>
    bool Remove(TKey key);

    /// <summary>Clears this typed store.</summary>
    void Clear();

    /// <summary>Gets a snapshot of this typed store's statistics.</summary>
    LfuCacheStats GetStats();
}
