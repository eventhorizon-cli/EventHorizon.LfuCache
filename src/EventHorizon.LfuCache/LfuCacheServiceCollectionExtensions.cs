using EventHorizon.LfuCache.Internal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EventHorizon.LfuCache;

/// <summary>Provides dependency-injection registration for LFU caches.</summary>
public static class LfuCacheServiceCollectionExtensions
{
    /// <summary>Registers an LFU cache in the <c>default</c> keyspace.</summary>
    public static IServiceCollection AddLfuCache<TKey, TValue>(this IServiceCollection services)
        where TKey : notnull
    {
        return AddCore<TKey, TValue>(services, null);
    }

    /// <summary>Registers an LFU cache in the specified keyspace.</summary>
    public static IServiceCollection AddLfuCache<TKey, TValue>(
        this IServiceCollection services,
        string? keyspace)
        where TKey : notnull
    {
        return AddCore<TKey, TValue>(services, keyspace);
    }

    /// <summary>Registers and configures an LFU cache in the specified keyspace.</summary>
    public static IServiceCollection AddLfuCache<TKey, TValue>(
        this IServiceCollection services,
        string? keyspace,
        Action<LfuCacheOptions> configureOptions)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(configureOptions);

        var normalized = KeyspaceNames.Normalize(keyspace);
        AddCore<TKey, TValue>(services, keyspace);
        services.Configure(normalized, configureOptions);
        return services;
    }

    /// <summary>Registers an LFU cache and binds its options from configuration.</summary>
    public static IServiceCollection AddLfuCache<TKey, TValue>(
        this IServiceCollection services,
        string? keyspace,
        IConfiguration configuration)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var normalized = KeyspaceNames.Normalize(keyspace);
        AddCore<TKey, TValue>(services, keyspace);
        services.Configure<LfuCacheOptions>(normalized, configuration);
        return services;
    }

    private static IServiceCollection AddCore<TKey, TValue>(IServiceCollection services, string? keyspace)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(services);

        var normalized = KeyspaceNames.Normalize(keyspace);
        var catalog = GetOrAddCatalog(services);
        catalog.Add(normalized, typeof(TKey), typeof(TValue), typeof(ILfuCache<TKey, TValue>));

        services.TryAddSingleton<LfuCacheRegistry>();
        services.TryAddSingleton<LfuCacheMetrics>();
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddOptions<LfuCacheOptions>(normalized);

        if (!services.Any(descriptor => descriptor.ServiceType == typeof(LfuCacheMaintenanceService)))
        {
            services.AddSingleton<LfuCacheMaintenanceService>();
            services.AddSingleton<IHostedService>(
                serviceProvider => serviceProvider.GetRequiredService<LfuCacheMaintenanceService>());
        }

        services.TryAddKeyedSingleton<ILfuCache<TKey, TValue>>(
            normalized,
            (serviceProvider, _) => CreateCache<TKey, TValue>(serviceProvider, normalized));
        services.TryAddKeyedSingleton<ILfuCache>(
            normalized,
            (serviceProvider, _) => CreateDynamicCache(serviceProvider, normalized));

        RegisterNormalizedFallback<TKey, TValue>(services);

        if (normalized == KeyspaceNames.Default)
        {
            services.TryAddSingleton<ILfuCache<TKey, TValue>>(
                serviceProvider => serviceProvider.GetRequiredKeyedService<ILfuCache<TKey, TValue>>(normalized));
            services.TryAddSingleton<ILfuCache>(
                serviceProvider => serviceProvider.GetRequiredKeyedService<ILfuCache>(normalized));
        }

        return services;
    }

    private static LfuCache<TKey, TValue> CreateCache<TKey, TValue>(
        IServiceProvider serviceProvider,
        string keyspace)
        where TKey : notnull
    {
        var loggerFactory = serviceProvider.GetService<ILoggerFactory>();
        var cache = new LfuCache<TKey, TValue>(
            keyspace,
            serviceProvider.GetRequiredService<IOptionsMonitor<LfuCacheOptions>>(),
            serviceProvider.GetRequiredService<TimeProvider>(),
            serviceProvider.GetRequiredService<LfuCacheRegistry>(),
            serviceProvider.GetRequiredService<LfuCacheMetrics>(),
            loggerFactory?.CreateLogger<LfuCache<TKey, TValue>>());
        serviceProvider.GetRequiredService<LfuCacheRegistry>().Register(keyspace, cache);
        return cache;
    }

    private static DynamicLfuCache CreateDynamicCache(IServiceProvider serviceProvider, string keyspace)
    {
        var catalog = serviceProvider.GetRequiredService<LfuCacheCatalog>();
        if (!catalog.TryGetRegistration(keyspace, out var registration))
        {
            throw new InvalidOperationException($"LFU cache keyspace '{keyspace}' is not registered.");
        }

        serviceProvider.GetRequiredKeyedService(registration.ServiceType, keyspace);
        return new DynamicLfuCache(keyspace, serviceProvider.GetRequiredService<LfuCacheRegistry>());
    }

    private static void RegisterNormalizedFallback<TKey, TValue>(IServiceCollection services)
        where TKey : notnull
    {
        services.TryAddKeyedTransient<ILfuCache<TKey, TValue>>(
            KeyedService.AnyKey,
            (serviceProvider, requestedKey) =>
            {
                var normalized = GetRegisteredKeyspace<TKey, TValue>(serviceProvider, requestedKey);
                return serviceProvider.GetRequiredKeyedService<ILfuCache<TKey, TValue>>(normalized);
            });
        services.TryAddKeyedTransient<ILfuCache>(
            KeyedService.AnyKey,
            (serviceProvider, requestedKey) =>
            {
                var normalized = GetRegisteredKeyspace(serviceProvider, requestedKey);
                return serviceProvider.GetRequiredKeyedService<ILfuCache>(normalized);
            });
    }

    private static string GetRegisteredKeyspace<TKey, TValue>(
        IServiceProvider serviceProvider,
        object? requestedKey)
        where TKey : notnull
    {
        var normalized = GetRegisteredKeyspace(serviceProvider, requestedKey);
        var registration = serviceProvider.GetRequiredService<LfuCacheCatalog>();
        registration.TryGetRegistration(normalized, out var registered);

        if (registered.KeyType != typeof(TKey) || registered.ValueType != typeof(TValue))
        {
            throw new InvalidOperationException(
                $"LFU cache keyspace '{normalized}' is registered for " +
                $"<{registered.KeyType.Name}, {registered.ValueType.Name}>, not " +
                $"<{typeof(TKey).Name}, {typeof(TValue).Name}>.");
        }

        return normalized;
    }

    private static string GetRegisteredKeyspace(IServiceProvider serviceProvider, object? requestedKey)
    {
        if (requestedKey is not string keyspace)
        {
            throw new InvalidOperationException("LFU cache service keys must be strings.");
        }

        var normalized = KeyspaceNames.Normalize(keyspace);
        var catalog = serviceProvider.GetRequiredService<LfuCacheCatalog>();
        if (!catalog.TryGetRegistration(normalized, out _))
        {
            throw new InvalidOperationException($"LFU cache keyspace '{normalized}' is not registered.");
        }

        return normalized;
    }

    private static LfuCacheCatalog GetOrAddCatalog(IServiceCollection services)
    {
        var descriptor = services.FirstOrDefault(item => item.ServiceType == typeof(LfuCacheCatalog));
        if (descriptor?.ImplementationInstance is LfuCacheCatalog catalog)
        {
            return catalog;
        }

        if (descriptor is not null)
        {
            throw new InvalidOperationException(
                $"{nameof(LfuCacheCatalog)} must be registered by {nameof(AddLfuCache)}.");
        }

        catalog = new LfuCacheCatalog();
        services.AddSingleton(catalog);
        return catalog;
    }
}
