using System.Diagnostics.CodeAnalysis;

namespace EventHorizon.LfuCache;

/// <summary>Provides a typed, in-process least-frequently-used cache.</summary>
/// <typeparam name="TKey">The cache key type.</typeparam>
/// <typeparam name="TValue">The cache value type.</typeparam>
public interface ILfuCache<TKey, TValue>
    where TKey : notnull
{
    /// <summary>Gets the normalized keyspace served by this cache.</summary>
    /// <value>The normalized keyspace name.</value>
    string Keyspace { get; }

    /// <summary>Gets the number of physically stored entries, including expired entries not yet reclaimed.</summary>
    /// <value>The number of physically stored entries.</value>
    int Count { get; }

    /// <summary>Attempts to get a non-expired cached value.</summary>
    /// <param name="key">The key of the value to get.</param>
    /// <param name="value">
    /// When this method returns <see langword="true"/>, the cached value; otherwise, the default value of
    /// <typeparamref name="TValue"/>.
    /// </param>
    /// <returns><see langword="true"/> if a non-expired value was found; otherwise, <see langword="false"/>.</returns>
    bool TryGet(TKey key, [MaybeNullWhen(false)] out TValue value);

    /// <summary>
    /// Adds or replaces a value in this cache.
    /// </summary>
    /// <param name="key">The key of the value to set.</param>
    /// <param name="value">The value to cache.</param>
    /// <param name="expiry">
    /// The relative expiration duration. <see langword="null"/> uses the keyspace default, and
    /// <see cref="TimeSpan.Zero"/> disables expiration for this entry.
    /// </param>
    void Set(TKey key, TValue value, TimeSpan? expiry = null);

    /// <summary>Gets an existing value or creates it once for concurrent callers of the same key.</summary>
    /// <param name="key">The key of the value to get or add.</param>
    /// <param name="factory">The function used to create a value when the key is not present.</param>
    /// <param name="expiry">
    /// The relative expiration duration. <see langword="null"/> uses the keyspace default, and
    /// <see cref="TimeSpan.Zero"/> disables expiration for the created entry.
    /// </param>
    /// <returns>The existing or newly created value.</returns>
    TValue GetOrAdd(TKey key, Func<TKey, TValue> factory, TimeSpan? expiry = null);

    /// <summary>
    /// Asynchronously gets an existing value or creates it once for concurrent callers of the same key.
    /// </summary>
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
    ValueTask<TValue> GetOrAddAsync(
        TKey key,
        Func<TKey, CancellationToken, ValueTask<TValue>> factory,
        TimeSpan? expiry = null,
        CancellationToken cancellationToken = default);

    /// <summary>Removes a value if it is present.</summary>
    /// <param name="key">The key of the value to remove.</param>
    /// <returns><see langword="true"/> if the value was removed; otherwise, <see langword="false"/>.</returns>
    bool Remove(TKey key);

    /// <summary>Clears this typed store.</summary>
    void Clear();

    /// <summary>Gets a snapshot of this typed store's statistics.</summary>
    /// <returns>A point-in-time snapshot of the cache statistics.</returns>
    LfuCacheStats GetStats();
}
