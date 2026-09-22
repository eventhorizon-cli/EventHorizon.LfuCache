using EventHorizon.LfuCache.Configuration;
using EventHorizon.LfuCache.Maintenance;
using EventHorizon.LfuCache.Metrics;
using EventHorizon.LfuCache.Registration;
using EventHorizon.LfuCache.Storage;
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
    /// <typeparam name="TKey">The cache key type.</typeparam>
    /// <typeparam name="TValue">The cache value type.</typeparam>
    /// <param name="services">The service collection to add the cache to.</param>
    /// <returns>The same service collection so that additional calls can be chained.</returns>
    public static IServiceCollection AddLfuCache<TKey, TValue>(this IServiceCollection services)
        where TKey : notnull
    {
        return AddCore<TKey, TValue>(services, KeyspaceNames.Default);
    }

    /// <summary>Registers an LFU cache in the specified keyspace.</summary>
    /// <typeparam name="TKey">The cache key type.</typeparam>
    /// <typeparam name="TValue">The cache value type.</typeparam>
    /// <param name="services">The service collection to add the cache to.</param>
    /// <param name="keyspace">The keyspace name. Whitespace and casing are normalized.</param>
    /// <returns>The same service collection so that additional calls can be chained.</returns>
    public static IServiceCollection AddLfuCache<TKey, TValue>(
        this IServiceCollection services,
        string? keyspace)
        where TKey : notnull
    {
        return AddCore<TKey, TValue>(services, KeyspaceNames.Normalize(keyspace));
    }

    /// <summary>Registers and configures an LFU cache in the specified keyspace.</summary>
    /// <typeparam name="TKey">The cache key type.</typeparam>
    /// <typeparam name="TValue">The cache value type.</typeparam>
    /// <param name="services">The service collection to add the cache to.</param>
    /// <param name="keyspace">The keyspace name. Whitespace and casing are normalized.</param>
    /// <param name="configureOptions">The delegate used to configure this keyspace.</param>
    /// <returns>The same service collection so that additional calls can be chained.</returns>
    public static IServiceCollection AddLfuCache<TKey, TValue>(
        this IServiceCollection services,
        string? keyspace,
        Action<LfuCacheOptions> configureOptions)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(configureOptions);

        var normalized = KeyspaceNames.Normalize(keyspace);
        AddCore<TKey, TValue>(services, normalized);
        services.Configure(normalized, configureOptions);
        return services;
    }

    /// <summary>Registers an LFU cache and binds its options from configuration.</summary>
    /// <typeparam name="TKey">The cache key type.</typeparam>
    /// <typeparam name="TValue">The cache value type.</typeparam>
    /// <param name="services">The service collection to add the cache to.</param>
    /// <param name="keyspace">The keyspace name. Whitespace and casing are normalized.</param>
    /// <param name="configuration">The configuration section to bind to this keyspace.</param>
    /// <returns>The same service collection so that additional calls can be chained.</returns>
    public static IServiceCollection AddLfuCache<TKey, TValue>(
        this IServiceCollection services,
        string? keyspace,
        IConfiguration configuration)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var normalized = KeyspaceNames.Normalize(keyspace);
        AddCore<TKey, TValue>(services, normalized);
        services.Configure<LfuCacheOptions>(normalized, configuration);
        return services;
    }

    private static IServiceCollection AddCore<TKey, TValue>(IServiceCollection services, string normalized)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(services);

        var catalog = GetCatalog(services);
        if (catalog is null || !catalog.TryGetRegistration(normalized, out _))
        {
            EnsureServiceSlotsAvailable<TKey, TValue>(services, catalog, normalized);
        }

        catalog ??= AddCatalog(services);
        var registrationAdded = catalog.Add(
            normalized,
            typeof(TKey),
            typeof(TValue),
            typeof(LfuCache<TKey, TValue>));

        services.TryAddSingleton<LfuCacheRegistry>();
        services.TryAddSingleton<LfuCacheMetrics>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<LfuCacheOptions>, LfuCacheOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);
        if (registrationAdded)
        {
            services.AddOptions<LfuCacheOptions>(normalized).ValidateOnStart();
        }

        if (services.All(descriptor => descriptor.ServiceType != typeof(LfuCacheMaintenanceService)))
        {
            services.AddSingleton<LfuCacheMaintenanceService>();
            services.AddSingleton<IHostedService>(
                serviceProvider => serviceProvider.GetRequiredService<LfuCacheMaintenanceService>());
        }

        if (registrationAdded)
        {
            services.AddKeyedSingleton<LfuCache<TKey, TValue>>(
                normalized,
                (serviceProvider, _) => CreateCache<TKey, TValue>(serviceProvider, normalized));
            services.TryAddKeyedSingleton<ILfuCache<TKey, TValue>>(
                normalized,
                (serviceProvider, _) => new TypedLfuCacheFacade<TKey, TValue>(
                    serviceProvider.GetRequiredKeyedService<LfuCache<TKey, TValue>>(normalized)));
            services.TryAddKeyedSingleton<ILfuCache>(
                normalized,
                (serviceProvider, _) => CreateDynamicCache(serviceProvider, normalized));
        }

        RegisterNormalizedFallback<TKey, TValue>(services);

        if (registrationAdded && normalized == KeyspaceNames.Default)
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
        var registry = serviceProvider.GetRequiredService<LfuCacheRegistry>();
        var cache = new LfuCache<TKey, TValue>(
            keyspace,
            serviceProvider.GetRequiredService<IOptionsMonitor<LfuCacheOptions>>(),
            serviceProvider.GetRequiredService<TimeProvider>(),
            registry,
            serviceProvider.GetRequiredService<LfuCacheMetrics>(),
            loggerFactory?.CreateLogger<LfuCache<TKey, TValue>>());
        registry.Register(keyspace, cache);
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
                var registration = GetRegistration<TKey, TValue>(serviceProvider, requestedKey);
                var cache = serviceProvider.GetRequiredKeyedService<ILfuCache<TKey, TValue>>(registration.Keyspace);
                return cache is IDisposable or IAsyncDisposable
                    ? new TypedLfuCacheFacade<TKey, TValue>(cache)
                    : cache;
            });
        services.TryAddKeyedTransient<ILfuCache>(
            KeyedService.AnyKey,
            (serviceProvider, requestedKey) =>
            {
                var registration = GetRegistration(serviceProvider, requestedKey);
                var cache = serviceProvider.GetRequiredKeyedService<ILfuCache>(registration.Keyspace);
                return cache is IDisposable or IAsyncDisposable
                    ? new DynamicLfuCacheFacade(cache)
                    : cache;
            });
    }

    private static void EnsureServiceSlotsAvailable<TKey, TValue>(
        IServiceCollection services,
        LfuCacheCatalog? catalog,
        string keyspace)
        where TKey : notnull
    {
        var registrations = catalog?.GetRegistrations() ?? [];
        var typedFallbackRegistered = registrations.Any(
            registration => registration.KeyType == typeof(TKey) && registration.ValueType == typeof(TValue));
        EnsureKeyedServiceSlotAvailable(
            services,
            typeof(ILfuCache<TKey, TValue>),
            keyspace,
            rejectAnyKey: !typedFallbackRegistered);
        EnsureKeyedServiceSlotAvailable(
            services,
            typeof(ILfuCache),
            keyspace,
            rejectAnyKey: registrations.Length == 0);
    }

    private static void EnsureKeyedServiceSlotAvailable(
        IServiceCollection services,
        Type serviceType,
        string keyspace,
        bool rejectAnyKey)
    {
        if (services.Any(descriptor => descriptor.IsKeyedService
            && descriptor.ServiceType == serviceType
            && ((rejectAnyKey && Equals(descriptor.ServiceKey, KeyedService.AnyKey))
                || (descriptor.ServiceKey is string registeredKeyspace
                    && StringComparer.Ordinal.Equals(KeyspaceNames.Normalize(registeredKeyspace), keyspace)))))
        {
            throw new InvalidOperationException(
                $"Cannot register LFU cache keyspace '{keyspace}' because keyed service " +
                $"'{serviceType}' is already registered.");
        }
    }

    private static LfuCacheRegistration GetRegistration<TKey, TValue>(
        IServiceProvider serviceProvider,
        object? requestedKey)
        where TKey : notnull
    {
        var registration = GetRegistration(serviceProvider, requestedKey);

        if (registration.KeyType != typeof(TKey) || registration.ValueType != typeof(TValue))
        {
            throw new InvalidOperationException(
                $"LFU cache keyspace '{registration.Keyspace}' is registered for " +
                $"<{registration.KeyType.Name}, {registration.ValueType.Name}>, not " +
                $"<{typeof(TKey).Name}, {typeof(TValue).Name}>.");
        }

        return registration;
    }

    private static LfuCacheRegistration GetRegistration(IServiceProvider serviceProvider, object? requestedKey)
    {
        if (requestedKey is not string keyspace)
        {
            throw new InvalidOperationException("LFU cache service keys must be strings.");
        }

        var normalized = KeyspaceNames.Normalize(keyspace);
        var catalog = serviceProvider.GetRequiredService<LfuCacheCatalog>();
        if (!catalog.TryGetRegistration(normalized, out var registration))
        {
            throw new InvalidOperationException($"LFU cache keyspace '{normalized}' is not registered.");
        }

        return registration;
    }

    private static LfuCacheCatalog? GetCatalog(IServiceCollection services)
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

        return null;
    }

    private static LfuCacheCatalog AddCatalog(IServiceCollection services)
    {
        var catalog = new LfuCacheCatalog();
        services.AddSingleton(catalog);
        return catalog;
    }
}
