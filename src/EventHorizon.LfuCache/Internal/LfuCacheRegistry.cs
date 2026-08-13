using System.Collections.Concurrent;
using System.Threading.Channels;

namespace EventHorizon.LfuCache.Internal;

internal sealed class LfuCacheRegistry : IDisposable
{
    private readonly ConcurrentDictionary<string, ILfuCacheHandle> _caches =
        new(StringComparer.Ordinal);
    private readonly Channel<bool> _signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        AllowSynchronousContinuations = false,
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false,
    });

    public void Register<TKey, TValue>(string keyspace, LfuCache<TKey, TValue> cache)
        where TKey : notnull
    {
        var registered = _caches.GetOrAdd(keyspace, cache);

        if (!ReferenceEquals(registered, cache))
        {
            var detail = registered.KeyType == typeof(TKey) && registered.ValueType == typeof(TValue)
                ? "another instance of the same store"
                : $"<{registered.KeyType.Name}, {registered.ValueType.Name}>";

            throw new InvalidOperationException(
                $"LFU cache keyspace '{keyspace}' is already backed by {detail}.");
        }

        Signal();
    }

    public ILfuCache<TKey, TValue>? Resolve<TKey, TValue>(string keyspace)
        where TKey : notnull
    {
        if (!_caches.TryGetValue(keyspace, out var cache)
            || cache.KeyType != typeof(TKey)
            || cache.ValueType != typeof(TValue))
        {
            return null;
        }

        return (ILfuCache<TKey, TValue>)cache;
    }

    public ILfuCacheHandle? Resolve(string keyspace)
    {
        _caches.TryGetValue(keyspace, out var cache);
        return cache;
    }

    public ILfuCacheHandle[] All()
    {
        return [.. _caches.Values];
    }

    public void Signal()
    {
        _signals.Writer.TryWrite(true);
    }

    public Task WaitForSignalAsync(CancellationToken cancellationToken)
    {
        return _signals.Reader.ReadAsync(cancellationToken).AsTask();
    }

    public void Dispose()
    {
        _signals.Writer.TryComplete();
    }
}
