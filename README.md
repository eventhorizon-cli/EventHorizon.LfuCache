# EventHorizon.LfuCache

[English](README.md) | [简体中文](README.zh-CN.md)

`EventHorizon.LfuCache` is an in-process LFU cache. It integrates with Microsoft dependency injection, isolates data
by keyspace, expires entries independently, and evicts cold entries in proportional batches.

## Features

- One typed `ConcurrentDictionary` per keyspace, with one `(TKey, TValue)` pair per keyspace.
- Typed `ILfuCache<TKey, TValue>` API for normal use and non-generic `ILfuCache` for dynamic scenarios.
- Keyed-service registration; the `default` keyspace also supports ordinary, non-keyed injection.
- Per-entry relative expiry, read-time expiration, and incremental background cleanup.
- Batch LFU eviction with frequency as the primary order, one-second new-entry protection as the secondary order,
  and LRU as the tertiary order.
- Time-based frequency decay with lazy normalization on reads and eviction, plus incremental maintenance work.
- Single-execution `GetOrAdd` and `GetOrAddAsync` for concurrent callers of the same key.
- A per-keyspace limit for concurrently running factories, with same-key sharing.
- Whole-object hot reload through named options.
- Built-in statistics, metrics, and structured logging.
- `null` is a valid cached value for reference types.

## Installation

```bash
dotnet add package EventHorizon.LfuCache
```

## Registration

Register a typed cache in a named keyspace:

```csharp
services.AddLfuCache<Guid, string>("profiles", options =>
{
    options.Capacity = 10_000;
    options.EvictionRatio = 0.1;
    options.DefaultExpiry = TimeSpan.FromMinutes(30);
    options.DecayInterval = TimeSpan.FromMinutes(5);
});
```

Resolve and use it as a keyed service:

```csharp
var cache = serviceProvider.GetRequiredKeyedService<ILfuCache<Guid, string>>("profiles");

cache.Set(profileId, profileName);

if (cache.TryGet(profileId, out var cachedName))
{
    // cachedName is available, including null when TValue permits null.
}
```

For the `default` keyspace, registration and ordinary injection are available without a service key:

```csharp
services.AddLfuCache<Guid, string>();

public sealed class ProfileReader(ILfuCache<Guid, string> cache)
{
    // Use cache from application methods.
}
```

Keyspace names are trimmed and normalized case-insensitively. A keyspace can be registered repeatedly for the same
type pair, but registering another `(TKey, TValue)` pair for that keyspace throws immediately. Use another keyspace
when the types or configuration must differ.

## Expiry

The API uses relative-expiry terminology similar to StackExchange.Redis:

```csharp
cache.Set(key, value);                          // Uses DefaultExpiry.
cache.Set(key, value, TimeSpan.FromMinutes(2)); // Entry-specific expiry.
cache.Set(key, value, TimeSpan.Zero);           // Never expires.
```

A `null` method argument uses `DefaultExpiry`; an explicit `TimeSpan.Zero` disables expiration for that entry.
Negative expiry values are rejected. `DefaultExpiry` itself must be `null` or positive, where `null` means entries do
not expire by default.

## Configuration

```csharp
services.AddLfuCache<Guid, string>(
    "profiles",
    options =>
    {
        options.Capacity = 10_000;
        options.DefaultExpiry = TimeSpan.FromMinutes(30);
        options.MaintenanceInterval = TimeSpan.FromSeconds(10);
        options.MaxInflight = 2_000;
    });
```

Configure a keyspace directly at registration. If an external options source later produces a change, the cache
replaces its validated immutable snapshot; invalid runtime values are rejected and the previous snapshot remains
active. `MaxInflight` defaults to `null`, which inherits `Capacity`; a positive value overrides that limit.

`GetOrAdd` and `GetOrAddAsync` consume an in-flight slot only when they start a factory for a new key. Concurrent
callers for the same key share the existing factory. A new key whose keyspace has reached `MaxInflight` throws
`InvalidOperationException`; cache hits and `Set` do not consume a slot. Cancellation of a waiter, `Remove`, or
`Clear` does not release a slot while its factory is still running. A factory releases its slot only when it exits.
Lowering the limit lets existing factories finish and affects subsequent new-key calls.

`Capacity` controls entry-count eviction watermarks. It is not a byte budget or an absolute memory boundary, and
pending factories or concurrent operations may temporarily put the physical entry count above the capacity.

## Dynamic API

Use `ILfuCache` only when the key and value types are unavailable at compile time. It forwards to the same typed store
and never owns separate entries:

```csharp
var cache = serviceProvider.GetRequiredKeyedService<ILfuCache>("profiles");
cache.Set<Guid, string>(profileId, profileName, TimeSpan.FromMinutes(5));
```

The method type arguments must match the single type pair registered for the keyspace; mismatches throw
`InvalidOperationException`.

## OpenTelemetry

Register the cache's `EventHorizon.LfuCache` meter with an OpenTelemetry metrics pipeline by calling
`AddLfuCacheInstrumentation`:

```csharp
using EventHorizon.LfuCache;

services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddLfuCacheInstrumentation());
```

Configure the exporter separately in the host application. The cache publishes counters, observable gauges, and an
eviction-duration histogram with `keyspace` and `value_type` tags.

## Sample

Run the Web API sample:

```bash
dotnet run --project samples/EventHorizon.LfuCache.Sample -- --urls http://localhost:5057
```

It injects the keyed typed cache directly into minimal API handlers with
`[FromKeyedServices("sample")]`. Write and retrieve a value with:

```bash
curl -X PUT http://localhost:5057/cache/example \
  -H 'Content-Type: application/json' \
  -d '{"value":"cached"}'
curl http://localhost:5057/cache/example
```

## Build and Test

The repository uses a .NET 10 SDK to build the `net8.0` and `net10.0` package targets. `global.json` rolls forward to
newer major SDKs instead of pinning the repository to one installed patch.

```bash
dotnet restore EventHorizon.LfuCache.slnx
dotnet format EventHorizon.LfuCache.slnx --verify-no-changes
dotnet build EventHorizon.LfuCache.slnx -c Release --no-restore
dotnet test EventHorizon.LfuCache.slnx -c Release --no-build
```

The benchmark suite includes typed and dynamic operations, `ConcurrentDictionary` and `MemoryCache` comparisons,
capacity-pressure eviction, pressure writes, multi-threaded workloads, and deterministic policy scenarios. See the
[benchmark methodology and before/after comparison report](docs/benchmarks.md) for the current run instructions and
results.

See the [design document](docs/design.md) for concurrency, eviction, maintenance, configuration, and observability
details.

## License

Licensed under the [MIT License](LICENSE).
