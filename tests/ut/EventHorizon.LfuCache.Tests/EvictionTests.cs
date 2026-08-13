namespace EventHorizon.LfuCache.Tests;

public sealed class EvictionTests
{
    [Fact]
    public void Set_CapacityExceeded_EvictsLeastFrequentEntriesByConfiguredPercentage()
    {
        using var host = new TestCacheHost<int, string>(
            configure: options =>
            {
                options.Capacity = 4;
                options.EvictionRatio = 0.5;
                options.OverflowRatio = 0;
                options.DefaultExpiry = null;
            });

        host.Cache.Set(1, "one");
        host.Cache.Set(2, "two");
        host.Cache.Set(3, "three");
        host.Cache.Set(4, "four");
        host.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(host.Cache.TryGet(1, out _));
        Assert.True(host.Cache.TryGet(1, out _));
        Assert.True(host.Cache.TryGet(1, out _));
        host.Clock.Advance(TimeSpan.FromSeconds(2));

        host.Cache.Set(5, "five");

        Assert.Equal(2, host.Cache.Count);
        Assert.True(host.Cache.TryGet(1, out _));
        Assert.True(host.Cache.TryGet(5, out _));
        Assert.False(host.Cache.TryGet(2, out _));
        Assert.Equal(3, host.Cache.GetStats().Evictions);
        Assert.Equal(1, host.Cache.GetStats().EvictionBatches);
    }

    [Fact]
    public void Set_CapacityExceeded_EvictsOldestLeastFrequentTie()
    {
        using var host = new TestCacheHost<int, string>(
            configure: options =>
            {
                options.Capacity = 4;
                options.EvictionRatio = 0.1;
                options.OverflowRatio = 0;
                options.DefaultExpiry = null;
            });

        host.Cache.Set(1, "one");
        host.Clock.Advance(TimeSpan.FromSeconds(2));
        host.Cache.Set(2, "two");
        host.Clock.Advance(TimeSpan.FromSeconds(2));
        host.Cache.Set(3, "three");
        host.Clock.Advance(TimeSpan.FromSeconds(2));
        host.Cache.Set(4, "expired", TimeSpan.FromSeconds(1));
        host.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(host.Cache.TryGet(1, out _));
        host.Clock.Advance(TimeSpan.FromSeconds(2));

        host.Cache.Set(5, "five");

        Assert.Equal(3, host.Cache.Count);
        Assert.True(host.Cache.TryGet(1, out _));
        Assert.False(host.Cache.TryGet(2, out _));
        Assert.True(host.Cache.TryGet(3, out _));
        Assert.False(host.Cache.TryGet(4, out _));
        Assert.True(host.Cache.TryGet(5, out _));
        Assert.Equal(1, host.Cache.GetStats().Evictions);
    }
}
