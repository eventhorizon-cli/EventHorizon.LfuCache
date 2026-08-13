namespace EventHorizon.LfuCache;

/// <summary>
/// Provides a dynamic cache facade for callers that do not know the key and value types at compile time.
/// Prefer <see cref="ILfuCache{TKey,TValue}"/> when the types are known.
/// </summary>
public interface ILfuCache
{
    /// <summary>Gets the normalized keyspace served by this cache.</summary>
    string Keyspace { get; }

    /// <summary>Attempts to get a value from the registered store for the requested types.</summary>
    bool TryGet<TKey, TValue>(TKey key, out TValue value)
        where TKey : notnull;

    /// <summary>
    /// Sets a value. A <see langword="null"/> expiry uses the keyspace default; zero disables expiration.
    /// </summary>
    void Set<TKey, TValue>(TKey key, TValue value, TimeSpan? expiry = null)
        where TKey : notnull;

    /// <summary>Gets an existing value or creates it once for concurrent callers of the same key.</summary>
    TValue GetOrAdd<TKey, TValue>(TKey key, Func<TKey, TValue> factory, TimeSpan? expiry = null)
        where TKey : notnull;

    /// <summary>Asynchronously gets an existing value or creates it once for concurrent callers of the same key.</summary>
    ValueTask<TValue> GetOrAddAsync<TKey, TValue>(
        TKey key,
        Func<TKey, CancellationToken, ValueTask<TValue>> factory,
        TimeSpan? expiry = null,
        CancellationToken ct = default)
        where TKey : notnull;

    /// <summary>Removes a value from the registered store for the requested types.</summary>
    bool Remove<TKey, TValue>(TKey key)
        where TKey : notnull;

    /// <summary>Clears the store for this keyspace.</summary>
    void Clear();

    /// <summary>Gets statistics for this keyspace.</summary>
    LfuCacheStats GetStats();
}
