# EventHorizon.LfuCache

[English](README.md) | [简体中文](README.zh-CN.md)

`EventHorizon.LfuCache` is an in-process LFU cache. It integrates with Microsoft dependency injection, isolates data
by keyspace, expires entries independently, and evicts cold entries in proportional batches.

## Features

- One typed `ConcurrentDictionary` per keyspace, with one `(TKey, TValue)` pair per keyspace.
- Typed `ILfuCache<TKey, TValue>` API for normal use and non-generic `ILfuCache` for dynamic scenarios.
- Keyed-service registration; the `default` keyspace also supports ordinary, non-keyed injection.
- Per-entry relative expiry, read-time expiration, and incremental background cleanup.
- Batch LFU eviction with LRU ordering as the tie-breaker.
- Frequency decay, new-entry protection, and synchronous backpressure above the overflow watermark.
- Single-execution `GetOrAdd` and `GetOrAddAsync` for concurrent callers of the same key.
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

## Configuration Binding

```csharp
services.AddLfuCache<Guid, string>(
    "profiles",
    configuration.GetSection("LfuCache:profiles"));
```

```json
{
  "LfuCache": {
    "profiles": {
      "Capacity": 10000,
      "EvictionRatio": 0.1,
      "DefaultExpiry": "00:30:00",
      "MaintenanceInterval": "00:00:10",
      "DecayInterval": "00:05:00",
      "OverflowRatio": 0.05
    }
  }
}
```

Configuration reload replaces one validated immutable snapshot. Invalid runtime values are rejected and the previous
snapshot remains active.

## Dynamic API

Use `ILfuCache` only when the key and value types are unavailable at compile time. It forwards to the same typed store
and never owns separate entries:

```csharp
var cache = serviceProvider.GetRequiredKeyedService<ILfuCache>("profiles");
cache.Set<Guid, string>(profileId, profileName, TimeSpan.FromMinutes(5));
```

The method type arguments must match the single type pair registered for the keyspace; mismatches throw
`InvalidOperationException`.

## Sample

Run the generic-host sample:

```bash
dotnet run --project samples/EventHorizon.LfuCache.Sample
```

It demonstrates keyed DI, entry-specific expiry, `null` values, stampede-protected loading, statistics, and the dynamic
facade sharing the typed store.

## Build and Test

The repository uses a .NET 10 SDK to build the `net8.0` and `net10.0` package targets. `global.json` rolls forward to
newer major SDKs instead of pinning the repository to one installed patch.

```bash
dotnet restore EventHorizon.LfuCache.slnx
dotnet format EventHorizon.LfuCache.slnx --verify-no-changes
dotnet build EventHorizon.LfuCache.slnx -c Release --no-restore
dotnet test EventHorizon.LfuCache.slnx -c Release --no-build
```

Run the benchmark suite with:

```bash
dotnet run --project tests/benchmarks/EventHorizon.LfuCache.Benchmarks -c Release -- --filter '*'
```

The benchmarks compare typed and dynamic cache operations with `ConcurrentDictionary` and `MemoryCache`, and include a
capacity-pressure eviction workload.

See the [design document](docs/design.md) for concurrency, eviction, maintenance, configuration, and observability
details.

## License

Licensed under the [MIT License](LICENSE).
