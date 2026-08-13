namespace EventHorizon.LfuCache.Internal;

internal static class TimestampMath
{
    public static long Add(long timestamp, long delta)
    {
        if (delta >= 0 && timestamp >= long.MaxValue - delta)
        {
            return long.MaxValue;
        }

        if (delta < 0 && timestamp <= long.MinValue - delta)
        {
            return long.MinValue;
        }

        return timestamp + delta;
    }

    public static long ToTimestampTicks(TimeSpan duration, TimeProvider timeProvider)
    {
        if (duration <= TimeSpan.Zero)
        {
            return 0;
        }

        var ticks = duration.TotalSeconds * timeProvider.TimestampFrequency;
        return ticks >= long.MaxValue ? long.MaxValue : Math.Max(1, (long)Math.Ceiling(ticks));
    }
}
