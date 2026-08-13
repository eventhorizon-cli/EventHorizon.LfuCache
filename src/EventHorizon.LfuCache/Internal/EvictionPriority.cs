namespace EventHorizon.LfuCache.Internal;

internal readonly record struct EvictionPriority(long Frequency, long LastAccessTicks)
    : IComparable<EvictionPriority>
{
    public int CompareTo(EvictionPriority other)
    {
        var frequency = Frequency.CompareTo(other.Frequency);
        return frequency != 0 ? frequency : LastAccessTicks.CompareTo(other.LastAccessTicks);
    }
}
