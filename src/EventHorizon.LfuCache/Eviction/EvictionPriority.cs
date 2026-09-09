namespace EventHorizon.LfuCache.Eviction;

internal readonly record struct EvictionPriority(long Frequency, bool IsProtected, long LastAccessTicks)
    : IComparable<EvictionPriority>
{
    public int CompareTo(EvictionPriority other)
    {
        var frequency = Frequency.CompareTo(other.Frequency);
        if (frequency != 0)
        {
            return frequency;
        }

        var protection = IsProtected.CompareTo(other.IsProtected);
        return protection != 0 ? protection : LastAccessTicks.CompareTo(other.LastAccessTicks);
    }
}
