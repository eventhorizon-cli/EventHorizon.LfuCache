using System.Collections.Concurrent;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace EventHorizon.LfuCache.Benchmarks;

[MemoryDiagnoser]
public class CacheMutationBenchmarks
{
    private const string _keyspace = "mutation-benchmark";

    private readonly ReferenceValue _value = new(42);
    private readonly ConcurrentDictionary<int, ReferenceValue> _dictionary = new();
    private ILfuCache<int, ReferenceValue>? _typedCache;
    private ILfuCache? _dynamicCache;
    private ServiceProvider? _provider;

    [GlobalSetup]
    public void GlobalSetup()
    {
        var services = new ServiceCollection();
        services.AddLfuCache<int, ReferenceValue>(
            _keyspace,
            options => options.DefaultExpiry = null);

        _provider = services.BuildServiceProvider();
        _typedCache = _provider.GetRequiredKeyedService<ILfuCache<int, ReferenceValue>>(_keyspace);
        _dynamicCache = _provider.GetRequiredKeyedService<ILfuCache>(_keyspace);
        _typedCache.Set(1, _value, expiry: null);
        _dictionary[1] = _value;
    }

    [Benchmark]
    public int TypedCacheSet()
    {
        _typedCache!.Set(1, _value, expiry: null);
        return _value.Id;
    }

    [Benchmark]
    public int DynamicFacadeSet()
    {
        _dynamicCache!.Set<int, ReferenceValue>(1, _value, expiry: null);
        return _value.Id;
    }

    [Benchmark(Baseline = true)]
    public int ConcurrentDictionaryUpdate()
    {
        _dictionary[1] = _value;
        return _value.Id;
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _provider?.Dispose();
    }
}
