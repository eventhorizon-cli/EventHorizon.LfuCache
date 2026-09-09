using EventHorizon.LfuCache.Metrics;
using EventHorizon.LfuCache.Registration;
using EventHorizon.LfuCache.Storage;
using Microsoft.Extensions.Logging;

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

    [Fact]
    public void Set_SynchronousEvictionLoggingFailure_SchedulesMaintenanceRetry()
    {
        var options = new LfuCacheOptions
        {
            Capacity = 1,
            OverflowRatio = 0,
            MaintenanceInterval = TimeSpan.FromSeconds(1),
            DecayInterval = TimeSpan.FromMinutes(1),
        };
        var clock = new TestTimeProvider();
        var monitor = new TestOptionsMonitor<LfuCacheOptions>(options);
        using var registry = new LfuCacheRegistry();
        using var metrics = new LfuCacheMetrics(registry);
        var logger = new ThrowingLogger();
        using var cache = new LfuCache<int, string>(
            "hot",
            monitor,
            clock,
            registry,
            metrics,
            logger);
        registry.Register("hot", cache);

        cache.Set(1, "one");
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Throws<InvalidOperationException>(() => cache.Set(2, "two"));

        var nowTicks = clock.GetTimestamp();
        cache.RunMaintenance(nowTicks);
        var retryDueTicks = cache.NextDueTicks;
        Assert.Equal(nowTicks + clock.TimestampFrequency, retryDueTicks);

        logger.ThrowOnLog = false;
        clock.Advance(options.MaintenanceInterval);
        Assert.True(clock.GetTimestamp() >= retryDueTicks);

        cache.RunMaintenance(clock.GetTimestamp());

        Assert.InRange(cache.Count, 0, options.Capacity);
        Assert.Equal(1, cache.GetStats().EvictionBatches);
    }

    [Fact]
    public async Task GetOrAddAsync_SynchronousEvictionFailure_RemovesPendingEntryBeforeFactoryStarts()
    {
        var options = new LfuCacheOptions
        {
            Capacity = 1,
            MaxInflight = 2,
            OverflowRatio = 0,
            MaintenanceInterval = TimeSpan.FromSeconds(1),
            DecayInterval = TimeSpan.FromMinutes(1),
        };
        var clock = new TestTimeProvider();
        var monitor = new TestOptionsMonitor<LfuCacheOptions>(options);
        using var registry = new LfuCacheRegistry();
        using var metrics = new LfuCacheMetrics(registry);
        var logger = new ThrowingLogger();
        using var cache = new LfuCache<int, string>(
            "hot",
            monitor,
            clock,
            registry,
            metrics,
            logger);
        registry.Register("hot", cache);
        var firstFactoryStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstFactory = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondFactoryCalls = 0;

        async ValueTask<string> FirstFactory(int _, CancellationToken cancellationToken)
        {
            firstFactoryStarted.TrySetResult(true);
            await releaseFirstFactory.Task.WaitAsync(cancellationToken);
            return "one";
        }

        var first = cache.GetOrAddAsync(1, FirstFactory, cancellationToken: TestContext.Current.CancellationToken)
            .AsTask();
        await firstFactoryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await cache.GetOrAddAsync(
                2,
                (_, _) =>
                {
                    Interlocked.Increment(ref secondFactoryCalls);
                    return new ValueTask<string>("unexpected");
                },
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, secondFactoryCalls);

        logger.ThrowOnLog = false;
        releaseFirstFactory.TrySetResult(true);
        Assert.Equal("one", await first.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        var value = await cache.GetOrAddAsync(
            2,
            (_, _) =>
            {
                Interlocked.Increment(ref secondFactoryCalls);
                return new ValueTask<string>("two");
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("two", value);
        Assert.Equal(1, secondFactoryCalls);
    }

    private sealed class ThrowingLogger : ILogger
    {
        public bool ThrowOnLog { get; set; } = true;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (ThrowOnLog)
            {
                throw new InvalidOperationException("test logger failure");
            }
        }
    }
}
