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
}
