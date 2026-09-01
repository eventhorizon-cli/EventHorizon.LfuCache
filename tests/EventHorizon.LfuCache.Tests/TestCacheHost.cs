using EventHorizon.LfuCache.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace EventHorizon.LfuCache.Tests;

internal sealed class TestCacheHost<TKey, TValue> : IDisposable
    where TKey : notnull
{
    private readonly ServiceProvider _services;
    private readonly string _normalizedKeyspace;

    public TestCacheHost(string? keyspace = "test", Action<LfuCacheOptions>? configure = null)
    {
        Clock = new TestTimeProvider();

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(Clock);
        if (configure is null)
        {
            services.AddLfuCache<TKey, TValue>(keyspace);
        }
        else
        {
            services.AddLfuCache<TKey, TValue>(keyspace, configure);
        }

        _services = services.BuildServiceProvider();
        _normalizedKeyspace = string.IsNullOrWhiteSpace(keyspace)
            ? "default"
            : keyspace.Trim().ToLowerInvariant();
        Cache = _services.GetRequiredKeyedService<ILfuCache<TKey, TValue>>(_normalizedKeyspace);
    }

    public TestTimeProvider Clock { get; }

    public ILfuCache<TKey, TValue> Cache { get; }

    public LfuCache<TKey, TValue> Implementation =>
        _services.GetRequiredKeyedService<LfuCache<TKey, TValue>>(_normalizedKeyspace);

    public IServiceProvider Services => _services;

    public void Dispose()
    {
        _services.Dispose();
    }
}
