# LFU Cache Component Design (.NET 8 / .NET 10)

[English](design.md) | [简体中文](design.zh-CN.md)

## 1. Goals and Boundaries

This component provides an in-process LFU cache registered with the dependency injection container through
`AddLfuCache`.

The current implementation includes:

- keyed-service injection, where the service key is the keyspace; `default` is used when none is specified.
- one independent `ConcurrentDictionary<TKey, CacheEntry<TValue>>` per keyspace.
- a typed interface and a non-generic interface with generic methods, both forwarding to the same dictionary.
- per-entry expiry, read-time expiration, and incremental background cleanup.
- proportional batch LFU eviction, with LRU as the secondary order for equal frequencies.
- a capacity overflow watermark and synchronous eviction backpressure.
- frequency decay and new-entry protection.
- whole-object hot reload of named options.
- one background maintenance loop, statistics, metrics, and structured logging.
- single-key stampede protection for `GetOrAdd` / `GetOrAddAsync`.

It does not include distributed consistency, persistence, byte-based accounting, or application integration logic.
The repository's generic console sample demonstrates only the component API.

## 2. Keyspaces and Type Constraints

A keyspace is simultaneously a configuration boundary, a DI boundary, and a storage boundary. Each keyspace can
register only one `(TKey, TValue)` combination and creates only one corresponding
`ConcurrentDictionary<TKey, CacheEntry<TValue>>`.

```text
keyspace-a -> ConcurrentDictionary<TKeyA, CacheEntry<TValueA>>
keyspace-b -> ConcurrentDictionary<TKeyB, CacheEntry<TValueB>>
```

Repeated registration of the same type combination for one keyspace is idempotent; registering a different type
combination throws `InvalidOperationException` immediately. To cache another type pair, use another keyspace.

This constraint guarantees that:

- `Capacity` is the capacity of the entire keyspace and does not need to be summed across type combinations.
- LFU ordering covers every entry in the keyspace and is exact LFU.
- Keys and values retain their actual generic types, with no `object` backing store and no boxing of value types.
- The dynamic entry point can forward only to the type combination registered for the keyspace; it cannot lazily
  create a dictionary per call.

Keyspaces are normalized consistently:

- `null`, the empty string, and strings containing only whitespace normalize to `default`.
- Non-empty values are trimmed and then converted to invariant lowercase.
- Registration, keyed DI, and the registry use the same normalized result.

## 3. Public API

Only two cache interfaces, options, stats, and registration extension methods are public. Storage, maintenance,
registration indexes, validation, and metrics implementations are all `internal`.

### 3.1 Typed Interface

```csharp
public interface ILfuCache<TKey, TValue> where TKey : notnull
{
    string Keyspace { get; }
    int Count { get; }

    bool TryGet(TKey key, out TValue value);
    void Set(TKey key, TValue value, TimeSpan? expiry = null);

    TValue GetOrAdd(
        TKey key,
        Func<TKey, TValue> factory,
        TimeSpan? expiry = null);

    ValueTask<TValue> GetOrAddAsync(
        TKey key,
        Func<TKey, CancellationToken, ValueTask<TValue>> factory,
        TimeSpan? expiry = null,
        CancellationToken ct = default);

    bool Remove(TKey key);
    void Clear();
    LfuCacheStats GetStats();
}
```

The typed interface is the default entry point.

### 3.2 Dynamic Interface

```csharp
public interface ILfuCache
{
    string Keyspace { get; }

    bool TryGet<TKey, TValue>(TKey key, out TValue value) where TKey : notnull;

    void Set<TKey, TValue>(TKey key, TValue value, TimeSpan? expiry = null)
        where TKey : notnull;

    TValue GetOrAdd<TKey, TValue>(
        TKey key,
        Func<TKey, TValue> factory,
        TimeSpan? expiry = null)
        where TKey : notnull;

    ValueTask<TValue> GetOrAddAsync<TKey, TValue>(
        TKey key,
        Func<TKey, CancellationToken, ValueTask<TValue>> factory,
        TimeSpan? expiry = null,
        CancellationToken ct = default)
        where TKey : notnull;

    bool Remove<TKey, TValue>(TKey key) where TKey : notnull;
    void Clear();
    LfuCacheStats GetStats();
}
```

`DynamicLfuCache` does not store entries. Its typed methods first read the unique cache handle for the keyspace from
the registry, validate `TKey` and `TValue`, and then forward to `ILfuCache<TKey, TValue>`. A single call adds only
one dictionary lookup and one interface conversion; it does not perform DI resolution or box keys or values.

If the call's types do not match the types registered for the keyspace, the dynamic entry point throws
`InvalidOperationException`. The exception message contains the keyspace, the actual types, and the correct
`AddLfuCache<TKey, TValue>` registration form.

`Clear()` and `GetStats()` operate directly on the keyspace's unique storage and do not aggregate across types.

### 3.3 Stats

```csharp
public readonly record struct LfuCacheStats(
    long Hits,
    long Misses,
    long Evictions,
    long Expirations,
    long EvictionBatches,
    int Count,
    int Capacity);
```

`Count` is the number of physical entries and may temporarily include expired entries that have not yet been
reclaimed by a background scan.

### 3.4 `null` Values

`null` is a valid cached value for reference types. Callers must use the Boolean result of `TryGet` to distinguish a
hit returning `null` from a miss; internally, entry existence must not be determined with `value is null`.

The cache stores object references and does not make defensive copies. Callers should treat cached values as
immutable objects.

## 4. Configuration

Each keyspace has one named `LfuCacheOptions` instance:

```csharp
public sealed class LfuCacheOptions
{
    public int Capacity { get; set; } = 10_000;
    public double EvictionRatio { get; set; } = 0.1;
    public TimeSpan? DefaultExpiry { get; set; }
    public TimeSpan MaintenanceInterval { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan DecayInterval { get; set; } = TimeSpan.FromMinutes(1);
    public double OverflowRatio { get; set; } = 0.05;
}
```

| Parameter | Meaning |
| --- | --- |
| `Capacity` | Entry capacity of the keyspace |
| `EvictionRatio` | Proportion of capacity evicted in one batch |
| `DefaultExpiry` | Relative expiry used when an entry expiry is not supplied; `null` means no expiry by default |
| `MaintenanceInterval` | Incremental background expiration scan interval |
| `DecayInterval` | Access-frequency half-life |
| `OverflowRatio` | Proportion by which the cache may temporarily exceed capacity during background eviction |

### 4.1 Validation

- `Capacity >= 1`
- `0 < EvictionRatio <= 0.5`
- `0 <= OverflowRatio <= 0.5`
- `DefaultExpiry` is unset or greater than zero
- `1s <= MaintenanceInterval <= 1h`
- `1s <= DecayInterval <= 24h`

The maintenance service eagerly resolves every cache when the host starts; the cache constructor validates the
complete named options instance, so invalid configuration causes host startup to fail. Invalid runtime
configuration does not replace the current snapshot and is logged at Warning level.

### 4.2 Whole-Object Hot Reload

Internally, options are converted to an immutable `OptionsSnapshot`, with the target, hard limit, scan budget, and
timestamp intervals calculated in advance. At runtime, a volatile reference replaces the entire snapshot; each
operation reads the snapshot only once.

`OnChange` should compare fields first. When no value has changed, it does not replace the snapshot, wake the
maintenance loop, or write a configuration-change log.

| Change | Behavior |
| --- | --- |
| `Capacity` increases | Update the watermarks with no additional action |
| `Capacity` decreases | Wake the maintenance loop and shrink in batches according to `EvictionRatio` |
| `EvictionRatio` / `OverflowRatio` | Subsequent eviction uses the new watermarks |
| `DefaultExpiry` | Affects only subsequent writes; existing entries are not modified |
| `MaintenanceInterval` / `DecayInterval` | Recalculate the next execution time from the change time and wake the maintenance loop |

The scan budget for one pass is derived from `Capacity`, `DefaultExpiry`, and `MaintenanceInterval`, with the goal
of completing one full scan within `min(DefaultExpiry, 1min)`; when expiry is not configured, a one-minute window is
used.

## 5. DI and Lifetime

The registration extensions provide:

```csharp
services.AddLfuCache<TKey, TValue>();
services.AddLfuCache<TKey, TValue>(keyspace);
services.AddLfuCache<TKey, TValue>(keyspace, configureOptions);
services.AddLfuCache<TKey, TValue>(keyspace, configurationSection);
```

Each registration performs the following steps:

1. Idempotently register the catalog, registry, metrics, `TimeProvider.System`, and maintenance service.
2. Validate that the keyspace is not already bound to another type combination.
3. Register configuration using the normalized keyspace as the options name.
4. Register a closed keyed singleton `ILfuCache<TKey, TValue>`.
5. Idempotently register one keyed singleton `ILfuCache` for the keyspace.
6. When the keyspace is `default`, additionally register an ordinary singleton; the ordinary service forwards to the
   keyed service, and both are the same instance.

No keyed open generic is registered.

Keyed singletons are created lazily by default. `LfuCacheMaintenanceService.StartAsync` enumerates the catalog and
proactively resolves every typed cache, so the registry contains the unique storage for every keyspace before host
startup completes and configuration errors are exposed during startup.

## 6. Data Structures and Concurrency

Each keyspace has the following storage structure:

```text
LfuCache<TKey, TValue>
├── ConcurrentDictionary<TKey, CacheEntry<TValue>> entries
├── long count
├── int evictionGate
├── expiration scan cursor
└── decay scan cursor
```

`count` is maintained with `Interlocked`; the hot path does not read `ConcurrentDictionary.Count`.
`evictionGate` uses CAS to guarantee that only one eviction executor runs for a keyspace at a time; a trigger that
loses the race returns immediately.

### 6.1 Entry

Completed entries are stored as follows:

```csharp
internal sealed class CacheEntry<TValue>
{
    public TValue Value;
    public long Frequency;
    public long LastAccessTicks;
    public long CreatedTicks;
    public long ExpiresAtTicks;
}
```

`Frequency` starts at 1 and is incremented atomically on a hit. `LastAccessTicks` is the LRU secondary order for
equal frequencies, `CreatedTicks` is used for new-entry protection, and `long.MaxValue` means never expires.

Stampede-protection state is also stored in the same primary dictionary; no second entry dictionary is created.
`TryGet` does not treat an incomplete factory as a hit; concurrent `GetOrAdd` calls share the single-execution state
in that entry.

### 6.2 Reference-Checked Removal

Expiration, eviction, and factory-failure paths must compare entry references:

```csharp
((ICollection<KeyValuePair<TKey, CacheEntry<TValue>>>)entries)
    .Remove(new(key, observedEntry));
```

These paths must not remove by key alone, or an old read could delete a newer entry later written for the same key.

### 6.3 Time Source

Expiry, maintenance intervals, decay intervals, and the protection window all use the injected `TimeProvider`.
Internally, use `GetTimestamp()` and convert durations using `TimestampFrequency`; a monotonic timestamp must not be
treated as `DateTime.UtcNow.Ticks`.

## 7. Read and Write Paths

### 7.1 `TryGet`

1. Call `TryGetValue`; if the entry does not exist or its factory is incomplete, record a miss.
2. If `now >= ExpiresAtTicks`, remove it by reference, record an expiration on success, and return a miss.
3. Atomically increment `Frequency` and update `LastAccessTicks`.
4. Record a hit and return `Value`; the value may be `null`.

The hit path acquires no explicit lock.

### 7.2 `Set`

1. An explicit expiry takes precedence; otherwise read the current snapshot's `DefaultExpiry`. A `null` argument uses
   the default, an explicit `TimeSpan.Zero` means that entry never expires, and negative values are rejected.
2. Add or replace the entry using a CAS loop.
3. Preserve the old entry's frequency on replacement; increment `count` for a new entry.
4. When `count` exceeds capacity, wake background maintenance; when it exceeds the hard limit, the writing thread
   attempts synchronous eviction.

### 7.3 `GetOrAdd`

Concurrent calls for the same key execute the factory only once. After the factory succeeds, the original entry is
atomically published as complete; on failure or cancellation, remove the entry by reference so subsequent calls can
retry.

An ordinary `Set` may replace an entry whose factory is running. The factory caller still receives its own result, but
when the factory completes it cannot overwrite the newer entry written by `Set`.

### 7.4 `Remove` and `Clear`

`Remove` deletes the entry observed at call time and decrements `count`. `Clear` atomically replaces the internal
store state, preventing `dictionary.Clear()` combined with concurrent inserts from causing count drift; operations
that acquired the old state do not affect the new dictionary.

## 8. Expiration and the Maintenance Loop

Expired entries have three reclamation paths: read-time expiration, incremental background scanning, and incidental
reclamation during eviction scanning. The read-time check guarantees that an expired value is never returned;
background scanning is responsible only for reclaiming capacity.

The component has only one `LfuCacheMaintenanceService`. Each cache handle exposes `NextDueTicks` and
`RunMaintenance(nowTicks)`. The maintenance loop:

1. Reads the minimum `NextDueTicks` across all keyspaces in the registry.
2. Uses `TimeProvider` to wait until that time, or wakes earlier on a watermark or configuration-change signal.
3. Runs only keyspaces that are due or need eviction.
4. Lets each instance independently decide whether to run expiration scanning, decay, or one eviction batch.

Expiration and decay each maintain a weakly consistent enumeration cursor. Each pass advances within its budget and
the next pass resumes from the previous position. All physical removals still use reference checks.

## 9. Batch Eviction

A keyspace uses three watermarks:

| Watermark | Calculation | Behavior |
| --- | --- | --- |
| soft limit | `Capacity` | Notify background maintenance when count exceeds it |
| target | `Capacity - ceil(Capacity * EvictionRatio)` | Target after one eviction batch |
| hard limit | `floor(Capacity * (1 + OverflowRatio))` | Have the writing thread attempt synchronous eviction when count exceeds it |

The eviction process:

1. Enumerate the unique dictionary and first remove expired entries by reference.
2. Compute how many entries, `k`, must be removed to reach target from the current count.
3. Use a max-heap of capacity `k` to select candidates with the smallest `(Frequency, LastAccessTicks)`, avoiding a
   full sort.
4. Remove candidates by reference and update eviction and batch statistics.

Entries created less than one second ago do not participate in the first candidate selection round. If there are not
enough other candidates, include these entries so eviction can make progress.

At every `DecayInterval`, incremental scanning shifts frequencies right by one, with a lower bound of 1.

| Operation | Complexity |
| --- | --- |
| Typed `TryGet` | Average `O(1)` |
| Dynamic `TryGet` | Average `O(1)`, plus one registry lookup |
| `Set` | Average `O(1)`; may evict synchronously when crossing the hard limit |
| Eviction batch | `O(N log k)` |
| Maintenance scan | `O(scan budget)` |

## 10. Observability

The component publishes through `System.Diagnostics.Metrics`:

- Counter: `lfu_cache.hits`, `lfu_cache.misses`, `lfu_cache.evictions`,
  `lfu_cache.expirations`, `lfu_cache.eviction.batches`, `lfu_cache.eviction.synchronous`.
- ObservableGauge: `lfu_cache.entries`, `lfu_cache.capacity`.
- Histogram: `lfu_cache.eviction.duration`.

Metrics carry `keyspace` and `value_type` tags.

Log levels:

- Information: whole-object configuration replacement and eviction-batch summaries.
- Warning: rejected invalid runtime configuration and synchronous eviction.
- Debug: maintenance wakeups and cleanup or decay results for each keyspace.

## 11. Acceptance Criteria

Core behavior:

- LFU ordering is correct; LRU is the secondary order for equal frequencies.
- A `null` hit can be distinguished from a miss.
- Explicit expiry overrides the default expiry; both read-time and background expiration work correctly.
- Replacing a value preserves its existing frequency.
- After capacity is exceeded, one eviction reaches target; continued writes between target and capacity do not
  trigger another batch.
- The hard limit triggers synchronous eviction.
- New-entry protection and the fallback path when candidates are insufficient both make progress.
- Frequency is halved per interval with a lower bound of 1.
- Whole-object configuration hot reload takes effect; invalid configuration preserves the old snapshot.
- Reference-checked removal does not delete a newer value written later.
- Concurrent `GetOrAdd` calls execute the factory once, and a failure can be retried.

DI and dynamic entry point:

- For `default`, keyed and ordinary resolution return the same typed cache and the same dynamic cache.
- Keyspace casing and leading/trailing whitespace are normalized.
- Repeated registration of the same type combination for one keyspace is idempotent; registering a different
  combination fails immediately.
- A value written through the dynamic entry point can be read through the typed interface, proving that both share
  the unique dictionary.
- A dynamic call with the wrong type throws a clear exception.
- After host startup, caches not yet resolved by business code are already in the registry.

Concurrency acceptance:

- Mixed get, set, remove, and maintenance operations have no deadlocks or unhandled exceptions.
- After operations finish and maintenance completes, the internal count matches the actual entry count in the unique
  dictionary.
- After count exceeds the hard limit, synchronous or background eviction brings it back to the target watermark.
