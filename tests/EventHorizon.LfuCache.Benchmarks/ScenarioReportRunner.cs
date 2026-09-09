using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace EventHorizon.LfuCache.Benchmarks;

internal static class ScenarioReportRunner
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private const int _frequencyCapacity = 10_000;
    private const int _frequencyInitialHits = 7;
    private const int _hotKeyCount = 1_000;
    private const int _coldKeyCount = 1_000;
    private const int _hotKeyHits = 100;
    private const int _inflightCapacity = 1;
    private const int _inflightFactoryCount = 4;

    public static void Write(string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var report = new
        {
            formatVersion = 1,
            frequencyDecay = RunFrequencyDecayScenario(),
            protectedHotKeys = RunProtectedHotKeysScenario(),
            inflightFactories = RunInflightFactoryScenario(),
        };
        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? ".");
        File.WriteAllText(fullPath, JsonSerializer.Serialize(report, _jsonOptions));
    }

    private static object RunFrequencyDecayScenario()
    {
        var clock = new ScenarioTimeProvider();
        using var provider = CreateProvider(
            clock,
            "scenario-frequency",
            options =>
            {
                options.Capacity = _frequencyCapacity;
                options.DefaultExpiry = null;
                options.MaintenanceInterval = TimeSpan.FromSeconds(10);
                options.DecayInterval = TimeSpan.FromMinutes(1);
                options.OverflowRatio = 0;
            });
        var cache = provider.GetRequiredKeyedService<ILfuCache<int, int>>("scenario-frequency");
        for (var key = 0; key < _frequencyCapacity; key++)
        {
            cache.Set(key, key, expiry: null);
            for (var hit = 0; hit < _frequencyInitialHits; hit++)
            {
                _ = cache.TryGet(key, out _);
            }
        }

        var implementation = ResolveImplementation(provider, "scenario-frequency");
        var maintenance = CacheReflectionProbe.CreateMaintenanceDelegate(implementation);
        var snapshots = new List<object>();
        for (var elapsedSeconds = 60; elapsedSeconds <= 360; elapsedSeconds += 60)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            maintenance(clock.GetTimestamp());
            if (elapsedSeconds is 60 or 360)
            {
                var observation = CacheReflectionProbe.ReadFrequencies(implementation, clock.GetTimestamp());
                snapshots.Add(
                    new
                    {
                        elapsedSeconds,
                        observation.Supported,
                        observation.Error,
                        entryCount = observation.EntryCount,
                        countsByFrequency = ToStringKeyedDictionary(observation.CountsByFrequency),
                    });
            }
        }

        return new
        {
            capacity = _frequencyCapacity,
            initialFrequency = _frequencyInitialHits + 1,
            decayIntervalSeconds = 60,
            snapshots,
        };
    }

    private static object RunProtectedHotKeysScenario()
    {
        var clock = new ScenarioTimeProvider();
        using var provider = CreateProvider(
            clock,
            "scenario-protection",
            options =>
            {
                options.Capacity = _hotKeyCount;
                options.DefaultExpiry = null;
                options.EvictionRatio = 0.1;
                options.OverflowRatio = 0;
                options.MaintenanceInterval = TimeSpan.FromHours(1);
                options.DecayInterval = TimeSpan.FromHours(1);
            });
        var cache = provider.GetRequiredKeyedService<ILfuCache<int, int>>("scenario-protection");
        for (var key = 0; key < _hotKeyCount; key++)
        {
            cache.Set(key, key, expiry: null);
            for (var hit = 0; hit < _hotKeyHits; hit++)
            {
                _ = cache.TryGet(key, out _);
            }
        }

        clock.Advance(TimeSpan.FromSeconds(2));
        for (var key = _hotKeyCount; key < _hotKeyCount + _coldKeyCount; key++)
        {
            cache.Set(key, key, expiry: null);
        }

        var retainedHotKeys = 0;
        for (var key = 0; key < _hotKeyCount; key++)
        {
            if (cache.TryGet(key, out _))
            {
                retainedHotKeys++;
            }
        }

        var stats = cache.GetStats();
        return new
        {
            capacity = _hotKeyCount,
            hotKeyCount = _hotKeyCount,
            coldKeyCount = _coldKeyCount,
            hitsPerHotKey = _hotKeyHits,
            elapsedSecondsBeforeColdBurst = 2,
            retainedHotKeys,
            hotKeyRetentionRate = retainedHotKeys / (double)_hotKeyCount,
            storedEntryCount = stats.Count,
            evictionBatches = stats.EvictionBatches,
            evictions = stats.Evictions,
        };
    }

    private static object RunInflightFactoryScenario()
    {
        var clock = new ScenarioTimeProvider();
        using var provider = CreateProvider(
            clock,
            "scenario-inflight",
            options =>
            {
                options.Capacity = _inflightCapacity;
                options.DefaultExpiry = null;
                options.OverflowRatio = 0;
                options.MaintenanceInterval = TimeSpan.FromHours(1);
                options.DecayInterval = TimeSpan.FromHours(1);
            });
        var cache = provider.GetRequiredKeyedService<ILfuCache<int, int>>("scenario-inflight");
        var blockers = Enumerable.Range(0, _inflightFactoryCount)
            .Select(_ => new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        var pending = new List<Task<int>>(_inflightFactoryCount);
        var started = 0;
        var synchronousInvalidOperationCount = 0;

        for (var index = 0; index < _inflightFactoryCount; index++)
        {
            var factoryIndex = index;
            try
            {
                pending.Add(
                    cache.GetOrAddAsync(
                            factoryIndex,
                            async (_, cancellationToken) =>
                            {
                                Interlocked.Increment(ref started);
                                await blockers[factoryIndex].Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                                return factoryIndex;
                            },
                            expiry: TimeSpan.Zero)
                        .AsTask());
            }
            catch (InvalidOperationException)
            {
                synchronousInvalidOperationCount++;
            }
        }

        var countWhileBlocked = cache.GetStats().Count;
        for (var index = 0; index < blockers.Length; index++)
        {
            blockers[index].TrySetResult(index);
        }

        var invalidOperationCount = synchronousInvalidOperationCount;
        var unexpectedFailureCount = 0;
        var completedTaskCount = 0;
        foreach (var task in pending)
        {
            try
            {
                _ = task.GetAwaiter().GetResult();
                completedTaskCount++;
            }
            catch (InvalidOperationException)
            {
                invalidOperationCount++;
            }
            catch
            {
                unexpectedFailureCount++;
            }
        }

        var maxInflightProperty = typeof(LfuCacheOptions).GetProperty(
            "MaxInflight",
            BindingFlags.Instance | BindingFlags.Public);
        return new
        {
            capacity = _inflightCapacity,
            requestedFactoryCount = _inflightFactoryCount,
            factoriesStarted = Volatile.Read(ref started),
            countWhileBlocked,
            completedTaskCount,
            rejectedInvalidOperationCount = invalidOperationCount,
            unexpectedFailureCount,
            maxInflightOptionPresent = maxInflightProperty is not null,
            finalEntryCount = cache.GetStats().Count,
        };
    }

    private static ServiceProvider CreateProvider(
        ScenarioTimeProvider clock,
        string keyspace,
        Action<LfuCacheOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddLfuCache<int, int>(keyspace, configure);
        return services.BuildServiceProvider();
    }

    private static object ResolveImplementation(ServiceProvider provider, string keyspace)
    {
        var openType = typeof(LfuCacheOptions).Assembly.GetType(
            "EventHorizon.LfuCache.Storage.LfuCache`2",
            throwOnError: true)!;
        var implementationType = openType.MakeGenericType(typeof(int), typeof(int));
        return provider.GetRequiredKeyedService(implementationType, keyspace);
    }

    private static Dictionary<string, int> ToStringKeyedDictionary(
        IReadOnlyDictionary<long, int> source)
    {
        return source.ToDictionary(
            pair => pair.Key.ToString(CultureInfo.InvariantCulture),
            pair => pair.Value,
            StringComparer.Ordinal);
    }
}
