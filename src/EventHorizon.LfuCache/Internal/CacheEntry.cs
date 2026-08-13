namespace EventHorizon.LfuCache.Internal;

internal sealed class CacheEntry<TValue>
{
    private CacheEntry(
        TValue value,
        InflightOperation<TValue>? inflight,
        long frequency,
        long lastAccessTicks,
        long createdTicks,
        long expiresAtTicks)
    {
        Value = value;
        Inflight = inflight;
        Frequency = frequency;
        LastAccessTicks = lastAccessTicks;
        CreatedTicks = createdTicks;
        ExpiresAtTicks = expiresAtTicks;
    }

    public TValue Value { get; }

    public InflightOperation<TValue>? Inflight { get; }

    public bool IsCompleted => Inflight is null;

    public long Frequency;

    public long LastAccessTicks;

    public long CreatedTicks { get; }

    public long ExpiresAtTicks;

    public static CacheEntry<TValue> Completed(
        TValue value,
        long frequency,
        long lastAccessTicks,
        long createdTicks,
        long expiresAtTicks)
    {
        return new CacheEntry<TValue>(value, null, frequency, lastAccessTicks, createdTicks, expiresAtTicks);
    }

    public static CacheEntry<TValue> Pending(InflightOperation<TValue> inflight, long nowTicks)
    {
        return new CacheEntry<TValue>(default!, inflight, 1, nowTicks, nowTicks, long.MaxValue);
    }
}
