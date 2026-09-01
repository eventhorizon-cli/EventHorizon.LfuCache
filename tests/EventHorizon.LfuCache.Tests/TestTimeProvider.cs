namespace EventHorizon.LfuCache.Tests;

internal sealed class TestTimeProvider : TimeProvider
{
    private long _timestamp;

    public override long GetTimestamp()
    {
        return Volatile.Read(ref _timestamp);
    }

    public void Advance(TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);

        var delta = (long)Math.Ceiling(duration.TotalSeconds * TimestampFrequency);
        Interlocked.Add(ref _timestamp, Math.Max(1, delta));
    }
}
