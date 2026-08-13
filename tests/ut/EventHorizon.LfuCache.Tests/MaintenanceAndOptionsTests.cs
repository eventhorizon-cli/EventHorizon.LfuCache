using EventHorizon.LfuCache.Internal;
using Microsoft.Extensions.Logging.Abstractions;

namespace EventHorizon.LfuCache.Tests;

public sealed class MaintenanceAndOptionsTests
{
    [Fact]
    public async Task RegistrySignal_MultiplePendingSignals_CoalescesToOneWakeUp()
    {
        using var registry = new LfuCacheRegistry();

        registry.Signal();
        registry.Signal();

        await registry.WaitForSignalAsync(TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => registry.WaitForSignalAsync(cancellation.Token));
    }

    [Fact]
    public void RunMaintenance_ExpiredEntryReachedDueTime_ReclaimsIt()
    {
        using var host = new TestCacheHost<int, string>(
            configure: options => options.MaintenanceInterval = TimeSpan.FromSeconds(1));
        host.Cache.Set(1, "expired", TimeSpan.FromSeconds(1));
        host.Clock.Advance(TimeSpan.FromSeconds(1));

        host.Implementation.RunMaintenance(host.Clock.GetTimestamp());

        Assert.Equal(0, host.Cache.Count);
        Assert.Equal(1, host.Cache.GetStats().Expirations);
    }

    [Fact]
    public void RunMaintenance_DecayReachedDueTime_HalvesFrequencyWithFloorOfOne()
    {
        using var host = new TestCacheHost<int, string>(
            configure: options => options.DecayInterval = TimeSpan.FromSeconds(1));
        host.Cache.Set(1, "hot");
        host.Cache.Set(2, "cold");
        for (var index = 0; index < 4; index++)
        {
            Assert.True(host.Cache.TryGet(1, out _));
        }

        host.Clock.Advance(TimeSpan.FromSeconds(1));
        host.Implementation.RunMaintenance(host.Clock.GetTimestamp());

        var state = host.Implementation.GetType()
            .GetField("_state", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(host.Implementation)!;
        var entries = (System.Collections.IDictionary)state.GetType().GetProperty("Entries")!.GetValue(state)!;
        var hot = entries[1]!;
        var cold = entries[2]!;

        Assert.Equal(2L, hot.GetType().GetField("Frequency")!.GetValue(hot));
        Assert.Equal(1L, cold.GetType().GetField("Frequency")!.GetValue(cold));
    }

    [Fact]
    public void OptionsChange_ValidCapacity_ReplacesSnapshotAndEvictsInBatches()
    {
        using var fixture = CreateCache(new LfuCacheOptions
        {
            Capacity = 10,
            EvictionRatio = 0.2,
            OverflowRatio = 0.5,
            MaintenanceInterval = TimeSpan.FromMinutes(1),
            DecayInterval = TimeSpan.FromMinutes(1),
        });

        for (var key = 0; key < 10; key++)
        {
            fixture.Cache.Set(key, key);
        }

        fixture.Options.Update(new LfuCacheOptions
        {
            Capacity = 4,
            EvictionRatio = 0.5,
            OverflowRatio = 0.5,
            MaintenanceInterval = TimeSpan.FromMinutes(1),
            DecayInterval = TimeSpan.FromMinutes(1),
        }, "hot");

        Assert.Equal(4, fixture.Cache.GetStats().Capacity);

        fixture.Cache.RunMaintenance(fixture.Clock.GetTimestamp());
        Assert.Equal(8, fixture.Cache.Count);
        fixture.Cache.RunMaintenance(fixture.Clock.GetTimestamp());
        Assert.Equal(6, fixture.Cache.Count);
        fixture.Cache.RunMaintenance(fixture.Clock.GetTimestamp());
        Assert.Equal(4, fixture.Cache.Count);
        Assert.Equal(3, fixture.Cache.GetStats().EvictionBatches);
    }

    [Fact]
    public void OptionsChange_InvalidCapacity_KeepsPreviousSnapshot()
    {
        using var fixture = CreateCache(new LfuCacheOptions { Capacity = 8 });

        fixture.Options.Update(new LfuCacheOptions { Capacity = 0 }, "hot");

        Assert.Equal(8, fixture.Cache.GetStats().Capacity);
    }

    private static CacheFixture CreateCache(LfuCacheOptions options)
    {
        var clock = new TestTimeProvider();
        var monitor = new TestOptionsMonitor<LfuCacheOptions>(options);
        var registry = new LfuCacheRegistry();
        var metrics = new LfuCacheMetrics(registry);
        var cache = new LfuCache<int, int>(
            "hot",
            monitor,
            clock,
            registry,
            metrics,
            NullLogger.Instance);
        registry.Register("hot", cache);
        return new CacheFixture(cache, monitor, clock, registry, metrics);
    }

    private sealed class CacheFixture(
        LfuCache<int, int> cache,
        TestOptionsMonitor<LfuCacheOptions> options,
        TestTimeProvider clock,
        LfuCacheRegistry registry,
        LfuCacheMetrics metrics) : IDisposable
    {
        public LfuCache<int, int> Cache { get; } = cache;

        public TestOptionsMonitor<LfuCacheOptions> Options { get; } = options;

        public TestTimeProvider Clock { get; } = clock;

        public void Dispose()
        {
            Cache.Dispose();
            metrics.Dispose();
            registry.Dispose();
        }
    }
}
