using EventHorizon.LfuCache.Maintenance;

namespace EventHorizon.LfuCache.Configuration;

internal sealed class OptionsSnapshot
{
    private OptionsSnapshot(
        LfuCacheOptions source,
        int targetLimit,
        long hardLimit,
        int scanBudget,
        long maintenanceIntervalTicks,
        long decayIntervalTicks,
        long decayOriginTicks,
        uint decayOriginEpoch)
    {
        Capacity = source.Capacity;
        MaxInflight = source.MaxInflight;
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
        DecayOriginTicks = decayOriginTicks;
        DecayOriginEpoch = decayOriginEpoch;
        FirstDecayBoundaryTicks = TimestampMath.Add(decayOriginTicks, decayIntervalTicks);
    }

    public int Capacity { get; }

    public int? MaxInflight { get; }

    public int InflightLimit => MaxInflight ?? Capacity;

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

    private long DecayOriginTicks { get; }

    private uint DecayOriginEpoch { get; }

    private long FirstDecayBoundaryTicks { get; }

    public static OptionsSnapshot Create(
        LfuCacheOptions options,
        TimeProvider timeProvider,
        OptionsSnapshot? previous = null)
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
        var nowTicks = timeProvider.GetTimestamp();
        var preserveDecayOrigin = previous is not null && previous.DecayInterval == options.DecayInterval;
        var decayOriginTicks = preserveDecayOrigin ? previous!.DecayOriginTicks : nowTicks;
        var decayOriginEpoch = preserveDecayOrigin
            ? previous!.DecayOriginEpoch
            : previous?.GetFrequencyEpoch(nowTicks) ?? 0;

        return new OptionsSnapshot(
            options,
            targetLimit,
            hardLimit,
            scanBudget,
            TimestampMath.ToTimestampTicks(options.MaintenanceInterval, timeProvider),
            TimestampMath.ToTimestampTicks(options.DecayInterval, timeProvider),
            decayOriginTicks,
            decayOriginEpoch);
    }

    public uint GetFrequencyEpoch(long nowTicks)
    {
        if (nowTicks < FirstDecayBoundaryTicks)
        {
            return DecayOriginEpoch;
        }

        var elapsedTicks = Math.Max(0, nowTicks - DecayOriginTicks);
        var epoch = unchecked(DecayOriginEpoch + (uint)(elapsedTicks / DecayIntervalTicks));
        return epoch;
    }

    public bool HasSameValues(LfuCacheOptions options)
    {
        return Capacity == options.Capacity
            && MaxInflight == options.MaxInflight
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
