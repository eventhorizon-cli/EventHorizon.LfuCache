using EventHorizon.LfuCache.Registration;
using EventHorizon.LfuCache.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace EventHorizon.LfuCache.Tests;

public sealed class RegistrationTests
{
    [Fact]
    public void AddLfuCache_DefaultAndKeyedResolution_ReturnsSameTypedAndDynamicInstances()
    {
        var services = new ServiceCollection();
        services.AddLfuCache<int, string>();
        using var provider = services.BuildServiceProvider();

        var typed = provider.GetRequiredService<ILfuCache<int, string>>();
        var keyedTyped = provider.GetRequiredKeyedService<ILfuCache<int, string>>("default");
        var dynamic = provider.GetRequiredService<ILfuCache>();
        var keyedDynamic = provider.GetRequiredKeyedService<ILfuCache>("default");

        Assert.Same(typed, keyedTyped);
        Assert.Same(dynamic, keyedDynamic);
        Assert.Equal("default", typed.Keyspace);
        Assert.Equal("default", dynamic.Keyspace);
    }

    [Fact]
    public void AddLfuCache_SameKeyspaceAndDifferentTypePair_ThrowsRegistrationException()
    {
        var services = new ServiceCollection();
        services.AddLfuCache<int, string>(" Shared ");
        services.AddLfuCache<int, string>("shared");

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddLfuCache<long, string>("SHARED"));

        Assert.Contains("already registered", exception.Message);
        Assert.Contains("different keyspace", exception.Message);
    }

    [Fact]
    public void AddLfuCache_NamedKeyspace_NormalizesCacheAndSupportsTrimmedAlias()
    {
        var services = new ServiceCollection();
        services.AddLfuCache<int, string>("  Orders ");
        using var provider = services.BuildServiceProvider();

        var normalized = provider.GetRequiredKeyedService<ILfuCache<int, string>>("orders");
        var alias = provider.GetRequiredKeyedService<ILfuCache<int, string>>("Orders");

        Assert.Same(normalized, alias);
        Assert.Equal("orders", normalized.Keyspace);
    }

    [Fact]
    public void AddLfuCache_MultipleKeyspaces_NormalizesEachRequestedServiceKey()
    {
        var services = new ServiceCollection();
        services.AddLfuCache<int, string>("Orders");
        services.AddLfuCache<int, string>("Products");
        using var provider = services.BuildServiceProvider();

        var orders = provider.GetRequiredKeyedService<ILfuCache<int, string>>("  ORDERS  ");
        var normalizedOrders = provider.GetRequiredKeyedService<ILfuCache<int, string>>("orders");
        var products = provider.GetRequiredKeyedService<ILfuCache<int, string>>("pRoDuCtS");

        Assert.Same(normalizedOrders, orders);
        Assert.NotSame(orders, products);
        Assert.Equal("orders", orders.Keyspace);
        Assert.Equal("products", products.Keyspace);
    }

    [Fact]
    public void AddLfuCache_UnknownKeyspace_ThrowsDescriptiveException()
    {
        var services = new ServiceCollection();
        services.AddLfuCache<int, string>("orders");
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredKeyedService<ILfuCache<int, string>>("missing"));

        Assert.Contains("missing", exception.Message);
        Assert.Contains("not registered", exception.Message);
    }

    [Fact]
    public void AddLfuCache_ExistingKeyedCacheService_ThrowsInsteadOfPublishingMismatchedCatalogEntry()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ILfuCache<int, string>>(
            "reserved",
            static (_, _) => throw new InvalidOperationException("custom service should not be resolved"));

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddLfuCache<int, string>("reserved"));

        Assert.Contains("reserved", exception.Message);
        Assert.Contains("already registered", exception.Message);
    }

    [Fact]
    public void AddLfuCache_PublicTypedOverride_LeavesInternalDynamicStoreUsable()
    {
        var services = new ServiceCollection();
        services.AddLfuCache<int, string>("shared");
        var custom = new StubTypedCache<int, string>("custom");
        services.AddKeyedSingleton<ILfuCache<int, string>>("shared", custom);
        using var provider = services.BuildServiceProvider();

        var publicTyped = provider.GetRequiredKeyedService<ILfuCache<int, string>>("shared");
        var dynamic = provider.GetRequiredKeyedService<ILfuCache>("shared");

        Assert.Same(custom, publicTyped);
        Assert.Equal("custom", publicTyped.Keyspace);

        dynamic.Set<int, string>(1, "value");

        Assert.True(dynamic.TryGet<int, string>(1, out var value));
        Assert.Equal("value", value);
        Assert.Equal("shared", dynamic.Keyspace);
    }

    [Fact]
    public void AddLfuCache_AliasResolvedFromChildScope_DoesNotDisposeInternalStore()
    {
        var services = new ServiceCollection();
        services.AddLfuCache<int, string>("orders");
        using var provider = services.BuildServiceProvider();

        var internalStore = provider.GetRequiredKeyedService<LfuCache<int, string>>("orders");
        using (var scope = provider.CreateScope())
        {
            var alias = scope.ServiceProvider.GetRequiredKeyedService<ILfuCache<int, string>>(" ORDERS ");
            alias.Set(1, "value");
        }

        Assert.Same(internalStore, provider.GetRequiredKeyedService<LfuCache<int, string>>("orders"));
        var disposed = (int)typeof(LfuCache<int, string>)
            .GetField("_disposed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(internalStore)!;
        Assert.Equal(0, disposed);
        Assert.True(internalStore.TryGet(1, out var value));
        Assert.Equal("value", value);
    }

    [Fact]
    public void AddLfuCache_AliasResolvedFromChildScope_DoesNotDisposePublicOverrides()
    {
        var services = new ServiceCollection();
        services.AddLfuCache<int, string>("orders");
        var typedOverride = new StubTypedCache<int, string>("typed");
        var dynamicOverride = new DisposableDynamicCache("dynamic");
        services.AddKeyedSingleton<ILfuCache<int, string>>("orders", typedOverride);
        services.AddKeyedSingleton<ILfuCache>("orders", dynamicOverride);
        using var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            _ = scope.ServiceProvider.GetRequiredKeyedService<ILfuCache<int, string>>(" ORDERS ");
            _ = scope.ServiceProvider.GetRequiredKeyedService<ILfuCache>(" ORDERS ");
        }

        Assert.False(typedOverride.IsDisposed);
        Assert.False(dynamicOverride.IsDisposed);
    }

    [Fact]
    public void AddLfuCache_NormalizedKeyedServiceAlias_ConflictsBeforeRegistration()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ILfuCache<int, string>>(
            "  ORDERS  ",
            new StubTypedCache<int, string>("custom"));

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddLfuCache<int, string>("orders"));

        Assert.Contains("orders", exception.Message);
        Assert.Contains("already registered", exception.Message);
    }

    [Fact]
    public void AddLfuCache_PreexistingAnyKeyFallback_ConflictsOnFirstRegistration()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ILfuCache<int, string>>(
            KeyedService.AnyKey,
            new StubTypedCache<int, string>("fallback"));

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddLfuCache<int, string>("orders"));

        Assert.Contains("orders", exception.Message);
        Assert.Contains("already registered", exception.Message);
    }

    [Fact]
    public void AddLfuCache_DefaultRegistration_PreservesUnkeyedOverrideAndPublishesKeyedCache()
    {
        var services = new ServiceCollection();
        var custom = new StubTypedCache<int, string>("custom");
        services.AddSingleton<ILfuCache<int, string>>(custom);
        services.AddLfuCache<int, string>();
        using var provider = services.BuildServiceProvider();

        var unkeyed = provider.GetRequiredService<ILfuCache<int, string>>();
        var keyed = provider.GetRequiredKeyedService<ILfuCache<int, string>>("default");

        Assert.Same(custom, unkeyed);
        Assert.NotSame(custom, keyed);

        keyed.Set(1, "value");
        Assert.True(keyed.TryGet(1, out var value));
        Assert.Equal("value", value);
    }

    [Fact]
    public void AddLfuCache_InitialConflict_DoesNotLeaveCatalogDescriptor()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ILfuCache<int, string>>(
            "orders",
            new StubTypedCache<int, string>("custom"));

        Assert.Throws<InvalidOperationException>(
            () => services.AddLfuCache<int, string>("orders"));

        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType == typeof(LfuCacheCatalog));
    }

    [Fact]
    public void AddLfuCache_DuplicateNormalizedRegistration_DoesNotAddDescriptors()
    {
        var services = new ServiceCollection();
        services.AddLfuCache<int, string>(" Orders ");
        var descriptorCount = services.Count;

        services.AddLfuCache<int, string>("orders");

        Assert.Equal(descriptorCount, services.Count);
        Assert.Equal(1, services.Count(descriptor => descriptor.ServiceType == typeof(LfuCacheCatalog)));
    }

    private sealed class StubTypedCache<TKey, TValue>(string keyspace) : ILfuCache<TKey, TValue>, IDisposable
        where TKey : notnull
    {
        public string Keyspace { get; } = keyspace;

        public int Count => 0;

        public bool IsDisposed { get; private set; }

        public bool TryGet(TKey key, out TValue value)
        {
            value = default!;
            return false;
        }

        public void Set(TKey key, TValue value, TimeSpan? expiry = null)
        {
            throw new NotSupportedException();
        }

        public TValue GetOrAdd(TKey key, Func<TKey, TValue> factory, TimeSpan? expiry = null)
        {
            throw new NotSupportedException();
        }

        public ValueTask<TValue> GetOrAddAsync(
            TKey key,
            Func<TKey, CancellationToken, ValueTask<TValue>> factory,
            TimeSpan? expiry = null,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromException<TValue>(new NotSupportedException());
        }

        public bool Remove(TKey key)
        {
            return false;
        }

        public void Clear()
        {
        }

        public LfuCacheStats GetStats()
        {
            return new LfuCacheStats(0, 0, 0, 0, 0, 0, 0);
        }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }

    private sealed class DisposableDynamicCache(string keyspace) : ILfuCache, IDisposable
    {
        public string Keyspace { get; } = keyspace;

        public bool IsDisposed { get; private set; }

        public bool TryGet<TKey, TValue>(TKey key, out TValue value)
            where TKey : notnull
        {
            value = default!;
            return false;
        }

        public void Set<TKey, TValue>(TKey key, TValue value, TimeSpan? expiry = null)
            where TKey : notnull
        {
            throw new NotSupportedException();
        }

        public TValue GetOrAdd<TKey, TValue>(TKey key, Func<TKey, TValue> factory, TimeSpan? expiry = null)
            where TKey : notnull
        {
            throw new NotSupportedException();
        }

        public ValueTask<TValue> GetOrAddAsync<TKey, TValue>(
            TKey key,
            Func<TKey, CancellationToken, ValueTask<TValue>> factory,
            TimeSpan? expiry = null,
            CancellationToken cancellationToken = default)
            where TKey : notnull
        {
            return ValueTask.FromException<TValue>(new NotSupportedException());
        }

        public bool Remove<TKey, TValue>(TKey key)
            where TKey : notnull
        {
            return false;
        }

        public void Clear()
        {
        }

        public LfuCacheStats GetStats()
        {
            return new LfuCacheStats(0, 0, 0, 0, 0, 0, 0);
        }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }
}
