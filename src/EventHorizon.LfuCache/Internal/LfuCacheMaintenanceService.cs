using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EventHorizon.LfuCache.Internal;

internal sealed class LfuCacheMaintenanceService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly LfuCacheCatalog _catalog;
    private readonly LfuCacheRegistry _registry;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LfuCacheMaintenanceService> _logger;

    public LfuCacheMaintenanceService(
        IServiceProvider serviceProvider,
        LfuCacheCatalog catalog,
        LfuCacheRegistry registry,
        TimeProvider timeProvider,
        ILogger<LfuCacheMaintenanceService>? logger = null)
    {
        _serviceProvider = serviceProvider;
        _catalog = catalog;
        _registry = registry;
        _timeProvider = timeProvider;
        _logger = logger ?? NullLogger<LfuCacheMaintenanceService>.Instance;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var registration in _catalog.GetRegistrations())
        {
            _serviceProvider.GetRequiredKeyedService(registration.ServiceType, registration.Keyspace);
        }

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var caches = _registry.All();
            if (caches.Length == 0)
            {
                await _registry.WaitForSignalAsync(stoppingToken).ConfigureAwait(false);
                continue;
            }

            var nowTicks = _timeProvider.GetTimestamp();
            var nextDueTicks = caches.Min(cache => cache.NextDueTicks);
            if (nextDueTicks > nowTicks)
            {
                var delay = _timeProvider.GetElapsedTime(nowTicks, nextDueTicks);
                await WaitForDueTimeOrSignalAsync(delay, stoppingToken).ConfigureAwait(false);
                continue;
            }

            foreach (var cache in caches)
            {
                if (cache.NextDueTicks > nowTicks)
                {
                    continue;
                }

                try
                {
                    cache.RunMaintenance(nowTicks);
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(
                        exception,
                        "LFU maintenance failed for keyspace {Keyspace}; the loop will continue",
                        cache.Keyspace);
                }
            }
        }
    }

    private async Task WaitForDueTimeOrSignalAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        using var iterationCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var signalTask = _registry.WaitForSignalAsync(iterationCancellation.Token);
        var delayTask = Task.Delay(delay, _timeProvider, iterationCancellation.Token);

        await Task.WhenAny(signalTask, delayTask).ConfigureAwait(false);
        await iterationCancellation.CancelAsync().ConfigureAwait(false);

        try
        {
            await Task.WhenAll(signalTask, delayTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // The unfinished side is canceled after either the due time or a signal wins.
        }
    }
}
