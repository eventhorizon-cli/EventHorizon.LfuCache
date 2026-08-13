using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace EventHorizon.LfuCache.Benchmarks;

[MemoryDiagnoser]
public class CapacityPressureBenchmarks
{
    private const int _capacity = 128;
    private const string _keyspace = "capacity-benchmark";

    private readonly ReferenceValue _value = new(42);
    private ILfuCache<int, ReferenceValue>? _cache;
    private ServiceProvider? _provider;

    [GlobalSetup]
    public void GlobalSetup()
    {
        var services = new ServiceCollection();
        services.AddLfuCache<int, ReferenceValue>(
            _keyspace,
            options =>
            {
                options.Capacity = _capacity;
                options.EvictionRatio = 0.25;
                options.OverflowRatio = 0;
                options.DefaultExpiry = null;
                options.MaintenanceInterval = TimeSpan.FromHours(1);
                options.DecayInterval = TimeSpan.FromHours(1);
            });

        _provider = services.BuildServiceProvider();
        _cache = _provider.GetRequiredKeyedService<ILfuCache<int, ReferenceValue>>(_keyspace);
    }

    [Benchmark]
    public int SetBatchWithCapacityPressure()
    {
        _cache!.Clear();
        for (var key = 0; key < _capacity; key++)
        {
            _cache.Set(key, _value, expiry: null);
        }

        var before = _cache.GetStats().EvictionBatches;
        _cache.Set(_capacity, _value, expiry: null);
        var after = _cache.GetStats();
        return (int)(after.EvictionBatches - before);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _provider?.Dispose();
    }
}
