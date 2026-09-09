using System.Diagnostics.Metrics;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace EventHorizon.LfuCache.Benchmarks;

[MemoryDiagnoser]
[ThreadingDiagnoser]
public class ConcurrentReadBenchmarks
{
    private const int _keyCount = 4_096;
    private const int _operationsPerWorker = 16_384;
    private const string _keyspace = "concurrent-read-benchmark";

    private ILfuCache<int, ReferenceValue>? _cache;
    private MeterListener? _meterListener;
    private ServiceProvider? _provider;
    private long _measurementCount;
    private int _sink;

    [Params("different", "hot")]
    public string AccessPattern { get; set; } = "different";

    [Params(false, true)]
    public bool MeterListenerEnabled { get; set; }

    [Params(4)]
    public int WorkerCount { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        var services = new ServiceCollection();
        services.AddLfuCache<int, ReferenceValue>(
            _keyspace,
            options =>
            {
                options.Capacity = _keyCount;
                options.DefaultExpiry = null;
                options.MaintenanceInterval = TimeSpan.FromHours(1);
                options.DecayInterval = TimeSpan.FromHours(1);
            });

        _provider = services.BuildServiceProvider();
        _cache = _provider.GetRequiredKeyedService<ILfuCache<int, ReferenceValue>>(_keyspace);
        _measurementCount = 0;
        _sink = 0;
        for (var key = 0; key < _keyCount; key++)
        {
            _cache.Set(key, new ReferenceValue(key), expiry: null);
        }

        if (MeterListenerEnabled)
        {
            _meterListener = CreateMeterListener();
            _meterListener.Start();
        }
    }

    [Benchmark]
    public int ConcurrentTypedCacheHits()
    {
        var sink = 0;
        Parallel.For(
            0,
            WorkerCount,
            worker =>
            {
                var workerSink = 0;
                for (var operation = 0; operation < _operationsPerWorker; operation++)
                {
                    var key = AccessPattern == "hot"
                        ? 0
                        : (worker * _operationsPerWorker + operation) % _keyCount;
                    if (_cache!.TryGet(key, out var value))
                    {
                        workerSink += value.Id;
                    }
                }

                Interlocked.Add(ref sink, workerSink);
            });

        Volatile.Write(ref _sink, sink);
        return Volatile.Read(ref _sink) + (int)Volatile.Read(ref _measurementCount);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _meterListener?.Dispose();
        _provider?.Dispose();
    }

    private MeterListener CreateMeterListener()
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, currentListener) =>
            {
                if (StringComparer.Ordinal.Equals(instrument.Meter.Name, "EventHorizon.LfuCache"))
                {
                    currentListener.EnableMeasurementEvents(instrument);
                }
            },
        };

        listener.SetMeasurementEventCallback<long>(
            (_, _, _, _) =>
            {
                // Keep the callback work small while still exercising the listener path.
                Interlocked.Increment(ref _measurementCount);
            });
        listener.SetMeasurementEventCallback<int>((_, _, _, _) => { });
        listener.SetMeasurementEventCallback<double>((_, _, _, _) => { });
        return listener;
    }
}
