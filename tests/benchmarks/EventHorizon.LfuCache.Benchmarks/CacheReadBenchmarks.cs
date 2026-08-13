using System.Collections.Concurrent;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace EventHorizon.LfuCache.Benchmarks;

[MemoryDiagnoser]
public class CacheReadBenchmarks
{
    private const string _keyspace = "read-benchmark";

    private readonly ReferenceValue _value = new(42);
    private readonly ConcurrentDictionary<int, ReferenceValue> _dictionary = new();
    private MemoryCache? _memoryCache;
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
        _memoryCache = new MemoryCache(new MemoryCacheOptions());
        _typedCache.Set(1, _value, expiry: null);
        _memoryCache.Set(1, _value);
        _dictionary[1] = _value;
    }

    [Benchmark]
    public int TypedCacheHit()
    {
        return _typedCache!.TryGet(1, out var value) ? value.Id : -1;
    }

    [Benchmark]
    public int DynamicFacadeHit()
    {
        return _dynamicCache!.TryGet<int, ReferenceValue>(1, out var value) ? value.Id : -1;
    }

    [Benchmark]
    public int MemoryCacheHit()
    {
        return _memoryCache!.TryGetValue(1, out ReferenceValue? value) ? value!.Id : -1;
    }

    [Benchmark(Baseline = true)]
    public int ConcurrentDictionaryHit()
    {
        return _dictionary.TryGetValue(1, out var value) ? value.Id : -1;
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _memoryCache?.Dispose();
        _provider?.Dispose();
    }
}
