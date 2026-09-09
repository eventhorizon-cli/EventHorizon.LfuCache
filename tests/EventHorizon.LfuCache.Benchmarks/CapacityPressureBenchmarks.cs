using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace EventHorizon.LfuCache.Benchmarks;

[MemoryDiagnoser]
public class CapacityPressureBenchmarks
{
    private const string _keyspace = "capacity-benchmark";
    private const int _operationsPerInvocation = 16;

    private readonly ReferenceValue _value = new(42);
    private ILfuCache<int, ReferenceValue>? _cache;
    private ServiceProvider? _provider;
    private int _nextKey;

    [Params(1_000, 100_000)]
    public int Capacity { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        var services = new ServiceCollection();
        services.AddLfuCache<int, ReferenceValue>(
            _keyspace,
            options =>
            {
                options.Capacity = Capacity;
                options.EvictionRatio = 0.25;
                options.OverflowRatio = 0;
                options.DefaultExpiry = null;
                options.MaintenanceInterval = TimeSpan.FromHours(1);
                options.DecayInterval = TimeSpan.FromHours(1);
            });

        _provider = services.BuildServiceProvider();
        _cache = _provider.GetRequiredKeyedService<ILfuCache<int, ReferenceValue>>(_keyspace);
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _cache!.Clear();
        for (var key = 0; key < Capacity; key++)
        {
            _cache.Set(key, _value, expiry: null);
        }

        _ = _cache.TryGet(0, out _);
        _nextKey = Capacity;

        // Prime the eviction path so subsequent measurements include reused candidate storage.
        _cache.Set(_nextKey++, _value, expiry: null);
        while (_cache.Count < Capacity)
        {
            _cache.Set(_nextKey++, _value, expiry: null);
        }
    }

    [Benchmark(OperationsPerInvoke = _operationsPerInvocation)]
    public int SetExistingKeyAfterPressure()
    {
        for (var operation = 0; operation < _operationsPerInvocation; operation++)
        {
            _cache!.Set(0, _value, expiry: null);
        }

        return _cache!.Count;
    }

    [Benchmark(OperationsPerInvoke = _operationsPerInvocation)]
    public int SetNewKeyUnderPressure()
    {
        for (var operation = 0; operation < _operationsPerInvocation; operation++)
        {
            _cache!.Set(_nextKey++, _value, expiry: null);
        }

        return _cache!.Count;
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _provider?.Dispose();
    }
}
