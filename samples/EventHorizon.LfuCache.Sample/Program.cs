using EventHorizon.LfuCache;

const string CacheKeyspace = "sample";

var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { Args = args });
builder.WebHost.UseKestrelCore();
builder.Services.AddRoutingCore();
builder.Services.AddLfuCache<string, string?>(
    CacheKeyspace,
    options =>
    {
        options.Capacity = 1_000;
        options.DefaultExpiry = TimeSpan.FromMinutes(5);
        options.MaintenanceInterval = TimeSpan.FromSeconds(10);
        options.DecayInterval = TimeSpan.FromMinutes(1);
    });

var app = builder.Build();

app.MapGet("/", () => TypedResults.Ok(new CacheSampleInfo(CacheKeyspace)));

app.MapGet(
    "/cache/{key}",
    (string key, [FromKeyedServices(CacheKeyspace)] ILfuCache<string, string?> cache) =>
    {
        return cache.TryGet(key, out var value)
            ? Results.Ok(new CacheValueResponse(value))
            : Results.NotFound();
    });

app.MapPut(
    "/cache/{key}",
    (string key, CacheValueRequest request, [FromKeyedServices(CacheKeyspace)] ILfuCache<string, string?> cache) =>
    {
        cache.Set(key, request.Value);
        return TypedResults.NoContent();
    });

app.MapDelete(
    "/cache/{key}",
    (string key, [FromKeyedServices(CacheKeyspace)] ILfuCache<string, string?> cache) =>
    {
        return cache.Remove(key) ? Results.NoContent() : Results.NotFound();
    });

app.MapGet(
    "/cache/stats",
    ([FromKeyedServices(CacheKeyspace)] ILfuCache cache) => TypedResults.Ok(cache.GetStats()));

app.Run();

internal sealed record CacheSampleInfo(string Keyspace);

internal sealed record CacheValueRequest(string? Value);

internal sealed record CacheValueResponse(string? Value);
