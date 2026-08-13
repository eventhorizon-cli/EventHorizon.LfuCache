namespace EventHorizon.LfuCache.Internal;

internal sealed class OptionsSnapshot
{
    private OptionsSnapshot(
        LfuCacheOptions source,
        int targetLimit,
        long hardLimit,
        int scanBudget,
        long maintenanceIntervalTicks,
        long decayIntervalTicks)
    {
        Capacity = source.Capacity;
        EvictionRatio = source.EvictionRatio;
        DefaultExpiry = source.DefaultExpiry;
        MaintenanceInterval = source.MaintenanceInterval;
        DecayInterval = source.DecayInterval;
        OverflowRatio = source.OverflowRatio;
        TargetLimit = targetLimit;
        HardLimit = hardLimit;
        ScanBudget = scanBudget;
        MaintenanceIntervalTicks = maintenanceIntervalTicks;
        DecayIntervalTicks = decayIntervalTicks;
    }

    public int Capacity { get; }

    public double EvictionRatio { get; }

    public TimeSpan? DefaultExpiry { get; }

    public TimeSpan MaintenanceInterval { get; }

    public TimeSpan DecayInterval { get; }

    public double OverflowRatio { get; }

    public int TargetLimit { get; }

    public long HardLimit { get; }

    public int ScanBudget { get; }

    public long MaintenanceIntervalTicks { get; }

    public long DecayIntervalTicks { get; }

    public static OptionsSnapshot Create(LfuCacheOptions options, TimeProvider timeProvider)
    {
        var evictionCount = Math.Max(1, (int)Math.Ceiling(options.Capacity * options.EvictionRatio));
        var targetLimit = Math.Max(0, options.Capacity - evictionCount);
        var hardLimit = (long)Math.Floor(options.Capacity * (1 + options.OverflowRatio));
        var sweepWindow = options.DefaultExpiry is { } expiry && expiry < TimeSpan.FromMinutes(1)
            ? expiry
            : TimeSpan.FromMinutes(1);
        var scansPerSweep = Math.Max(
            1L,
            (long)Math.Floor(sweepWindow.TotalSeconds / options.MaintenanceInterval.TotalSeconds));
        var scanBudget = Math.Max(1, SaturatingCeilingDivide(options.Capacity, scansPerSweep));

        return new OptionsSnapshot(
            options,
            targetLimit,
            hardLimit,
            scanBudget,
            TimestampMath.ToTimestampTicks(options.MaintenanceInterval, timeProvider),
            TimestampMath.ToTimestampTicks(options.DecayInterval, timeProvider));
    }

    public bool HasSameValues(LfuCacheOptions options)
    {
        return Capacity == options.Capacity
            && EvictionRatio.Equals(options.EvictionRatio)
            && DefaultExpiry == options.DefaultExpiry
            && MaintenanceInterval == options.MaintenanceInterval
            && DecayInterval == options.DecayInterval
            && OverflowRatio.Equals(options.OverflowRatio);
    }

    private static int SaturatingCeilingDivide(int value, long divisor)
    {
        var result = ((long)value + divisor - 1) / divisor;
        return result >= int.MaxValue ? int.MaxValue : (int)result;
    }
}
