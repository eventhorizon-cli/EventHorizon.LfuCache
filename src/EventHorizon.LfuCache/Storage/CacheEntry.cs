namespace EventHorizon.LfuCache.Storage;

internal sealed class CacheEntry<TValue>
{
    private long _frequencyState;

    private CacheEntry(
        TValue value,
        InflightOperation<TValue>? inflight,
        long frequency,
        uint frequencyEpoch,
        long lastAccessTicks,
        long createdTicks,
        long expiresAtTicks)
    {
        Value = value;
        Inflight = inflight;
        _frequencyState = PackFrequency(frequencyEpoch, frequency);
        LastAccessTicks = lastAccessTicks;
        CreatedTicks = createdTicks;
        ExpiresAtTicks = expiresAtTicks;
    }

    public TValue Value { get; }

    public InflightOperation<TValue>? Inflight { get; }

    public bool IsCompleted => Inflight is null;

    public long LastAccessTicks;

    public long CreatedTicks { get; }

    public long ExpiresAtTicks;

    public static CacheEntry<TValue> Completed(
        TValue value,
        long frequency,
        uint frequencyEpoch,
        long lastAccessTicks,
        long createdTicks,
        long expiresAtTicks)
    {
        return new CacheEntry<TValue>(value, null, frequency, frequencyEpoch, lastAccessTicks, createdTicks, expiresAtTicks);
    }

    public static CacheEntry<TValue> Pending(InflightOperation<TValue> inflight, long nowTicks, uint frequencyEpoch)
    {
        return new CacheEntry<TValue>(default!, inflight, 1, frequencyEpoch, nowTicks, nowTicks, long.MaxValue);
    }

    public long GetFrequency(uint epoch)
    {
        return DecayedFrequency(Volatile.Read(ref _frequencyState), epoch);
    }

    public void RecordAccess(uint epoch, long nowTicks)
    {
        UpdateFrequency(epoch, increment: true);
        Volatile.Write(ref LastAccessTicks, nowTicks);
    }

    public bool Decay(uint epoch)
    {
        return UpdateFrequency(epoch, increment: false);
    }

    private bool UpdateFrequency(uint epoch, bool increment)
    {
        var observed = Volatile.Read(ref _frequencyState);
        while (true)
        {
            // A reader can have captured an older epoch before a newer reader updated this entry.
            // Keep the newer epoch so that concurrent accesses cannot move frequency accounting backwards.
            var storedEpoch = (uint)((ulong)observed >> 32);
            var activeEpoch = unchecked((int)(epoch - storedEpoch)) < 0 ? storedEpoch : epoch;
            var frequency = DecayedFrequency(observed, activeEpoch);
            var updated = PackFrequency(activeEpoch, increment ? Math.Min(uint.MaxValue, frequency + 1) : frequency);
            if (observed == updated)
            {
                return false;
            }

            var actual = Interlocked.CompareExchange(ref _frequencyState, updated, observed);
            if (actual == observed)
            {
                return true;
            }

            observed = actual;
        }
    }

    private static long DecayedFrequency(long state, uint epoch)
    {
        var storedEpoch = (uint)((ulong)state >> 32);
        var elapsed = unchecked((int)(epoch - storedEpoch));
        var frequency = (uint)state;
        return elapsed <= 0 ? frequency : elapsed >= 32 ? 1 : Math.Max(1, frequency >> elapsed);
    }

    private static long PackFrequency(uint epoch, long frequency)
    {
        return unchecked((long)(((ulong)epoch << 32) | (uint)Math.Clamp(frequency, 1, uint.MaxValue)));
    }
}
