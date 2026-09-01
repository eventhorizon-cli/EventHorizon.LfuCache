using System.Diagnostics.CodeAnalysis;

namespace EventHorizon.LfuCache;

/// <summary>
/// Provides a dynamic cache facade for callers that do not know the key and value types at compile time.
/// Prefer <see cref="ILfuCache{TKey,TValue}"/> when the types are known.
/// </summary>
public interface ILfuCache
{
    /// <summary>Gets the normalized keyspace served by this cache.</summary>
    /// <value>The normalized keyspace name.</value>
    string Keyspace { get; }

    /// <summary>Attempts to get a value from the registered store for the requested types.</summary>
    /// <typeparam name="TKey">The cache key type.</typeparam>
    /// <typeparam name="TValue">The cache value type.</typeparam>
    /// <param name="key">The key of the value to get.</param>
    /// <param name="value">
    /// When this method returns <see langword="true"/>, the cached value; otherwise, the default value of
    /// <typeparamref name="TValue"/>.
    /// </param>
    /// <returns><see langword="true"/> if a non-expired value was found; otherwise, <see langword="false"/>.</returns>
    bool TryGet<TKey, TValue>(TKey key, [MaybeNullWhen(false)] out TValue value)
        where TKey : notnull;

    /// <summary>
    /// Adds or replaces a value in the registered store for the requested types.
    /// </summary>
    /// <typeparam name="TKey">The cache key type.</typeparam>
    /// <typeparam name="TValue">The cache value type.</typeparam>
    /// <param name="key">The key of the value to set.</param>
    /// <param name="value">The value to cache.</param>
    /// <param name="expiry">
    /// The relative expiration duration. <see langword="null"/> uses the keyspace default, and
    /// <see cref="TimeSpan.Zero"/> disables expiration for this entry.
    /// </param>
    void Set<TKey, TValue>(TKey key, TValue value, TimeSpan? expiry = null)
        where TKey : notnull;

    /// <summary>Gets an existing value or creates it once for concurrent callers of the same key.</summary>
    /// <typeparam name="TKey">The cache key type.</typeparam>
    /// <typeparam name="TValue">The cache value type.</typeparam>
    /// <param name="key">The key of the value to get or add.</param>
    /// <param name="factory">The function used to create a value when the key is not present.</param>
    /// <param name="expiry">
    /// The relative expiration duration. <see langword="null"/> uses the keyspace default, and
    /// <see cref="TimeSpan.Zero"/> disables expiration for the created entry.
    /// </param>
    /// <returns>The existing or newly created value.</returns>
    TValue GetOrAdd<TKey, TValue>(TKey key, Func<TKey, TValue> factory, TimeSpan? expiry = null)
        where TKey : notnull;

    /// <summary>
    /// Asynchronously gets an existing value or creates it once for concurrent callers of the same key.
    /// </summary>
    /// <typeparam name="TKey">The cache key type.</typeparam>
    /// <typeparam name="TValue">The cache value type.</typeparam>
    /// <param name="key">The key of the value to get or add.</param>
    /// <param name="factory">The asynchronous function used to create a value when the key is not present.</param>
    /// <param name="expiry">
    /// The relative expiration duration. <see langword="null"/> uses the keyspace default, and
    /// <see cref="TimeSpan.Zero"/> disables expiration for the created entry.
    /// </param>
    /// <param name="cancellationToken">
    /// The token used to cancel this caller's wait. The factory receives a shared token that is canceled only when
    /// every caller waiting for the value has canceled.
    /// </param>
    /// <returns>A task-like object containing the existing or newly created value.</returns>
    ValueTask<TValue> GetOrAddAsync<TKey, TValue>(
        TKey key,
        Func<TKey, CancellationToken, ValueTask<TValue>> factory,
        TimeSpan? expiry = null,
        CancellationToken cancellationToken = default)
        where TKey : notnull;

    /// <summary>Removes a value from the registered store for the requested types.</summary>
    /// <typeparam name="TKey">The cache key type.</typeparam>
    /// <typeparam name="TValue">The cache value type.</typeparam>
    /// <param name="key">The key of the value to remove.</param>
    /// <returns><see langword="true"/> if the value was removed; otherwise, <see langword="false"/>.</returns>
    bool Remove<TKey, TValue>(TKey key)
        where TKey : notnull;

    /// <summary>Clears the store for this keyspace.</summary>
    void Clear();

    /// <summary>Gets statistics for this keyspace.</summary>
    /// <returns>A point-in-time snapshot of the cache statistics.</returns>
    LfuCacheStats GetStats();
}
