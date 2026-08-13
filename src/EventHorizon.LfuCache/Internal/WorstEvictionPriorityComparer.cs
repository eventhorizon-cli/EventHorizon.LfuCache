namespace EventHorizon.LfuCache.Internal;

internal sealed class WorstEvictionPriorityComparer : IComparer<EvictionPriority>
{
    public static WorstEvictionPriorityComparer Instance { get; } = new();

    public int Compare(EvictionPriority x, EvictionPriority y)
    {
        return y.CompareTo(x);
    }
}
