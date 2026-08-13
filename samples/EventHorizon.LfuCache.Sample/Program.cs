using EventHorizon.LfuCache;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

const string keyspace = "sample";

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    DisableDefaults = true,
});
builder.Services.AddLfuCache<int, string?>(
    keyspace,
    options =>
    {
        options.Capacity = 32;
        options.DefaultExpiry = TimeSpan.FromSeconds(1);
        options.MaintenanceInterval = TimeSpan.FromSeconds(1);
        options.DecayInterval = TimeSpan.FromMinutes(1);
    });

using var host = builder.Build();
using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
Console.WriteLine("Starting the LFU cache sample...");
await host.StartAsync(startupTimeout.Token);
Console.WriteLine("Host started.");

try
{
    var cache = host.Services.GetRequiredKeyedService<ILfuCache<int, string?>>(keyspace);
    var facade = host.Services.GetRequiredKeyedService<ILfuCache>(keyspace);

    var relativeExpiry = TimeSpan.FromMilliseconds(150);
    cache.Set(1, "expires soon", relativeExpiry);
    Console.WriteLine($"Positive relative expiry: {cache.TryGet(1, out var beforeExpiry)} ({beforeExpiry})");

    await Task.Delay(relativeExpiry + TimeSpan.FromMilliseconds(50));
    Console.WriteLine($"After expiry: {cache.TryGet(1, out _)}");

    cache.Set(2, null, TimeSpan.Zero);
    await Task.Delay(200);
    Console.WriteLine($"Null value with no-expiry override: {cache.TryGet(2, out var nullValue)} (value is null: {nullValue is null})");

    var asyncValue = await cache.GetOrAddAsync(
        3,
        static (_, _) => new ValueTask<string?>("created asynchronously"),
        TimeSpan.Zero);
    Console.WriteLine($"GetOrAddAsync: {asyncValue}");

    facade.Set<int, string?>(4, "written through facade", TimeSpan.Zero);
    facade.TryGet<int, string?>(4, out var forwardedValue);
    cache.TryGet(4, out var typedValue);
    Console.WriteLine($"Non-generic facade forwards to typed store: {forwardedValue} / {typedValue}");

    var typedStats = cache.GetStats();
    var facadeStats = facade.GetStats();
    Console.WriteLine($"Stats (typed): hits={typedStats.Hits}, misses={typedStats.Misses}, count={typedStats.Count}, capacity={typedStats.Capacity}");
    Console.WriteLine($"Stats (facade): hits={facadeStats.Hits}, misses={facadeStats.Misses}, count={facadeStats.Count}, capacity={facadeStats.Capacity}");
}
finally
{
    using var shutdownTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await host.StopAsync(shutdownTimeout.Token);
}
